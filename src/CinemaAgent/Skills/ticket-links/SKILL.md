---
name: ticket-links
description: Znajdowanie linku do zakupu biletu na konkretny seans.
---

# Linki do biletów

- Ustal dokładne kino, tytuł, datę i godzinę seansu na podstawie wyników `get_screenings`.
- Następnie wywołaj `get_ticket_url` z tymi danymi. Nie konstruuj ani nie zgaduj adresu samodzielnie.
- Podaj link tylko wtedy, gdy narzędzie go zwróciło. Jeśli seans nie jest jednoznaczny albo linku brak, poproś o wybór seansu lub powiedz, że nie udało się uzyskać linku.
- Dla osoby niepełnoletniej nie podawaj linku do filmu 18+ ani do filmu o nieznanej kategorii jako rekomendacji.
