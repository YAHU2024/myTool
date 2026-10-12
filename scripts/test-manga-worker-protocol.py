#!/usr/bin/env python3
"""Fast protocol checks for manga-worker.py; does not load OCR models."""
import json, pathlib, subprocess, sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
WORKER = ROOT / "scripts" / "manga-worker.py"
PYTHON = sys.executable

def run(lines):
    p = subprocess.run([PYTHON, str(WORKER)], input="\n".join(json.dumps(x) for x in lines) + "\n", text=True, capture_output=True, timeout=10)
    return [json.loads(x) for x in p.stdout.splitlines() if x.strip()]

def check(name, condition):
    if not condition: raise AssertionError(name)
    print(f"ok: {name}")

schema = "quicktranslate.manga-worker.v1"
rows = run([{"schema": "wrong", "type": "translate", "request_id": "bad-schema"}])
check("invalid schema fails", rows[0]["type"] == "failed" and rows[0]["error_type"] == "ValueError")

rows = run([{"schema": schema, "type": "cancel", "request_id": "cancel-1"}])
check("queued cancel acknowledged", rows[0]["type"] == "cancelled" and rows[0]["stage"] == "queued")

rows = run([{"schema": schema, "type": "translate", "request_id": "missing", "image_path": "Z:/missing.png", "source_language": "en"}])
check("missing image is retryable", rows[0]["type"] == "failed" and rows[0]["retryable"] is True)

rows = run([{"schema": schema, "type": "translate", "request_id": "bad-lang", "image_path": "Z:/missing.png", "source_language": "xx"}])
check("invalid language rejected", rows[0]["type"] == "failed" and rows[0]["retryable"] is False)

print("protocol checks passed")
