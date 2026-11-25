---
name: cinema-location
description: Wyszukiwanie kin, sieci i repertuaru w określonej lokalizacji.
---

# Kino i lokalizacja

- Użyj `find_cinemas` do wyszukania kina, sieci lub miasta; `get_screenings` do repertuaru konkretnego kina.
- Korzystaj z miasta profilu, jeśli użytkownik nie poda innego. Szukaj we wszystkich miastach tylko wtedy, gdy użytkownik o to poprosi.
- Jeśli nazwa jest niejednoznaczna, doprecyzuj miasto lub pokaż pasujące wyniki. W argumencie kina używaj nazwy możliwie zgodnej z repertuarem.
- Jeśli brak danych dla lokalizacji, jasno to powiedz i podaj dostępne miasta zwrócone przez narzędzie. Nie zakładaj, że brak repertuaru oznacza brak kina.
- Sieć z profilu traktuj jako preferencję, chyba że użytkownik prosi o wyłączny wybór tej sieci.
