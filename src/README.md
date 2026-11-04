# CinemaAgent – agent AI (Tool Calling) do repertuaru kin

Wymagania: .NET SDK 9, Ollama (`ollama pull gemma4`) lub klucz API w zmiennej środowiskowej (`GEMINI_API_KEY` / `OPENAI_API_KEY`).

Uruchomienie:

    dotnet run -- --user anna
    dotnet run -- --model mistral
    dotnet run -- --provider gemini --model gemini-2.5-flash
    dotnet run -- --provider openai --model gpt-4o-mini
    dotnet run -- --user piotr --ask "Znajdź film fantasy na czwartek wieczór"
    dotnet run -- --user ola --ask "Podaj mi najbliższy film w Multikino Złote Tarasy"

Dostępne parametry CLI:
- `--provider` – wybór silnika: `ollama` (domyślnie), `gemini` lub `openai`
- `--model` – nazwa modelu (np. `qwen3:4b`, `mistral`, `gemini-2.5-flash`, `gpt-4o-mini`)
- `--user` – profil użytkownika (`anna`, `piotr`, `ola` ze ścieżki `Users/UserProfiles.cs`)
- `--ask` – pojedyncze zapytanie (bez tego parametru startuje czat interaktywny)

Konfiguracja: `appsettings.json` lub zmienne środowiskowe, np. `Ollama__Model=qwen3:14b`.

W trybie Ollama aplikacja przed startem weryfikuje dostępność modelu przez endpoint `/api/tags`. Jeśli model nie został pobrany, wyświetla polecenie `ollama pull <model>` i przerywa działanie.

Twarde ograniczenie kategorii wiekowej można włączyć w `Agent.EnforceAgeRating` w `appsettings.json` (lub przez `Agent__EnforceAgeRating=true`). Gdy opcja jest włączona, wyniki wyszukiwania filmów i seansów nie zawierają pozycji z kategorią wyższą niż wiek użytkownika; nieznane formaty kategorii są odrzucane.

Pole `Description` w profilu użytkownika jest dołączane do promptu jako dodatkowy kontekst przy doborze filmu.
