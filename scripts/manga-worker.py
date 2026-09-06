#!/usr/bin/env python3
"""QuickTranslate M4.3 isolated manga worker (JSONL over stdin/stdout)."""
from __future__ import annotations
import importlib.util, json, pathlib, sys, tempfile, shutil

ROOT = pathlib.Path(__file__).resolve().parents[1]
REPOSITORY = ROOT / ".m4-external-spike" / "comic-translate"
sys.path.insert(0, str(REPOSITORY))
SPIKE = ROOT / "scripts" / "run-comic-detection-ocr-spike.py"
spec = importlib.util.spec_from_file_location("comic_spike", SPIKE)
comic = importlib.util.module_from_spec(spec); assert spec.loader; spec.loader.exec_module(comic)

cancelled: set[str] = set()

def emit(value):
    sys.stdout.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()

def fail(req, stage, exc, retryable=False):
    emit({"schema":"quicktranslate.manga-worker.v1","type":"failed","request_id":req,
          "stage":stage,"error_type":type(exc).__name__,"retryable":retryable})

def handle(msg):
    if msg.get("schema") != "quicktranslate.manga-worker.v1":
        raise ValueError("InvalidSchema")
    typ, req = msg.get("type"), msg.get("request_id")
    if not isinstance(req, str) or not req:
        raise ValueError("InvalidRequestId")
    if typ == "cancel":
        cancelled.add(req); emit({"schema":msg["schema"],"type":"cancelled","request_id":req,"stage":"queued"}); return
    if typ != "translate": raise ValueError("UnsupportedMessageType")
    language = msg.get("source_language")
    if language not in comic.ROUTES: raise ValueError("InvalidSourceLanguage")
    image_path = pathlib.Path(msg.get("image_path", ""));
    if not image_path.is_file(): raise FileNotFoundError("ImageNotFound")
    stages = msg.get("stages", ["detect", "ocr"])
    if not isinstance(stages, list) or not set(stages).issubset({"detect","ocr","inpaint","layout"}):
        raise ValueError("InvalidStages")
    if "layout" in stages: stages = [s for s in stages if s != "layout"]
    out_dir = pathlib.Path(msg.get("output_directory", image_path.parent)).resolve(); out_dir.mkdir(parents=True, exist_ok=True)
    # Only write artifacts below the caller-provided output directory.
    if not out_dir.is_dir(): raise NotADirectoryError("OutputDirectoryInvalid")
    import numpy as np
    from PIL import Image
    from modules.detection.rtdetr_v2_onnx import RTDetrV2ONNXDetection
    with Image.open(image_path) as src:
        src.load(); rgb = src.convert("RGB")
    image = np.asarray(rgb); detector = RTDetrV2ONNXDetection(); detector.initialize("cpu", 0.3)
    engines = comic.LocalEngines(language, pathlib.Path(msg["japanese_model_directory"]) if msg.get("japanese_model_directory") else None, bool(msg.get("allow_model_download")))
    stage = "detect"
    try:
        if req in cancelled: emit({"schema":msg["schema"],"type":"cancelled","request_id":req,"stage":"detect"}); return
        emit({"schema":msg["schema"],"type":"progress","request_id":req,"stage":"detect","completed":0,"total":1})
        blocks = detector.detect(image)
        emit({"schema":msg["schema"],"type":"progress","request_id":req,"stage":"detect","completed":1,"total":1})
        stage = "ocr"
        if "ocr" in stages or "inpaint" in stages:
            engines.recognize(image, blocks)
        result = {"schema":msg["schema"],"type":"completed","request_id":req,"image_width":rgb.width,"image_height":rgb.height,"blocks":[]}
        for i, block in enumerate(blocks, 1):
            m = comic.block_metadata(block, i, rgb.width, rgb.height, comic.ROUTES[language]); m.update({"source_language":language,"source_text":block.text if msg.get("include_source_text",False) else None})
            result["blocks"].append(m)
        if "inpaint" in stages:
            stage = "inpaint"
            cleaned, mask, status = engines.clean(image, blocks)
            clean_path = out_dir / f"{req}.cleaned.png"; Image.fromarray(cleaned).save(clean_path); result["cleaned_image_path"] = str(clean_path); result["inpainting"] = {"status":status,"mask_pixels":int(np.count_nonzero(mask))}
        emit(result)
    except KeyboardInterrupt:
        emit({"schema":msg["schema"],"type":"cancelled","request_id":req,"stage":stage})
    finally: engines.close()

for line in sys.stdin:
    try:
        msg = json.loads(line); handle(msg)
    except Exception as exc:
        req = msg.get("request_id") if isinstance(msg, dict) else "unknown"
        fail(req, locals().get("stage", "validate"), exc, retryable=isinstance(exc, (OSError, TimeoutError)))
