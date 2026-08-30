#!/usr/bin/env python3
"""Validate private M4 OCR fixture annotations without printing source text.

Each image must have a same-stem .json annotation following
quicktranslate.ocr-fixture.v1. The report contains only hashes, dimensions,
counts, and error types. Use --strict in CI or before a baseline review.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import sys
from typing import Any


IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".bmp", ".webp"}
SOURCE_KINDS = {"comic", "poster", "ui", "web", "pdf", "terminal", "no_text"}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture-directory", required=True, type=pathlib.Path)
    parser.add_argument("--output-path", default=pathlib.Path(".m4-fixture-output/fixture-validation.json"), type=pathlib.Path)
    parser.add_argument("--strict", action="store_true")
    return parser.parse_args()


def sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def error(code: str) -> dict[str, str]:
    return {"error_type": code}


def validate_annotation(image: pathlib.Path, annotation_path: pathlib.Path) -> tuple[dict[str, Any], list[dict[str, str]]]:
    errors: list[dict[str, str]] = []
    try:
        document = json.loads(annotation_path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        return {"file": image.name, "sha256": sha256(image), "status": "error"}, [error(type(exc).__name__)]

    if not isinstance(document, dict):
        errors.append(error("AnnotationNotObject"))
        document = {}
    if document.get("schema") != "quicktranslate.ocr-fixture.v1":
        errors.append(error("SchemaMismatch"))
    if document.get("fixture_id") != image.stem:
        errors.append(error("FixtureIdMismatch"))
    source_kind = document.get("source_kind")
    if source_kind not in SOURCE_KINDS:
        errors.append(error("InvalidSourceKind"))
    width = document.get("width")
    height = document.get("height")
    if not isinstance(width, int) or width <= 0 or not isinstance(height, int) or height <= 0:
        errors.append(error("InvalidImageDimensions"))
        width = height = 0
    if document.get("image_file") != image.name:
        errors.append(error("ImageFileMismatch"))

    blocks = document.get("blocks")
    if not isinstance(blocks, list):
        errors.append(error("BlocksNotArray"))
        blocks = []
    ids: set[str] = set()
    orders: list[int] = []
    for block in blocks:
        if not isinstance(block, dict):
            errors.append(error("BlockNotObject"))
            continue
        block_id = block.get("block_id")
        if not isinstance(block_id, str) or not block_id:
            errors.append(error("BlockIdMissing"))
        elif block_id in ids:
            errors.append(error("DuplicateBlockId"))
        else:
            ids.add(block_id)
        order = block.get("reading_order")
        if not isinstance(order, int) or order < 1:
            errors.append(error("InvalidReadingOrder"))
        else:
            orders.append(order)
        polygon = block.get("polygon")
        if not isinstance(polygon, list) or len(polygon) < 4:
            errors.append(error("PolygonTooShort"))
            continue
        for point in polygon:
            if not isinstance(point, list) or len(point) != 2:
                errors.append(error("InvalidPolygonPoint"))
                continue
            x, y = point
            if not isinstance(x, (int, float)) or not isinstance(y, (int, float)):
                errors.append(error("NonNumericPolygonPoint"))
            elif not (0 <= x <= width and 0 <= y <= height):
                errors.append(error("PolygonOutOfBounds"))

    if orders and sorted(orders) != list(range(1, len(orders) + 1)):
        errors.append(error("ReadingOrderNotContiguous"))
    if source_kind == "no_text" and blocks:
        errors.append(error("NoTextHasBlocks"))

    return {
        "file": image.name,
        "sha256": sha256(image),
        "width": width,
        "height": height,
        "source_kind": source_kind,
        "block_count": len(blocks),
        "status": "ok" if not errors else "error",
        "error_types": sorted({item["error_type"] for item in errors}),
    }, errors


def main() -> int:
    args = parse_args()
    if not args.fixture_directory.is_dir():
        print("Fixture directory does not exist.", file=sys.stderr)
        return 2

    rows: list[dict[str, Any]] = []
    missing_annotations = 0
    for image in sorted(path for path in args.fixture_directory.iterdir() if path.is_file() and path.suffix.lower() in IMAGE_EXTENSIONS):
        annotation = image.with_suffix(".json")
        if not annotation.exists():
            missing_annotations += 1
            rows.append({"file": image.name, "sha256": sha256(image), "status": "error", "error_types": ["AnnotationMissing"]})
            continue
        row, _ = validate_annotation(image, annotation)
        rows.append(row)

    report = {
        "schema": "quicktranslate.ocr-fixture-validation.v1",
        "fixture_directory": str(args.fixture_directory.resolve()),
        "fixture_count": len(rows),
        "valid_count": sum(row.get("status") == "ok" for row in rows),
        "error_count": sum(row.get("status") != "ok" for row in rows),
        "missing_annotation_count": missing_annotations,
        "rows": rows,
    }
    args.output_path.parent.mkdir(parents=True, exist_ok=True)
    args.output_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: report[key] for key in ("fixture_count", "valid_count", "error_count", "missing_annotation_count")}, ensure_ascii=False, indent=2))
    return 1 if args.strict and report["error_count"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
