using System.ComponentModel;
using System.Text.RegularExpressions;
using CinemaAgent.Models;
using CinemaAgent.Services;
using Microsoft.Extensions.AI;

namespace CinemaAgent.Tools;

/// <summary>
/// Narzędzia udostępnione modelowi. Każda metoda to jedno narzędzie.
/// Błędne argumenty kończą się wyjątkiem - pętla agenta zwraca go modelowi jako błąd narzędzia.
/// </summary>
public sealed class CinemaTools(CinemaRepository repo, UserProfile user, DateTimeOffset now)
{
    object CinemaNotFound(string query) => new
    {
        total = 0,
        note = $"No cinema matches '{query}'. This is not an error: tell the user politely that you " +
            "did not find such a cinema and offer the available ones.",
        availableCinemas = repo.Cinemas.Select(c => c.Name).ToList()
    };
    public IReadOnlyList<AIFunction> CreateAll() =>
        new[] { nameof(FindCinemas), nameof(SearchMovies), nameof(GetScreenings),
                nameof(GetUserProfile) }
        .Select(n => AIFunctionFactory.Create(
            typeof(CinemaTools).GetMethod(n)!, this,
            new AIFunctionFactoryOptions { Name = Regex.Replace(n, "(?<!^)([A-Z])", "_$1").ToLowerInvariant() }))
        .ToList();

    [Description("Find cinemas by free text (name, city or network), e.g. 'Złote Tarasy', 'Warszawa' or 'Multikino'. Empty query lists all.")]
    public object FindCinemas([Description("Free text query")] string? query = null)
    {
        var found = repo.FindCinemas(query).ToList();
        return new
        {
            total = found.Count,
            cinemas = found.Take(30).Select(c => new
            {
                c.Id,
                c.Name,
                c.City,
                Network = repo.NetworkName(c.NetworkId),
                Address = FormatAddress(c.Address)
            }).ToList()
        };
    }

    static string? FormatAddress(Address? a)
    {
        if (a is null) return null;
        var parts = new[] { a.Street, a.PostalCode }.Where(p => !string.IsNullOrWhiteSpace(p));
        var text = string.Join(", ", parts);
        return text.Length == 0 ? null : text;
    }

    [Description("Search movies (title, genre) and return their details: duration, genres, age rating, description.")]
    public object SearchMovies(
        [Description("Part of the title (Polish or original)")] string? title = null,
        [Description("Genre, e.g. Horror, Komedia, Sci-Fi, Animacja")] string? genre = null) =>
        repo.FindMovies(title, genre).Select(m => new
        { m.Id, m.Title, m.Year, m.DurationMinutes, m.Genres, m.AgeRating, m.Description }).ToList();

    [Description("List upcoming screenings (starting from the current time), sorted chronologically. " +
                 "Use it for 'the nearest movie in cinema X', 'a horror on Friday evening', 'when does movie Y play'. " +
                 "If only dateFrom is given, it means a single day.")]
    public object GetScreenings(
        [Description("Cinema name fragment, e.g. 'Złote Tarasy' or 'Cinema City Arkadia'")] string? cinema = null,
        [Description("Movie title fragment")] string? title = null,
        [Description("Genre, e.g. Horror")] string? genre = null,
        [Description("Date yyyy-MM-dd")] string? dateFrom = null,
        [Description("Date yyyy-MM-dd (inclusive); defaults to dateFrom")] string? dateTo = null,
        [Description("Earliest start time HH:mm; evening = 17:00")] string? timeFrom = null,
        [Description("Latest start time HH:mm")] string? timeTo = null,
        [Description("Max results (1-20)")] int limit = 10)
    {
        var filter = BuildFilter(cinema, title, genre, dateFrom, dateTo, timeFrom, timeTo);
        if (!string.IsNullOrWhiteSpace(cinema) && !repo.FindCinemas(cinema).Any())
    return CinemaNotFound(cinema);
        var all = repo.QueryScreenings(filter, now).ToList();
        return new
        {
            total = all.Count,
            note = all.Count == 0 ? "No upcoming screenings match these filters." : "Sorted chronologically; first item is the nearest.",
            screenings = all.Take(Math.Clamp(limit, 1, 20)).ToList()
        };
    }

    [Description("Returns the profile of the current user: age, favourite and disliked genres, preferences.")]
    public UserProfile GetUserProfile() => user;

    // ---- logika pomocnicza ----

    static ScreeningFilter BuildFilter(string? cinema, string? title, string? genre,
        string? dateFrom, string? dateTo, string? timeFrom, string? timeTo)
    {
        DateOnly? d1 = ParseDate(dateFrom, nameof(dateFrom));
        DateOnly? d2 = ParseDate(dateTo, nameof(dateTo)) ?? d1;
        return new ScreeningFilter(cinema, title, genre, d1, d2,
            ParseTime(timeFrom, nameof(timeFrom)), ParseTime(timeTo, nameof(timeTo)));
    }

    static DateOnly? ParseDate(string? s, string name) =>
        string.IsNullOrWhiteSpace(s) ? null
        : DateOnly.TryParseExact(s, "yyyy-MM-dd", out var d) ? d
        : throw new ArgumentException($"{name} must be in yyyy-MM-dd format, got '{s}'");

    static TimeOnly? ParseTime(string? s, string name) =>
        string.IsNullOrWhiteSpace(s) ? null
        : TimeOnly.TryParseExact(s, "HH:mm", out var t) ? t
        : throw new ArgumentException($"{name} must be in HH:mm format, got '{s}'");
}
