using System.Runtime.CompilerServices;
using StackExchange.Redis;

namespace CinemaAgent.Storage;

/// <summary>Dane z Redis JSON (zapisane przez kinowo_scraper.py --redis).</summary>
public sealed class RedisCinemaStore : ICinemaStore
{
    private const string SchedulePattern = "repertoire:*:*:*:????-??-??";
    private const string MoviePattern = "movie:*";
    private const int BatchSize = 128;

    private readonly IDatabase database;
    private readonly IServer server;

    // Dla lokalnego, pojedynczego serwera Redis. Współdziel multiplexer między instancjami.
    public RedisCinemaStore(IConnectionMultiplexer redis, int databaseNumber = 0)
    {
        database = redis.GetDatabase(databaseNumber);
        server = redis.GetServer(redis.GetEndPoints().Single());
    }

    public async Task<IReadOnlyList<string>> ScheduleKeysAsync(CancellationToken cancellationToken = default) =>
        await CollectKeys(SchedulePattern, cancellationToken);

    public async Task<IReadOnlyList<string>> MovieKeysAsync(CancellationToken cancellationToken = default) =>
        await CollectKeys(MoviePattern, cancellationToken);

    private async Task<IReadOnlyList<string>> CollectKeys(string pattern, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        await foreach (var key in server.KeysAsync(database.Database, pattern).WithCancellation(cancellationToken))
            keys.Add(key.ToString());
        return keys;
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadJsonAsync(
        IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var batch in keys.Chunk(BatchSize))
        {
            var arguments = batch.Cast<object>().Append(".").ToArray();
            var result = await database.ExecuteAsync("JSON.MGET", arguments).WaitAsync(cancellationToken);
            var values = (RedisResult[])result!;
            for (var i = 0; i < batch.Length; i++)
            {
                // Klucz może wygasnąć między SCAN a odczytem.
                if (!values[i].IsNull && (string?)values[i] is { } json)
                    documents[batch[i]] = json;
            }
        }
        return documents;
    }
}