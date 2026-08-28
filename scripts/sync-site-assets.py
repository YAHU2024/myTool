#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""素材单一来源同步脚本

约定：素材只在 docs/images/ 维护（README 与站点共用单一来源，统一英文文件名），
site/assets/img/ 由本脚本生成（部署产物，不入库；GitHub Pages 只发布 site/）。

用法:
  python scripts/sync-site-assets.py            # 显示待同步清单（不一致时退出码 1）
  python scripts/sync-site-assets.py --sync     # 复制到 site/assets/img/
  python scripts/sync-site-assets.py --check    # 供 CI：内容不一致则退出码 1
"""

import argparse
import hashlib
import os
import shutil
import sys

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC_DIR = os.path.join(REPO_ROOT, "docs", "images")
DST_DIR = os.path.join(REPO_ROOT, "site", "assets", "img")

# 参与同步的图片扩展名（docs/images 与 site/assets/img 同名）
IMG_EXTS = (".gif", ".png", ".jpg", ".jpeg", ".webp", ".ico")


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    parser = argparse.ArgumentParser(description="同步站点素材（docs/images -> site/assets/img）")
    parser.add_argument("--sync", action="store_true", help="复制素材到 site/assets/img")
    parser.add_argument("--check", action="store_true", help="校验一致性（CI 用），不一致退出码 1")
    args = parser.parse_args()

    files = sorted(
        f for f in os.listdir(SRC_DIR)
        if f.lower().endswith(IMG_EXTS) and os.path.isfile(os.path.join(SRC_DIR, f))
    )
    if not files:
        print("docs/images 下没有可同步的素材文件")
        sys.exit(1)

    os.makedirs(DST_DIR, exist_ok=True)
    stale = []
    for fn in files:
        sp = os.path.join(SRC_DIR, fn)
        dp = os.path.join(DST_DIR, fn)
        if not os.path.exists(dp) or sha256(sp) != sha256(dp):
            stale.append(fn)

    if args.sync:
        for fn in stale:
            shutil.copy2(os.path.join(SRC_DIR, fn), os.path.join(DST_DIR, fn))
            print(f"同步: {fn}")
        print("同步完成" if stale else "已是最新")
    elif args.check:
        if stale:
            print("素材不一致，需运行 --sync:", stale)
            sys.exit(1)
        print("素材一致")
    else:
        for fn in stale:
            print(f"待同步: {fn}")
        sys.exit(1 if stale else 0)


if __name__ == "__main__":
    main()
