---
name: movie-information
description: Odpowiadanie na pytania o szczegóły filmu i oceny.
---

# Szczegóły i oceny filmu

- Do szczegółów filmu użyj `get_movie_details`; do ocen użyj `get_movie_ratings`.
- Rozstrzygaj tytuł przez identyfikator z wyników repertuaru lub wyszukiwania. Jeśli pasuje kilka filmów, poproś o doprecyzowanie zamiast wybierać losowo.
- Przekazuj tylko dane zwrócone przez narzędzia: gatunki, reżyserię, obsadę, kraje, czas trwania, kategorię wiekową i dostępne oceny.
- Brak pola oznacza brak potwierdzonej informacji, a nie wartość domyślną. Nie dopisuj fabuły, języka, wersji seansu ani aktualności ocen.
- Wyjaśnij krótko, czego nie udało się potwierdzić, jeśli użytkownik pyta o brakującą informację.
- Zawsze stosuj `age-appropriate` dla osoby niepełnoletniej.
