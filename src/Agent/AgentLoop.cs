using Microsoft.Extensions.AI;

namespace CinemaAgent.Agent;

/// <summary>
/// Ręczna pętla Tool Calling. Model PROPONUJE wywołanie narzędzia (FunctionCallContent),
/// a aplikacja je waliduje, wykonuje i oddaje wynik (FunctionResultContent).
/// </summary>
public sealed class AgentLoop(IChatClient chat, IReadOnlyList<AIFunction> tools, AgentOptions opt)
{
    readonly Dictionary<string, AIFunction> _byName = tools.ToDictionary(t => t.Name);

    public async Task<string> RunTurnAsync(List<ChatMessage> history, string userMessage)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(opt.TimeoutSeconds));
        history.Add(new ChatMessage(ChatRole.User, userMessage));

        var options = new ChatOptions
        {
            Tools = [.. tools],
            ToolMode = ChatToolMode.Auto,
            Temperature = opt.Temperature,
            Seed = opt.Seed
        };

        for (var i = 1; i <= opt.MaxIterations; i++)
        {
            var response = await chat.GetResponseAsync(history, options, cts.Token);
            history.AddRange(response.Messages);

            var calls = response.Messages.SelectMany(m => m.Contents)
                                         .OfType<FunctionCallContent>().ToList();
            if (calls.Count == 0)
                return response.Text;

            var results = new List<AIContent>();
            foreach (var call in calls)
                results.Add(new FunctionResultContent(call.CallId, await ExecuteAsync(call, cts.Token)));

            history.Add(new ChatMessage(ChatRole.Tool, results));
        }
        return $"Przerwano: przekroczono limit {opt.MaxIterations} iteracji agenta.";
    }

    void Log(string text, ConsoleColor color)
{
    if (!opt.Debug) return;
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ResetColor();
}

    async Task<object?> ExecuteAsync(FunctionCallContent call, CancellationToken ct)
    {
        var args = call.Arguments is null ? "" :
            string.Join(", ", call.Arguments.Select(a => $"{a.Key}={a.Value}"));
        Log($"  [tool] {call.Name}({args})", ConsoleColor.DarkGray);

        // Walidacja: tylko narzędzia z białej listy
        if (!_byName.TryGetValue(call.Name, out var fn))
        {
            Log($"  [tool error] unknown tool '{call.Name}'", ConsoleColor.Red);
            return $"Unknown tool '{call.Name}'. Available: {string.Join(", ", _byName.Keys)}";
        }

        try
        {
            var result = await fn.InvokeAsync(new AIFunctionArguments(call.Arguments), ct);
            var json = System.Text.Json.JsonSerializer.Serialize(result);
            Log($"  [result] {(json.Length > 500 ? json[..500] + "…" : json)}", ConsoleColor.DarkGray);
            return result;
        }
        catch (ArgumentException ex)
        {
            // Błędne argumenty (np. zły format daty): model może je poprawić
            Log($"  [tool error] {ex.Message}", ConsoleColor.Red);
            return $"Invalid arguments: {ex.Message}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Błąd wewnętrzny: pełne szczegóły tylko w debugu, model dostaje neutralny komunikat
            Log($"  [tool error] {ex}", ConsoleColor.Red);
            return "Internal error: data is temporarily unavailable. Tell the user you cannot check it right now.";
        }
    }
}
