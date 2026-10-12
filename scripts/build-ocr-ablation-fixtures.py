#!/usr/bin/env python3
"""Build deterministic OCR preprocessing ablation fixtures with transformed GT polygons."""
from __future__ import annotations
import argparse, json, pathlib
from PIL import Image, ImageEnhance, ImageFilter

def transform(image, name):
    if name == 'baseline': return image.copy()
    if name == 'upscale': return image.resize((round(image.width*1.5), round(image.height*1.5)), Image.Resampling.LANCZOS)
    if name == 'contrast': return ImageEnhance.Contrast(image).enhance(1.35)
    if name == 'sharpen': return image.filter(ImageFilter.UnsharpMask(radius=1.2, percent=140, threshold=3))
    if name == 'denoise': return image.filter(ImageFilter.MedianFilter(size=3))
    if name == 'rotate_cw': return image.rotate(-90, expand=True)
    if name == 'rotate_ccw': return image.rotate(90, expand=True)
    raise ValueError(name)

def point(p, name, w, h):
    x,y=p
    if name=='upscale': return [x*1.5,y*1.5]
    if name=='rotate_cw': return [h-y,x]
    if name=='rotate_ccw': return [y,w-x]
    return [x,y]

def main():
    ap=argparse.ArgumentParser()
    ap.add_argument('--fixture-directory', type=pathlib.Path, required=True)
    ap.add_argument('--output-directory', type=pathlib.Path, required=True)
    args=ap.parse_args(); src=args.fixture_directory; out=args.output_directory
    if not src.is_dir(): raise SystemExit(f'Fixture directory does not exist: {src}')
    variants=['baseline','upscale','contrast','sharpen','denoise','rotate_cw','rotate_ccw']
    manifest={'schema':'quicktranslate.ocr-ablation-fixtures.v1','source_directory':str(src.resolve()),'variants':variants,'fixtures':[]}
    for variant in variants:
        d=out/variant; d.mkdir(parents=True,exist_ok=True)
        for image_path in sorted(src.iterdir()):
            if image_path.suffix.lower() not in {'.png','.jpg','.jpeg','.bmp'}: continue
            with Image.open(image_path) as im:
                image=transform(im.convert('RGB'),variant); image.save(d/(image_path.stem+'.png'),'PNG')
                ann_path=image_path.with_suffix('.json')
                if ann_path.exists():
                    ann=json.loads(ann_path.read_text(encoding='utf-8-sig'))
                    w,h=im.size; ann['fixture_id']=image_path.stem; ann['image_file']=image_path.stem+'.png'; ann['width']=image.width; ann['height']=image.height
                    for block in ann.get('blocks',[]): block['polygon']=[point(p,variant,w,h) for p in block['polygon']]
                    (d/(image_path.stem+'.json')).write_text(json.dumps(ann,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
                manifest['fixtures'].append({'variant':variant,'file':image_path.stem+'.png','width':image.width,'height':image.height})
    out.mkdir(parents=True,exist_ok=True); (out/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({'variant_count':len(variants),'fixture_count':len(manifest['fixtures'])},ensure_ascii=False))
if __name__=='__main__': main()
