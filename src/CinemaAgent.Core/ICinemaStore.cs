namespace CinemaAgent.Storage;

/// <summary>
/// Źródło danych dla narzędzi agenta. Dwie implementacje: Redis (RedisCinemaStore)
/// oraz lokalne pliki JSON ze scrapera (LocalCinemaStore).
/// Klucze mają ten sam format w obu: "movie:{slug}" oraz "{miasto}:{sieć}:{kino}:{yyyy-MM-dd}".
/// </summary>
public interface ICinemaStore
{
    /// <summary>Klucze repertuaru: "{miasto}:{sieć}:{kino}:{yyyy-MM-dd}".</summary>
    Task<IReadOnlyList<string>> ScheduleKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>Klucze filmów: "movie:{slug}".</summary>
    Task<IReadOnlyList<string>> MovieKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>Surowy JSON dokumentów. Brakujące (np. wygasłe) klucze są po prostu pomijane.</summary>
    Task<IReadOnlyDictionary<string, string>> ReadJsonAsync(
        IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default);
}