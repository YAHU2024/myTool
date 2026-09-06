#!/usr/bin/env python3
"""Run the Apache comic text/bubble detector against local fixtures."""
from __future__ import annotations
import argparse, json, pathlib, time
from PIL import Image
import numpy as np

def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('--repository', type=pathlib.Path, required=True)
    ap.add_argument('--fixture-directory', type=pathlib.Path, required=True)
    ap.add_argument('--output-path', type=pathlib.Path, required=True)
    args = ap.parse_args()
    import sys
    sys.path.insert(0, str(args.repository.resolve()))
    from modules.detection.rtdetr_v2_onnx import RTDetrV2ONNXDetection
    engine = RTDetrV2ONNXDetection(); engine.initialize('cpu', 0.3)
    rows = []
    for path in sorted(args.fixture_directory.glob('comic-*.jpg')):
        with Image.open(path) as image:
            started = time.perf_counter(); blocks = engine.detect(np.asarray(image.convert('RGB')))
            rows.append({'file': path.name, 'detected_block_count': len(blocks), 'elapsed_ms': round((time.perf_counter()-started)*1000, 2)})
    report = {'schema':'quicktranslate.comic-detector-spike.v1','engine':'RT-DETR-v2 comic-text-and-bubble-detector','threshold':0.3,'rows':rows}
    args.output_path.parent.mkdir(parents=True, exist_ok=True); args.output_path.write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n', encoding='utf-8'); print(json.dumps(report, ensure_ascii=False, indent=2)); return 0
if __name__ == '__main__': raise SystemExit(main())
