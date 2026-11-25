---
name: screening-search
description: Wyszukiwanie seansów według filmu, kina, miasta, daty, pory dnia i formatu.
---

# Wyszukiwanie seansów

## Procedura

1. Rozpoznaj ograniczenia użytkownika: tytuł lub gatunki, kino, miasto, daty, godziny i format (np. IMAX, 3D, napisy, dubbing). Wiele gatunków przekaż w jednym argumencie `genre`, rozdzielając je przecinkami, np. `Sci-Fi, Dramat`; wynik pasuje do dowolnego z podanych gatunków.
2. Użyj `get_screenings`. Jeśli użytkownik szuka kina, użyj `find_cinemas`; jeśli najpierw szuka tytułu lub gatunku, możesz użyć `search_movies`.
3. Daty względne wyliczaj z kalendarza w system prompcie. „Wieczorem” oznacza od 17:00, „po południu” od 12:00 do 17:00.
4. Domyślnie zachowaj miasto, preferowaną porę i filtry profilu. Zmień je, gdy użytkownik poda inne kryteria. `city="all"` stosuj tylko na prośbę o wszystkie miasta lub ogólnokrajowy wybór.
5. Przy wyborze „najbliższego” seansu wykorzystaj kolejność zwróconą przez narzędzie i ogranicz wyniki do wskazanego kina, jeśli je podano.
6. Podaj tytuł, kino, dzień, godzinę i dostępny format. Nie sugeruj dostępności języka, napisów ani dubbingu, jeśli wynik tego nie potwierdza.

## Brak wyników

Powiedz, że nie znaleziono pasującego seansu. Zaproponuj konkretne rozszerzenie: inny dzień, porę, kino lub miasto. Jeśli narzędzie zwróci listę miast albo kin, podaj kilka z niej.

Nie pokazuj ukrytych, niechcianych gatunków ani tytułów, chyba że użytkownik wyraźnie o nie poprosi. Zawsze przestrzegaj ograniczeń wiekowych.
