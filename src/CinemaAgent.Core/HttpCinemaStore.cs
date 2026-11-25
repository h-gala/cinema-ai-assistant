using System.Net.Http.Json;

namespace CinemaAgent.Storage;

public sealed class HttpCinemaStore : ICinemaStore
{
    private const int ChunkSize = 500;
    private readonly HttpClient http;

    public HttpCinemaStore(HttpClient http) => this.http = http;

    public async Task<IReadOnlyList<string>> ScheduleKeysAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<string[]>("schedule-keys", cancellationToken) ?? [];

    public async Task<IReadOnlyList<string>> MovieKeysAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<string[]>("movie-keys", cancellationToken) ?? [];

    public async Task<IReadOnlyDictionary<string, string>> ReadJsonAsync(
        IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in keys.Chunk(ChunkSize))
        {
            using var response = await http.PostAsJsonAsync("documents", new { Keys = chunk }, cancellationToken);
            response.EnsureSuccessStatusCode();
            var part = await response.Content
                .ReadFromJsonAsync<Dictionary<string, string>>(cancellationToken);
            if (part is null) continue;
            foreach (var (key, json) in part) documents[key] = json;
        }
        return documents;
    }
}