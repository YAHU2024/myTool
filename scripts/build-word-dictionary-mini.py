#!/usr/bin/env python3
"""Build a small SQLite validation database from ECDICT and OEWN 2025 JSON.

This is a minimal validation script, not the production importer. It keeps the
raw source files out of Git and writes generated databases under an ignored
output directory such as .build-output/.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import os
import sqlite3
import sys
import time
import zipfile
from datetime import datetime, timezone
from pathlib import Path


SAMPLE_WORDS = [
    "hello",
    "run",
    "gave",
    "taken",
    "long-time",
    "house",
    "apple",
    "serendipity",
    "polyglot",
    "phrasal verb",
    "unfriend",
    "chatgpt",
]

SCHEMA = """
CREATE TABLE IF NOT EXISTS ecdict_entries (
    word TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
    phonetic TEXT,
    definition TEXT,
    translation TEXT,
    pos TEXT,
    collins TEXT,
    oxford TEXT,
    tag TEXT,
    bnc INTEGER,
    frq INTEGER,
    exchange TEXT,
    sw TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_ecdict_sw ON ecdict_entries(sw);

CREATE TABLE IF NOT EXISTS wordnet_senses (
    lemma TEXT NOT NULL COLLATE NOCASE,
    pos TEXT,
    sense_id TEXT,
    synset_id TEXT,
    definition TEXT,
    example TEXT,
    members TEXT
);
CREATE INDEX IF NOT EXISTS idx_wordnet_lemma ON wordnet_senses(lemma);
CREATE INDEX IF NOT EXISTS idx_wordnet_synset ON wordnet_senses(synset_id);

CREATE TABLE IF NOT EXISTS wordnet_forms (
    form TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
    lemma TEXT NOT NULL COLLATE NOCASE
);

CREATE TABLE IF NOT EXISTS dictionary_meta (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
"""


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def strip_word(word: str) -> str:
    return "".join(ch for ch in word if ch.isalnum()).lower()


def parse_int(value: str) -> int | None:
    value = (value or "").strip()
    return int(value) if value else None


def import_ecdict(conn: sqlite3.Connection, path: Path) -> int:
    cursor = conn.cursor()
    rows: list[tuple[str, ...]] = []
    count = 0
    with path.open(encoding="utf-8", newline="") as handle:
        reader = csv.DictReader(handle)
        for record in reader:
            word = record.get("word", "")
            if not word:
                continue
            rows.append(
                (
                    word,
                    record.get("phonetic") or "",
                    record.get("definition") or "",
                    record.get("translation") or "",
                    record.get("pos") or "",
                    record.get("collins") or "",
                    record.get("oxford") or "",
                    record.get("tag") or "",
                    parse_int(record.get("bnc")),
                    parse_int(record.get("frq")),
                    record.get("exchange") or "",
                    strip_word(word),
                )
            )
            if len(rows) >= 50_000:
                cursor.executemany(
                    """INSERT OR REPLACE INTO ecdict_entries
                       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                    rows,
                )
                count += len(rows)
                rows.clear()
    if rows:
        cursor.executemany(
            """INSERT OR REPLACE INTO ecdict_entries
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
            rows,
        )
        count += len(rows)
    conn.commit()
    return count


def build_synset_map(zip_handle: zipfile.ZipFile) -> dict[str, dict[str, object]]:
    synsets: dict[str, dict[str, object]] = {}
    synset_names = [
        name
        for name in zip_handle.namelist()
        if name.endswith(".json")
        and not name.startswith("entries-")
        and name.split(".")[0] in {"noun", "verb", "adj", "adv"}
    ]
    for name in synset_names:
        payload = json.loads(zip_handle.read(name).decode("utf-8"))
        for synset_id, value in payload.items():
            synsets[synset_id] = {
                "definition": value.get("definition") or [],
                "example": value.get("example") or [],
                "members": value.get("members") or [],
                "pos": value.get("partOfSpeech") or "",
            }
    return synsets


def import_oewn(conn: sqlite3.Connection, path: Path) -> tuple[int, int]:
    cursor = conn.cursor()
    sense_count = 0
    form_count = 0
    seen_senses: set[tuple[str, str]] = set()
    seen_forms: set[tuple[str, str]] = set()
    entry_names = [
        name
        for name in zipfile.ZipFile(path).namelist()
        if name.startswith("entries-") and name.endswith(".json")
    ]
    with zipfile.ZipFile(path) as zip_handle:
        synsets = build_synset_map(zip_handle)
        sense_rows: list[tuple[str, str, str, str, str, str, str]] = []
        form_rows: list[tuple[str, str]] = []
        for name in entry_names:
            payload = json.loads(zip_handle.read(name).decode("utf-8"))
            for lemma, pos_map in payload.items():
                if not isinstance(pos_map, dict):
                    continue
                for pos, entry in pos_map.items():
                    if not isinstance(entry, dict):
                        continue
                    for form in entry.get("form") or []:
                        form_key = (form.casefold(), lemma.casefold())
                        if form_key not in seen_forms:
                            seen_forms.add(form_key)
                            form_rows.append((form, lemma))
                    for sense in entry.get("sense") or []:
                        if not isinstance(sense, dict):
                            continue
                        synset_id = sense.get("synset")
                        if not synset_id:
                            continue
                        sense_key = (lemma.casefold(), str(synset_id))
                        if sense_key in seen_senses:
                            continue
                        seen_senses.add(sense_key)
                        synset = synsets.get(str(synset_id), {})
                        definition = " ".join(
                            str(item) for item in synset.get("definition") or []
                        )
                        example = " ".join(
                            str(item) for item in synset.get("example") or []
                        )
                        members = "; ".join(
                            str(item) for item in synset.get("members") or []
                        )
                        sense_rows.append(
                            (
                                lemma,
                                str(pos),
                                str(sense.get("id") or ""),
                                str(synset_id),
                                definition,
                                example,
                                members,
                            )
                        )
                        if len(sense_rows) >= 50_000:
                            cursor.executemany(
                                """INSERT INTO wordnet_senses
                                   VALUES (?, ?, ?, ?, ?, ?, ?)""",
                                sense_rows,
                            )
                            sense_count += len(sense_rows)
                            sense_rows.clear()
                if len(form_rows) >= 50_000:
                    cursor.executemany(
                        "INSERT OR REPLACE INTO wordnet_forms VALUES (?, ?)",
                        form_rows,
                    )
                    form_count += len(form_rows)
                    form_rows.clear()
        if sense_rows:
            cursor.executemany(
                """INSERT INTO wordnet_senses
                   VALUES (?, ?, ?, ?, ?, ?, ?)""",
                sense_rows,
            )
            sense_count += len(sense_rows)
        if form_rows:
            cursor.executemany(
                "INSERT OR REPLACE INTO wordnet_forms VALUES (?, ?)",
                form_rows,
            )
            form_count += len(form_rows)
    conn.commit()
    return sense_count, form_count


def count_table(conn: sqlite3.Connection, table: str) -> int:
    return int(conn.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0])


def lookup_word(conn: sqlite3.Connection, word: str) -> dict[str, object]:
    exact = conn.execute(
        """SELECT word, phonetic, translation, definition, pos, collins, oxford, tag
           FROM ecdict_entries WHERE word = ? LIMIT 1""",
        (word,),
    ).fetchone()
    sw_hits = conn.execute(
        """SELECT word, translation, bnc, frq
           FROM ecdict_entries
           WHERE sw = ?
           ORDER BY COALESCE(bnc, 999999999), COALESCE(frq, 999999999)
           LIMIT 5""",
        (strip_word(word),),
    ).fetchall()
    oewn_hits = conn.execute(
        """SELECT s.lemma, s.pos, s.definition, s.example
           FROM wordnet_senses s
           WHERE s.lemma = ?
           ORDER BY s.pos, s.sense_id
           LIMIT 5""",
        (word,),
    ).fetchall()
    oewn_form = conn.execute(
        """SELECT lemma FROM wordnet_forms WHERE form = ? LIMIT 1""",
        (word,),
    ).fetchone()
    if oewn_form and not oewn_hits:
        oewn_hits = conn.execute(
            """SELECT s.lemma, s.pos, s.definition, s.example
               FROM wordnet_senses s
               WHERE s.lemma = ?
               ORDER BY s.pos, s.sense_id
               LIMIT 5""",
            (oewn_form[0],),
        ).fetchall()

    return {
        "word": word,
        "ecdict_exact": bool(exact),
        "ecdict_sw": bool(sw_hits),
        "oewn": bool(oewn_hits),
        "ecdict_exact_row": tuple(exact) if exact else None,
        "ecdict_sw_rows": [tuple(row) for row in sw_hits],
        "oewn_rows": [tuple(row) for row in oewn_hits],
    }


def build_report(
    db_path: Path,
    ecdict_path: Path,
    oewn_path: Path,
    words: list[str],
) -> dict[str, object]:
    conn = sqlite3.connect(db_path)
    conn.row_factory = sqlite3.Row
    ecdict_count = count_table(conn, "ecdict_entries")
    wordnet_senses = count_table(conn, "wordnet_senses")
    wordnet_forms = count_table(conn, "wordnet_forms")
    lookups = [lookup_word(conn, word) for word in words]
    conn.close()

    return {
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "inputs": {
            "ecdict": {
                "file": ecdict_path.name,
                "bytes": ecdict_path.stat().st_size,
                "sha256": sha256(ecdict_path),
                "license": "MIT (see ECDICT-LICENSE.txt)",
                "source": "https://github.com/skywind3000/ECDICT",
            },
            "oewn": {
                "file": oewn_path.name,
                "bytes": oewn_path.stat().st_size,
                "sha256": sha256(oewn_path),
                "license": "CC BY 4.0",
                "source": "https://en-word.net/downloads/english-wordnet-2025-json.zip",
            },
        },
        "database": {
            "file": db_path.name,
            "bytes": db_path.stat().st_size,
            "sha256": sha256(db_path),
            "ecdict_entries": ecdict_count,
            "wordnet_senses": wordnet_senses,
            "wordnet_forms": wordnet_forms,
        },
        "sample_lookups": lookups,
        "coverage": {
            "total_words": len(words),
            "ecdict_exact": sum(1 for item in lookups if item["ecdict_exact"]),
            "ecdict_sw": sum(1 for item in lookups if item["ecdict_sw"]),
            "oewn": sum(1 for item in lookups if item["oewn"]),
            "combined": sum(
                1 for item in lookups if item["ecdict_exact"] or item["oewn"]
            ),
        },
    }


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Build a minimal validation SQLite database from ECDICT and OEWN."
    )
    parser.add_argument(
        "--ecdict",
        type=Path,
        required=True,
        help="Path to ecdict.csv",
    )
    parser.add_argument(
        "--oewn",
        type=Path,
        required=True,
        help="Path to OEWN 2025 JSON zip",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=Path(".build-output/word-dict-mini/word-dictionary-mini.db"),
        help="Output SQLite path",
    )
    parser.add_argument(
        "--report",
        type=Path,
        default=None,
        help="Output JSON report path (default: output path with .report.json)",
    )
    parser.add_argument(
        "--words",
        default=",".join(SAMPLE_WORDS),
        help="Comma-separated sample words for coverage validation",
    )
    args = parser.parse_args()

    if not args.ecdict.is_file() or not args.oewn.is_file():
        print("ECDICT CSV or OEWN JSON zip is missing.", file=sys.stderr)
        return 2

    args.output.parent.mkdir(parents=True, exist_ok=True)
    report_path = args.report or args.output.with_name(
        args.output.stem + ".report.json"
    )
    words = [word.strip() for word in args.words.split(",") if word.strip()]

    started = time.monotonic()
    conn = sqlite3.connect(args.output)
    try:
        conn.executescript(
            """
            DROP TABLE IF EXISTS ecdict_entries;
            DROP TABLE IF EXISTS wordnet_senses;
            DROP TABLE IF EXISTS wordnet_forms;
            DROP TABLE IF EXISTS dictionary_meta;
            """
        )
        conn.executescript(SCHEMA)
        ecdict_count = import_ecdict(conn, args.ecdict)
        sense_count, form_count = import_oewn(conn, args.oewn)
        conn.execute(
            "INSERT OR REPLACE INTO dictionary_meta VALUES ('built_at', ?)",
            (datetime.now(timezone.utc).isoformat(),),
        )
        conn.commit()
    finally:
        conn.close()

    report = build_report(args.output, args.ecdict, args.oewn, words)
    report["import"] = {
        "ecdict_inserted": ecdict_count,
        "wordnet_sense_rows": sense_count,
        "wordnet_form_rows": form_count,
        "duration_seconds": round(time.monotonic() - started, 2),
    }
    report_path.write_text(
        json.dumps(report, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )

    print(f"database: {args.output} ({report['database']['bytes']} bytes)")
    print(
        "rows: ecdict={} wordnet_senses={} wordnet_forms={}".format(
            report["database"]["ecdict_entries"],
            report["database"]["wordnet_senses"],
            report["database"]["wordnet_forms"],
        )
    )
    print(
        "sample coverage: exact={} sw={} oewn={} combined={}/{}".format(
            report["coverage"]["ecdict_exact"],
            report["coverage"]["ecdict_sw"],
            report["coverage"]["oewn"],
            report["coverage"]["combined"],
            report["coverage"]["total_words"],
        )
    )
    print(f"report: {report_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
