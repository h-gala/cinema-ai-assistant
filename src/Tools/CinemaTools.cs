using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using CinemaAgent.Storage;
using CinemaAgent.Models;
using Microsoft.Extensions.AI;

namespace CinemaAgent.Tools;

public sealed class CinemaTools
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly StringComparer PolishOrder = StringComparer.Create(Polish, ignoreCase: true);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Jedno, spójne nazewnictwo (camelCase, bez nulli) dla wszystkiego, co widzi model.
    private static readonly JsonSerializerOptions ToolJson = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Regex RuntimePattern = new(
        @"^(?:(?<hours>[0-9]+)\s*h\s*)?(?:(?<minutes>[0-9]+)\s*min)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private const int MaxPlacesPerFilm = 8;

    private readonly ICinemaStore store;
    private readonly UserProfile user;
    private readonly bool enforceAgeRating;
    private readonly Func<DateTimeOffset> utcNow;

    public CinemaTools(
        ICinemaStore store,
        UserProfile user,
        bool enforceAgeRating,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.store = store;
        this.user = user;
        this.enforceAgeRating = enforceAgeRating;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<AIFunction> CreateAll() =>
        new[] { nameof(FindCinemas), nameof(GetScreenings), nameof(SearchMovies),
                nameof(GetMovieDetails), nameof(GetMovieRatings), nameof(GetTicketUrl) }
            .Select(name => AIFunctionFactory.Create(
                typeof(CinemaTools).GetMethod(name)!, this,
                new AIFunctionFactoryOptions
                {
                    Name = Regex.Replace(name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant()
                }))
            .ToList();

    private static JsonElement Result(object value) =>
        JsonSerializer.SerializeToElement(value, ToolJson);

    // ───────────────────────── narzędzia ─────────────────────────

    [Description("Find cinemas with available repertory. All supplied filters must match. " +
                 "By default only the user's home city is searched; pass city='all' for every city.")]
    public async Task<JsonElement> FindCinemas(
        [Description("City name or Kinowo slug, e.g. Warszawa, Łódź or trojmiasto; 'all' = every city")] string? city = null,
        [Description("Cinema network, e.g. Multikino, Cinema City, Helios or inne")] string? network = null,
        [Description("Cinema name fragment, e.g. Złote Tarasy or Arkadia")] string? name = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveCity = EffectiveCity(city, specificTarget: !string.IsNullOrWhiteSpace(name));
        var schedules = await ListSchedules(cancellationToken);
        if (effectiveCity is not null && !schedules.Any(s => Matches(s.Cinema.City, effectiveCity)))
            return CityNotFound(effectiveCity, schedules);

        var found = GetCinemaEntries(schedules)
            .Where(cinema => Matches(cinema.City, effectiveCity) &&
                Matches(cinema.Network, network) && Matches(cinema.Name, name))
            .ToList();

        return Result(new
        {
            city = effectiveCity ?? "all",
            total = found.Count,
            cinemas = found.Take(30).Select(cinema => new
            {
                cinema.Id,
                cinema.Name,
                cinema.City,
                cinema.Network
            }).ToList()
        });
    }

    [Description("Search movies by title or one or more comma-separated genres, e.g. 'Sci-Fi, Dramat'. " +
                 "A movie matches if it has any requested genre. Returns id, title, year, genres. Use get_movie_details for more. " +
                 "Genres the user dislikes are hidden unless asked for explicitly.")]
    public async Task<JsonElement> SearchMovies(
        [Description("Part of the title stored in Kinowo")] string? title = null,
        [Description("One or more comma-separated genres, e.g. 'Sci-Fi, Dramat'. A movie matches any listed genre.")] string? genre = null,
        [Description("Max results (1-30)")] int limit = 15,
        CancellationToken cancellationToken = default)
    {
        var hidden = HiddenGenres(title, genre);
        var keys = new HashSet<string>(await store.MovieKeysAsync(cancellationToken), StringComparer.Ordinal);

        var movies = await ReadDocuments<MovieDocument>(keys, cancellationToken);
        var matching = movies
            .Where(pair => MatchesMovie(pair.Value, title, genre) &&
                !IsHidden(pair.Value, hidden) && IsAllowed(pair.Value.AgeRating))
            .OrderBy(pair => pair.Value.Title, PolishOrder)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();

        return Result(new
        {
            total = matching.Count,
            hiddenGenres = hidden.Length > 0 ? hidden : null,
            movies = matching.Take(Math.Clamp(limit, 1, 30)).Select(pair => new
            {
                Id = pair.Key["movie:".Length..],
                pair.Value.Title,
                pair.Value.Year,
                pair.Value.Genres
            }).ToList()
        });
    }

    [Description("List upcoming screenings grouped by film, films with the nearest screening first. " +
                 "Each film lists cinemas, dates and times. Use for what is playing, the nearest film or a film's times. " +
                 "By default searches the user's home city and hides genres the user dislikes. " +
                 "If only dateFrom is given, search that day. limit = number of films.")]
    public async Task<JsonElement> GetScreenings(
        [Description("Cinema name fragment, e.g. Cinema City Arkadia")] string? cinema = null,
        [Description("Movie title fragment")] string? title = null,
        [Description("One or more comma-separated genres, e.g. 'Sci-Fi, Dramat'. A movie matches any listed genre.")] string? genre = null,
        [Description("Date yyyy-MM-dd")] string? dateFrom = null,
        [Description("Date yyyy-MM-dd inclusive; defaults to dateFrom")] string? dateTo = null,
        [Description("Earliest local start HH:mm; evening = 17:00")] string? timeFrom = null,
        [Description("Latest local start HH:mm inclusive")] string? timeTo = null,
        [Description("Max films per page (1-30)")] int limit = 20,
        [Description("Number of films to skip, for the next page")] int offset = 0,
        [Description("City name or Kinowo slug, e.g. Warszawa, trojmiasto. Defaults to the user's home city; 'all' = every city")] string? city = null,
        [Description("Screening format tokens, e.g. 3D, IMAX, NAP, DUB or 'IMAX 3D NAP'. " +
        "All specified tokens must match. Omit to include every format.")] string? format = null,
        CancellationToken cancellationToken = default)

    {
        var startDate = ParseDate(dateFrom);
        var parsedEnd = ParseDate(dateTo);
        var endDate = parsedEnd ?? startDate;
        var startTime = ParseTime(timeFrom);
        var endTime = ParseTime(timeTo);

        if ((!string.IsNullOrWhiteSpace(dateFrom) && startDate is null) ||
            (!string.IsNullOrWhiteSpace(dateTo) && parsedEnd is null))
        {
            return Result(new { error = "Dates must be yyyy-MM-dd." });
        }

        if ((!string.IsNullOrWhiteSpace(timeFrom) && startTime is null) ||
            (!string.IsNullOrWhiteSpace(timeTo) && endTime is null))
        {
            return Result(new { error = "Times must be HH:mm." });
        }

        if (startDate > endDate)
            return Result(new { error = "dateTo must be on or after dateFrom." });

        if (startTime > endTime)
            return Result(new { error = "timeTo must be at or after timeFrom." });

        var now = TimeZoneInfo.ConvertTime(utcNow(), Warsaw);
        var today = DateOnly.FromDateTime(now.DateTime);

        var effectiveCity = EffectiveCity(
            city,
            specificTarget: !string.IsNullOrWhiteSpace(cinema));

        var allSchedules = await ListSchedules(cancellationToken);

        var schedules = allSchedules
            .Where(schedule => Matches(schedule.Cinema.City, effectiveCity))
            .ToList();

        if (effectiveCity is not null && schedules.Count == 0)
            return CityNotFound(effectiveCity, allSchedules);

        var cinemas = FindCinemaEntries(schedules, cinema);

        if (!string.IsNullOrWhiteSpace(cinema) && cinemas.Count == 0)
            return CinemaNotFound(cinema, schedules);

        var cinemaIds = cinemas
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);

        var selected = schedules
            .Where(schedule =>
                cinemaIds.Contains(schedule.Cinema.Id) &&
                schedule.Day >= today &&
                (startDate is null || schedule.Day >= startDate) &&
                (endDate is null || schedule.Day <= endDate))
            .ToDictionary(schedule => schedule.Key);

        var documents = await ReadDocuments<ScreeningDocument[]>(
            selected.Keys,
            cancellationToken);

        var candidates = new List<(
            ScheduleKey Schedule,
            ScreeningDocument Screening,
            DateTimeOffset StartsAt)>();

        foreach (var (key, screenings) in documents)
        {
            var schedule = selected[key];

            foreach (var screening in screenings)
            {
                if (screening is null ||
                    string.IsNullOrWhiteSpace(screening.MovieId) ||
                    !MatchesScreeningFormat(
                        screening.Format,
                        screening.Room,
                        format) ||
                    !TimeOnly.TryParseExact(
                        screening.Time,
                        "HH:mm",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var localTime) ||
                    (startTime is not null && localTime < startTime) ||
                    (endTime is not null && localTime > endTime))
                {
                    continue;
                }

                var startsAt = NextStart(
                    schedule.Day.ToDateTime(localTime),
                    now);

                if (startsAt is not null)
                    candidates.Add((schedule, screening, startsAt.Value));
            }
        }

        var hidden = HiddenGenres(title, genre);

        var movieKeys = candidates
            .Select(candidate => $"movie:{candidate.Screening.MovieId}")
            .Distinct(StringComparer.Ordinal);

        var movies = await ReadDocuments<MovieDocument>(
            movieKeys,
            cancellationToken);

        var matching = candidates
            .Select(candidate => (
                Candidate: candidate,
                Movie: movies.GetValueOrDefault(
                    $"movie:{candidate.Screening.MovieId}")))
            .Where(item =>
                item.Movie is not null &&
                IsAllowed(item.Movie.AgeRating) &&
                MatchesMovie(item.Movie, title, genre) &&
                !IsHidden(item.Movie, hidden))
            .OrderBy(item => item.Candidate.StartsAt)
            .ThenBy(
                item => item.Candidate.Schedule.Cinema.Id,
                StringComparer.Ordinal)
            .ThenBy(
                item => item.Candidate.Screening.MovieId,
                StringComparer.Ordinal)
            .ToList();

        // Jeden film zajmuje jedną pozycję, niezależnie od liczby kin i dni.
        // Kolejność filmów wynika z ich najbliższego pasującego seansu.
        var films = matching
            .GroupBy(
                item => item.Candidate.Screening.MovieId,
                StringComparer.Ordinal)
            .ToList();

        var take = Math.Clamp(limit, 1, 30);
        var skip = Math.Max(offset, 0);
        var page = films.Skip(skip).Take(take).ToList();
        var hasMore = skip + page.Count < films.Count;

        var notes = new List<string>
    {
        films.Count == 0
            ? "No upcoming screenings match these filters."
            : hasMore
                ? $"Showing {page.Count} of {films.Count} films. " +
                  $"Call again with offset={skip + page.Count} for the rest, " +
                  "or tell the user there are more."
                : "All matching films are listed. Sorted by nearest screening."
    };

        if (hidden.Length > 0)
        {
            notes.Add(
                $"Hidden by the user's profile (disliked genres): " +
                $"{string.Join(", ", hidden)}. " +
                "They are shown only when the user asks for that genre " +
                "or a specific title.");
        }

        if (effectiveCity is not null)
        {
            notes.Add(
                $"Only city '{effectiveCity}' was searched; " +
                "use city='all' to search everywhere.");
        }

        return Result(new
        {
            city = effectiveCity ?? "all",
            totalFilms = films.Count,
            totalScreenings = matching.Count,
            shown = page.Count,
            note = string.Join(" ", notes),

            films = page.Select(group =>
            {
                var first = group.First();

                var places = group
                    .GroupBy(item => (
                        item.Candidate.Schedule.Cinema.Id,
                        item.Candidate.Schedule.Day))
                    .ToList();

                return new
                {
                    Id = group.Key,
                    first.Movie!.Title,
                    Age = first.Movie.AgeRating,
                    first.Movie.Genres,
                    Minutes = ParseDuration(first.Movie.Duration),

                    Showings = places
                        .Take(MaxPlacesPerFilm)
                        .Select(place => new
                        {
                            Cinema = place.First()
                                .Candidate.Schedule.Cinema.Name,

                            Date = place.Key.Day.ToString(
                                "yyyy-MM-dd",
                                CultureInfo.InvariantCulture),

                            Times = place.Select(item =>
                                item.Candidate.Screening.Format
                                    is { Length: > 0 } screeningFormat
                                        ? $"{item.Candidate.Screening.Time} {screeningFormat}"
                                        : item.Candidate.Screening.Time)
                                .ToList()
                        })
                        .ToList(),

                    MoreShowings = places.Count > MaxPlacesPerFilm
                        ? places.Count - MaxPlacesPerFilm
                        : (int?)null
                };
            }).ToList()
        });
    }


    [Description("Get details of one movie: genres, director, cast, countries, length, age rating. " +
                 "Pass movieId from get_screenings, or a title fragment.")]
    public async Task<JsonElement> GetMovieDetails(
        [Description("Movie id from get_screenings")] string? movieId = null,
        [Description("Title fragment, used if movieId is unknown")] string? title = null,
        CancellationToken cancellationToken = default)
    {
        var found = await ResolveMovies(movieId, title, cancellationToken);
        if (MovieLookupProblem(found) is { } problem)
            return problem;

        var (id, movie) = found[0];
        return Result(new
        {
            Id = id,
            movie.Title,
            movie.Year,
            Minutes = ParseDuration(movie.Duration),
            movie.AgeRating,
            movie.Genres,
            movie.Director,
            movie.Cast,
            movie.Countries
        });
    }

    [Description("Get ratings (IMDb, Filmweb, Rotten Tomatoes, Metacritic) of one movie. " +
                 "Pass movieId from get_screenings, or a title fragment.")]
    public async Task<JsonElement> GetMovieRatings(
        [Description("Movie id from get_screenings")] string? movieId = null,
        [Description("Title fragment, used if movieId is unknown")] string? title = null,
        CancellationToken cancellationToken = default)
    {
        var found = await ResolveMovies(movieId, title, cancellationToken);
        if (MovieLookupProblem(found) is { } problem)
            return problem;

        var (id, movie) = found[0];
        var ratings = new Dictionary<string, string>();
        foreach (var (source, value) in movie.Ratings ?? new())
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("score", out var score) || score.ValueKind != JsonValueKind.Number)
                continue;

            var scale = value.TryGetProperty("scale", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt32() : 0;
            var unit = value.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString() : null;

            ratings[source] = unit == "%"
                ? $"{score.GetDouble().ToString(CultureInfo.InvariantCulture)}%"
                : $"{score.GetDouble().ToString(CultureInfo.InvariantCulture)}/{scale}";
        }

        return Result(new
        {
            Id = id,
            movie.Title,
            Ratings = ratings,
            note = ratings.Count == 0 ? "No ratings available for this movie." : null
        });
    }

    [Description("Get the ticket purchase URL for one specific screening. " +
                 "Call get_screenings first, then pass the cinema, title, date and time from its result.")]
    public async Task<JsonElement> GetTicketUrl(
        [Description("Cinema name, e.g. Cinema City Bemowo")] string cinema,
        [Description("Movie title fragment")] string title,
        [Description("Date yyyy-MM-dd")] string date,
        [Description("Start time HH:mm")] string time,
        CancellationToken cancellationToken = default)
    {
        var day = ParseDate(date);
        var start = ParseTime(time);
        if (day is null || start is null)
            return Result(new { error = "date must be yyyy-MM-dd and time must be HH:mm." });

        var schedules = (await ListSchedules(cancellationToken))
            .Where(schedule => schedule.Day == day).ToList();
        var cinemaIds = FindCinemaEntries(schedules, cinema)
            .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var selected = schedules
            .Where(schedule => cinemaIds.Contains(schedule.Cinema.Id))
            .ToDictionary(schedule => schedule.Key);
        if (selected.Count == 0)
            return Result(new { error = $"No schedule for '{cinema}' on {date}." });

        var documents = await ReadDocuments<ScreeningDocument[]>(selected.Keys, cancellationToken);
        var candidates = documents
            .SelectMany(pair => pair.Value
                .Where(screening => screening is not null &&
                    !string.IsNullOrWhiteSpace(screening.MovieId) &&
                    TimeOnly.TryParseExact(screening.Time, "HH:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var local) && local == start)
                .Select(screening => (Schedule: selected[pair.Key], Screening: screening)))
            .ToList();

        var movieKeys = candidates.Select(c => $"movie:{c.Screening.MovieId}").Distinct(StringComparer.Ordinal);
        var movies = await ReadDocuments<MovieDocument>(movieKeys, cancellationToken);

        var matches = candidates
            .Select(c => (c.Schedule, c.Screening, Movie: movies.GetValueOrDefault($"movie:{c.Screening.MovieId}")))
            .Where(item => item.Movie is not null &&
                IsAllowed(item.Movie.AgeRating) && MatchesMovie(item.Movie, title, null))
            .ToList();

        if (matches.Count == 0)
            return Result(new { error = "No matching screening. Check cinema, title, date and time with get_screenings." });

        return Result(new
        {
            total = matches.Count,
            screenings = matches.Take(5).Select(item => new
            {
                item.Movie!.Title,
                Cinema = item.Schedule.Cinema.Name,
                Date = date,
                Time = time,
                item.Screening.Room,
                item.Screening.Format,
                item.Screening.TicketUrl
            }).ToList()
        });
    }

    // ───────────────────────── profil ─────────────────────────

    /// <summary>
    /// Miasto użyte w zapytaniu: jawne, albo z profilu. Domyślne miasto nie obowiązuje, gdy użytkownik
    /// wskazał konkretne kino. "all" / "wszystkie" wyłącza filtr.
    /// </summary>
    private string? EffectiveCity(string? requested, bool specificTarget)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var value = requested.Trim();
            return value.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("wszystkie", StringComparison.OrdinalIgnoreCase)
                ? null : value;
        }
        return specificTarget || string.IsNullOrWhiteSpace(user.City) ? null : user.City;
    }

    /// <summary>Niechciane gatunki ukrywamy, chyba że użytkownik prosi o konkretny tytuł lub o ten gatunek.</summary>
    private string[] HiddenGenres(string? title, string? genre)
    {
        if (!string.IsNullOrWhiteSpace(title) || user.DislikedGenres.Length == 0)
            return [];

        var requestedGenres = ParseGenres(genre);
        return user.DislikedGenres
            .Where(disliked => requestedGenres.Length == 0 ||
                !requestedGenres.Any(requested =>
                    Matches(disliked, requested) || Matches(requested, disliked)))
            .ToArray();
    }

    private static bool IsHidden(MovieDocument movie, string[] hidden) =>
        hidden.Length > 0 &&
        (movie.Genres?.Any(g => hidden.Any(h => Matches(g, h))) ?? false);

    // ───────────────────────── helpers ─────────────────────────

    private async Task<List<(string Id, MovieDocument Movie)>> ResolveMovies(
        string? movieId, string? title, CancellationToken cancellationToken)
    {
        var found = new List<(string Id, MovieDocument Movie)>();

        if (!string.IsNullOrWhiteSpace(movieId))
        {
            var id = movieId.Trim();
            if (id.StartsWith("movie:", StringComparison.Ordinal))
                id = id["movie:".Length..];
            var documents = await ReadDocuments<MovieDocument>(new[] { $"movie:{id}" }, cancellationToken);
            if (documents.TryGetValue($"movie:{id}", out var movie) && IsAllowed(movie.AgeRating))
                found.Add((id, movie));
            return found;
        }

        if (string.IsNullOrWhiteSpace(title))
            return found;

        var keys = new HashSet<string>(await store.MovieKeysAsync(cancellationToken), StringComparer.Ordinal);

        var movies = await ReadDocuments<MovieDocument>(keys, cancellationToken);
        return movies
            .Where(pair => MatchesMovie(pair.Value, title, null) && IsAllowed(pair.Value.AgeRating))
            .OrderBy(pair => pair.Value.Title, PolishOrder)
            .Take(5)
            .Select(pair => (pair.Key["movie:".Length..], pair.Value))
            .ToList();
    }

    private static JsonElement? MovieLookupProblem(List<(string Id, MovieDocument Movie)> found)
    {
        if (found.Count == 0)
            return Result(new { error = "No such movie (or not allowed for this user). Check id or title with get_screenings or search_movies." });
        if (found.Count > 1)
            return Result(new
            {
                note = "Several movies match. Ask the user which one, or call again with movieId.",
                matches = found.Select(item => new { item.Id, item.Movie.Title, item.Movie.Year }).ToList()
            });
        return null;
    }

    private async Task<List<ScheduleKey>> ListSchedules(CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var schedules = new List<ScheduleKey>();
        foreach (var key in await store.ScheduleKeysAsync(cancellationToken))
        {
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
        var raw = await store.ReadJsonAsync(keys.Distinct(StringComparer.Ordinal).ToList(), cancellationToken);
        foreach (var (key, json) in raw)
            if (JsonSerializer.Deserialize<T>(json, JsonOptions) is { } document)
                documents.Add(key, document);
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

    private static JsonElement CinemaNotFound(string query, IEnumerable<ScheduleKey> schedules) =>
        Result(new
        {
            total = 0,
            note = $"No cinema matches '{query}'. Tell the user politely and offer available cinemas.",
            availableCinemas = GetCinemaEntries(schedules).Take(30).Select(cinema => cinema.Name).ToList()
        });

    private static JsonElement CityNotFound(string city, IEnumerable<ScheduleKey> schedules) =>
        Result(new
        {
            total = 0,
            note = $"No repertory data for city '{city}'. Tell the user politely and offer a city from availableCities, " +
                   "or retry with city='all' if the user agrees to search elsewhere.",
            availableCities = schedules.Select(s => s.Cinema.City)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(40).ToList()
        });

    private static bool Matches(string? text, string? query) =>
        string.IsNullOrWhiteSpace(query) || (text is not null &&
            SearchText(text).Contains(SearchText(query.Trim()), StringComparison.OrdinalIgnoreCase));

    private static string SearchText(string text) =>
        new string(text.Replace('-', ' ').Replace('ł', 'l').Replace('Ł', 'L')
            .Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .ToArray());

    private static bool MatchesMovie(MovieDocument movie, string? title, string? genre)
    {
        if (!Matches(movie.Title, title))
            return false;

        var requestedGenres = ParseGenres(genre);
        return requestedGenres.Length == 0 ||
            (movie.Genres?.Any(movieGenre =>
                requestedGenres.Any(requested => Matches(movieGenre, requested))) ?? false);
    }

    private static string[] ParseGenres(string? genres) =>
        string.IsNullOrWhiteSpace(genres)
            ? []
            : genres.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static bool MatchesScreeningFormat(
    string? screeningFormat,
    string? room,
    string? requestedFormat)
    {
        if (string.IsNullOrWhiteSpace(requestedFormat))
            return true;

        var tokens = Regex.Split(requestedFormat.Trim(), @"[\s,;+/]+")
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (tokens.Length == 0)
            return false;

        // IMAX bywa zapisany w nazwie sali, np. "Sala 1 IMAX".
        var available = $"{screeningFormat} {room}";

        return tokens.All(token => Regex.IsMatch(
            available,
            @"(?<![\p{L}\p{N}])" + Regex.Escape(token) + @"(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

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
            CultureInfo.InvariantCulture, out var minimumAge) && user.Age >= minimumAge;
    }

    private static DateOnly? ParseDate(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null
            : DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date : null;

    private static TimeOnly? ParseTime(string? value) =>
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
