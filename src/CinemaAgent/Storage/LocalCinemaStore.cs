using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CinemaAgent.Storage;

/// <summary>
/// Dane z lokalnych plików JSON, bez Redisa. Czyta wyjście kinowo_scraper.py:
///   python kinowo_scraper.py --city warszawa --output-dir data/kinowo
/// Ścieżka to katalog z plikami {slug-miasta}.json albo pojedynczy plik (nazwa pliku = slug miasta).
/// Przy wczytaniu odtwarza w pamięci dokładnie te klucze i dokumenty, które scraper zapisuje do Redisa
/// (port build_redis_documents), więc narzędzia agenta działają bez zmian.
/// </summary>
public sealed class LocalCinemaStore : ICinemaStore
{
    private static readonly string[] KnownChains = ["Multikino", "Cinema City", "Helios"];

    private readonly Dictionary<string, string> movies;
    private readonly Dictionary<string, string> schedules;

    public string Summary { get; }

    private LocalCinemaStore(Dictionary<string, string> movies, Dictionary<string, string> schedules,
        string summary)
    {
        this.movies = movies;
        this.schedules = schedules;
        Summary = summary;
    }

    public Task<IReadOnlyList<string>> ScheduleKeysAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(schedules.Keys.ToList());

    public Task<IReadOnlyList<string>> MovieKeysAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(movies.Keys.ToList());

    public Task<IReadOnlyDictionary<string, string>> ReadJsonAsync(
        IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (movies.TryGetValue(key, out var movie)) result[key] = movie;
            else if (schedules.TryGetValue(key, out var schedule)) result[key] = schedule;
        }
        return Task.FromResult<IReadOnlyDictionary<string, string>>(result);
    }

    public static LocalCinemaStore Load(string path)
    {
        string[] files;
        if (Directory.Exists(path))
            files = Directory.GetFiles(path, "*.json").Order(StringComparer.Ordinal).ToArray();
        else if (File.Exists(path))
            files = new[] { path };
        else
            throw new DirectoryNotFoundException($"Brak katalogu lub pliku z danymi: {Path.GetFullPath(path)}");

        if (files.Length == 0)
            throw new InvalidDataException($"Brak plików *.json w: {Path.GetFullPath(path)}");

        var movies = new Dictionary<string, string>(StringComparer.Ordinal);
        var grouped = new Dictionary<string, List<(string Time, string MovieId, JsonObject Doc)>>(StringComparer.Ordinal);
        var cities = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;

        foreach (var file in files)
        {
            var city = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonArray movieArray)
                throw new InvalidDataException($"{file}: oczekiwano listy filmów w formacie kinowo_scraper.py.");
            cities.Add(city);

            foreach (var item in movieArray)
            {
                if (item is not JsonObject movie || Str(movie["slug"]) is not { Length: > 0 } slug)
                    continue;

                var showtimes = movie["showtimes"] as JsonArray;
                movie.Remove("showtimes");
                movie.Remove("url");
                movies.TryAdd($"movie:{slug}", movie.ToJsonString()); // jak NX w scraperze: pierwszy wygrywa

                if (showtimes is null) continue;
                foreach (var entry in showtimes)
                {
                    if (entry is not JsonObject showtime ||
                        !DateOnly.TryParseExact(Str(showtime["date"]), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var day) ||
                        Str(showtime["time"]) is not { Length: > 0 } time ||
                        Str(showtime["cinema"]) is not { Length: > 0 } cinema)
                    {
                        skipped++;
                        continue;
                    }

                    // Nazwa kina: kodujemy tylko separator klucza i znak ucieczki (jak w scraperze).
                    var cinemaKey = cinema.Replace("%", "%25").Replace(":", "%3A");
                    var key = $"{city}:{Chain(cinema)}:{cinemaKey}:{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
                    if (!grouped.TryGetValue(key, out var list))
                        grouped[key] = list = new();

                    list.Add((time, slug, new JsonObject
                    {
                        ["movie_id"] = slug,
                        ["time"] = time,
                        ["room"] = Str(showtime["room"]),
                        ["format"] = Str(showtime["format"]),
                        ["ticket_url"] = Str(showtime["ticket_url"])
                    }));
                }
            }
        }

        if (grouped.Count == 0)
            throw new InvalidDataException("W plikach nie znaleziono żadnych seansów.");

        var schedules = new Dictionary<string, string>(StringComparer.Ordinal);
        var screeningCount = 0;
        foreach (var (key, list) in grouped)
        {
            var ordered = list.OrderBy(x => x.Time, StringComparer.Ordinal)
                              .ThenBy(x => x.MovieId, StringComparer.Ordinal)
                              .Select(x => (JsonNode?)x.Doc)
                              .ToArray();
            screeningCount += ordered.Length;
            schedules[key] = new JsonArray(ordered).ToJsonString();
        }

        var days = schedules.Keys.Select(k => k[(k.LastIndexOf(':') + 1)..]).Order(StringComparer.Ordinal).ToList();
        var summary = $"Dane lokalne: {movies.Count} filmów, {screeningCount} seansów, miasta: {cities.Count}, " +
                      $"daty {days[0]} … {days[^1]}" +
                      (skipped > 0 ? $", pominięto niepełnych seansów: {skipped}" : "") + ".";
        if (string.CompareOrdinal(days[^1], DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) < 0)
            summary += " UWAGA: dane są nieaktualne. Ustaw sztuczny czas, np. Agent__Now=" + days[0] + "T18:00";

        return new LocalCinemaStore(movies, schedules, summary);
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // Port cinema_chain() ze scrapera.
    private static string Chain(string cinema)
    {
        foreach (var chain in KnownChains)
            if (cinema.Equals(chain, StringComparison.OrdinalIgnoreCase) ||
                cinema.StartsWith(chain + " ", StringComparison.OrdinalIgnoreCase))
                return chain;
        return "inne";
    }
}
