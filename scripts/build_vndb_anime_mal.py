#!/usr/bin/env python
"""Regenerate Shared/resources/vndb_anime_mal.json from a VNDB database dump.

The VNDB kana (HTTPS) API does not expose anime adaptations, and the legacy TCP
API only returns AniDB ids. The full database dump is the only source that maps a
VN to its related anime *and* carries MyAnimeList ids (anime.mal_id, integer[]).
Since our anime decks are keyed by MAL link, this builds a small static map:

    { "v4": [1723, 2167, 4059, 4181, 6351], ... }   # vndb id -> sorted unique MAL ids

The result is tiny (~75 KB) and committed to Shared/resources so it can be loaded
into memory by both the API (per-deck on VN metadata refresh) and the CLI
(bulk backfill). Anime-adaptation data changes slowly, so an occasional rerun is
enough.

Usage:
    python scripts/build_vndb_anime_mal.py                 # download latest dump
    python scripts/build_vndb_anime_mal.py --dump-path X    # use a local .tar.zst

Requires the `zstandard` package (pip install zstandard).
"""
import argparse
import json
import os
import sys
import tarfile
import tempfile
import urllib.request

DUMP_URL = "https://dl.vndb.org/dump/vndb-db-latest.tar.zst"
WANTED = ("db/anime", "db/anime.header", "db/vn_anime", "db/vn_anime.header")
NULL = "\\N"

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_PATH = os.path.join(REPO_ROOT, "Shared", "resources", "vndb_anime_mal.json")


def download_dump(dest):
    print(f"Downloading {DUMP_URL} ...")
    with urllib.request.urlopen(DUMP_URL) as resp, open(dest, "wb") as f:
        total = 0
        while True:
            chunk = resp.read(1 << 20)
            if not chunk:
                break
            f.write(chunk)
            total += len(chunk)
    print(f"Downloaded {total / 1e6:.1f} MB")


def extract_tables(dump_path):
    """Stream the .tar.zst and return the raw text of the four wanted files."""
    try:
        import zstandard
    except ImportError:
        raise SystemExit("This script needs the 'zstandard' package. Install it with: pip install zstandard")

    out = {}
    dctx = zstandard.ZstdDecompressor()
    with open(dump_path, "rb") as fh, dctx.stream_reader(fh) as reader:
        with tarfile.open(fileobj=reader, mode="r|") as tar:
            for member in tar:
                if member.name in WANTED:
                    out[member.name] = tar.extractfile(member).read().decode("utf-8")
                if len(out) == len(WANTED):
                    break
    missing = [w for w in WANTED if w not in out]
    if missing:
        raise SystemExit(f"Dump is missing expected tables: {missing}")
    return out


def col_index(header_text, column):
    cols = header_text.rstrip("\n").split("\t")
    if column not in cols:
        raise SystemExit(f"Column '{column}' not found in header: {cols}")
    return cols.index(column)


def parse_int_array(value):
    if value in (NULL, "{}", ""):
        return []
    return [int(x) for x in value.strip("{}").split(",") if x]


def build_map(tables):
    a_id = col_index(tables["db/anime.header"], "id")
    a_mal = col_index(tables["db/anime.header"], "mal_id")
    v_id = col_index(tables["db/vn_anime.header"], "id")
    v_aid = col_index(tables["db/vn_anime.header"], "aid")

    anime_mal = {}
    for line in tables["db/anime"].splitlines():
        if not line:
            continue
        cols = line.split("\t")
        anime_mal[cols[a_id]] = parse_int_array(cols[a_mal])

    vn_to_mal = {}
    for line in tables["db/vn_anime"].splitlines():
        if not line:
            continue
        cols = line.split("\t")
        mals = anime_mal.get(cols[v_aid])
        if not mals:
            continue
        vn_to_mal.setdefault(cols[v_id], set()).update(mals)

    return {
        vid: sorted(mals)
        for vid, mals in sorted(vn_to_mal.items(), key=lambda kv: int(kv[0][1:]))
        if mals
    }


def main():
    ap = argparse.ArgumentParser(description="Build vndb_anime_mal.json from a VNDB dump.")
    ap.add_argument("--dump-path", help="Path to a local vndb-db-*.tar.zst (otherwise downloads latest).")
    args = ap.parse_args()

    tmp = None
    try:
        dump_path = args.dump_path
        if not dump_path:
            tmp = tempfile.NamedTemporaryFile(suffix=".tar.zst", delete=False)
            tmp.close()
            download_dump(tmp.name)
            dump_path = tmp.name

        tables = extract_tables(dump_path)
        result = build_map(tables)

        os.makedirs(os.path.dirname(OUT_PATH), exist_ok=True)
        with open(OUT_PATH, "w", encoding="utf-8") as f:
            json.dump(result, f, separators=(",", ":"), ensure_ascii=False)

        pairs = sum(len(v) for v in result.values())
        size = os.path.getsize(OUT_PATH)
        print(f"Wrote {OUT_PATH}")
        print(f"  {len(result)} VNs, {pairs} VN->MAL pairs, {size / 1024:.0f} KB")
    finally:
        if tmp and os.path.exists(tmp.name):
            os.remove(tmp.name)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
