#!/usr/bin/env python3
"""Local comic OCR routing and optional LaMa cleanup; no translation API calls.

The report is text-free. --preview-directory explicitly exports private images
and OCR text. Source language is supplied by the caller, never inferred from GT.
Use the isolated Comic Translate environment, not the system Python.
"""
from __future__ import annotations

import argparse
import contextlib
import hashlib
import importlib.metadata
import json
import math
import pathlib
import subprocess
import sys
import time


REVISION = "8f13ae5c4bab567c12b9383f085ba32d98b43348"
ROUTES = {"en": "ppocr-v5-en", "zh": "ppocr-v6-small-ch", "ja": "manga-ocr-base"}
EXTENSIONS = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def crop_bounds(bounds, width, height):
    if len(bounds) != 4 or not all(math.isfinite(float(v)) for v in bounds):
        raise ValueError("InvalidBounds")
    x1, y1, x2, y2 = (int(round(float(v))) for v in bounds)
    box = (max(0, x1), max(0, y1), min(width, x2), min(height, y2))
    if box[2] <= box[0] or box[3] <= box[1]:
        raise ValueError("EmptyCrop")
    return box


def select_images(directory, patterns):
    images = sorted({p for pattern in patterns for p in directory.glob(pattern)
                     if p.is_file() and p.suffix.lower() in EXTENSIONS})
    if not images:
        raise ValueError("NoImagesSelected")
    return images


def block_metadata(block, index, width, height, route):
    return {
        "block_id": f"b{index:04d}",
        "bounds": crop_bounds(block.xyxy, width, height),
        "region_type": str(block.text_class),
        "direction": str(block.direction),
        "orientation_degrees": float(block.angle),
        "engine": route,
        "text_length": len(block.text or ""),
        "nonempty": bool((block.text or "").strip()),
    }


class LocalEngines:
    def __init__(self, language, japanese_directory, allow_download):
        from modules.utils.download import ModelDownloader, ModelID

        self.language = language
        self.japanese_directory = japanese_directory
        self.recognizer = None
        self.inpainter = None
        self.model_files = {}
        self.initialization_ms = {}
        self.providers = {}
        self._original_get = ModelDownloader.__dict__["get"]
        original = ModelDownloader.get

        def guarded_get(cls, model):
            spec = cls.registry[model] if isinstance(model, ModelID) else model
            if spec.id.value not in self.model_files:
                if not cls.is_downloaded(model):
                    if not allow_download:
                        raise FileNotFoundError("ModelMissingOrInvalid")
                    original(model)
                if not cls.is_downloaded(model):
                    raise ValueError("ModelIntegrityFailure")
                files = []
                for name in spec.files:
                    local = (spec.save_as or {}).get(name, name)
                    path = pathlib.Path(spec.save_dir) / local
                    files.append({"file": local, "sha256": digest(path), "bytes": path.stat().st_size})
                self.model_files[spec.id.value] = {"source": spec.url, "files": files}

        ModelDownloader.get = classmethod(guarded_get)

    def close(self):
        from modules.utils.download import ModelDownloader
        ModelDownloader.get = self._original_get

    def recognize(self, image, blocks):
        if not blocks:
            return
        if self.recognizer is None:
            started = time.perf_counter()
            if self.language == "ja":
                from manga_ocr import MangaOcr
                if self.japanese_directory is None or not self.japanese_directory.is_dir():
                    raise FileNotFoundError("JapaneseLocalModelRequired")
                files = [{"file": p.name, "sha256": digest(p), "bytes": p.stat().st_size}
                         for p in sorted(self.japanese_directory.iterdir()) if p.is_file()]
                self.model_files["manga-ocr-base"] = {"source": "explicit-local-directory", "files": files}
                self.recognizer = MangaOcr(str(self.japanese_directory.resolve()), force_cpu=True)
                self.providers["ocr"] = ["PyTorch CPU"]
            else:
                from modules.ocr.ppocr.engine import PPOCRv5Engine
                self.recognizer = PPOCRv5Engine()
                self.recognizer.initialize("en" if self.language == "en" else "ch", "cpu")
                self.providers["ocr"] = self.recognizer.rec_sess.get_providers()
            self.initialization_ms["ocr"] = round((time.perf_counter() - started) * 1000, 2)
        for block in blocks:
            block.source_lang = self.language
        if self.language == "ja":
            from PIL import Image
            for block in blocks:
                x1, y1, x2, y2 = crop_bounds(block.xyxy, image.shape[1], image.shape[0])
                block.text = self.recognizer(Image.fromarray(image[y1:y2, x1:x2]))
        else:
            self.recognizer.process_image(image, blocks)

    def clean(self, image, blocks):
        import numpy as np
        from modules.utils.image_utils import generate_mask
        from modules.inpainting.lama import LaMa
        from modules.inpainting.schema import Config, HDStrategy
        mask = generate_mask(image, [b for b in blocks if (b.text or "").strip()])
        if not np.any(mask):
            return image.copy(), mask, "skipped_empty_mask"
        if self.inpainter is None:
            started = time.perf_counter()
            self.inpainter = LaMa("cpu", backend="onnx")
            self.providers["inpainting"] = self.inpainter.session.get_providers()
            self.initialization_ms["inpainting"] = round((time.perf_counter() - started) * 1000, 2)
        config = Config(hd_strategy=HDStrategy.RESIZE, hd_strategy_resize_limit=1024)
        cleaned = self.inpainter(image.copy(), mask, config).astype("uint8")
        if cleaned.shape != image.shape or np.any(cleaned[mask == 0] != image[mask == 0]):
            raise ValueError("UnmaskedPixelsChanged")
        return cleaned, mask, "applied"


def parse_args():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", type=pathlib.Path, required=True)
    parser.add_argument("--fixture-directory", type=pathlib.Path, required=True)
    parser.add_argument("--output-path", type=pathlib.Path, required=True)
    parser.add_argument("--source-language", choices=ROUTES, required=True)
    parser.add_argument("--include", action="append", help="Repeat file glob; default comic-*.jpg")
    parser.add_argument("--japanese-model-directory", type=pathlib.Path)
    parser.add_argument("--allow-model-download", action="store_true")
    parser.add_argument("--preview-directory", type=pathlib.Path)
    parser.add_argument("--inpaint", action="store_true")
    args = parser.parse_args()
    if args.inpaint and args.preview_directory is None:
        parser.error("--inpaint requires --preview-directory for visual review")
    return args


def run(args):
    import numpy as np
    import psutil
    from PIL import Image, ImageDraw
    revision = subprocess.check_output(["git", "-C", str(args.repository), "rev-parse", "HEAD"], text=True).strip()
    if revision != REVISION:
        raise ValueError("UnreviewedRepositoryRevision")
    dirty = subprocess.check_output(["git", "-C", str(args.repository), "diff", "HEAD", "--"], text=True)
    if dirty:
        raise ValueError("ModifiedUpstreamSource")
    images = select_images(args.fixture_directory, args.include or ["comic-*.jpg"])
    if args.output_path.exists():
        raise FileExistsError("ChooseNewReportPath")
    if args.preview_directory:
        args.preview_directory.mkdir(parents=True, exist_ok=False)
    sys.path.insert(0, str(args.repository.resolve()))
    engines = LocalEngines(args.source_language, args.japanese_model_directory, args.allow_model_download)
    rows = []
    started = time.perf_counter()
    try:
        from modules.detection.rtdetr_v2_onnx import RTDetrV2ONNXDetection
        detector = RTDetrV2ONNXDetection()
        detector.initialize("cpu", 0.3)
        engines.initialization_ms["detector"] = round((time.perf_counter() - started) * 1000, 2)
        engines.providers["detector"] = detector.session.get_providers()
        for path in images:
            row = {"sha256": digest(path), "source_language": args.source_language, "route": ROUTES[args.source_language]}
            stage = "decode"
            try:
                with Image.open(path) as source:
                    source.load()
                    rgb = source.convert("RGB")
                image = np.asarray(rgb)
                row.update(width=rgb.width, height=rgb.height)
                stage = "detect"
                t = time.perf_counter()
                blocks = detector.detect(image)
                row["detection_ms"] = round((time.perf_counter() - t) * 1000, 2)
                stage = "ocr"
                t = time.perf_counter()
                engines.recognize(image, blocks)
                row["ocr_ms_including_first_load"] = round((time.perf_counter() - t) * 1000, 2)
                row["blocks"] = [block_metadata(b, i, rgb.width, rgb.height, ROUTES[args.source_language])
                                 for i, b in enumerate(blocks, 1)]
                row["status"] = "ok" if blocks else "no_text"
                if args.preview_directory:
                    stage = "preview"
                    prefix = args.preview_directory / row["sha256"][:16]
                    preview = rgb.copy()
                    draw = ImageDraw.Draw(preview)
                    for i, b in enumerate(blocks, 1):
                        box = crop_bounds(b.xyxy, rgb.width, rgb.height)
                        draw.rectangle(box, outline="red", width=3)
                        draw.text(box[:2], str(i), fill="red")
                    preview.save(str(prefix) + ".boxes.png")
                    pathlib.Path(str(prefix) + ".ocr.json").write_text(json.dumps(
                        [{**m, "text": b.text} for m, b in zip(row["blocks"], blocks)], ensure_ascii=False, indent=2), encoding="utf-8")
                if args.inpaint:
                    stage = "inpaint"
                    t = time.perf_counter()
                    cleaned, mask, status = engines.clean(image, blocks)
                    row["inpainting"] = {"status": status, "ms_including_first_load": round((time.perf_counter()-t)*1000, 2),
                                         "mask_pixels": int(np.count_nonzero(mask)), "unmasked_changed_pixels": 0,
                                         "mask_correctness": "requires_visual_review"}
                    Image.fromarray(mask).save(str(prefix) + ".mask.png")
                    Image.fromarray(cleaned).save(str(prefix) + ".cleaned.png")
                    if max(rgb.size) <= 2200:
                        pair = Image.new("RGB", (rgb.width * 2, rgb.height))
                        pair.paste(rgb); pair.paste(Image.fromarray(cleaned), (rgb.width, 0))
                        pair.save(str(prefix) + ".before-after.png")
            except Exception as exc:
                row.update(status="error", failure_stage=stage, error_type=type(exc).__name__)
            rows.append(row)
            print(json.dumps({k: row[k] for k in ("sha256", "status")}), flush=True)
        mem = psutil.Process().memory_info()
        report = {"schema": "quicktranslate.comic-detection-ocr-spike.v2", "upstream_revision": revision,
                  "routing": "explicit-page-language-no-GT-routing", "device": "cpu", "providers": engines.providers,
                  "versions": {p: importlib.metadata.version(p) for p in ("numpy", "Pillow", "onnxruntime")},
                  "models": engines.model_files, "initialization_ms": engines.initialization_ms,
                  "peak_working_set_bytes": getattr(mem, "peak_wset", None),
                  "quality_gate": "not_evaluated", "translation_performed": False, "rows": rows}
        args.output_path.parent.mkdir(parents=True, exist_ok=True)
        args.output_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        errors = sum(r["status"] == "error" for r in rows)
        print(json.dumps({"fixture_count": len(rows), "error_count": errors}), flush=True)
        return 1 if errors else 0
    finally:
        engines.close()


def main():
    args = parse_args()
    try:
        # Third-party libraries can print paths, text and tracebacks. Keep those
        # out of stdout and out of saved reports, even on failed model loading.
        with open(__import__("os").devnull, "w") as sink:
            with contextlib.redirect_stderr(sink):
                return run(args)
    except KeyboardInterrupt:
        print(json.dumps({"status": "cancelled"})); return 130
    except Exception as exc:
        print(json.dumps({"status": "error", "error_type": type(exc).__name__})); return 1


if __name__ == "__main__":
    raise SystemExit(main())
