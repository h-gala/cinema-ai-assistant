# CinemaAgent – agent AI (Tool Calling) do repertuaru kin

Asystent odpowiadający po polsku na pytania o repertuar kin. Model (Ollama) proponuje wywołania narzędzi, a aplikacja je waliduje, wykonuje i oddaje wyniki. Dane o kinach, filmach i seansach pochodzą ze scrapera Kinowo i mogą być czytane z **Redisa** albo z **lokalnych plików JSON** (bez Redisa).

## Wymagania

| Komponent | Do czego | Wymagany |
|---|---|---|
| .NET SDK 9 | uruchomienie agenta | tak |
| Ollama + model (`ollama pull gemma4`) | model językowy | tak |
| Python 3.9+ (zalecany 3.12) | scraper `kinowo_scraper_redis.py` | tak, do pobrania danych |
| Redis 8 lub Redis Stack (moduł JSON) | źródło danych w trybie Redis | tylko w trybie Redis |

## Szybki start (bez Redisa)

```powershell
# 1. Pobierz repertuar jednego miasta do plików JSON
python run.py --fetch --city warszawa --output-dir data/kinowo

# 2. Uruchom agenta na lokalnych danych
dotnet run -- --user anna --data data/kinowo
```

Agent wypisze podsumowanie wczytanych danych (liczba filmów, seansów, miast i zakres dat), a potem przejdzie do trybu interaktywnego (pusta linia lub `exit` kończy).

## Uruchomienie agenta

```powershell
dotnet run -- --user anna
dotnet run -- --model qwen3:8b --user anna --data data/kinowo
dotnet run -- --user piotr --data data/kinowo --ask "Znajdź film fantasy na czwartek wieczór"
dotnet run -- --user ola   --ask "Podaj mi najbliższy film w Multikino Złote Tarasy"
```

| Argument | Znaczenie |
|---|---|
| `--model <nazwa>` | model Ollamy na tę sesję (np. `qwen3:8b`); ma pierwszeństwo przed `Ollama__Model` i `appsettings.json` |
| `--user <id>` | profil użytkownika: `anna`, `piotr`, `ola` (`Users/UserProfiles.cs`), domyślnie `anna` |
| `--ask "<pytanie>"` | jedno pytanie i koniec; bez tego tryb interaktywny |
| `--data <ścieżka>` | dane lokalne zamiast Redisa: katalog z plikami `{slug-miasta}.json` albo pojedynczy plik |

Nazwy zainstalowanych modeli wyświetlisz poleceniem `ollama list`. Wybierz model z obsługą narzędzi (tool calling).

Przed startem aplikacja sprawdza model przez endpoint Ollamy `/api/tags`. Jeśli go brakuje, wypisuje polecenie `ollama pull <model>` i kończy działanie.

## Źródła danych

Agent czyta dane przez interfejs `ICinemaStore` (`Storage/`). Wybór źródła:

- **Dane lokalne** – gdy podano `--data` albo ustawiono `Data:LocalPath` w `appsettings.json`. Nie wymaga Redisa.
- **Redis** – w pozostałych przypadkach. Adres w `Redis:ConnectionString` (domyślnie `127.0.0.1:6379`).

Oba tryby zwracają te same wyniki, bo `LocalCinemaStore` odtwarza w pamięci klucze i dokumenty, które scraper zapisuje do Redisa.

### Nieaktualne dane lokalne

Narzędzia pomijają seanse z przeszłości. Jeśli pliki są starsze niż dziś, agent ostrzeże o tym przy starcie. Wtedy ustaw sztuczny czas:

```powershell
$env:Agent__Now = "2026-11-16T18:00"
dotnet run -- --user anna --data data/kinowo
```

Wartość bez strefy jest traktowana jako czas warszawski.

### Redis – rozwiązywanie problemów

Przy błędach typu `SocketClosed` / `message timed out in the backlog` sprawdź `redis-cli ping` i ustaw w connection stringu keep-alive oraz dłuższe timeouty:

```
127.0.0.1:6379,abortConnect=false,connectTimeout=10000,syncTimeout=10000,keepAlive=15,connectRetry=5
```

## Konfiguracja

`appsettings.json` lub zmienne środowiskowe (separator `__`, np. `Ollama__Model=qwen3:14b`).

| Klucz | Domyślnie | Opis |
|---|---|---|
| `Ollama:Endpoint` | `http://localhost:11434` | adres Ollamy |
| `Ollama:Model` | `gemma4` | model |
| `Ollama:NumCtx` | `16384` | okno kontekstu wysyłane do każdego żądania |
| `Agent:MaxIterations` | `6` | limit iteracji pętli narzędzi w jednej turze |
| `Agent:TimeoutSeconds` | `1800` | limit czasu jednej tury |
| `Agent:Temperature` / `Agent:Seed` | `0` / `42` | powtarzalność odpowiedzi |
| `Agent:Now` | `null` | sztuczny czas (patrz wyżej) |
| `Agent:Debug` | `true` | log wywołań narzędzi, wyników i przycinania historii |
| `Agent:EnforceAgeRating` | `true` | twarde ograniczenie kategorii wiekowej |
| `Data:LocalPath` | `null` | ścieżka do danych lokalnych |
| `Redis:ConnectionString` | `127.0.0.1:6379` | połączenie z Redisem |

## Profile użytkowników

Profil (`Models/UserProfile.cs`) zawiera: imię, wiek, miasto, lubiane i niechciane gatunki, preferowaną sieć kin i porę dnia oraz opis. Cały profil trafia do promptu systemowego, a opis jest w nim oznaczony jako dane użytkownika, nie jako polecenia.

Profil wpływa też na narzędzia:

- **Miasto** z profilu jest domyślnym filtrem w `get_screenings` i `find_cinemas`. Nie obowiązuje, gdy podano konkretne kino. Użytkownik może poprosić o inne miasto albo o wszystkie (`city="all"`).
- **Niechciane gatunki** są domyślnie ukrywane w `get_screenings` i `search_movies`. Pojawiają się dopiero, gdy użytkownik poda tytuł albo wprost poprosi o ten gatunek.
- **Ograniczenie wieku:** gdy `Agent:EnforceAgeRating` jest włączone, wyniki nie zawierają filmów z kategorią wyższą niż wiek użytkownika. Nieznane formaty kategorii są odrzucane.
- Jeśli dla miasta nie ma danych (np. Biłgoraj), agent informuje o tym i proponuje miasta, które dane mają.

## Struktura projektu

```text
Program.cs                    wejście: konfiguracja, wybór źródła danych, pętla pytań
Agent/
  AgentLoop.cs                pętla tool calling
  SystemPrompt.cs             budowanie promptu (profil, kalendarz, zasady)
  SkillTools.cs               narzędzie read_skill z białą listą skilli
  HistoryTrimmer.cs           opcjonalne przycinanie historii (można usunąć)
Tools/CinemaTools.cs          narzędzia dla modelu
Skills/                       instrukcje ładowane na żądanie przez read_skill
Storage/
  ICinemaStore.cs             źródło danych
  RedisCinemaStore.cs         Redis JSON
  LocalCinemaStore.cs         lokalne pliki JSON ze scrapera
Models/UserProfile.cs         profil użytkownika
Users/UserProfiles.cs         zdefiniowani użytkownicy
Services/TextUtil.cs          nazwy dni tygodnia
kinowo_scraper_redis.py             scraper Kinowo
```

`HistoryTrimmer` ogranicza rozmiar historii rozmowy (stare tury tracą wyniki narzędzi). Instrukcja usunięcia jest w komentarzu na górze klasy.

### Skille agenta

Krótkie opisy skilli są widoczne w system prompcie. Agent wczytuje pełny plik `Skills/<id>/SKILL.md` narzędziem `read_skill` przed wykonaniem odpowiedniej procedury. Identyfikatory są jawnie dozwolone w `Agent/SkillTools.cs`; dodając skill, zaktualizuj katalog w tej klasie i dodaj plik w `Skills/<id>/SKILL.md`. Pliki są kopiowane do katalogu uruchomieniowego aplikacji.

Obecne skille obejmują wyszukiwanie seansów, rekomendacje, ochronę osób niepełnoletnich, wskazówki dotyczące języka filmu, szczegóły i oceny, lokalizację kina oraz linki do biletów.

## Kinowo Scraper

Scraper pobiera repertuar ze strony Kinowo i zapisuje go do Redis JSON albo do plików JSON. Korzysta tylko z biblioteki standardowej Pythona. Pakiety `redis` i `tzdata` są potrzebne wyłącznie do zapisu w Redisie.

### Tryby pracy

```powershell
# Pliki JSON, jedno miasto (zalecane dla kontrybutorów bez Redisa)
python run.py --fetch --city warszawa --output-dir data/kinowo

# Pliki JSON, wszystkie miasta z mapy strony (2 s przerwy między żądaniami)
python run.py --fetch --output-dir data/kinowo

# Pojedynczy plik; nazwa musi być slugiem miasta, jeśli ma trafić do --data
python run.py --fetch --city warszawa -o data/kinowo/warszawa.json

# Zapis do Redisa (Redis 8 lub Redis Stack); adres w zmiennej REDIS_URL
python fetch.py --fetch --redis --city warszawa
```

Bez żadnej z opcji zapisu (`--redis`, `--output-dir`, `-o`, `--html`) scraper zapisuje do Redisa. Opcja `--html <plik>` wczytuje zapisaną stronę zamiast pobierać ją z sieci.

W Redisie klucze repertuaru wygasają o północy w dniu seansu, a dane filmów są zapisywane tylko raz (`NX`). Przy zapisie do plików nic nie wygasa.

