#!/usr/bin/env python3
"""Evaluate orientation candidates from prebuilt ablation reports.
Selection is confidence-first and does not use ground truth; quality metrics are audit-only.
"""
from __future__ import annotations
import argparse,json,pathlib

def main():
 ap=argparse.ArgumentParser(); ap.add_argument('--report-directory',type=pathlib.Path,required=True); ap.add_argument('--output-path',type=pathlib.Path,required=True); args=ap.parse_args()
 names=['baseline','rotate_cw','rotate_ccw']; rows=[]
 for name in names:
  p=args.report_directory/f'ocr-ablation-summary-{name}.json'
  if not p.exists(): continue
  d=json.loads(p.read_text(encoding='utf-8')); rows.append({'candidate':name,'report':str(p.resolve()),'fixture_count':d.get('fixture_count'),'detection_recall':d.get('quality_overall',{}).get('detection_recall'),'polygon_iou_median':d.get('quality_overall',{}).get('polygon_iou_median_of_fixture_medians'),'character_error_rate':d.get('quality_overall',{}).get('character_error_rate'),'reading_order_pairwise_accuracy':d.get('quality_overall',{}).get('reading_order_pairwise_accuracy'),'false_positive_block_count':d.get('quality_overall',{}).get('false_positive_block_count')})
 result={'schema':'quicktranslate.ocr-orientation-spike.v1','selection_policy':'per-fixture production adapter must use OCR-only confidence/geometry; aggregate rows here are audit metrics and do not select using GT','candidates':rows,'decision':'No-Go' if not rows else 'Research-only'}
 args.output_path.parent.mkdir(parents=True,exist_ok=True); args.output_path.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8'); print(json.dumps(result,ensure_ascii=False,indent=2))
if __name__=='__main__': main()
