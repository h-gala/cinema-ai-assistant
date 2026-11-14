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



## Kinowo Scraper – eksport repertuaru kin do JSON

Scraper jest na obecnym etapie osobnym komponentem projektu. Jego zadaniem jest pobieranie repertuaru ze strony Kinowo i eksportowanie danych do pliku JSON.

### Wymagania

- Python 3.12 (64-bit)
- pip

### Struktura

```text
cinema-ai-assistant/
├── .venv/
├── requirements.txt
├── kinowo_scraper.py
└── ...
```

### Instalacja i uruchomienie (Windows)

Wszystkie poniższe polecenia należy wykonywać w terminalu PowerShell, w katalogu projektu.

**1. Utworzenie środowiska wirtualnego**

```powershell
py -3.12 -m venv .venv
```

**2. Aktywacja środowiska**

```powershell
.venv\Scripts\Activate.ps1
```

**3. Aktualizacja pip i instalacja zależności**

```powershell
python -m pip install --upgrade pip
python -m pip install -r requirements.txt
```

**4. Uruchomienie scrapera**

```powershell
python kinowo_scraper.py -o repertuar.json
```

Wynikiem działania programu jest plik `repertuar.json` zawierający pobrany repertuar w formacie JSON.

### Ponowne uruchomienie

Przy kolejnych uruchomieniach nie trzeba ponownie tworzyć środowiska ani instalować zależności.

Wystarczy aktywować środowisko i uruchomić skrypt:

```powershell
.venv\Scripts\Activate.ps1
python kinowo_scraper.py -o repertuar.json
```

Po zakończeniu pracy można dezaktywować środowisko:

```powershell
deactivate
```

### Zależności

Zależności scrapera znajdują się w pliku `requirements.txt`.

Aktualnie wymagany pakiet:

- `lxml` – parsowanie dokumentów HTML i XML.

Środowisko wirtualne `.venv` nie powinno być dodawane do repozytorium Git. Należy uwzględnić je w pliku `.gitignore`:

```gitignore
.venv/
__pycache__/
*.py[cod]
```

