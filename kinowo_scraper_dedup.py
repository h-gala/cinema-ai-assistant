#!/usr/bin/env python3
"""Eksport repertuarów Kinowo do Redis JSON lub do plików.

Wymagania: Python 3.9 lub nowszy.
Instalacja klienta Redis: python -m pip install redis tzdata
Uruchomienie: python kinowo_scraper_dedup.py --redis
"""

import argparse
import hashlib
import json
import os
import re
import sys
import unicodedata
from collections import defaultdict
from datetime import date, datetime, time, timedelta
from functools import partial
from html.parser import HTMLParser
from http.client import HTTPException
from pathlib import Path
from tempfile import NamedTemporaryFile
from time import sleep
from urllib.error import HTTPError
from urllib.parse import urljoin, urlsplit
from urllib.request import Request, urlopen
from xml.etree import ElementTree
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

try:
    import redis
except ImportError:
    redis = None

KINOWO_URL = "https://kinowo.net/"
SITEMAP_URL = urljoin(KINOWO_URL, "sitemap.xml")
REPERTORY_URL = urljoin(KINOWO_URL, "warszawa/")
CITY_SLUG = re.compile(r"[a-z0-9]+(?:-[a-z0-9]+)*")
REQUEST_PAUSE_SECONDS = 2
DEFAULT_REDIS_URL = "redis://127.0.0.1:6379/0"
KNOWN_CHAINS = ("Multikino", "Cinema City", "Helios")
REDIS_ERRORS = (redis.RedisError,) if redis is not None else ()
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


def connect_redis():
    if redis is None:
        raise ValueError("Zainstaluj klienta Redis poleceniem: python -m pip install redis tzdata")
    client = redis.Redis.from_url(
        os.environ.get("REDIS_URL", DEFAULT_REDIS_URL),
        socket_connect_timeout=5,
        socket_timeout=15,
    )
    try:
        # Odczyt sprawdza połączenie i obsługę Redis JSON przed pobieraniem stron.
        client.execute_command("JSON.GET", "movie:__kinowo_probe__")
    except redis.ResponseError as exc:
        if "unknown command" in str(exc).lower():
            raise ValueError("Serwer nie obsługuje JSON.GET. Użyj Redis 8 lub Redis Stack.") from exc
        raise
    return client


def cinema_chain(cinema):
    name = cinema.casefold()
    for chain in KNOWN_CHAINS:
        prefix = chain.casefold()
        if name == prefix or name.startswith(prefix + " "):
            return chain
    return "inne"


def build_redis_documents(city, movies, timezone, today):
    movie_documents = {}
    schedules = defaultdict(list)
    for movie in movies:
        slug = movie["slug"]
        movie_documents.setdefault(f"movie:{slug}", {
            key: value for key, value in movie.items() if key not in {"url", "showtimes"}
        })
        for showtime in movie["showtimes"]:
            day = date.fromisoformat(showtime["date"])
            if day < today:
                continue
            cinema = showtime["cinema"]
            if not cinema:
                raise ValueError(f"Brak nazwy kina dla seansu filmu {slug} ({day}).")
            # Zachowaj nazwę kina, kodując tylko separator klucza i znak ucieczki.
            cinema_key = cinema.replace("%", "%25").replace(":", "%3A")
            key = f"{city}:{cinema_chain(cinema)}:{cinema_key}:{day.isoformat()}"
            expires = int(datetime.combine(day + timedelta(days=1), time.min, timezone).timestamp())
            schedules[key, expires].append({
                "movie_id": slug,
                "source_slug": movie.get("source_slug", slug),
                "source_title": movie.get("source_title", movie.get("title")),
                "time": showtime["time"],
                "room": showtime["room"],
                "format": showtime["format"],
                "ticket_url": showtime["ticket_url"],
            })
    for showtimes in schedules.values():
        showtimes.sort(key=lambda row: (row["time"], row["movie_id"]))
    return movie_documents, schedules



# Reguły obejmują wyłącznie rozpoznane nazwy wydarzeń.
EVENT_PREFIXES = (
    "janosik dzieciom", "kino przyjazne sensorycznie", "kmw",
    "kino konesera", "kino kobiet", "kultura dostepna",
)
MOVIE_MIN_DAYS = 7
MOVIE_GRACE_DAYS = 2


def normalized_text(value):
    value = (value or "").casefold().replace("ł", "l")
    value = unicodedata.normalize("NFKD", value)
    value = "".join(char for char in value if not unicodedata.combining(char))
    return " ".join(re.findall(r"[a-z0-9]+", value))


def base_title(title):
    title = clean(title) or ""
    while ":" in title:
        prefix, rest = title.split(":", 1)
        if normalized_text(prefix) not in EVENT_PREFIXES or not rest.strip():
            break
        title = rest.strip()
    return title


def external_ids(movie):
    ids = dict(movie.get("external_ids") or {})
    for source, rating in (movie.get("ratings") or {}).items():
        url = urlsplit(rating.get("url") or "")
        host = (url.hostname or "").lower().removeprefix("www.")
        if source == "imdb" and host == "imdb.com":
            match = re.search(r"/title/(tt[0-9]+)(?:/|$)", url.path)
        elif source == "filmweb" and host == "filmweb.pl":
            match = re.search(r"/film/[^/]*-([0-9]+)/?$", url.path)
        else:
            continue
        if match:
            ids[source] = match.group(1)
    return ids


def duration_minutes(value):
    match = re.fullmatch(r"\s*(?:(\d+)h\s*)?(?:(\d+)min)?\s*", value or "")
    if not match or not any(match.groups()):
        return None
    return 60 * int(match.group(1) or 0) + int(match.group(2) or 0)


def same_film(first, second):
    left, right = external_ids(first), external_ids(second)
    # Sprzeczne identyfikatory lub lata oznaczają osobne wpisy.
    if any(left[source] != right[source] for source in left.keys() & right.keys()):
        return False
    if first.get("year") and second.get("year") and first["year"] != second["year"]:
        return False
    if any(left[source] == right[source] for source in left.keys() & right.keys()):
        return True
    if normalized_text(base_title(first.get("title"))) != normalized_text(base_title(second.get("title"))):
        return False
    directors = [normalized_text(movie.get("director")) for movie in (first, second)]
    runtimes = [duration_minutes(movie.get("duration")) for movie in (first, second)]
    return bool(
        first.get("year") and first.get("year") == second.get("year")
        and all(directors) and directors[0] == directors[1]
        and all(value is not None for value in runtimes)
        and abs(runtimes[0] - runtimes[1]) <= 2
    )


class MovieCatalog:
    def __init__(self, documents):
        self.documents = {}
        self.by_external_id = defaultdict(set)
        self.by_title = defaultdict(set)
        for slug, document in sorted(documents.items()):
            self.add(slug, document)

    def add(self, slug, movie):
        self.documents[slug] = movie
        for source, identifier in external_ids(movie).items():
            self.by_external_id[source, identifier].add(slug)
        self.by_title[normalized_text(base_title(movie.get("title")))].add(slug)

    def resolve(self, movie, alias=None):
        slug = movie["slug"]
        if slug in self.documents:
            return slug
        if alias in self.documents and same_film(self.documents[alias], movie):
            return alias
        candidates = set()
        for source, identifier in external_ids(movie).items():
            candidates.update(self.by_external_id[source, identifier])
        candidates.update(self.by_title[normalized_text(base_title(movie.get("title")))])
        matching = sorted(candidate for candidate in candidates
                          if same_film(self.documents[candidate], movie))
        # Nie wybieraj arbitralnie spośród sprzecznych rekordów.
        for first in matching:
            for second in matching:
                if not same_film(self.documents[first], self.documents[second]):
                    return slug
        if matching:
            # W istniejącej bazie preferuj już występujący tytuł bez prefiksu.
            matching.sort(key=lambda candidate: (
                self.documents[candidate].get("title") != base_title(self.documents[candidate].get("title")),
                candidate,
            ))
            return matching[0]
        # Usuwanie prefiksu bez potwierdzonych metadanych byłoby zbyt ryzykowne.
        # Pierwszy wariant zachowuje więc swój źródłowy slug.
        return slug


def movie_document(movie, canonical_slug):
    document = {key: value for key, value in movie.items() if key not in {"url", "showtimes"}}
    document["slug"] = canonical_slug
    document["title"] = base_title(movie.get("title"))
    document["external_ids"] = external_ids(movie)
    return document


def read_json_keys(client, pattern):
    keys = list(set(client.scan_iter(match=pattern, count=500)))
    for offset in range(0, len(keys), 200):
        batch = keys[offset:offset + 200]
        with client.pipeline(transaction=False) as pipeline:
            for key in batch:
                pipeline.execute_command("JSON.GET", key)
                pipeline.execute_command("EXPIRETIME", key)
            replies = pipeline.execute()
        for index, key in enumerate(batch):
            raw, expires = replies[2 * index:2 * index + 2]
            if raw is not None:
                yield key.decode() if isinstance(key, bytes) else key, json.loads(raw), expires


def extend_expiry(pipeline, key, expires):
    # GT traktuje klucz bez TTL jako nieskończony. Najpierw obsłuż ten przypadek.
    pipeline.execute_command("EXPIREAT", key, expires, "NX")
    pipeline.execute_command("EXPIREAT", key, expires, "GT")


class RedisMovieStore:
    def __init__(self, client, timezone):
        self.client = client
        self.timezone = timezone

    def midnight(self, day):
        return int(datetime.combine(day, time.min, self.timezone).timestamp())

    def minimum_movie_expiry(self):
        return int((datetime.now(self.timezone) + timedelta(days=MOVIE_MIN_DAYS)).timestamp())

    def initialize_legacy_ttls(self, records, today):
        persistent = {key for key, _, expires in records if expires == -1}
        if not persistent:
            return
        horizons = {key: self.minimum_movie_expiry() for key in persistent}
        # Stare dokumenty nie zawierają seansów. Ich ostatni dzień odczytujemy
        # ze wszystkich istniejących repertuarów, także z innych miast.
        for key, rows, _ in read_json_keys(self.client, "*:*:*:????-??-??"):
            parts = key.split(":")
            if len(parts) != 4 or not isinstance(rows, list):
                continue
            try:
                day = date.fromisoformat(parts[-1])
            except ValueError:
                continue
            expires = self.midnight(day + timedelta(days=1 + MOVIE_GRACE_DAYS))
            for row in rows:
                movie_key = "movie:" + row.get("movie_id", "")
                if movie_key in horizons:
                    horizons[movie_key] = max(horizons[movie_key], expires)
        with self.client.pipeline(transaction=True) as pipeline:
            for key, expires in horizons.items():
                extend_expiry(pipeline, key, expires)
            pipeline.execute()

    def write(self, city, movies):
        # Blokada obejmuje tylko lokalny zapis. Pobieranie HTTP odbywa się wcześniej.
        # Chroni rozpoznawanie nowego filmu przed równoległym importem innego miasta.
        with self.client.lock("kinowo:dedup-import-lock", timeout=300, blocking_timeout=30) as lock:
            return self.write_locked(city, movies, lock)

    def migrate_existing(self):
        """Połącz stare wpisy i przepnij odwołania we wszystkich repertuarach."""
        with self.client.lock("kinowo:dedup-import-lock", timeout=300, blocking_timeout=30) as lock:
            today = datetime.now(self.timezone).date()
            records = list(read_json_keys(self.client, "movie:*"))
            catalog = MovieCatalog({})
            mapping = {}
            horizons = {}
            minimum = self.minimum_movie_expiry()
            original_titles = {key[len("movie:"):]: document.get("title") for key, document, _ in records}
            records.sort(key=lambda record: (
                record[1].get("title") != base_title(record[1].get("title")), record[0]
            ))
            for key, document, expires in records:
                source = key[len("movie:"):]
                incoming = dict(document, slug=source)
                canonical = catalog.resolve(incoming)
                mapping[source] = canonical
                if canonical not in catalog.documents:
                    catalog.add(canonical, movie_document(incoming, canonical))
                horizons[canonical] = max(horizons.get(canonical, minimum), minimum, expires)

            schedules = []
            for key, rows, expires in read_json_keys(self.client, "*:*:*:????-??-??"):
                if len(key.split(":")) != 4 or not isinstance(rows, list):
                    continue
                try:
                    day = date.fromisoformat(key.rsplit(":", 1)[-1])
                except ValueError:
                    continue
                changed = False
                for row in rows:
                    source = row.get("movie_id")
                    canonical = mapping.get(source)
                    if canonical is None:
                        continue
                    if canonical != source:
                        row.setdefault("source_slug", source)
                        row.setdefault("source_title", original_titles[source])
                        row["movie_id"] = canonical
                        changed = True
                    horizons[canonical] = max(
                        horizons[canonical],
                        self.midnight(day + timedelta(days=1 + MOVIE_GRACE_DAYS)),
                        expires + MOVIE_GRACE_DAYS * 86400,
                    )
                if changed:
                    schedules.append((key, rows, expires))

            # Przepnij także istniejące aliasy prowadzące do starych dokumentów.
            aliases = dict(mapping)
            for key in self.client.scan_iter(match="movie_alias:*", count=500):
                value = self.client.get(key)
                target = value.decode() if isinstance(value, bytes) else value
                if target in mapping:
                    name = key.decode() if isinstance(key, bytes) else key
                    aliases[name[len("movie_alias:"):]] = mapping[target]

            lock.reacquire()
            with self.client.pipeline(transaction=True) as pipeline:
                for canonical, document in catalog.documents.items():
                    key = "movie:" + canonical
                    pipeline.execute_command("JSON.SET", key, "$", json.dumps(document, ensure_ascii=False))
                    extend_expiry(pipeline, key, horizons[canonical])
                for source, canonical in aliases.items():
                    key = "movie_alias:" + source
                    pipeline.set(key, canonical, keepttl=True)
                    extend_expiry(pipeline, key, horizons[canonical])
                for key, rows, expires in schedules:
                    pipeline.execute_command("JSON.SET", key, "$", json.dumps(rows, ensure_ascii=False))
                    if expires > 0:
                        pipeline.expireat(key, expires)
                for source, canonical in mapping.items():
                    if source != canonical:
                        pipeline.delete("movie:" + source)
                pipeline.execute()
            return f"Migracja: {len(records)} wpisów → {len(catalog.documents)} filmów; " + (
                f"przepisano {len(schedules)} repertuarów."
            )

    def write_locked(self, city, movies, lock):
        today = datetime.now(self.timezone).date()
        records = list(read_json_keys(self.client, "movie:*"))
        self.initialize_legacy_ttls(records, today)
        catalog = MovieCatalog({key[len("movie:"):]: document for key, document, _ in records})
        resolved = []
        aliases = {}
        # Zwykły tytuł otrzymuje pierwszeństwo przed wariantem wydarzenia.
        for movie in sorted(movies, key=lambda movie: (
            movie.get("title") != base_title(movie.get("title")), movie["slug"]
        )):
            raw_alias = self.client.get("movie_alias:" + movie["slug"])
            alias = raw_alias.decode() if isinstance(raw_alias, bytes) else raw_alias
            canonical = catalog.resolve(movie, alias)
            if canonical not in catalog.documents:
                catalog.add(canonical, movie_document(movie, canonical))
            aliases[movie["slug"]] = canonical
            resolved.append(dict(movie, slug=canonical, source_slug=movie["slug"], source_title=movie.get("title")))

        documents, schedules = build_redis_documents(city, resolved, self.timezone, today)
        # Dokument filmu jest wspólny. Dane wydarzenia pozostają przy seansie.
        documents = {key: catalog.documents[key[len("movie:"):]] for key in documents}
        horizons = {key: self.minimum_movie_expiry() for key in documents}
        for (_, expires), rows in schedules.items():
            for row in rows:
                movie_key = "movie:" + row["movie_id"]
                # Policz dodatkowe dni w lokalnym kalendarzu, z uwzględnieniem DST.
                schedule_day = datetime.fromtimestamp(expires, self.timezone).date()
                horizon = self.midnight(schedule_day + timedelta(days=MOVIE_GRACE_DAYS))
                horizons[movie_key] = max(horizons[movie_key], horizon)

        lock.reacquire()
        with self.client.pipeline(transaction=True) as pipeline:
            for key, document in documents.items():
                pipeline.execute_command("JSON.SET", key, "$", json.dumps(document, ensure_ascii=False), "NX")
                extend_expiry(pipeline, key, horizons[key])
            for source, canonical in aliases.items():
                # Mapowanie ma co najmniej horyzont bieżącego importu.
                alias_key = "movie_alias:" + source
                pipeline.set(alias_key, canonical, keepttl=True)
                extend_expiry(pipeline, alias_key, horizons["movie:" + canonical])
            for (key, expires), rows in schedules.items():
                pipeline.execute_command("JSON.SET", key, "$", json.dumps(rows, ensure_ascii=False))
                pipeline.expireat(key, expires)
            pipeline.execute()

        reduced = len(movies) - len(documents)
        return f"Redis ({len(schedules)} repertuarów, {len(documents)} filmów, połączono {reduced} wariantów)"


def write_city_file(output_dir, city, movies):
    path = output_dir / f"{city}.json"
    write_json(movies, path)
    return str(path)


def describe_error(error):
    if isinstance(error, HTTPError) and error.headers:
        retry_after = error.headers.get("Retry-After")
        if retry_after:
            return f"{error} (Retry-After: {retry_after})"
    return str(error)


def export_cities(cities, save_city):
    print(f"Miasta do pobrania: {len(cities)}", file=sys.stderr)
    saved = 0
    for index, (slug, url) in enumerate(cities.items(), 1):
        # Przerwa obejmuje też przejście od mapy do pierwszej strony miasta.
        sleep(REQUEST_PAUSE_SECONDS)
        try:
            movies = fetch_repertoire(url)
            destination = save_city(slug, movies)
        except (OSError, ValueError, HTTPException) as exc:
            print(f"[{index}/{len(cities)}] {slug}: {describe_error(exc)}", file=sys.stderr)
            if isinstance(exc, HTTPError) and exc.code in {403, 429}:
                print("Kinowo ograniczyło dostęp. Zatrzymano pobieranie.", file=sys.stderr)
                break
        else:
            saved += 1
            count = sum(len(movie["showtimes"]) for movie in movies)
            print(f"[{index}/{len(cities)}] {slug}: {destination} ({count} seansów w źródle)", file=sys.stderr)

    print(f"Zapisano repertuar dla {saved}/{len(cities)} miast.", file=sys.stderr)
    return int(saved != len(cities))


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--html", type=Path, help="Wczytaj zapisany HTML lub fragment z pliku UTF-8.")
    parser.add_argument("--city", metavar="SLUG", help="Pobierz jedno miasto, np. krakow lub trojmiasto.")
    parser.add_argument("--migrate-existing", action="store_true",
                        help="Połącz istniejące filmy i przepnij wszystkie repertuary; zakończ bez pobierania stron.")
    output = parser.add_mutually_exclusive_group()
    output.add_argument("--redis", action="store_true", help="Zapisz do Redis JSON (tryb domyślny online).")
    output.add_argument("--output-dir", type=Path, help="Zamiast Redisa zapisz osobne pliki JSON w tym folderze.")
    output.add_argument("-o", "--output", type=Path, help="Zapisz pojedynczy JSON (domyślnie dla Warszawy).")
    args = parser.parse_args()

    if args.migrate_existing and (args.html or args.city or args.output or args.output_dir):
        parser.error("--migrate-existing uruchom osobno, bez opcji pobierania lub zapisu plików.")
    if args.city and not CITY_SLUG.fullmatch(args.city):
        parser.error("--city wymaga identyfikatora z adresu Kinowo, np. poznan.")
    slug = args.city or "warszawa"
    city_url = urljoin(KINOWO_URL, slug + "/")

    try:
        save_city = None
        if args.redis or args.migrate_existing or not (args.html or args.output or args.output_dir):
            try:
                timezone = ZoneInfo("Europe/Warsaw")
            except ZoneInfoNotFoundError as exc:
                raise ValueError("Brak danych stref czasowych. Zainstaluj pakiet tzdata.") from exc
            store = RedisMovieStore(connect_redis(), timezone)
            if args.migrate_existing:
                print(store.migrate_existing())
                return 0
            save_city = store.write
        elif args.output_dir:
            args.output_dir.mkdir(parents=True, exist_ok=True)
            save_city = partial(write_city_file, args.output_dir)
        if args.html:
            movies = parse_repertoire(args.html.read_text(encoding="utf-8-sig"), city_url)
            if save_city is None:
                write_json(movies, args.output)
            else:
                print(f"{slug}: {save_city(slug, movies)}", file=sys.stderr)
            return 0
        if args.output:
            write_json(fetch_repertoire(city_url), args.output)
            return 0
        if args.city:
            cities = {slug: city_url}
        else:
            sitemap, _ = fetch(SITEMAP_URL, "application/xml, text/xml")
            cities = parse_city_urls(sitemap)
        return export_cities(cities, save_city)
    except REDIS_ERRORS as exc:
        parser.exit(1, f"Błąd Redis: {exc}\n")
    except (OSError, ValueError, HTTPException, ElementTree.ParseError) as exc:
        parser.exit(1, f"Błąd: {describe_error(exc)}\n")


if __name__ == "__main__":
    raise SystemExit(main())
