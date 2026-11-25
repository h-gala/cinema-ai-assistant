using System.Globalization;
using System.Text.Json;
using CinemaAgent.Agent;
using CinemaAgent.Storage;
using CinemaAgent.Tools;
using CinemaAgent.Users;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OllamaSharp;
using StackExchange.Redis;
using OllamaChatRequest = OllamaSharp.Models.Chat.ChatRequest;
using OllamaRequestOptions = OllamaSharp.Models.RequestOptions;

// Użycie: dotnet run -- [--model qwen3:8b] [--user anna] [--data data/kinowo] [--ask "Pytanie"]
var arguments = new Dictionary<string, string>();
for (var i = 0; i < args.Length; i++)
{
    var option = args[i];
    if (option is not ("--model" or "--user" or "--data" or "--ask" or "--api-url"))
        continue;

    if (option == "--model" && (i + 1 == args.Length || string.IsNullOrWhiteSpace(args[i + 1])
                                || args[i + 1].StartsWith("--", StringComparison.Ordinal)))
    {
        Console.Error.WriteLine("Podaj nazwę modelu po --model, np. --model qwen3:8b.");
        return 1;
    }

    if (i + 1 < args.Length)
        arguments.TryAdd(option, args[++i]);
}

var useApi = args.Contains("--api");

// Sztuczny czas z konfiguracji: wartość bez strefy traktujemy jako czas warszawski.
static DateTimeOffset ParseNow(string value, TimeZoneInfo zone)
{
    var parsed = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    return parsed.Kind == DateTimeKind.Unspecified
        ? new DateTimeOffset(parsed, zone.GetUtcOffset(parsed))
        : new DateTimeOffset(parsed);
}

var cfg = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json")
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var ollama = cfg.GetSection("Ollama").Get<OllamaOptions>() ?? new();
if (arguments.TryGetValue("--model", out var model))
    ollama.Model = model.Trim();

var agentOpt = cfg.GetSection("Agent").Get<AgentOptions>() ?? new();
var redisConnection = cfg["Redis:ConnectionString"] ?? "127.0.0.1:6379";
var numCtx = int.TryParse(cfg["Ollama:NumCtx"], out var configuredCtx) ? configuredCtx : 16384;

var userId = arguments.GetValueOrDefault("--user") ?? "anna";
if (!UserProfiles.All.TryGetValue(userId, out var user))
{
    Console.Error.WriteLine($"Nieznany użytkownik '{userId}'. Dostępni: {string.Join(", ", UserProfiles.All.Keys)}");
    return 1;
}

// Ollama nie zgłasza błędu o brakującym modelu aż do pierwszego zapytania.
// Sprawdź lokalny katalog /api/tags przed uruchomieniem pętli agenta.
using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
{
    try
    {
        var tagsUri = new Uri($"{ollama.Endpoint.TrimEnd('/')}/api/tags");
        using var response = await http.GetAsync(tagsUri);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var models = json.RootElement.GetProperty("models").EnumerateArray()
            .Select(m => m.GetProperty("name").GetString())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        static string NormalizeModelName(string name) =>
            name.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? name[..^7] : name;

        if (!models.Any(name => string.Equals(
                NormalizeModelName(name!), NormalizeModelName(ollama.Model), StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"Brak modelu Ollama '{ollama.Model}'. Pobierz go poleceniem: ollama pull {ollama.Model}");
            return 1;
        }
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
    {
        Console.Error.WriteLine($"Nie można sprawdzić modelu Ollama pod adresem '{ollama.Endpoint}': {ex.Message}");
        Console.Error.WriteLine("Upewnij się, że Ollama działa i udostępnia endpoint /api/tags.");
        return 1;
    }
}

// Cały agent (prompt i narzędzia) pracuje w czasie warszawskim, niezależnie od strefy maszyny.
var warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
var now = TimeZoneInfo.ConvertTime(
    agentOpt.Now is { } fixedNow ? ParseNow(fixedNow, warsaw) : DateTimeOffset.UtcNow, warsaw);

// Źródło danych: lokalne pliki JSON ze scrapera (--data <ścieżka> albo Data:LocalPath) lub Redis.
var localData = arguments.GetValueOrDefault("--data") ?? cfg["Data:LocalPath"];
ConnectionMultiplexer? redis = null;
HttpClient? apiHttp = null;
ICinemaStore store;
if (!string.IsNullOrWhiteSpace(localData))
{
    try
    {
        var local = LocalCinemaStore.Load(localData);
        Console.WriteLine(local.Summary);
        store = local;
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
    {
        Console.Error.WriteLine($"Nie można wczytać danych lokalnych ({localData}): {ex.Message}");
        return 1;
    }
}
else if (useApi)
{
    var baseUrl = arguments.GetValueOrDefault("--api-url") ?? cfg["CinemaApi:BaseUrl"];
    var apiKey = cfg["CinemaApi:Key"];
    if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
    {
        Console.Error.WriteLine("Brak konfiguracji API. Ustaw CinemaApi:BaseUrl i CinemaApi:Key " +
                                "(appsettings.json lub zmienne CinemaApi__BaseUrl / CinemaApi__Key).");
        return 1;
    }

    try
    {
        // Końcowy "/" jest ważny: bez niego ścieżki względne ("schedule-keys") gubią segment bazowy.
        apiHttp = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        apiHttp.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        using var health = await apiHttp.GetAsync("health");
        health.EnsureSuccessStatusCode();
        store = new HttpCinemaStore(apiHttp);
        Console.WriteLine($"Źródło danych: API ({apiHttp.BaseAddress})");
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
    {
        Console.Error.WriteLine($"Nie można połączyć się z API ({baseUrl}): {ex.Message}");
        apiHttp?.Dispose();
        return 1;
    }
}
else
{
    try
    {
        redis = await ConnectionMultiplexer.ConnectAsync(redisConnection);
        store = new RedisCinemaStore(redis);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Nie można połączyć się z Redisem ({redisConnection}): {ex.Message}");
        return 1;
    }
}

using (redis)
{
    using var _ = apiHttp;
    var cinemaTools = new CinemaTools(
        store,
        user,
        agentOpt.EnforceAgeRating,
        // Gdy w konfiguracji ustawiono sztuczny czas, narzędzia muszą używać tego samego zegara co prompt.
        utcNow: agentOpt.Now is not null ? () => now.ToUniversalTime() : null);

    var tools = cinemaTools.CreateAll()
        .Concat(new SkillTools().CreateAll())
        .ToList();

    // Zmiana dostawcy modelu = podmiana implementacji IChatClient.
    // NumCtxClient dopisuje num_ctx do każdego żądania, więc Ollama nie wraca do domyślnych 4096.
    IChatClient chat = new NumCtxClient(new OllamaApiClient(new Uri(ollama.Endpoint), ollama.Model), numCtx);

    // Opcjonalne przycinanie historii (ok. połowa okna kontekstu). Aby wyłączyć: usuń te dwie linie i argument trimmer.
    var trimmer = new HistoryTrimmer(maxChars: numCtx * 3 / 2, log: agentOpt.Debug ? (m => Console.WriteLine(m)) : null);
    var agent = new AgentLoop(chat, tools, agentOpt, trimmer);

    var history = new List<ChatMessage>
    {
        new(ChatRole.System, SystemPrompt.Build(user, now, agentOpt.EnforceAgeRating))
    };

    Console.WriteLine($"Użytkownik: {user.Name} | model: {ollama.Model} | num_ctx: {numCtx} | teraz: {now:yyyy-MM-dd HH:mm}");

    async Task Ask(string q)
    {
        try
        {
            var answer = await agent.RunTurnAsync(history, q);
            if (string.IsNullOrWhiteSpace(answer))
                Console.WriteLine("\n[model zwrócił pustą odpowiedź — sprawdź log Ollamy (n_ctx_slot, truncated)]\n");
            else
                Console.WriteLine($"\n{answer}\n");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\nOdpowiedź trwa zbyt długo. Spróbuj ponownie.\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[błąd] {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null)
                Console.WriteLine($"        {ex.InnerException.Message}");
            Console.WriteLine();
        }
    }

    if (arguments.GetValueOrDefault("--ask") is { } single)
    {
        await Ask(single);
        return 0;
    }

    Console.WriteLine("Zadaj pytanie (pusta linia / 'exit' kończy).");
    while (true)
    {
        Console.Write("> ");
        var line = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(line) || line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
            break;
        await Ask(line);
    }
    return 0;
}

// Nakłada num_ctx na każde żądanie do Ollamy.
sealed class NumCtxClient(IChatClient inner, int numCtx) : DelegatingChatClient(inner)
{
    private ChatOptions Patch(ChatOptions? options)
    {
        options = options?.Clone() ?? new ChatOptions();
        options.RawRepresentationFactory = _ => new OllamaChatRequest
        {
            Options = new OllamaRequestOptions { NumCtx = numCtx }
        };
        return options;
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(messages, Patch(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(messages, Patch(options), cancellationToken);
}
