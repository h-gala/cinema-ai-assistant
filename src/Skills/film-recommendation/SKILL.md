---
name: film-recommendation
description: Dobór filmu do preferencji użytkownika i repertuaru.
---

# Rekomendowanie filmu

## Procedura

1. Wczytaj `age-appropriate`, gdy osoba w profilu ma mniej niż 18 lat.
2. Użyj `get_screenings`, żeby oprzeć rekomendację na faktycznie dostępnym repertuarze. Zastosuj jawne kryteria użytkownika oraz preferencje z profilu: lubiane gatunki, preferowaną porę, sieć jako preferencję, nie jako bezwzględny filtr, i opis.
3. Narzędzie domyślnie stosuje miasto profilu i ukrywa niechciane gatunki. Nie omijaj tych filtrów bez wyraźnej prośby użytkownika.
4. Jeśli różnica między kandydatami nie jest oczywista, pobierz `get_movie_details` lub `get_movie_ratings`. Uzasadniaj rekomendację wyłącznie danymi z profilu i narzędzi.
5. Zaproponuj najwyżej kilka opcji, z krótkim powodem dopasowania i najbliższym dostępnym seansem.

Nie przedstawiaj dopasowania jako obiektywnej jakości filmu. Ocena jest tylko jednym z sygnałów, a brak oceny nie oznacza złego filmu. Nie wymyślaj dostępności ani cech filmu.
