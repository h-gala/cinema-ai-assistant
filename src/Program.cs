using CinemaAgent.Agent;
using CinemaAgent.Services;
using CinemaAgent.Tools;
using CinemaAgent.Users;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OllamaSharp;

// Użycie: dotnet run -- --user anna [--ask "Znajdź horror na piątek wieczór"]
string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

var cfg = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json")
    .AddEnvironmentVariables()
    .Build();

var ollama = cfg.GetSection("Ollama").Get<OllamaOptions>() ?? new();
var agentOpt = cfg.GetSection("Agent").Get<AgentOptions>() ?? new();
var dataDir = cfg["DataDirectory"] ?? "data";

var userId = Arg("--user") ?? "anna";
if (!UserProfiles.All.TryGetValue(userId, out var user))
{
    Console.Error.WriteLine($"Nieznany użytkownik '{userId}'. Dostępni: {string.Join(", ", UserProfiles.All.Keys)}");
    return 1;
}

var now = agentOpt.Now is not null ? DateTimeOffset.Parse(agentOpt.Now) : DateTimeOffset.Now;
var repo = new CinemaRepository(dataDir);
Console.WriteLine($"Dane: {repo.Cinemas.Count} kin, {repo.Movies.Count} filmów, {repo.Screenings.Count} seansów");
var tools = new CinemaTools(repo, user, now).CreateAll();

// Zmiana dostawcy modelu = podmiana implementacji IChatClient
IChatClient chat = new OllamaApiClient(new Uri(ollama.Endpoint), ollama.Model);
var agent = new AgentLoop(chat, tools, agentOpt);

var history = new List<ChatMessage> { new(ChatRole.System, SystemPrompt.Build(user, now)) };

Console.WriteLine($"Użytkownik: {user.Name} | model: {ollama.Model} | teraz: {now:yyyy-MM-dd HH:mm}");

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
