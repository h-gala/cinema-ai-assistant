using CinemaAgent.Agent;
using CinemaAgent.Services;
using CinemaAgent.Tools;
using CinemaAgent.Users;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OllamaSharp;
using System.Text.Json;
using OpenAI;
using System.ClientModel;
using OpenAI.Chat;

using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

// Użycie: dotnet run -- [--provider openai|ollama] [--model qwen3:4b] --user anna [--ask "Znajdź horror na piątek wieczór"]
string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

var cfg = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json")
    .AddEnvironmentVariables()
    .Build();

var provider = (Arg("--provider") ?? cfg["Provider"] ?? "ollama").ToLowerInvariant();
var ollama = cfg.GetSection("Ollama").Get<OllamaOptions>() ?? new();
var openAi = cfg.GetSection("OpenAI").Get<OpenAiOptions>() ?? new();
var agentOpt = cfg.GetSection("Agent").Get<AgentOptions>() ?? new();
var dataDir = cfg["DataDirectory"] ?? "data";

if (Arg("--model") is { } cliModel)
{
    if (provider == "openai") openAi.Model = cliModel;
    else ollama.Model = cliModel;
}

var userId = Arg("--user") ?? "anna";
if (!UserProfiles.All.TryGetValue(userId, out var user))
{
    Console.Error.WriteLine($"Nieznany użytkownik '{userId}'. Dostępni: {string.Join(", ", UserProfiles.All.Keys)}");
    return 1;
}

IChatClient chat;
string activeModel;

if (provider == "openai")
{
    var apiKey = openAi.ApiKey
        ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
        ?? throw new InvalidOperationException("Brak klucza API. Ustaw zmienną środowiskową OPENAI_API_KEY lub pole OpenAI:ApiKey w appsettings.json.");

    OpenAIClient client = string.IsNullOrEmpty(openAi.Endpoint)
        ? new OpenAIClient(apiKey)
        : new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = new Uri(openAi.Endpoint) });

    chat = client.GetChatClient(openAi.Model).AsIChatClient();
    activeModel = openAi.Model;
}
else
{
    activeModel = ollama.Model;

    // Sprawdzanie lokalnej Ollamy tylko w trybie ollama
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

            if (!models.Any(name => string.Equals(NormalizeModelName(name!), NormalizeModelName(activeModel), StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine($"Brak modelu Ollama '{activeModel}'. Pobierz go poleceniem: ollama pull {activeModel}");
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

    chat = new OllamaApiClient(new Uri(ollama.Endpoint), ollama.Model);
}
// Ollama nie zgłasza błędu o brakującym modelu aż do pierwszego zapytania.
// Sprawdź lokalny katalog /api/tags przed uruchomieniem pętli agenta.

var now = agentOpt.Now is not null ? DateTimeOffset.Parse(agentOpt.Now) : DateTimeOffset.Now;
var repo = new CinemaRepository(dataDir);
Console.WriteLine($"Dane: {repo.Cinemas.Count} kin, {repo.Movies.Count} filmów, {repo.Screenings.Count} seansów");
var tools = new CinemaTools(repo, user, now, agentOpt.EnforceAgeRating).CreateAll();

// Zmiana dostawcy modelu = podmiana implementacji IChatClient
//IChatClient chat = new OllamaApiClient(new Uri(ollama.Endpoint), ollama.Model);
var blockedMovieTitles = agentOpt.EnforceAgeRating
    ? repo.Movies.Where(m => !AgeRatingPolicy.IsAllowed(m.AgeRating, user.Age))
        .SelectMany(m => new[] { m.Title, m.OriginalTitle })
        .Where(title => !string.IsNullOrWhiteSpace(title)).Select(title => title!).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
    : new List<string>();
var agent = new AgentLoop(chat, tools, agentOpt, blockedMovieTitles);

var history = new List<ChatMessage> { new(ChatRole.System, SystemPrompt.Build(user, now, agentOpt.EnforceAgeRating)) };

Console.WriteLine($"Użytkownik: {user.Name} | model: {activeModel} | teraz: {now:yyyy-MM-dd HH:mm}");

async Task Ask(string q)
{
    try { Console.WriteLine($"\n{await agent.RunTurnAsync(history, q)}\n"); }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\nOdpowiedź trwa zbyt długo. Spróbuj ponownie.\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine("\nPrzepraszam, coś poszło nie tak. Spróbuj ponownie za chwilę.\n");
        if (agentOpt.Debug)
        {
            var incomplete = repo.Cinemas.Where(c => c.Address is null).Select(c => c.Id).ToList();
            if (incomplete.Count > 0)
                Console.WriteLine($"Kina bez adresu ({incomplete.Count}): {string.Join(", ", incomplete)}");
        }
    }
}

if (Arg("--ask") is { } single) { await Ask(single); return 0; }

Console.WriteLine("Zadaj pytanie (pusta linia / 'exit' kończy).");
while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(line) || line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
    await Ask(line);
}
return 0;
