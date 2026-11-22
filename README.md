# cinema-ai-assistant
Asystent AI pomagający zaplanować wyjście do kina małym grupom znajomych, uwzględniając ich indywidualne wymagania. Analizuje dostępność całej grupy, historię obejrzanych filmów, sprawdza aktualny repertuar kin oraz opinie o filmach, a następnie proponuje najlepsze opcje uwzględniające również lokalizację kina oraz cenę biletów.

## Struktura projektu

```text
cinema-ai-assistant/
├── README.md
├── docs/
│   └── vision.md                  # wizja projektu
└── src/
    ├── CinemaAgent.csproj
    ├── CinemaAgent.sln
    ├── appsettings.json           # model Ollama i konfiguracja agenta
    ├── Program.cs                 # konfiguracja aplikacji i pętla rozmowy
    ├── README.md                  # instalacja, uruchamianie i konfiguracja
    ├── Agent/
    │   ├── AgentLoop.cs           # pętla tool calling i wykonanie narzędzi
    │   ├── AgentOptions.cs        # opcje modelu i agenta
    │   ├── HistoryTrimmer.cs      # ograniczanie historii rozmowy
    │   ├── SkillTools.cs          # narzędzie read_skill i katalog dozwolonych skilli
    │   └── SystemPrompt.cs        # profil, bieżący czas, kalendarz i zasady bazowe
    ├── Skills/                    # szczegółowe instrukcje wczytywane na żądanie
    │   ├── age-appropriate/SKILL.md
    │   ├── cinema-location/SKILL.md
    │   ├── film-recommendation/SKILL.md
    │   ├── movie-information/SKILL.md
    │   ├── movie-language/SKILL.md
    │   ├── screening-search/SKILL.md
    │   └── ticket-links/SKILL.md
    ├── Tools/
    │   └── CinemaTools.cs         # find_cinemas, search_movies, get_screenings,
    │                              # get_movie_details, get_movie_ratings,
    │                              # get_ticket_url
    ├── Services/
    │   ├── CinemaRepository.cs    # wczytywanie i wyszukiwanie danych
    │   └── TextUtil.cs            # dopasowanie tekstu i nazwy dni
    ├── Storage/
    │   ├── ICinemaStore.cs        # interfejs źródła danych
    │   ├── LocalCinemaStore.cs    # lokalne pliki JSON
    │   └── RedisCinemaStore.cs    # Redis JSON
    ├── Models/
    │   └── Models.cs              # modele kina, filmu, seansu i profilu
    ├── Users/
    │   └── UserProfiles.cs        # przykładowe profile użytkowników
    ├── data/
    │   └── kinowo/                # lokalne dane repertuarowe
    ├── kinowo_scraper.py
    ├── kinowo_scraper_redis.py    # pobieranie repertuaru do plików lub Redisa
    ├── kinowo_scraper_wszystkie.py
    └── requirements.txt
```

## Uruchamianie

Wymagania i przykłady komend znajdują się w [instrukcji projektu](src/README.md).

## Autorzy
- Natalia Bakalarska
- Zofia Cymerys
- Helena Gala

**Grupa:** 2
