#!/usr/bin/env python3
"""Line-oriented local OCR worker for the RapidOCR ONNX spike.

The parent process sends one JSON request per line. OCR text is returned to the
caller, but this worker never logs image data, OCR text, or exception messages.
RapidOCR initialization output is redirected to stderr so stdout remains a
strict JSON protocol.
"""

from __future__ import annotations

import base64
import contextlib
import json
import sys
from typing import Any


def response(request_id: str | None, **values: Any) -> None:
    payload = {"request_id": request_id, **values}
    sys.stdout.write(json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def create_engine() -> Any:
    from rapidocr import RapidOCR

    # RapidOCR logs model resolution during construction. Keep the protocol
    # stream clean without persisting those messages anywhere.
    with contextlib.redirect_stdout(sys.stderr):
        return RapidOCR()


def handle(engine: Any, request: dict[str, Any]) -> None:
    request_id = request.get("request_id")
    if request.get("operation") == "shutdown":
        response(request_id, status="ok")
        raise SystemExit(0)
    if request.get("operation") != "recognize":
        response(request_id, status="error", error_type="InvalidOperation")
        return

    try:
        import cv2
        import numpy as np

        width = int(request["width"])
        height = int(request["height"])
        stride = int(request["stride"])
        if width <= 0 or height <= 0 or stride < width * 4:
            raise ValueError("InvalidImageShape")
        encoded = request["bgra_base64"]
        payload = base64.b64decode(encoded, validate=True)
        expected = stride * height
        if len(payload) != expected:
            raise ValueError("InvalidImagePayload")

        rows = np.frombuffer(payload, dtype=np.uint8).reshape((height, stride))
        bgra = rows[:, : width * 4].reshape((height, width, 4))
        bgr = cv2.cvtColor(bgra, cv2.COLOR_BGRA2BGR)
        result = engine(bgr)
        boxes = result.boxes.tolist() if result.boxes is not None else []
        texts = list(result.txts) if result.txts is not None else []
        scores = list(result.scores) if result.scores is not None else []
        if len(boxes) != len(texts) or len(boxes) != len(scores):
            raise ValueError("OutputCountMismatch")

        entries = []
        for polygon, text, score in zip(boxes, texts, scores):
            text = str(text).strip()
            if not text:
                continue
            points = [
                {"x": float(point[0]), "y": float(point[1])}
                for point in polygon
            ]
            entries.append((points, text, float(score)))
        entries.sort(
            key=lambda entry: (
                min(point["y"] for point in entry[0]),
                min(point["x"] for point in entry[0]),
            )
        )

        blocks = []
        for index, (polygon, text, score) in enumerate(entries, 1):
            left = max(0, int(min(point["x"] for point in polygon)))
            top = max(0, int(min(point["y"] for point in polygon)))
            right = min(width, int(max(point["x"] for point in polygon) + 1))
            bottom = min(height, int(max(point["y"] for point in polygon) + 1))
            if right <= left or bottom <= top:
                continue
            blocks.append(
                {
                    "block_id": f"b{index:04d}",
                    "text": text,
                    "confidence": score,
                    "polygon": polygon,
                    "bounds": {"x": left, "y": top, "width": right - left, "height": bottom - top},
                }
            )

        response(
            request_id,
            status="ok",
            used_language_tag=str(request.get("language_hint") or "multi"),
            language_fallback_used=False,
            text_angle_degrees=0.0,
            elapsed_ms=float(result.elapse * 1000),
            blocks=blocks,
        )
    except Exception as error:  # noqa: BLE001 - only type crosses the protocol
        response(request_id, status="error", error_type=type(error).__name__)


def main() -> int:
    try:
        engine = create_engine()
    except Exception as error:  # noqa: BLE001 - only type crosses the protocol
        response(None, kind="ready", status="error", error_type=type(error).__name__)
        return 1

    response(None, kind="ready", status="ok", engine="rapidocr-onnx", model_family="PP-OCRv6 small")
    for line in sys.stdin:
        if not line.strip():
            continue
        try:
            request = json.loads(line)
            if not isinstance(request, dict):
                response(None, status="error", error_type="InvalidRequest")
                continue
            handle(engine, request)
        except SystemExit:
            return 0
        except Exception as error:  # noqa: BLE001 - keep protocol alive and redact details
            response(None, status="error", error_type=type(error).__name__)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
