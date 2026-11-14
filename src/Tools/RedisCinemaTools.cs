using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CinemaAgent.Models;
using Microsoft.Extensions.AI;
using StackExchange.Redis;

public sealed class RedisCinemaTools
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly StringComparer PolishOrder = StringComparer.Create(Polish, ignoreCase: true);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex RuntimePattern = new(
        @"^(?:(?<hours>[0-9]+)\s*h\s*)?(?:(?<minutes>[0-9]+)\s*min)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private const string SchedulePattern = "*:*:*:????-??-??";
    private const int BatchSize = 128;

    private readonly IDatabase database;
    private readonly IServer server;
    private readonly int userAge;
    private readonly bool enforceAgeRating;
    private readonly UserProfile user;
    private readonly Func<DateTimeOffset> utcNow;

    // Dla lokalnego, pojedynczego serwera Redis. Współdziel multiplexer między instancjami.
    public RedisCinemaTools(
        IConnectionMultiplexer redis,
        int userAge,
        bool enforceAgeRating,
        UserProfile userP,
        int databaseNumber = 0,
        Func<DateTimeOffset>? utcNow = null)
    {
        database = redis.GetDatabase(databaseNumber);
        server = redis.GetServer(redis.GetEndPoints().Single());
        this.userAge = userAge;
        user = userP;
        this.enforceAgeRating = enforceAgeRating;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<AIFunction> CreateAll() =>
        new[] { nameof(FindCinemas), nameof(SearchMovies), nameof(GetScreenings), nameof(GetUserProfile) }
            .Select(name => AIFunctionFactory.Create(
                typeof(RedisCinemaTools).GetMethod(name)!, this,
                new AIFunctionFactoryOptions
                {
                    Name = Regex.Replace(name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant()
                }))
            .ToList();

    [Description("Return the current user's profile.")]
    public object? GetUserProfile() => user;

    [Description("Find cinemas with available repertory. All supplied filters must match. " +
                 "Omit the filters to list all cinemas.")]
    public async Task<object> FindCinemas(
        [Description("City name or Kinowo slug, e.g. Warszawa, Łódź or trojmiasto")] string? city = null,
        [Description("Cinema network, e.g. Multikino, Cinema City, Helios or inne")] string? network = null,
        [Description("Cinema name fragment, e.g. Złote Tarasy or Arkadia")] string? name = null,
        CancellationToken cancellationToken = default)
    {
        var found = GetCinemaEntries(await ListSchedules(cancellationToken))
            .Where(cinema => Matches(cinema.City, city) &&
                Matches(cinema.Network, network) && Matches(cinema.Name, name))
            .ToList();
        return new
        {
            total = found.Count,
            cinemas = found.Take(30).Select(cinema => new
            {
                cinema.Id,
                cinema.Name,
                cinema.City,
                cinema.Network,
                Address = (string?)null
            }).ToList()
        };
    }

    [Description("Search movies by title or genre and return their details, including ratings.")]
    public async Task<object> SearchMovies(
        [Description("Part of the title stored in Kinowo")] string? title = null,
        [Description("Genre, e.g. Horror, Komedia, Sci-Fi, Animacja")] string? genre = null,
        CancellationToken cancellationToken = default)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var key in server.KeysAsync(database.Database, "movie:*").WithCancellation(cancellationToken))
            keys.Add(key.ToString());

        var movies = await ReadDocuments<MovieDocument>(keys, cancellationToken);
        return movies
            .Where(pair => MatchesMovie(pair.Value, title, genre) && IsAllowed(pair.Value.AgeRating))
            .OrderBy(pair => pair.Value.Title, PolishOrder)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new
            {
                Id = pair.Key["movie:".Length..],
                pair.Value.Title,
                pair.Value.Year,
                DurationMinutes = ParseDuration(pair.Value.Duration),
                pair.Value.Genres,
                pair.Value.AgeRating,
                Description = (string?)null,
                pair.Value.Ratings,
                pair.Value.Director,
                pair.Value.Cast,
                pair.Value.Countries
            }).ToList();
    }

    [Description("List upcoming screenings in chronological order, using Polish local dates and times. " +
                 "Use it for the nearest film in a cinema, a genre at a certain time or when a film plays. " +
                 "If only dateFrom is given, search that day.")]
    public async Task<object> GetScreenings(
        [Description("Cinema name fragment, e.g. Złote Tarasy or Cinema City Arkadia")] string? cinema = null,
        [Description("Movie title fragment")] string? title = null,
        [Description("Genre, e.g. Horror")] string? genre = null,
        [Description("Date yyyy-MM-dd")] string? dateFrom = null,
        [Description("Date yyyy-MM-dd inclusive; defaults to dateFrom")] string? dateTo = null,
        [Description("Earliest local start HH:mm; evening = 17:00")] string? timeFrom = null,
        [Description("Latest local start HH:mm inclusive")] string? timeTo = null,
        [Description("Max results (1-20)")] int limit = 10,
        [Description("City name or its Kinowo slug, e.g. Warszawa, Łódź or trojmiasto")] string? city = null,
        CancellationToken cancellationToken = default)
    {
        var startDate = ParseDate(dateFrom, nameof(dateFrom));
        var endDate = ParseDate(dateTo, nameof(dateTo)) ?? startDate;
        var startTime = ParseTime(timeFrom, nameof(timeFrom));
        var endTime = ParseTime(timeTo, nameof(timeTo));
        if (startDate > endDate)
            throw new ArgumentException("dateTo must be on or after dateFrom.");
        if (startTime > endTime)
            throw new ArgumentException("timeTo must be at or after timeFrom.");

        var now = TimeZoneInfo.ConvertTime(utcNow(), Warsaw);
        var today = DateOnly.FromDateTime(now.DateTime);
        var schedules = (await ListSchedules(cancellationToken))
            .Where(schedule => Matches(schedule.Cinema.City, city)).ToList();
        var cinemas = FindCinemaEntries(schedules, cinema);
        if (!string.IsNullOrWhiteSpace(cinema) && cinemas.Count == 0)
            return CinemaNotFound(cinema, schedules);

        var cinemaIds = cinemas.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var selected = schedules.Where(schedule =>
            cinemaIds.Contains(schedule.Cinema.Id) &&
            schedule.Day >= today &&
            (startDate is null || schedule.Day >= startDate) &&
            (endDate is null || schedule.Day <= endDate)).ToDictionary(schedule => schedule.Key);

        var documents = await ReadDocuments<ScreeningDocument[]>(selected.Keys, cancellationToken);
        var candidates = new List<(ScheduleKey Schedule, ScreeningDocument Screening, DateTimeOffset StartsAt)>();
        foreach (var (key, screenings) in documents)
        {
            var schedule = selected[key];
            foreach (var screening in screenings)
            {
                if (string.IsNullOrWhiteSpace(screening.MovieId) ||
                    !TimeOnly.TryParseExact(screening.Time, "HH:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var localTime) ||
                    (startTime is not null && localTime < startTime) ||
                    (endTime is not null && localTime > endTime))
                    continue;

                var startsAt = NextStart(schedule.Day.ToDateTime(localTime), now);
                if (startsAt is not null)
                    candidates.Add((schedule, screening, startsAt.Value));
            }
        }

        var movieKeys = candidates.Select(candidate => $"movie:{candidate.Screening.MovieId}")
            .Distinct(StringComparer.Ordinal);
        var movies = await ReadDocuments<MovieDocument>(movieKeys, cancellationToken);
        var matching = candidates
            .Select(candidate => (Candidate: candidate,
                Movie: movies.GetValueOrDefault($"movie:{candidate.Screening.MovieId}")))
            .Where(item => item.Movie is not null && IsAllowed(item.Movie.AgeRating) &&
                MatchesMovie(item.Movie, title, genre))
            .OrderBy(item => item.Candidate.StartsAt)
            .ThenBy(item => item.Candidate.Schedule.Cinema.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Candidate.Screening.MovieId, StringComparer.Ordinal)
            .ToList();

        return new
        {
            total = matching.Count,
            note = matching.Count == 0
                ? "No upcoming screenings match these filters."
                : "Sorted chronologically; first item is the nearest.",
            screenings = matching.Take(Math.Clamp(limit, 1, 20)).Select(item => new
            {
                item.Candidate.Screening.MovieId,
                item.Movie!.Title,
                item.Movie.AgeRating,
                item.Movie.Genres,
                Cinema = item.Candidate.Schedule.Cinema.Name,
                item.Candidate.Schedule.Cinema.City,
                item.Candidate.Schedule.Cinema.Network,
                Date = item.Candidate.Schedule.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                item.Candidate.Screening.Time,
                item.Candidate.StartsAt,
                item.Candidate.Screening.Room,
                item.Candidate.Screening.Format,
                item.Candidate.Screening.TicketUrl
            }).ToList()
        };
    }

    private async Task<List<ScheduleKey>> ListSchedules(CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var schedules = new List<ScheduleKey>();
        await foreach (var redisKey in server.KeysAsync(database.Database, SchedulePattern)
                           .WithCancellation(cancellationToken))
        {
            var key = redisKey.ToString();
            if (!seen.Add(key))
                continue;

            var parts = key.Split(':');
            if (parts.Length != 4 || !DateOnly.TryParseExact(parts[3], "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                continue;

            var name = parts[2].Replace("%3A", ":").Replace("%25", "%");
            var cinema = new CinemaEntry(string.Join(":", parts, 0, 3), name, parts[0], parts[1]);
            schedules.Add(new ScheduleKey(key, cinema, day));
        }
        return schedules;
    }

    private async Task<Dictionary<string, T>> ReadDocuments<T>(
        IEnumerable<string> keys, CancellationToken cancellationToken) where T : class
    {
        var documents = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var batch in keys.Chunk(BatchSize))
        {
            var arguments = batch.Cast<object>().Append(".").ToArray();
            var result = await database.ExecuteAsync("JSON.MGET", arguments).WaitAsync(cancellationToken);
            var values = (RedisResult[])result!;
            for (var i = 0; i < batch.Length; i++)
            {
                // Klucz może wygasnąć między SCAN a odczytem.
                if (!values[i].IsNull && (string?)values[i] is { } json &&
                    JsonSerializer.Deserialize<T>(json, JsonOptions) is { } document)
                    documents.Add(batch[i], document);
            }
        }
        return documents;
    }

    private static IEnumerable<CinemaEntry> GetCinemaEntries(IEnumerable<ScheduleKey> schedules) =>
        schedules.Select(item => item.Cinema)
            .DistinctBy(cinema => cinema.Id)
            .OrderBy(cinema => cinema.City, StringComparer.Ordinal)
            .ThenBy(cinema => cinema.Name, PolishOrder);

    private static List<CinemaEntry> FindCinemaEntries(IEnumerable<ScheduleKey> schedules, string? query) =>
        GetCinemaEntries(schedules)
            .Where(cinema => Matches(cinema.Name, query) ||
                Matches(cinema.City, query) || Matches(cinema.Network, query))
            .ToList();

    private static object CinemaNotFound(string query, IEnumerable<ScheduleKey> schedules)
    {
        return new
        {
            total = 0,
            note = $"No cinema matches '{query}'. Tell the user politely and offer available cinemas.",
            availableCinemas = GetCinemaEntries(schedules).Take(30).Select(cinema => cinema.Name).ToList()
        };
    }

    private static bool Matches(string? text, string? query) =>
        string.IsNullOrWhiteSpace(query) || (text is not null &&
            SearchText(text).Contains(SearchText(query.Trim()), StringComparison.OrdinalIgnoreCase));

    private static string SearchText(string text) =>
        new string(text.Replace('-', ' ').Replace('ł', 'l').Replace('Ł', 'L')
            .Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .ToArray());

    private static bool MatchesMovie(MovieDocument movie, string? title, string? genre) =>
        Matches(movie.Title, title) && (string.IsNullOrWhiteSpace(genre) ||
            (movie.Genres?.Any(value => Matches(value, genre)) ?? false));

    private bool IsAllowed(string? rating)
    {
        if (!enforceAgeRating)
            return true;

        var value = rating?.Trim().ToUpperInvariant();
        if (value is "BO" or "BO+" or "B.O" or "AL" or "BEZ-OGRANICZEN")
            return true;

        if (value?.EndsWith('+') == true)
            value = value[..^1].TrimEnd();

        return int.TryParse(value, NumberStyles.None,
            CultureInfo.InvariantCulture, out var minimumAge) && userAge >= minimumAge;
    }

    private static DateOnly? ParseDate(string? value, string argument) =>
        string.IsNullOrWhiteSpace(value) ? null
            : DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date : null;

    private static TimeOnly? ParseTime(string? value, string argument) =>
        string.IsNullOrWhiteSpace(value) ? null
            : TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                ? time : null;

    private static DateTimeOffset? NextStart(DateTime localTime, DateTimeOffset now)
    {
        if (Warsaw.IsInvalidTime(localTime))
            return null;

        var offset = Warsaw.IsAmbiguousTime(localTime)
            ? Warsaw.GetAmbiguousTimeOffsets(localTime).Max()
            : Warsaw.GetUtcOffset(localTime);
        var startsAt = new DateTimeOffset(localTime, offset);
        return startsAt >= now ? startsAt : null;
    }

    private static int? ParseDuration(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
            return null;

        var match = RuntimePattern.Match(duration.Trim());
        if (!match.Success ||
            !int.TryParse(match.Groups["hours"].Success ? match.Groups["hours"].Value : "0", out var hours) ||
            !int.TryParse(match.Groups["minutes"].Success ? match.Groups["minutes"].Value : "0", out var minutes))
            return null;
        var total = 60L * hours + minutes;
        return total is > 0 and <= int.MaxValue ? (int)total : null;
    }

    private sealed record CinemaEntry(string Id, string Name, string City, string Network);
    private sealed record ScheduleKey(string Key, CinemaEntry Cinema, DateOnly Day);

    private sealed record ScreeningDocument(
        [property: JsonPropertyName("movie_id")] string MovieId,
        string Time,
        string? Room,
        string? Format,
        [property: JsonPropertyName("ticket_url")] string? TicketUrl);

    private sealed record MovieDocument(
        string Title,
        int? Year,
        string? Duration,
        string[]? Genres,
        [property: JsonPropertyName("age_rating")] string? AgeRating,
        Dictionary<string, JsonElement>? Ratings,
        string? Director,
        string? Cast,
        string[]? Countries);
}
