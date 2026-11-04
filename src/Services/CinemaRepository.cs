using System.Text.Json;
using CinemaAgent.Models;

namespace CinemaAgent.Services;

public record ScreeningFilter(
    string? Cinema = null, string? Title = null, string? Genre = null,
    DateOnly? DateFrom = null, DateOnly? DateTo = null,
    TimeOnly? TimeFrom = null, TimeOnly? TimeTo = null);

/// <summary>Wczytuje i waliduje dane testowe (cinemas/movies/networks/catalog*.json).</summary>
public sealed class CinemaRepository
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public IReadOnlyList<Cinema> Cinemas { get; }
    public IReadOnlyList<Movie> Movies { get; }
    public IReadOnlyList<Network> Networks { get; }
    public IReadOnlyList<Screening> Screenings { get; }

    readonly Dictionary<string, Cinema> _cinemas;
    readonly Dictionary<string, Movie> _movies;
    readonly Dictionary<string, Network> _networks;

    public CinemaRepository(string dir)
    {
        T Read<T>(string path) =>
            JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"Pusty plik: {path}");

        Cinemas = Read<CinemasFile>(Path.Combine(dir, "cinemas.json")).Cinemas;
        Movies = Read<MoviesFile>(Path.Combine(dir, "movies.json")).Movies;
        Networks = Read<NetworksFile>(Path.Combine(dir, "networks.json")).Networks;
        var weeklyDir = Path.Combine(dir, "weekly-screenings");
        if (!Directory.Exists(weeklyDir))
            throw new DirectoryNotFoundException($"Brak katalogu z seansami: {Path.GetFullPath(weeklyDir)}");

        Screenings = Directory.GetFiles(weeklyDir, "*.json")
            .SelectMany(f => Read<Catalog>(f).Screenings)
            .ToList();

        if (Screenings.Count == 0)
            throw new InvalidDataException($"Nie znaleziono żadnych seansów w: {Path.GetFullPath(weeklyDir)}");

        _cinemas = Cinemas.ToDictionary(c => c.Id);
        _movies = Movies.ToDictionary(m => m.Id);
        _networks = Networks.ToDictionary(n => n.Id);

        // Walidacja referencji
        var bad = Screenings.Where(s => !_cinemas.ContainsKey(s.CinemaId) || !_movies.ContainsKey(s.MovieId))
                            .Select(s => s.Id).ToList();
        if (bad.Count > 0)
            throw new InvalidDataException($"Seanse z nieistniejącym kinem/filmem: {string.Join(", ", bad)}");
    }

    public string NetworkName(string? id) =>
    id is not null && _networks.TryGetValue(id, out var n) ? n.Name : id ?? "";

    public IEnumerable<Cinema> FindCinemas(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? Cinemas
            : Cinemas.Where(c => TextUtil.ContainsAllTokens(
                $"{c.Name} {c.City} {c.Id} {NetworkName(c.NetworkId)}", query));

    public IEnumerable<Movie> FindMovies(string? title, string? genre) =>
        Movies.Where(m =>
            (string.IsNullOrWhiteSpace(title) ||
                TextUtil.ContainsAllTokens($"{m.Title} {m.OriginalTitle}", title)) &&
            (string.IsNullOrWhiteSpace(genre) ||
                m.Genres.Any(g => TextUtil.Norm(g).Contains(TextUtil.Norm(genre)))));

    /// <summary>Seanse od chwili "now" spełniające filtr, posortowane chronologicznie.</summary>
    public IEnumerable<ScreeningView> QueryScreenings(ScreeningFilter f, DateTimeOffset now)
    {
        var cinemaIds = f.Cinema is null ? null : FindCinemas(f.Cinema).Select(c => c.Id).ToHashSet();
        var movieIds = (f.Title is null && f.Genre is null)
            ? null : FindMovies(f.Title, f.Genre).Select(m => m.Id).ToHashSet();

        return Screenings
            .Where(s => s.Datetime >= now)
            .Where(s => cinemaIds is null || cinemaIds.Contains(s.CinemaId))
            .Where(s => movieIds is null || movieIds.Contains(s.MovieId))
            .Where(s => f.DateFrom is null || DateOnly.FromDateTime(s.Datetime.DateTime) >= f.DateFrom)
            .Where(s => f.DateTo is null || DateOnly.FromDateTime(s.Datetime.DateTime) <= f.DateTo)
            .Where(s => f.TimeFrom is null || TimeOnly.FromDateTime(s.Datetime.DateTime) >= f.TimeFrom)
            .Where(s => f.TimeTo is null || TimeOnly.FromDateTime(s.Datetime.DateTime) <= f.TimeTo)
            .OrderBy(s => s.Datetime)
            .Select(ToView);
    }

    ScreeningView ToView(Screening s)
    {
        var c = _cinemas[s.CinemaId];
        var m = _movies[s.MovieId];
        return new ScreeningView(
            s.Id, c.Name, c.Id, m.Title, m.Id, m.Genres, m.DurationMinutes, m.AgeRating,
            s.Datetime.ToString("yyyy-MM-dd HH:mm"), TextUtil.PlDays[(int)s.Datetime.DayOfWeek],
            s.Language, s.Version, s.Format, s.IsPremiere);
    }
}
