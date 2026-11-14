using CinemaAgent.Models;

namespace CinemaAgent.Users;

/// <summary>Użytkownicy zdefiniowani na sztywno. Wybierani argumentem --user.</summary>
public static class UserProfiles
{
    public static readonly IReadOnlyDictionary<string, UserProfile> All = new[]
    {
        new UserProfile("anna",  "Anna",  32, ["Sci-Fi", "Dramat"],     ["Horror"],  "multikino",    "evening",
            "Lubię kino science fiction i dramaty, ale horrory są okropne. Nie chcę mi się rano wstawać, więc chodzę do kina wieczorem", "Warszawa"),
        new UserProfile("piotr", "Piotr", 27, ["Horror", "Akcja"],      [],          "cinema-city",  "evening",
            "Najchętniej oglądam horrory i kino akcji.", "Biłgoraj"),
        new UserProfile("ola",   "Ola",   10, ["Animacja", "Familijny"],["Horror"],  null,           "afternoon",
            "Kocham My Little Pony, mój ulubiony kucyk to Sunset Shimmer, ona jest super!!!!!!!", "Warszawa"),
    }.ToDictionary(u => u.Id);
}
