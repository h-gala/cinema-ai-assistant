namespace CinemaAgent.Models;

// Modele danych testowych (JSON w snake_case)
public record Address(string? Street, string? PostalCode, string? Country);
public record Cinema(string Id, string? NetworkId, string Name, string? City, Address? Address);
public record Movie(string Id, string Title, string? OriginalTitle, int Year, int DurationMinutes,
                    List<string> Genres, string AgeRating, string? Description);
public record Network(string Id, string Name, string Slug);
public record Screening(string Id, string CinemaId, string MovieId, DateTimeOffset Datetime,
                        string Auditorium, string Language, string Version, string Format, bool IsPremiere);
public record Catalog(string Id, string Week, DateOnly DateFrom, DateOnly DateTo, string Timezone,
                      string Status, List<Screening> Screenings);

// Pliki-wrappery
public record CinemasFile(List<Cinema> Cinemas);
public record MoviesFile(List<Movie> Movies);
public record NetworksFile(List<Network> Networks);

// Widok seansu zwracany modelowi (płaski, z nazwami zamiast samych ID)
public record ScreeningView(
    string ScreeningId, string Cinema, string CinemaId, string Title, string MovieId,
    List<string> Genres, int DurationMinutes, string AgeRating,
    string Start, string Day, string Language, string Version, string Format, bool IsPremiere);

public record UserProfile(
    string Id, string Name, int Age,
    string[] FavoriteGenres, string[] DislikedGenres,
    string? PreferredNetworkId, string? PreferredPartOfDay,
    string Description);
