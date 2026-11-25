using System.Text.Json;
using Microsoft.Extensions.AI;

namespace CinemaAgent.Agent;

/// <summary>
/// OPCJONALNE przycinanie historii rozmowy. Aby usunąć tę funkcję z projektu:
///   1) skasuj ten plik,
///   2) w AgentLoop usuń parametr <c>trimmer</c> i linię <c>trimmer?.Trim(history);</c>,
///   3) w Program.cs usuń linię z <c>new HistoryTrimmer(...)</c> i argument <c>trimmer</c>.
/// Wywoływane na początku tury, więc nie rusza bieżącej tury. Prompt systemowy zostaje zawsze.
/// </summary>
/// <param name="maxChars">Budżet historii w znakach (ok. 3 znaki = 1 token).</param>
/// <param name="keepFullTurns">Ile ostatnich ukończonych tur zachowuje pełne wywołania i wyniki narzędzi.</param>
/// <param name="log">Opcjonalny odbiornik komunikatów diagnostycznych.</param>
public sealed class HistoryTrimmer(int maxChars, int keepFullTurns = 1, Action<string>? log = null)
{
    public void Trim(List<ChatMessage> history)
    {
        var before = EstimateChars(history);
        var messagesBefore = history.Count;

        // 1. Stare tury: usuń wywołania i wyniki narzędzi, zostaw pytanie i odpowiedź.
        CompactTurns(history, keepFullTurns);

        // 2. Nadal za duże: skompaktuj także ostatnią turę.
        if (EstimateChars(history) > maxChars)
            CompactTurns(history, 0);

        // 3. Wciąż za duże: wyrzucaj najstarsze tury w całości (zawsze zostaje co najmniej jedna).
        while (EstimateChars(history) > maxChars && DropOldestTurn(history)) { }

        var after = EstimateChars(history);
        if (after != before)
            log?.Invoke($"  [history] {before} → {after} znaków, wiadomości: {messagesBefore} → {history.Count}");
    }

    static List<int> TurnStarts(List<ChatMessage> history) =>
        history.Select((message, index) => (message, index))
               .Where(x => x.message.Role == ChatRole.User)
               .Select(x => x.index)
               .ToList();

    static bool IsToolTraffic(ChatMessage message) =>
        message.Role == ChatRole.Tool ||
        message.Contents.Any(c => c is FunctionCallContent or FunctionResultContent);

    static void CompactTurns(List<ChatMessage> history, int keepFull)
    {
        var starts = TurnStarts(history);
        // Od końca, żeby indeksy wcześniejszych tur pozostały poprawne po usunięciach.
        for (var turn = starts.Count - keepFull - 1; turn >= 0; turn--)
        {
            var from = starts[turn];
            var to = turn + 1 < starts.Count ? starts[turn + 1] : history.Count;
            for (var i = to - 1; i > from; i--)
                if (IsToolTraffic(history[i]))
                    history.RemoveAt(i);
        }
    }

    static bool DropOldestTurn(List<ChatMessage> history)
    {
        var starts = TurnStarts(history);
        if (starts.Count < 2) return false;
        history.RemoveRange(starts[0], starts[1] - starts[0]);
        return true;
    }

    static int EstimateChars(IEnumerable<ChatMessage> history) =>
        history.Sum(message => message.Contents.Sum(content => content switch
        {
            TextContent text => text.Text?.Length ?? 0,
            FunctionResultContent result => result.Result is JsonElement element
                ? element.GetRawText().Length
                : result.Result?.ToString()?.Length ?? 0,
            FunctionCallContent call => call.Name.Length + 20 +
                (call.Arguments?.Sum(a => a.Key.Length + (a.Value?.ToString()?.Length ?? 0)) ?? 0),
            _ => 0
        }));
}