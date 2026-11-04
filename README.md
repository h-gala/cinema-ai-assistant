# cinema-ai-assistant
Asystent AI pomagający zaplanować wyjście do kina małym grupom znajomych, uwzględniając ich indywidualne wymagania. Analizuje dostępność całej grupy, historię obejrzanych filmów, sprawdza aktualny repertuar kin oraz opinie o filmach, a następnie proponuje najlepsze opcje uwzględniające również lokalizację kina oraz cenę biletów.
## Struktura

```text
cinema-ai-assistant/
├── README.md
├── .gitignore
├── docs/
│   └── vision.md                  # założenia i wizja projektu
└── src/
    ├── CinemaAgent.csproj
    ├── appsettings.json           # model Ollama, limity agenta, flaga Debug
    ├── Program.cs                 # konfiguracja, IChatClient, obsługa argumentów i pętla rozmowy
    ├── Agent/
    │   ├── AgentLoop.cs           # ręczna pętla Tool Calling, timeout, limit iteracji
    │   ├── AgentOptions.cs        # opcje agenta i modelu
    │   └── SystemPrompt.cs        # instrukcje dla modelu, bieżący czas, kalendarz
    ├── Tools/
    │   └── CinemaTools.cs         # narzędzia: find_cinemas, search_movies, get_screenings,
    │                              # get_user_profile, recommend_screenings
    ├── Services/
    │   ├── CinemaRepository.cs    # wczytanie i walidacja danych, wyszukiwanie seansów
    │   └── TextUtil.cs            # dopasowanie tekstu bez polskich znaków, nazwy dni
    ├── Models/
    │   └── Models.cs              # modele danych: kina, filmy, sieci, seanse, profil użytkownika
    ├── Users/
    │   └── UserProfiles.cs        # użytkownicy zdefiniowani na sztywno (anna, piotr, ola)
    └── data/                      # dane testowe
        ├── networks.json
        ├── cinemas.json
        ├── movies.json
        └── weekly-screenings/
            └── 2026-W45.json      # repertuar tygodniowy (kolejne tygodnie wystarczy dodać tutaj)
```

## Autorzy
- Natalia Bakalarska
- Zofia Cymerys
- Helena Gala

**Grupa:** 2
