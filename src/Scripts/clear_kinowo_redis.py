#!/usr/bin/env python3
"""Usuń wszystkie klucze z bazy Redisa wskazanej w REDIS_URL.

Domyślnie redis://127.0.0.1:6379/0.
Uruchomienie: python clear_kinowo_redis.py
Usuwanie dotyczy całej wybranej bazy, także danych spoza Kinowo.
"""

import os
import sys
from urllib.parse import urlsplit

try:
    import redis
except ImportError:
    raise SystemExit("Zainstaluj klienta: python -m pip install redis")


def main():
    url = os.environ.get("REDIS_URL", "redis://127.0.0.1:6379/0")
    address = urlsplit(url)
    client = redis.Redis.from_url(url, socket_connect_timeout=5, socket_timeout=60)
    try:
        count = client.dbsize()
        client.flushdb(asynchronous=False)
    except redis.RedisError as exc:
        print(f"Błąd Redis: {exc}", file=sys.stderr)
        return 1
    finally:
        client.close()
    print(f"Wyczyszczono bazę {address.path or '/0'} na {address.hostname}:{address.port or 6379}. "
          f"Przed operacją zawierała {count} kluczy.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
