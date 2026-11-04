# CinemaAgent – agent AI (Tool Calling) do repertuaru kin

Wymagania: .NET SDK 10, Ollama (`ollama pull qwen3:8b`).

Dane: skopiuj pliki testowe do `data/` jako
`cinemas.json`, `movies.json`, `networks.json`, `catalog*.json` (np. `catalog-2026-W45.json`).

Uruchomienie:

    dotnet run -- --user anna
    dotnet run -- --user piotr --ask "Znajdź film fantasy na czwartek wieczór"
    dotnet run -- --user ola --ask "Podaj mi najbliższy film w Multikino Złote Tarasy"

Użytkownicy: anna, piotr, ola (`Users/UserProfiles.cs`).
Konfiguracja: `appsettings.json` lub zmienne środowiskowe, np. `Ollama__Model=qwen3:14b`.
Stałe "teraz" dla powtarzalnego demo: `Agent__Now=2026-11-02T09:00:00+01:00`.
