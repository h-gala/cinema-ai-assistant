using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace CinemaAgent.Agent;

/// <summary>Udostępnia agentowi wyłącznie zatwierdzone instrukcje ze skilli.</summary>
public sealed class SkillTools
{
    private sealed record Skill(string Id, string Description);

    private static readonly Skill[] Skills =
    [
        new("screening-search", "Wyszukiwanie repertuaru według filmu, kina, miasta, daty, pory i formatu."),
        new("film-recommendation", "Dobór filmu do preferencji, z użyciem repertuaru i dostępnych ocen."),
        new("age-appropriate", "Ograniczenia wiekowe i bezpieczne odpowiedzi dla osób niepełnoletnich."),
        new("movie-language", "Ostrożne rozpoznawanie wskazówek o języku filmu w tytule i oznaczeniach."),
        new("movie-information", "Szczegóły filmu i oceny: fakty, których nie wolno dopowiadać."),
        new("cinema-location", "Wybór kina i miasta oraz obsługa braku danych repertuarowych."),
        new("ticket-links", "Znajdowanie linku zakupu dla konkretnego seansu.")
    ];

    public IReadOnlyList<AIFunction> CreateAll() =>
    [
        AIFunctionFactory.Create(
            (Func<string, string>)ReadSkill,
            new AIFunctionFactoryOptions { Name = "read_skill" })
    ];

    public static string Catalog => string.Join("\n", Skills.Select(skill => $"- {skill.Id}: {skill.Description}"));

    [Description("Load the full instructions for one available cinema-assistant skill. Use the skill id exactly as listed in the system instructions.")]
    public string ReadSkill(
        [Description("Skill id from the available skill catalog, for example screening-search or movie-language.")] string skillId)
    {
        var skill = Skills.FirstOrDefault(item => string.Equals(item.Id, skillId, StringComparison.Ordinal));
        if (skill is null)
            return $"Unknown skill. Available skill ids: {string.Join(", ", Skills.Select(item => item.Id))}";

        var path = Path.Combine(AppContext.BaseDirectory, "Skills", skill.Id, "SKILL.md");
        if (!File.Exists(path))
            return $"Skill '{skill.Id}' is unavailable. Do not guess its instructions; continue using the system rules and available tools.";

        return File.ReadAllText(path);
    }
}
