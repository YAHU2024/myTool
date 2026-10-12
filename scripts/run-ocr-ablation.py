#!/usr/bin/env python3
"""Run the OCR spike against every preprocessing ablation and summarize metrics."""
from __future__ import annotations
import argparse, json, pathlib, subprocess, sys

def main() -> int:
    ap=argparse.ArgumentParser()
    ap.add_argument('--ablation-directory',type=pathlib.Path,required=True)
    ap.add_argument('--output-path',type=pathlib.Path,required=True)
    ap.add_argument('--det-model-path',type=pathlib.Path,required=True)
    ap.add_argument('--rec-model-path',type=pathlib.Path,required=True)
    ap.add_argument('--rec-keys-path',type=pathlib.Path,required=True)
    ap.add_argument('--spike-script',type=pathlib.Path,default=pathlib.Path('scripts/run-scene-ocr-spike.py'))
    args=ap.parse_args()
    variants=sorted(p for p in args.ablation_directory.iterdir() if p.is_dir())
    rows=[]
    for d in variants:
        report=args.output_path.parent/(args.output_path.stem+'-'+d.name+'.json')
        cmd=[sys.executable,str(args.spike_script),'--fixture-directory',str(d),'--model-size','small','--det-model-path',str(args.det_model_path),'--rec-model-path',str(args.rec_model_path),'--rec-keys-path',str(args.rec_keys_path),'--disable-cls','--output-path',str(report)]
        completed=subprocess.run(cmd,check=False)
        if completed.returncode!=0: raise SystemExit(completed.returncode)
        data=json.loads(report.read_text(encoding='utf-8'))
        overall=data.get('quality_overall') or {}
        rows.append({'variant':d.name,'report':str(report.resolve()),'fixture_count':data.get('fixture_count'),'detection_recall':overall.get('detection_recall'),'polygon_iou_median':overall.get('polygon_iou_median_of_fixture_medians'),'character_error_rate':overall.get('character_error_rate'),'false_positive_block_count':overall.get('false_positive_block_count'),'reading_order_pairwise_accuracy':overall.get('reading_order_pairwise_accuracy')})
    result={'schema':'quicktranslate.ocr-ablation-report.v1','ablation_directory':str(args.ablation_directory.resolve()),'variants':rows}
    args.output_path.parent.mkdir(parents=True,exist_ok=True); args.output_path.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8'); print(json.dumps(result,ensure_ascii=False,indent=2)); return 0
if __name__=='__main__': raise SystemExit(main())
