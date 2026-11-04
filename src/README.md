# CinemaAgent – agent AI (Tool Calling) do repertuaru kin

Wymagania: .NET SDK 9, Ollama (`ollama pull gemma4`).

Uruchomienie:

    dotnet run -- --user anna
    dotnet run -- --user piotr --ask "Znajdź film fantasy na czwartek wieczór"
    dotnet run -- --user ola --ask "Podaj mi najbliższy film w Multikino Złote Tarasy"

Użytkownicy: anna, piotr, ola (`Users/UserProfiles.cs`).
Konfiguracja: `appsettings.json` lub zmienne środowiskowe, np. `Ollama__Model=qwen3:14b`.

