# CinemaAgent – agent AI (Tool Calling) do repertuaru kin

Wymagania: .NET SDK 9, Ollama (`ollama pull gemma4`).

Uruchomienie:

    dotnet run -- --user anna
    dotnet run -- --user piotr --ask "Znajdź film fantasy na czwartek wieczór"
    dotnet run -- --user ola --ask "Podaj mi najbliższy film w Multikino Złote Tarasy"

Użytkownicy: anna, piotr, ola (`Users/UserProfiles.cs`).
Konfiguracja: `appsettings.json` lub zmienne środowiskowe, np. `Ollama__Model=qwen3:14b`.

Przed uruchomieniem aplikacja sprawdza model przez endpoint Ollama `/api/tags`. Jeśli go brakuje, wypisuje polecenie `ollama pull <model>` i kończy działanie.

Twarde ograniczenie kategorii wiekowej można włączyć w `Agent.EnforceAgeRating` w `appsettings.json` (lub przez `Agent__EnforceAgeRating=true`). Gdy opcja jest włączona, wyniki wyszukiwania filmów i seansów nie zawierają pozycji z kategorią wyższą niż wiek użytkownika; nieznane formaty kategorii są odrzucane.

Pole `Description` w profilu użytkownika jest dołączane do promptu jako dodatkowy kontekst przy doborze filmu.

