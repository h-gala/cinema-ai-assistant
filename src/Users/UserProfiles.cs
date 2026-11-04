using CinemaAgent.Models;

namespace CinemaAgent.Users;

/// <summary>Użytkownicy zdefiniowani na sztywno. Wybierani argumentem --user.</summary>
public static class UserProfiles
{
    public static readonly IReadOnlyDictionary<string, UserProfile> All = new[]
    {
        new UserProfile("anna",  "Anna",  32, ["Sci-Fi", "Dramat"],     ["Horror"],  "multikino",    "evening"),
        new UserProfile("piotr", "Piotr", 27, ["Horror", "Akcja"],      [],          "cinema-city",  "evening"),
        new UserProfile("ola",   "Ola",   10, ["Animacja", "Familijny"],["Horror"],  null,           "afternoon"),
    }.ToDictionary(u => u.Id);
}
