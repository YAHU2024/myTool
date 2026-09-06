#!/usr/bin/env python3
"""Run comic detector and manga-ocr on detected blocks (research only)."""
from __future__ import annotations
import argparse,json,pathlib,time,sys
from PIL import Image
import numpy as np

def main():
 ap=argparse.ArgumentParser(); ap.add_argument('--repository',type=pathlib.Path,required=True); ap.add_argument('--fixture-directory',type=pathlib.Path,required=True); ap.add_argument('--output-path',type=pathlib.Path,required=True); args=ap.parse_args()
 sys.path.insert(0,str(args.repository.resolve()))
 from modules.detection.rtdetr_v2_onnx import RTDetrV2ONNXDetection
 from manga_ocr import MangaOcr
 det=RTDetrV2ONNXDetection(); det.initialize('cpu',0.3); ocr=MangaOcr(); rows=[]
 for path in sorted(args.fixture_directory.glob('comic-*.jpg')):
  with Image.open(path) as im:
   rgb=im.convert('RGB'); blocks=det.detect(np.asarray(rgb)); started=time.perf_counter(); recognized=[]
   for block in blocks:
    x1,y1,x2,y2=map(int,block.xyxy); crop=rgb.crop((max(0,x1),max(0,y1),min(rgb.width,x2),min(rgb.height,y2)))
    try: text=ocr(crop)
    except Exception as exc: text=f'[error:{type(exc).__name__}]'
    recognized.append({'class':getattr(block,'text_class',''),'text':text})
   rows.append({'file':path.name,'detected_block_count':len(blocks),'ocr_block_count':len(recognized),'elapsed_ms':round((time.perf_counter()-started)*1000,2),'recognized':recognized})
 report={'schema':'quicktranslate.comic-detection-ocr-spike.v1','detector':'RT-DETR-v2 comic-text-and-bubble-detector','recognizer':'kha-white/manga-ocr-base','rows':rows}
 args.output_path.parent.mkdir(parents=True,exist_ok=True); args.output_path.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8'); print(json.dumps({'fixture_count':len(rows),'total_blocks':sum(r['detected_block_count'] for r in rows),'total_elapsed_ms':round(sum(r['elapsed_ms'] for r in rows),2)},ensure_ascii=False))
if __name__=='__main__': main()
