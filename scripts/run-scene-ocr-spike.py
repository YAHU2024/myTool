#!/usr/bin/env python3
"""Run a local RapidOCR/ONNX scene-OCR comparison over private fixtures.

The JSON report intentionally contains only hashes and metrics. Full OCR text is
written only when --preview-directory is explicitly supplied, alongside an
annotated image for local inspection.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import pathlib
import re
import statistics
import time
import unicodedata
from typing import Any


def normalize(text: str | None) -> str:
    if not text or not text.strip():
        return ""
    collapsed = re.sub(r"\s+", " ", text).strip()
    chars = list(collapsed)
    result: list[str] = []
    for index, char in enumerate(chars):
        if char != " ":
            result.append(char)
            continue
        previous = next((chars[i] for i in range(index - 1, -1, -1) if chars[i] != " "), None)
        following = next((chars[i] for i in range(index + 1, len(chars)) if chars[i] != " "), None)
        if previous is None or following is None:
            continue
        if is_cjk(previous) or is_cjk(following) or is_cjk_punctuation(previous) or is_cjk_punctuation(following):
            continue
        result.append(" ")
    return "".join(result).strip()


def is_cjk(char: str) -> bool:
    code = ord(char)
    return (
        0x3400 <= code <= 0x4DBF
        or 0x4E00 <= code <= 0x9FFF
        or 0xF900 <= code <= 0xFAFF
        or 0x20000 <= code <= 0x2EBEF
        or 0x3040 <= code <= 0x30FF
        or 0x31F0 <= code <= 0x31FF
        or 0xAC00 <= code <= 0xD7AF
    )


def is_cjk_punctuation(char: str) -> bool:
    return char in "。，：；！？、．（）【】「」『』《》〈〉“”‘’"


def levenshtein(first: str, second: str) -> int:
    if len(first) < len(second):
        first, second = second, first
    previous = list(range(len(second) + 1))
    for first_index, first_char in enumerate(first, 1):
        current = [first_index]
        for second_index, second_char in enumerate(second, 1):
            current.append(
                min(
                    current[-1] + 1,
                    previous[second_index] + 1,
                    previous[second_index - 1] + (first_char != second_char),
                )
            )
        previous = current
    return previous[-1]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture-directory", required=True, type=pathlib.Path)
    parser.add_argument("--output-path", default=pathlib.Path(".m3-scene-output/rapidocr-report.json"), type=pathlib.Path)
    parser.add_argument("--preview-directory", type=pathlib.Path)
    return parser.parse_args()


def write_preview(source: pathlib.Path, output_dir: pathlib.Path, output: Any) -> None:
    from PIL import Image, ImageDraw, ImageFont

    output_dir.mkdir(parents=True, exist_ok=True)
    image_path = output_dir / f"{source.stem}.scene-ocr-preview.png"
    text_path = output_dir / f"{source.stem}.scene-ocr.txt"
    with Image.open(source) as image:
        canvas = image.convert("RGBA")
        draw = ImageDraw.Draw(canvas)
        font = None
        for candidate in (r"C:\Windows\Fonts\msyh.ttc", r"C:\Windows\Fonts\segoeui.ttf"):
            try:
                font = ImageFont.truetype(candidate, 16)
                break
            except OSError:
                continue
        boxes = output.boxes.tolist() if output.boxes is not None else []
        texts = list(output.txts) if output.txts is not None else []
        scores = list(output.scores) if output.scores is not None else []
        lines = [
            f"# OCR preview: {source.name}",
            f"# engine: RapidOCR; blocks: {len(texts)}",
            "# columns: index<TAB>points<TAB>confidence<TAB>text",
        ]
        for index, (points, text, score) in enumerate(zip(boxes, texts, scores), 1):
            polygon = [(int(point[0]), int(point[1])) for point in points]
            if len(polygon) < 4:
                continue
            draw.line(polygon + [polygon[0]], fill=(255, 0, 0, 255), width=3, joint="curve")
            label = re.sub(r"\s+", " ", str(text)).strip()
            if len(label) > 120:
                label = label[:120] + "..."
            left = max(0, min(point[0] for point in polygon))
            top = max(0, min(point[1] for point in polygon))
            if font is not None:
                bbox = draw.textbbox((left, top), label, font=font)
                draw.rectangle(bbox, fill=(0, 0, 0, 210))
                draw.text((left, top), label or "(空文本)", fill=(255, 255, 0, 255), font=font)
            lines.append(f"{index:04d}\t{polygon}\t{float(score):.6f}\t{text}")
        canvas.convert("RGB").save(image_path, "PNG")
    text_path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    args = parse_args()
    if not args.fixture_directory.is_dir():
        raise SystemExit(f"Fixture directory does not exist: {args.fixture_directory}")

    try:
        import onnxruntime as ort
        from PIL import Image
        from rapidocr import RapidOCR
    except ImportError as error:
        raise SystemExit(
            "RapidOCR Spike dependencies are missing. Install rapidocr and onnxruntime in the isolated environment."
        ) from error

    engine = RapidOCR()
    files = sorted(
        path
        for path in args.fixture_directory.iterdir()
        if path.is_file() and path.suffix.lower() in {".png", ".jpg", ".jpeg", ".bmp"}
    )
    rows: list[dict[str, Any]] = []
    for path in files:
        expected_path = path.with_suffix(".txt")
        expected = normalize(expected_path.read_text(encoding="utf-8-sig")) if expected_path.exists() else None
        digest = hashlib.sha256(path.read_bytes()).hexdigest().upper()
        started = time.perf_counter()
        try:
            output = engine(str(path))
            elapsed_ms = (time.perf_counter() - started) * 1000
            with Image.open(path) as image:
                width, height = image.size
            raw_texts = list(output.txts) if output.txts is not None else []
            boxes = output.boxes.tolist() if output.boxes is not None else []
            scores = [float(score) for score in (list(output.scores) if output.scores is not None else [])]
            if len(boxes) != len(raw_texts) or len(boxes) != len(scores):
                raise ValueError("RapidOCR 输出的框、文本和置信度数量不一致。")
            entries = [(box, str(text), score) for box, text, score in zip(boxes, raw_texts, scores)]
            texts = [text for _, text, _ in entries if text.strip()]
            invalid_bounds = 0
            for polygon in boxes:
                if any(point[0] < 0 or point[1] < 0 or point[0] > width or point[1] > height for point in polygon):
                    invalid_bounds += 1
            ordered = sorted(
                (entry for entry in entries if entry[1].strip()),
                key=lambda item: (min(point[1] for point in item[0]), min(point[0] for point in item[0])),
            )
            recognized = normalize("\n".join(item[1] for item in ordered))
            distance = levenshtein(expected, recognized) if expected is not None else None
            if args.preview_directory is not None:
                write_preview(path, args.preview_directory, output)
            rows.append(
                {
                    "file": path.name,
                    "sha256": digest,
                    "width": width,
                    "height": height,
                    "block_count": len(texts),
                    "invalid_bbox_count": invalid_bounds,
                    "average_confidence": round(statistics.fmean(scores), 4) if scores else None,
                    "minimum_confidence": round(min(scores), 4) if scores else None,
                    "elapsed_ms": round(elapsed_ms, 2),
                    "engine_elapsed_ms": round(float(output.elapse) * 1000, 2) if output.elapse is not None else None,
                    "expected_text_present": expected is not None,
                    "expected_length": len(expected) if expected is not None else None,
                    "edit_distance": distance,
                    "character_error_rate": round(distance / max(1, len(expected)), 4) if distance is not None else None,
                    "status": "no_text" if not texts else "ok",
                    "error_type": None,
                }
            )
        except Exception as error:  # noqa: BLE001 - report only the exception type by design
            rows.append(
                {
                    "file": path.name,
                    "sha256": digest,
                    "width": None,
                    "height": None,
                    "block_count": None,
                    "invalid_bbox_count": None,
                    "average_confidence": None,
                    "minimum_confidence": None,
                    "elapsed_ms": round((time.perf_counter() - started) * 1000, 2),
                    "engine_elapsed_ms": None,
                    "expected_text_present": expected is not None,
                    "expected_length": len(expected) if expected is not None else None,
                    "edit_distance": None,
                    "character_error_rate": None,
                    "status": "error",
                    "error_type": type(error).__name__,
                }
            )

    args.output_path.parent.mkdir(parents=True, exist_ok=True)
    package_version = importlib.metadata.version("rapidocr")
    report = {
        "generated_at": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        "fixture_directory": str(args.fixture_directory.resolve()),
        "engine": "RapidOCR ONNX Runtime",
        "model_family": "PP-OCRv6 small",
        "package_version": package_version,
        "onnxruntime_version": ort.__version__,
        "execution_providers": ort.get_available_providers(),
        "fixture_count": len(rows),
        "rows": rows,
    }
    args.output_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    weighted_distance = sum(row["edit_distance"] or 0 for row in rows)
    weighted_length = sum(row["expected_length"] or 0 for row in rows)
    print(json.dumps({
        "engine": report["engine"],
        "model_family": report["model_family"],
        "fixture_count": report["fixture_count"],
        "execution_providers": report["execution_providers"],
        "weighted_character_error_rate": round(weighted_distance / max(1, weighted_length), 4),
        "median_elapsed_ms": round(statistics.median(row["elapsed_ms"] for row in rows), 2) if rows else None,
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
