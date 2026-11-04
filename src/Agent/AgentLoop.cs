using Microsoft.Extensions.AI;
using System.Text.Json;

namespace CinemaAgent.Agent;

/// <summary>
/// Ręczna pętla Tool Calling. Model PROPONUJE wywołanie narzędzia (FunctionCallContent),
/// a aplikacja je waliduje, wykonuje i oddaje wynik (FunctionResultContent).
/// </summary>
public sealed class AgentLoop(
    IChatClient chat, IReadOnlyList<AIFunction> tools, AgentOptions opt,
    IReadOnlyList<string>? blockedMovieTitles = null)
{
    readonly Dictionary<string, AIFunction> _byName = tools.ToDictionary(t => t.Name);
    readonly IReadOnlyList<string> _blockedMovieTitles = blockedMovieTitles ?? [];

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

            var calls = response.Messages.SelectMany(m => m.Contents)
                                         .OfType<FunctionCallContent>().ToList();
            if (calls.Count == 0)
            {
                // Niektóre modele zwracają "plan wywołania" jako tekst/JSON zamiast
                // FunctionCallContent. Odzyskaj wyłącznie JSON wskazujący znane narzędzie.
                calls = ExtractTextFunctionCalls(response.Text);
                if (calls.Count > 0)
                {
                    Log("  [tool recovery] Wykryto tekstowy zapis wywołania narzędzia.", ConsoleColor.DarkYellow);
                    history.Add(new ChatMessage(ChatRole.Assistant, calls.Select(c => (AIContent)c).ToList()));
                }
                else
                {
                    if (_blockedMovieTitles.Any(title => response.Text.Contains(title, StringComparison.OrdinalIgnoreCase)))
                        return "Nie mogę zaproponować filmu z powodu ograniczeń kategorii wiekowej. Mogę znaleźć inne seanse.";

                    history.AddRange(response.Messages);
                    return response.Text;
                }
            }
            else
                history.AddRange(response.Messages);

            var results = new List<AIContent>();
            foreach (var call in calls)
                results.Add(new FunctionResultContent(call.CallId, await ExecuteAsync(call, cts.Token)));

            history.Add(new ChatMessage(ChatRole.Tool, results));
        }
        return $"Przerwano: przekroczono limit {opt.MaxIterations} iteracji agenta.";
    }

    List<FunctionCallContent> ExtractTextFunctionCalls(string text)
    {
        var calls = new List<FunctionCallContent>();
        for (var start = 0; start < text.Length; start++)
        {
            if (text[start] != '{') continue;

            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var end = start; end < text.Length; end++)
            {
                var ch = text[end];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (ch == '\\') escaped = true;
                    else if (ch == '"') inString = false;
                    continue;
                }

                if (ch == '"') inString = true;
                else if (ch == '{') depth++;
                else if (ch == '}' && --depth == 0)
                {
                    TryAddTextCall(text.AsSpan(start, end - start + 1), calls);
                    start = end;
                    break;
                }
            }
        }
        return calls;
    }

    void TryAddTextCall(ReadOnlySpan<char> json, List<FunctionCallContent> calls)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToString());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            var name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var arguments = root.TryGetProperty("arguments", out var argsElement) ? argsElement
                : root.TryGetProperty("parameters", out argsElement) ? argsElement
                : default;
            if (string.IsNullOrWhiteSpace(name) || !_byName.ContainsKey(name) ||
                arguments.ValueKind != JsonValueKind.Object) return;

            var values = arguments.EnumerateObject().ToDictionary(
                p => ToCamelCase(p.Name), p => ToClrValue(p.Value));
            calls.Add(new FunctionCallContent($"text-call-{Guid.NewGuid():N}", name, values));
        }
        catch (JsonException)
        {
            // Obiekty JSON w zwykłym tekście mogą być niekompletne albo nie być wywołaniem.
        }
    }

    static string ToCamelCase(string name)
    {
        var result = new System.Text.StringBuilder(name.Length);
        var upperNext = false;
        foreach (var ch in name)
        {
            if (ch == '_') { upperNext = true; continue; }
            result.Append(upperNext ? char.ToUpperInvariant(ch) : ch);
            upperNext = false;
        }
        return result.ToString();
    }

    static object? ToClrValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var i) => i,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => value.EnumerateArray().Select(ToClrValue).ToList(),
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => ToClrValue(p.Value)),
        _ => null
    };

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
