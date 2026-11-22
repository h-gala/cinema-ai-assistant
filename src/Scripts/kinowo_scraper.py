#!/usr/bin/env python3
"""Eksport warszawskiego repertuaru Kinowo do JSON.

Instalacja: python -m pip install lxml
Uruchomienie: python kinowo_scraper.py -o repertuar.json
"""

import argparse
import json
import re
import sys
from pathlib import Path
from urllib.parse import urljoin, urlsplit
from urllib.request import Request, urlopen

from lxml import etree, html

REPERTORY_URL = "https://kinowo.net/warszawa/"
RATING_SOURCES = {
    "imdb": ("rating-imdb", "rating-imdb-value", 10),
    "metacritic": ("rating-meta", None, 100),
    "rotten_tomatoes": ("rating-rt", "rating-rt-value", 100),
    "filmweb": ("rating-fw", "rating-fw-value", 10),
}
SHOWTIME_PATTERN = re.compile(r"(?:[01]?\d|2[0-3]):[0-5]\d")


def css_class(name):
    """Zwraca warunek XPath dopasowujący całe słowo w atrybucie class."""
    return f'contains(concat(" ", normalize-space(@class), " "), " {name} ")'


def first(node, xpath):
    matches = node.xpath(xpath)
    return matches[0] if matches else None


def clean(value):
    return " ".join((value or "").split()) or None


def element_text(node):
    return clean(node.text_content()) if node is not None else None


def absolute_url(base_url, href):
    if not href or href.startswith("#"):
        return None
    url = urljoin(base_url, href)
    return url if urlsplit(url).scheme in {"http", "https"} else None


def list_attribute(node, attribute):
    return [
        item.strip()
        for item in node.get(attribute, "").split("|")
        if item.strip()
    ]


def parse_ratings(card, base_url):
    ratings = {}
    container = first(card, f'.//*[{css_class("ratings")}]')
    if container is None:
        return ratings

    for source, (link_class, value_class, scale) in RATING_SOURCES.items():
        link = first(container, f'.//a[{css_class(link_class)}]')
        if link is None:
            continue
        value_node = (
            first(link, f'.//*[{css_class(value_class)}]') if value_class else link
        )
        value = (element_text(value_node) or "").rstrip("%").replace(",", ".")
        ratings[source] = {
            "score": float(value) if re.fullmatch(r"\d+(?:\.\d+)?", value) else None,
            "scale": scale,
            "unit": "%" if source == "rotten_tomatoes" else None,
            "url": absolute_url(base_url, link.get("href")),
        }
    return ratings


def parse_showtimes(card, base_url):
    showtimes = []
    for day in card.xpath(f'.//*[{css_class("date-group")} and @data-date]'):
        for cinema in day.xpath(f'.//*[{css_class("cinema-group")}]'):
            label = first(cinema, f'./*[{css_class("cinema-label")}]')
            cinema_name = (element_text(label) or "").removesuffix("↗").strip()
            prefix = cinema.get("data-u")
            for entry in cinema.xpath(f'.//*[@data-s or {css_class("badge-time")}]'):
                # Odczytaj tekst godziny bez zagnieżdżonej plakietki z formatem.
                time = clean(" ".join(entry.xpath("./text()")))
                if not time or not SHOWTIME_PATTERN.fullmatch(time):
                    continue
                href = entry.get("href")
                suffix = entry.get("data-s")
                if not href and prefix is not None and suffix:
                    # Kinowo łączy te fragmenty bez dodatkowego separatora.
                    href = prefix + suffix
                format_badge = first(entry, f'.//*[{css_class("badge-fmt")}]')
                showtimes.append({
                    "date": day.get("data-date"),
                    "time": time.zfill(5),
                    "cinema": cinema_name or None,
                    "room": clean(entry.get("data-room")),
                    "format": clean(entry.get("data-format")) or element_text(format_badge),
                    "ticket_url": absolute_url(base_url, href),
                })
    return sorted(showtimes, key=lambda row: (row["date"], row["time"], row["cinema"] or ""))


def parse_repertoire(page, base_url=REPERTORY_URL):
    root = html.fromstring(page)
    cards = root.xpath(f'//div[{css_class("col")} and @data-title and @data-slug]')
    if not cards:
        empty = first(root, '//*[@id="no-films"]')
        if empty is not None and "display:none" not in empty.get("style", "").replace(" ", ""):
            return []
        raise ValueError("W HTML-u nie znaleziono kart filmów Kinowo.")

    movies = []
    for card in cards:
        title_box = first(card, f'.//*[{css_class("card-title")}]')
        movie_link = first(title_box, "./a[@href]/@href") if title_box is not None else None
        year_text = element_text(first(card, f'.//*[{css_class("year")}]'))
        movies.append({
            "title": clean(card.get("data-title")),
            "slug": card.get("data-slug"),
            "url": absolute_url(base_url, movie_link),
            "year": int(year_text) if year_text and year_text.isdecimal() else None,
            "duration": element_text(first(card, f'.//*[{css_class("runtime")}]')),
            "age_rating": clean(card.get("data-age-rating")),
            "genres": list_attribute(card, "data-genres"),
            "countries": list_attribute(card, "data-countries"),
            "director": clean(card.get("data-director")),
            "cast": clean(card.get("data-cast")),
            "ratings": parse_ratings(card, base_url),
            "showtimes": parse_showtimes(card, base_url),
        })
    if not any(movie["showtimes"] for movie in movies):
        raise ValueError("Znaleziono filmy, ale nie udało się odczytać żadnego seansu.")
    return movies


def fetch_repertoire():
    request = Request(
        REPERTORY_URL,
        headers={"User-Agent": "KinowoRepertoryScraper/1.0", "Accept": "text/html"},
    )
    with urlopen(request, timeout=30) as response:
        encoding = response.headers.get_content_charset() or "utf-8"
        return response.read().decode(encoding), response.geturl()


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--html", type=Path, help="Wczytaj zapisany HTML lub fragment z pliku UTF-8.")
    parser.add_argument("-o", "--output", type=Path, help="Zapisz JSON do pliku. Domyślnie wypisz go na ekran.")
    args = parser.parse_args()

    try:
        if args.html:
            page, base_url = args.html.read_text(encoding="utf-8-sig"), REPERTORY_URL
        else:
            page, base_url = fetch_repertoire()
        movies = parse_repertoire(page, base_url)
        result = json.dumps(movies, ensure_ascii=False, indent=2) + "\n"
        if args.output:
            args.output.write_text(result, encoding="utf-8")
        else:
            sys.stdout.write(result)
    except (OSError, ValueError, etree.LxmlError) as exc:
        parser.exit(1, f"Błąd: {exc}\n")


if __name__ == "__main__":
    main()
