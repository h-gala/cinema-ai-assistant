#!/usr/bin/env python3
"""Eksport repertuarów Kinowo do osobnych plików JSON dla miast.

Wymagania: Python 3.9 lub nowszy.
Uruchomienie: python kinowo_scraper_wszystkie.py --output-dir repertuary
"""

import argparse
import json
import re
import sys
from html.parser import HTMLParser
from http.client import HTTPException
from pathlib import Path
from tempfile import NamedTemporaryFile
from time import sleep
from urllib.error import HTTPError
from urllib.parse import urljoin, urlsplit
from urllib.request import Request, urlopen
from xml.etree import ElementTree

KINOWO_URL = "https://kinowo.net/"
SITEMAP_URL = urljoin(KINOWO_URL, "sitemap.xml")
REPERTORY_URL = urljoin(KINOWO_URL, "warszawa/")
CITY_SLUG = re.compile(r"[a-z0-9]+(?:-[a-z0-9]+)*")
REQUEST_PAUSE_SECONDS = 2
RATING_SOURCES = {
    "imdb": ("rating-imdb", "rating-imdb-value", 10),
    "metacritic": ("rating-meta", None, 100),
    "rotten_tomatoes": ("rating-rt", "rating-rt-value", 100),
    "filmweb": ("rating-fw", "rating-fw-value", 10),
}
SHOWTIME_PATTERN = re.compile(r"(?:[01]?\d|2[0-3]):[0-5]\d")


VOID_ELEMENTS = {
    "area", "base", "br", "col", "embed", "hr", "img", "input",
    "link", "meta", "param", "source", "track", "wbr",
}


class Element:
    def __init__(self, tag, attributes=()):
        self.tag = tag
        self.attributes = {key: value or "" for key, value in attributes}
        self.classes = self.get("class", "").split()
        self.content = []

    def get(self, key, default=None):
        return self.attributes.get(key, default)

    def children(self):
        return (item for item in self.content if isinstance(item, Element))

    def descendants(self):
        stack = list(reversed(list(self.children())))
        while stack:
            node = stack.pop()
            yield node
            stack.extend(reversed(list(node.children())))

    def with_class(self, name):
        return (node for node in self.descendants() if name in node.classes)

    def text_content(self):
        return "".join(
            item.text_content() if isinstance(item, Element) else item
            for item in self.content
        )


class RepertoireHTMLParser(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.root = Element("document")
        self.stack = [self.root]

    def handle_starttag(self, tag, attrs):
        node = Element(tag, attrs)
        self.stack[-1].content.append(node)
        if tag not in VOID_ELEMENTS:
            self.stack.append(node)

    def handle_startendtag(self, tag, attrs):
        self.stack[-1].content.append(Element(tag, attrs))

    def handle_endtag(self, tag):
        for index in range(len(self.stack) - 1, 0, -1):
            if self.stack[index].tag == tag:
                del self.stack[index:]
                break

    def handle_data(self, data):
        self.stack[-1].content.append(data)


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
    container = next(card.with_class("ratings"), None)
    if container is None:
        return ratings

    for source, (link_class, value_class, scale) in RATING_SOURCES.items():
        link = next((node for node in container.with_class(link_class) if node.tag == "a"), None)
        if link is None:
            continue
        value_node = (
            next(link.with_class(value_class), None) if value_class else link
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
    for day in card.with_class("date-group"):
        if not day.get("data-date"):
            continue
        for cinema in day.with_class("cinema-group"):
            label = next((node for node in cinema.children() if "cinema-label" in node.classes), None)
            cinema_name = (element_text(label) or "").removesuffix("↗").strip()
            prefix = cinema.get("data-u")
            for entry in cinema.descendants():
                if entry.get("data-s") is None and "badge-time" not in entry.classes:
                    continue
                # Odczytaj tekst godziny bez zagnieżdżonej plakietki z formatem.
                time = clean(" ".join((part for part in entry.content if isinstance(part, str))))
                if not time or not SHOWTIME_PATTERN.fullmatch(time):
                    continue
                href = entry.get("href")
                suffix = entry.get("data-s")
                if not href and prefix is not None and suffix:
                    # Kinowo łączy te fragmenty bez dodatkowego separatora.
                    href = prefix + suffix
                format_badge = next(entry.with_class("badge-fmt"), None)
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
    parser = RepertoireHTMLParser()
    parser.feed(page)
    parser.close()
    root = parser.root
    cards = [
        node for node in root.with_class("col")
        if node.tag == "div" and node.get("data-title") and node.get("data-slug")
    ]
    if not cards:
        page_elements = {
            node.get("id"): node for node in root.descendants()
            if node.get("id") in {"view-root", "film-grid", "no-films"}
        }
        view = page_elements.get("view-root")
        grid = page_elements.get("film-grid")
        if (view is not None and view.get("data-view") == "films"
                and "no-films" in page_elements and grid is not None
                and not any(grid.children())):
            return []
        raise ValueError("W HTML-u nie znaleziono kart filmów Kinowo.")

    movies = []
    for card in cards:
        title_box = next(card.with_class("card-title"), None)
        movie_link = None
        if title_box is not None:
            movie_link = next(
                (node.get("href") for node in title_box.children()
                 if node.tag == "a" and node.get("href")),
                None,
            )
        year_text = element_text(next(card.with_class("year"), None))
        movies.append({
            "title": clean(card.get("data-title")),
            "slug": card.get("data-slug"),
            "url": absolute_url(base_url, movie_link),
            "year": int(year_text) if year_text and year_text.isdecimal() else None,
            "duration": element_text(next(card.with_class("runtime"), None)),
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


def fetch(url, accept):
    request = Request(
        url,
        headers={"User-Agent": "KinowoRepertoryScraper/1.0", "Accept": accept},
    )
    with urlopen(request, timeout=30) as response:
        encoding = response.headers.get_content_charset() or "utf-8"
        return response.read().decode(encoding), response.geturl()


def parse_city_urls(sitemap):
    root = ElementTree.fromstring(sitemap)
    if root.tag.rsplit("}", 1)[-1] != "urlset":
        raise ValueError("Mapa Kinowo nie zawiera listy adresów URL.")

    cities = {}
    for location in root.findall("./{*}url/{*}loc"):
        url = urlsplit((location.text or "").strip())
        slug = url.path.removeprefix("/").removesuffix("/")
        if (url.scheme == "https" and url.netloc == "kinowo.net"
                and not url.query and not url.fragment and CITY_SLUG.fullmatch(slug)):
            cities[slug] = urljoin(KINOWO_URL, slug + "/")
    if not cities:
        raise ValueError("W mapie Kinowo nie znaleziono stron miast.")
    return dict(sorted(cities.items()))


def fetch_repertoire(url=REPERTORY_URL):
    page, final_url = fetch(url, "text/html")
    requested = urlsplit(url)
    final = urlsplit(final_url)
    if (final.netloc, final.path.rstrip("/")) != (requested.netloc, requested.path.rstrip("/")):
        raise ValueError(f"Kinowo przekierowało do innej strony ({final_url}).")
    return parse_repertoire(page, final_url)


def write_json(movies, path=None):
    result = json.dumps(movies, ensure_ascii=False, indent=2) + "\n"
    if path is None:
        sys.stdout.write(result)
        return

    # Zastąp istniejący plik dopiero po zapisaniu całego nowego repertuaru.
    temporary = NamedTemporaryFile(
        mode="w", encoding="utf-8", dir=path.parent, suffix=".tmp", delete=False
    )
    temporary_path = Path(temporary.name)
    try:
        with temporary:
            temporary.write(result)
        temporary_path.replace(path)
    finally:
        temporary_path.unlink(missing_ok=True)


def describe_error(error):
    if isinstance(error, HTTPError) and error.headers:
        retry_after = error.headers.get("Retry-After")
        if retry_after:
            return f"{error} (Retry-After: {retry_after})"
    return str(error)


def export_cities(cities, output_dir):
    output_dir.mkdir(parents=True, exist_ok=True)
    print(f"Miasta do pobrania: {len(cities)}", file=sys.stderr)
    saved = 0
    for index, (slug, url) in enumerate(cities.items(), 1):
        # Przerwa obejmuje też przejście od mapy do pierwszej strony miasta.
        sleep(REQUEST_PAUSE_SECONDS)
        path = output_dir / f"{slug}.json"
        try:
            movies = fetch_repertoire(url)
            write_json(movies, path)
        except (OSError, ValueError, HTTPException) as exc:
            print(f"[{index}/{len(cities)}] {slug}: {describe_error(exc)}", file=sys.stderr)
            if isinstance(exc, HTTPError) and exc.code in {403, 429}:
                print("Kinowo ograniczyło dostęp. Zatrzymano pobieranie.", file=sys.stderr)
                break
        else:
            saved += 1
            count = sum(len(movie["showtimes"]) for movie in movies)
            print(f"[{index}/{len(cities)}] {path} ({count} seansów)", file=sys.stderr)

    print(f"Zapisano {saved}/{len(cities)} plików.", file=sys.stderr)
    return int(saved != len(cities))


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--html", type=Path, help="Wczytaj zapisany HTML lub fragment z pliku UTF-8.")
    parser.add_argument("--city", metavar="SLUG", help="Pobierz jedno miasto, np. krakow lub trojmiasto.")
    output = parser.add_mutually_exclusive_group()
    output.add_argument("--output-dir", type=Path, help="Folder na pliki miast (domyślnie repertuary).")
    output.add_argument("-o", "--output", type=Path, help="Zapisz pojedynczy JSON (domyślnie dla Warszawy).")
    args = parser.parse_args()

    if args.city and not CITY_SLUG.fullmatch(args.city):
        parser.error("--city wymaga identyfikatora z adresu Kinowo, np. poznan.")
    slug = args.city or "warszawa"
    city_url = urljoin(KINOWO_URL, slug + "/")

    try:
        if args.html:
            movies = parse_repertoire(args.html.read_text(encoding="utf-8-sig"), city_url)
            path = args.output
            if args.output_dir:
                args.output_dir.mkdir(parents=True, exist_ok=True)
                path = args.output_dir / f"{slug}.json"
            write_json(movies, path)
            return 0
        if args.output:
            write_json(fetch_repertoire(city_url), args.output)
            return 0
        if args.city:
            cities = {slug: city_url}
        else:
            sitemap, _ = fetch(SITEMAP_URL, "application/xml, text/xml")
            cities = parse_city_urls(sitemap)
        return export_cities(cities, args.output_dir or Path("repertuary"))
    except (OSError, ValueError, HTTPException, ElementTree.ParseError) as exc:
        parser.exit(1, f"Błąd: {describe_error(exc)}\n")


if __name__ == "__main__":
    raise SystemExit(main())
