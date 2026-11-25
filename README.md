# cinema-ai-assistant
Asystent AI pomagający zaplanować wyjście do kina małym grupom znajomych, uwzględniając ich indywidualne wymagania. Analizuje dostępność całej grupy, historię obejrzanych filmów, sprawdza aktualny repertuar kin oraz opinie o filmach, a następnie proponuje najlepsze opcje uwzględniające również lokalizację kina oraz cenę biletów.

## Struktura projektu

```text
cinema-ai-assistant/
├── README.md
├── docs/
│   └── vision.md                         # wizja i założenia projektu
└── src/
    ├── CinemaAgent.sln                   # rozwiązanie .NET
    │
    ├── CinemaAgent.Api/
    │   ├── CinemaAgent.Api.csproj        # projekt API
    │   ├── Program.cs                    # konfiguracja i uruchomienie API
    │   ├── appsettings.json              # konfiguracja API
    │   └── Properties/
    │       └── launchSettings.json       # profile uruchomieniowe
    │
    ├── CinemaAgent.Core/
    │   ├── CinemaAgent.Core.csproj        # warstwa dostępu do danych
    │   ├── ICinemaStore.cs               # interfejs magazynu danych kinowych
    │   └── RedisCinemaStore.cs            # magazyn danych oparty o Redis
    │
    ├── CinemaAgent/
    │   ├── CinemaAgent.csproj             # główny projekt agenta
    │   ├── Program.cs                     # punkt wejścia aplikacji
    │   ├── appsettings.json               # konfiguracja modelu i agenta
    │   │
    │   ├── Agent/
    │   │   ├── AgentLoop.cs               # pętla agenta i obsługa tool calling
    │   │   ├── AgentOptions.cs            # konfiguracja modelu i agenta
    │   │   ├── HistoryTrimmer.cs           # ograniczanie historii rozmowy
    │   │   ├── SkillTools.cs              # ładowanie skilli na żądanie
    │   │   └── SystemPrompt.cs             # bazowe zasady i kontekst agenta
    │   │
    │   ├── Models/
    │   │   └── Models.cs                  # modele domenowe kina, filmu i seansu
    │   │
    │   ├── Services/
    │   │   ├── CinemaRepository.cs        # wyszukiwanie i dostęp do danych kinowych
    │   │   └── TextUtil.cs                # pomocnicze operacje na tekście
    │   │
    │   ├── Skills/                        # szczegółowe instrukcje dla agenta
    │   │   ├── age-appropriate/SKILL.md   # dobór treści odpowiednich wiekowo
    │   │   ├── cinema-location/SKILL.md   # wyszukiwanie kin i lokalizacji
    │   │   ├── film-recommendation/SKILL.md # rekomendowanie filmów
    │   │   ├── movie-information/SKILL.md # informacje o filmach
    │   │   ├── movie-language/SKILL.md    # język i wersje językowe filmów
    │   │   ├── screening-search/SKILL.md  # wyszukiwanie seansów
    │   │   └── ticket-links/SKILL.md      # linki do zakupu biletów
    │   │
    │   ├── Storage/
    │   │   └── LocalCinemaStore.cs        # lokalny magazyn danych kinowych
    │   │
    │   ├── Tools/
    │   │   └── CinemaTools.cs             # narzędzia dostępne dla agenta
    │   │
    │   └── Users/
    │       └── UserProfiles.cs             # profile i preferencje użytkowników
    │
    ├── Scripts/
    │   ├── clear_kinowo_redis.py          # czyszczenie danych w Redis
    │   └── kinowo_scraper.py              # pobieranie danych repertuarowych
    │
    ├── requirements.txt                   # zależności Pythona
    ├── run.py                             # skrypt uruchomieniowy / pomocniczy
    └── .gitignore                         # reguły ignorowania plików w src/
```

## Uruchamianie

Wymagania i przykłady komend znajdują się w [instrukcji projektu](src/README.md).

## Autorzy
- Natalia Bakalarska
- Zofia Cymerys
- Helena Gala

**Grupa:** 2
