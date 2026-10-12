#!/usr/bin/env python3
"""Run RapidOCR over private fixtures and evaluate JSON ground-truth metrics.

The JSON report intentionally contains only hashes and metrics. Full OCR text is
written only when --preview-directory is explicitly supplied, alongside an
annotated image for local inspection. A same-stem .json annotation enables
one-to-one polygon matching at the configured IoU threshold. Legacy .txt files
remain supported for the original text-only smoke fixtures.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import math
import pathlib
import re
import statistics
import time
import unicodedata
from typing import Any


Point = tuple[float, float]


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


def polygon_area(polygon: list[Point]) -> float:
    """Return the absolute shoelace area for a polygon."""
    if len(polygon) < 3:
        return 0.0
    return abs(
        sum(
            polygon[index][0] * polygon[(index + 1) % len(polygon)][1]
            - polygon[(index + 1) % len(polygon)][0] * polygon[index][1]
            for index in range(len(polygon))
        )
        / 2.0
    )


def signed_polygon_area(polygon: list[Point]) -> float:
    if len(polygon) < 3:
        return 0.0
    return sum(
        polygon[index][0] * polygon[(index + 1) % len(polygon)][1]
        - polygon[(index + 1) % len(polygon)][0] * polygon[index][1]
        for index in range(len(polygon))
    ) / 2.0


def cross(first: Point, second: Point, point: Point) -> float:
    return (second[0] - first[0]) * (point[1] - first[1]) - (second[1] - first[1]) * (point[0] - first[0])


def line_intersection(start: Point, end: Point, edge_start: Point, edge_end: Point) -> Point:
    """Intersect two lines; callers only use this for a crossing segment."""
    x1, y1 = start
    x2, y2 = end
    x3, y3 = edge_start
    x4, y4 = edge_end
    denominator = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4)
    if abs(denominator) < 1e-10:
        return end
    factor = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / denominator
    return (x1 + factor * (x2 - x1), y1 + factor * (y2 - y1))


def convex_polygon_intersection(subject: list[Point], clip: list[Point]) -> list[Point]:
    """Clip a quadrilateral/text polygon against another convex polygon."""
    if len(subject) < 3 or len(clip) < 3:
        return []
    clip_polygon = clip if signed_polygon_area(clip) >= 0 else list(reversed(clip))
    output = subject[:]
    for index, edge_start in enumerate(clip_polygon):
        edge_end = clip_polygon[(index + 1) % len(clip_polygon)]
        input_points = output
        output = []
        if not input_points:
            break
        previous = input_points[-1]
        previous_inside = cross(edge_start, edge_end, previous) >= -1e-8
        for current in input_points:
            current_inside = cross(edge_start, edge_end, current) >= -1e-8
            if current_inside:
                if not previous_inside:
                    output.append(line_intersection(previous, current, edge_start, edge_end))
                output.append(current)
            elif previous_inside:
                output.append(line_intersection(previous, current, edge_start, edge_end))
            previous = current
            previous_inside = current_inside
    return output


def polygon_iou(first: list[Point], second: list[Point]) -> float:
    first_area = polygon_area(first)
    second_area = polygon_area(second)
    if first_area <= 0 or second_area <= 0:
        return 0.0
    intersection = polygon_area(convex_polygon_intersection(first, second))
    union = first_area + second_area - intersection
    return intersection / union if union > 0 else 0.0


def percentile(values: list[float], percentage: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    if len(ordered) == 1:
        return ordered[0]
    position = (len(ordered) - 1) * percentage
    lower = int(position)
    upper = min(lower + 1, len(ordered) - 1)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def load_annotation(path: pathlib.Path) -> dict[str, Any] | None:
    annotation_path = path.with_suffix(".json")
    if not annotation_path.exists():
        return None
    document = json.loads(annotation_path.read_text(encoding="utf-8-sig"))
    if not isinstance(document, dict):
        raise ValueError("AnnotationNotObject")
    if document.get("schema") != "quicktranslate.ocr-fixture.v1":
        raise ValueError("SchemaMismatch")
    if document.get("fixture_id") != path.stem:
        raise ValueError("FixtureIdMismatch")
    blocks = document.get("blocks")
    if not isinstance(blocks, list):
        raise ValueError("BlocksNotArray")
    normalized_blocks = []
    for block in blocks:
        if not isinstance(block, dict):
            raise ValueError("BlockNotObject")
        polygon = block.get("polygon")
        if not isinstance(polygon, list) or len(polygon) < 3:
            raise ValueError("PolygonTooShort")
        points: list[Point] = []
        for point in polygon:
            if not isinstance(point, list) or len(point) != 2:
                raise ValueError("InvalidPolygonPoint")
            x, y = float(point[0]), float(point[1])
            if not math.isfinite(x) or not math.isfinite(y):
                raise ValueError("NonFinitePolygonPoint")
            points.append((x, y))
        normalized_blocks.append(
            {
                "text": str(block.get("text") or ""),
                "polygon": points,
                "reading_order": int(block.get("reading_order") or 0),
                "language_tag": str(block.get("language_tag") or "unknown"),
            }
        )
    normalized_blocks.sort(key=lambda block: block["reading_order"])
    return {
        "source_kind": str(document.get("source_kind") or "unknown"),
        "language_tags": [str(tag) for tag in document.get("language_tags", []) if tag],
        "blocks": normalized_blocks,
    }


def match_predictions(
    ground_truth: list[dict[str, Any]],
    predictions: list[tuple[list[Point], str, float]],
    threshold: float,
) -> list[tuple[int, int, float]]:
    candidates = sorted(
        [
            (polygon_iou(gt["polygon"], prediction[0]), gt_index, prediction_index)
            for gt_index, gt in enumerate(ground_truth)
            for prediction_index, prediction in enumerate(predictions)
            if polygon_iou(gt["polygon"], prediction[0]) >= threshold
        ],
        key=lambda item: (-item[0], item[1], item[2]),
    )
    used_gt: set[int] = set()
    used_predictions: set[int] = set()
    matches: list[tuple[int, int, float]] = []
    for score, gt_index, prediction_index in candidates:
        if gt_index in used_gt or prediction_index in used_predictions:
            continue
        used_gt.add(gt_index)
        used_predictions.add(prediction_index)
        matches.append((gt_index, prediction_index, score))
    return matches


def evaluate_annotation(
    annotation: dict[str, Any],
    predictions: list[tuple[list[Point], str, float]],
    low_confidence_threshold: float,
    iou_threshold: float,
) -> dict[str, Any]:
    ground_truth = annotation["blocks"]
    matches = match_predictions(ground_truth, predictions, iou_threshold)
    matched_gt = {item[0] for item in matches}
    matched_predictions = {item[1] for item in matches}
    ious = [item[2] for item in matches]
    gt_texts = [normalize(block["text"]) for block in ground_truth]
    character_count = sum(len(text) for text in gt_texts)
    character_error_distance = 0
    for gt_index, prediction_index, _ in matches:
        character_error_distance += levenshtein(gt_texts[gt_index], normalize(predictions[prediction_index][1]))
    character_error_distance += sum(len(gt_texts[index]) for index in range(len(ground_truth)) if index not in matched_gt)
    character_error_distance += sum(
        len(normalize(predictions[index][1])) for index in range(len(predictions)) if index not in matched_predictions
    )
    order_pairs = 0
    order_agreements = 0
    matched_in_prediction_order = sorted(matches, key=lambda item: item[1])
    for left_index, left in enumerate(matched_in_prediction_order):
        for right in matched_in_prediction_order[left_index + 1 :]:
            order_pairs += 1
            if left[0] < right[0]:
                order_agreements += 1
    prediction_count = len(predictions)
    low_confidence_count = sum(score < low_confidence_threshold for _, _, score in predictions)
    gt_count = len(ground_truth)
    return {
        "source_kind": annotation["source_kind"],
        "language_tags": annotation["language_tags"],
        "ground_truth_block_count": gt_count,
        "prediction_block_count": prediction_count,
        "matched_block_count": len(matches),
        "detection_recall": round(len(matches) / gt_count, 4) if gt_count else (1.0 if prediction_count == 0 else 0.0),
        "polygon_iou_median": round(statistics.median(ious), 4) if ious else None,
        "polygon_iou_p10": round(percentile(ious, 0.1), 4) if ious else None,
        "reading_order_pairwise_accuracy": round(order_agreements / order_pairs, 4) if order_pairs else None,
        "reading_order_pair_count": order_pairs,
        "reading_order_agreement_count": order_agreements,
        "character_count": character_count,
        "character_error_distance": character_error_distance,
        "character_error_rate": round(character_error_distance / max(1, character_count), 4) if character_count else None,
        "false_positive_block_count": prediction_count - len(matches),
        "low_confidence_threshold": low_confidence_threshold,
        "low_confidence_count": low_confidence_count,
        "low_confidence_ratio": round(low_confidence_count / prediction_count, 4) if prediction_count else None,
        "status": (
            "no_text"
            if gt_count == 0 and prediction_count == 0
            else "false_positive"
            if gt_count == 0
            else "ok"
        ),
    }


def aggregate_quality(rows: list[dict[str, Any]], group_key: str) -> list[dict[str, Any]]:
    groups: dict[str, list[dict[str, Any]]] = {}
    for row in rows:
        if row.get("annotation_present") is not True:
            continue
        values = row.get(group_key, []) if group_key == "language_tags" else [row.get(group_key)]
        for value in values:
            if value:
                groups.setdefault(str(value), []).append(row)

    result = []
    for value in sorted(groups):
        group_rows = groups[value]
        ious = [row["polygon_iou_median"] for row in group_rows if row.get("polygon_iou_median") is not None]
        all_iou_samples = [row.get("polygon_iou_p10") for row in group_rows if row.get("polygon_iou_p10") is not None]
        gt_count = sum(row.get("ground_truth_block_count") or 0 for row in group_rows)
        matched_count = sum(row.get("matched_block_count") or 0 for row in group_rows)
        characters = sum(row.get("character_count") or 0 for row in group_rows)
        errors = sum(row.get("character_error_distance") or 0 for row in group_rows)
        order_pairs = sum(row.get("reading_order_pair_count") or 0 for row in group_rows)
        order_agreements = sum(row.get("reading_order_agreement_count") or 0 for row in group_rows)
        predictions = sum(row.get("prediction_block_count") or 0 for row in group_rows)
        low_confidence = sum(row.get("low_confidence_count") or 0 for row in group_rows)
        result.append(
            {
                "group": value,
                "fixture_count": len(group_rows),
                "ground_truth_block_count": gt_count,
                "prediction_block_count": predictions,
                "matched_block_count": matched_count,
                "detection_recall": round(matched_count / gt_count, 4) if gt_count else None,
                "polygon_iou_median_of_fixture_medians": round(statistics.median(ious), 4) if ious else None,
                "polygon_iou_p10_of_fixture_p10": round(statistics.median(all_iou_samples), 4) if all_iou_samples else None,
                "reading_order_pairwise_accuracy": round(order_agreements / order_pairs, 4) if order_pairs else None,
                "character_count": characters,
                "character_error_distance": errors,
                "character_error_rate": round(errors / max(1, characters), 4) if characters else None,
                "false_positive_block_count": sum(row.get("false_positive_block_count") or 0 for row in group_rows),
                "low_confidence_ratio": round(low_confidence / predictions, 4) if predictions else None,
            }
        )
    return result


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture-directory", required=True, type=pathlib.Path)
    parser.add_argument("--output-path", default=pathlib.Path(".m3-scene-output/rapidocr-report.json"), type=pathlib.Path)
    parser.add_argument("--preview-directory", type=pathlib.Path)
    parser.add_argument("--iou-threshold", default=0.5, type=float, help="IoU threshold for one-to-one GT matching (default: 0.5)")
    parser.add_argument(
        "--low-confidence-threshold",
        default=0.5,
        type=float,
        help="Predictions below this confidence are counted as low confidence (default: 0.5)",
    )
    parser.add_argument(
        "--model-size",
        choices=("tiny", "small", "medium"),
        default="small",
        help="PP-OCRv6 model size; missing files may be downloaded by RapidOCR (default: small)",
    )
    parser.add_argument(
        "--det-model-path",
        type=pathlib.Path,
        help="Optional local detection model path; prevents RapidOCR from resolving a default model.",
    )
    parser.add_argument(
        "--rec-model-path",
        type=pathlib.Path,
        help="Optional local recognition model path; prevents RapidOCR from resolving a default model.",
    )
    parser.add_argument(
        "--cls-model-path",
        type=pathlib.Path,
        help="Optional local text-angle classifier model path.",
    )
    parser.add_argument(
        "--rec-keys-path",
        type=pathlib.Path,
        help="Optional local recognition dictionary path for a custom recognition model.",
    )
    parser.add_argument(
        "--disable-cls",
        action="store_true",
        help="Disable the optional text-angle classifier (useful for PP-OCRv6 det/rec-only bundles).",
    )
    parser.add_argument(
        "--model-label",
        help="Human-readable model label written to the report; defaults to PP-OCRv6 <model-size>.",
    )
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


def create_engine(
    rapid_ocr_type: Any,
    model_params: dict[str, Any],
    disable_cls: bool,
) -> Any:
    """Create RapidOCR without resolving an unlisted classifier in det/rec-only mode."""
    if not disable_cls:
        return rapid_ocr_type(params=model_params or None)

    # RapidOCR 3.9.2 constructs TextClassifier even when Global.use_cls is false.
    # Replace it only during construction so an official det/rec-only model test
    # cannot implicitly download the legacy default classifier.
    import rapidocr.main as rapidocr_main

    class NoopTextClassifier:
        def __init__(self, _config: Any) -> None:
            pass

    original_classifier = rapidocr_main.TextClassifier
    rapidocr_main.TextClassifier = NoopTextClassifier
    try:
        return rapid_ocr_type(params={**model_params, "Global.use_cls": False})
    finally:
        rapidocr_main.TextClassifier = original_classifier


def main() -> int:
    args = parse_args()
    if not args.fixture_directory.is_dir():
        raise SystemExit(f"Fixture directory does not exist: {args.fixture_directory}")
    if not 0 < args.iou_threshold <= 1:
        raise SystemExit("--iou-threshold must be greater than 0 and no greater than 1")
    if not 0 <= args.low_confidence_threshold <= 1:
        raise SystemExit("--low-confidence-threshold must be between 0 and 1")

    try:
        import onnxruntime as ort
        from PIL import Image
        from rapidocr import RapidOCR
        from rapidocr.utils.typings import ModelType
    except ImportError as error:
        raise SystemExit(
            "RapidOCR Spike dependencies are missing. Install rapidocr and onnxruntime in the isolated environment."
        ) from error

    model_params: dict[str, Any] = {}
    if args.model_size != "small":
        model_type = ModelType(args.model_size)
        model_params.update({"Det.model_type": model_type, "Rec.model_type": model_type})
    custom_paths = {
        "Det.model_path": args.det_model_path,
        "Rec.model_path": args.rec_model_path,
        "Cls.model_path": args.cls_model_path,
        "Rec.rec_keys_path": args.rec_keys_path,
    }
    for parameter, path in custom_paths.items():
        if path is None:
            continue
        if not path.is_file():
            raise SystemExit(f"{parameter} 指向的文件不存在：{path}")
        model_params[parameter] = str(path.resolve())
    engine = create_engine(RapidOCR, model_params, args.disable_cls)
    files = sorted(
        path
        for path in args.fixture_directory.iterdir()
        if path.is_file() and path.suffix.lower() in {".png", ".jpg", ".jpeg", ".bmp"}
    )
    rows: list[dict[str, Any]] = []
    for path in files:
        expected_path = path.with_suffix(".txt")
        expected = normalize(expected_path.read_text(encoding="utf-8-sig")) if expected_path.exists() else None
        annotation: dict[str, Any] | None = None
        annotation_error_type: str | None = None
        if path.with_suffix(".json").exists():
            try:
                annotation = load_annotation(path)
            except Exception as error:  # noqa: BLE001 - report only the exception type by design
                annotation_error_type = type(error).__name__
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
            entries = [
                ([(float(point[0]), float(point[1])) for point in box], str(text), score)
                for box, text, score in zip(boxes, raw_texts, scores)
            ]
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
            quality = (
                evaluate_annotation(annotation, [entry for entry in entries if entry[1].strip()], args.low_confidence_threshold, args.iou_threshold)
                if annotation is not None
                else {}
            )
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
                    "legacy_character_error_rate": round(distance / max(1, len(expected)), 4) if distance is not None else None,
                    "annotation_present": annotation is not None,
                    "annotation_error_type": annotation_error_type,
                    **quality,
                    "character_error_rate": quality.get(
                        "character_error_rate",
                        round(distance / max(1, len(expected)), 4) if distance is not None else None,
                    ),
                    "status": quality.get("status", "no_text" if not texts else "ok"),
                    "error_type": None if annotation_error_type is None else annotation_error_type,
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
                    "legacy_character_error_rate": None,
                    "annotation_present": annotation is not None,
                    "annotation_error_type": annotation_error_type,
                    "character_error_rate": None,
                    "status": "error",
                    "error_type": type(error).__name__,
                }
            )

    evaluated_rows = [row for row in rows if row.get("annotation_present") is True]
    args.output_path.parent.mkdir(parents=True, exist_ok=True)
    package_version = importlib.metadata.version("rapidocr")
    report = {
        "generated_at": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        "fixture_directory": str(args.fixture_directory.resolve()),
        "engine": "RapidOCR ONNX Runtime",
        "model_family": args.model_label or f"PP-OCRv6 {args.model_size}",
        "package_version": package_version,
        "onnxruntime_version": ort.__version__,
        "execution_providers": ort.get_available_providers(),
        "fixture_count": len(rows),
        "evaluation": {
            "ground_truth_schema": "quicktranslate.ocr-fixture.v1",
            "iou_threshold": args.iou_threshold,
            "low_confidence_threshold": args.low_confidence_threshold,
            "reading_order_definition": "pairwise relative-order agreement among matched blocks",
            "matching": "greedy one-to-one matching by descending polygon IoU",
        },
        "quality_overall": aggregate_quality(
            [{**row, "source_kind": "all"} for row in rows],
            "source_kind",
        )[0]
        if evaluated_rows
        else None,
        "quality_by_source_kind": aggregate_quality(rows, "source_kind"),
        "quality_by_language": aggregate_quality(rows, "language_tags"),
        "rows": rows,
    }
    args.output_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    weighted_distance = sum(row.get("character_error_distance") or 0 for row in evaluated_rows)
    weighted_length = sum(row.get("character_count") or 0 for row in evaluated_rows)
    matched_count = sum(row.get("matched_block_count") or 0 for row in evaluated_rows)
    ground_truth_count = sum(row.get("ground_truth_block_count") or 0 for row in evaluated_rows)
    predictions_count = sum(row.get("prediction_block_count") or 0 for row in evaluated_rows)
    low_confidence_count = sum(row.get("low_confidence_count") or 0 for row in evaluated_rows)
    iou_samples = [row["polygon_iou_median"] for row in evaluated_rows if row.get("polygon_iou_median") is not None]
    print(json.dumps({
        "engine": report["engine"],
        "model_family": report["model_family"],
        "fixture_count": report["fixture_count"],
        "execution_providers": report["execution_providers"],
        "annotation_fixture_count": len(evaluated_rows),
        "ground_truth_block_count": ground_truth_count,
        "matched_block_count": matched_count,
        "detection_recall": round(matched_count / ground_truth_count, 4) if ground_truth_count else None,
        "polygon_iou_median_of_fixture_medians": round(statistics.median(iou_samples), 4) if iou_samples else None,
        "weighted_character_error_rate": round(weighted_distance / max(1, weighted_length), 4) if evaluated_rows else None,
        "false_positive_block_count": sum(row.get("false_positive_block_count") or 0 for row in evaluated_rows),
        "low_confidence_ratio": round(low_confidence_count / predictions_count, 4) if predictions_count else None,
        "median_elapsed_ms": round(statistics.median(row["elapsed_ms"] for row in rows), 2) if rows else None,
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
