using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CinemaAgent.Agent;

/// <summary>
/// Ręczna pętla Tool Calling. Model PROPONUJE wywołanie narzędzia (FunctionCallContent),
/// a aplikacja je waliduje, wykonuje i oddaje wynik (FunctionResultContent).
/// </summary>
public sealed class AgentLoop(
    IChatClient chat, IReadOnlyList<AIFunction> tools, AgentOptions opt,
    HistoryTrimmer? trimmer = null)
{
    readonly Dictionary<string, AIFunction> _byName = tools.ToDictionary(t => t.Name);

    public async Task<string> RunTurnAsync(
        List<ChatMessage> history,
        string userMessage)
    {
        trimmer?.Trim(history);

        using var cts = new CancellationTokenSource(
            TimeSpan.FromSeconds(opt.TimeoutSeconds));

        history.Add(new ChatMessage(ChatRole.User, userMessage));

        var options = new ChatOptions
        {
            Tools = [.. tools],
            ToolMode = ChatToolMode.Auto,
            Temperature = opt.Temperature,
            Seed = opt.Seed
        };

        var toolNames = tools
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);

        var requireNativeCall = false;

        for (var i = 1; i <= opt.MaxIterations; i++)
        {
            options.ToolMode = requireNativeCall
                ? ChatToolMode.RequireAny
                : ChatToolMode.Auto;

            // Instrukcja naprawcza dotyczy tylko bieżącego żądania.
            var requestHistory = history.ToList();

            if (requireNativeCall)
            {
                requestHistory.Add(new ChatMessage(
                    ChatRole.System,
                    """
                Your previous response contained textual tool instructions
                instead of native function calls.

                Use the provided tools through the native function-calling
                interface. Select the appropriate tool and provide arguments
                matching its schema.

                For get_screenings:
                - genre contains movie genres, such as Sci-Fi or Dramat.
                - format contains screening formats, such as 3D, IMAX, NAP or DUB.

                After receiving tool results, continue working on the user's
                request and provide a final answer in Polish.
                """));
            }

            var response = await chat.GetResponseAsync(
                requestHistory,
                options,
                cts.Token);

            var calls = response.Messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            if (calls.Count > 0)
            {
                history.AddRange(response.Messages);
            }
            else
            {
                var text = response.Text ?? string.Empty;

                // Np. find_cinemas(city='Warszawa').
                // Sprawdzenie działa również wewnątrz bloku Markdown.
                if (ContainsTextToolInvocation(text, toolNames))
                {
                    Log(
                        "  [tool recovery] Wykryto tekstowe instrukcje " +
                        "narzędzi. Ponawiam żądanie z wymuszeniem tool calla.",
                        ConsoleColor.DarkYellow);

                    history.AddRange(response.Messages);
                    requireNativeCall = true;
                    continue;
                }

                // Zachowujemy dotychczasowe odzyskiwanie JSON-a.
                calls = ExtractTextFunctionCalls(text);

                if (calls.Count == 0)
                {
                    var blocks = Regex.Matches(
                        text,
                        @"```[ \t]*(?:json)?[ \t]*\r?\n?(?<json>[\s\S]*?)```",
                        RegexOptions.IgnoreCase |
                        RegexOptions.CultureInvariant);

                    foreach (Match block in blocks)
                    {
                        var recovered = ExtractTextFunctionCalls(
                            block.Groups["json"].Value.Trim());

                        calls.AddRange(recovered);
                    }
                }

                if (calls.Count > 0)
                {
                    Log(
                        "  [tool recovery] Wykryto JSON wskazujący narzędzie.",
                        ConsoleColor.DarkYellow);

                    history.Add(new ChatMessage(
                        ChatRole.Assistant,
                        calls.Select(call => (AIContent)call).ToList()));
                }
                else
                {
                    // Po wymuszeniu wywołania nie kończymy tury tekstem,
                    // który mógłby zastępować niewykonane narzędzie.
                    if (requireNativeCall)
                    {
                        history.AddRange(response.Messages);
                        continue;
                    }

                    history.AddRange(response.Messages);
                    return text;
                }
            }

            var results = new List<AIContent>();

            foreach (var call in calls)
            {
                var result = await ExecuteAsync(call, cts.Token);

                results.Add(new FunctionResultContent(
                    call.CallId,
                    result));
            }

            history.Add(new ChatMessage(ChatRole.Tool, results));

            // Narzędzia zostały wykonane. Model może teraz udzielić odpowiedzi
            // lub samodzielnie zdecydować o kolejnym wywołaniu.
            requireNativeCall = false;
        }

        return "Nie udało się zakończyć wyszukiwania w dostępnym " +
               $"limicie {opt.MaxIterations} kroków. Spróbuj ponownie.";
    }

    private static bool ContainsTextToolInvocation(
        string text,
        IReadOnlySet<string> toolNames)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var name in toolNames)
        {
            var pattern =
                @"(?<![\p{L}\p{N}_])" +
                Regex.Escape(name) +
                @"\s*\(";

            if (Regex.IsMatch(
                text,
                pattern,
                RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        return false;
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
            var json = result is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(result);
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