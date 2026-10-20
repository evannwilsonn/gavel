#!/usr/bin/env python3
"""Renders .github/thumbnail/thumbnail.html to .github/thumbnail.png (1200x627 at 2x) and copies it
to docs/thumbnail.png for og:image. The numbers come from results/holdout.json."""
import json, shutil
from pathlib import Path
from playwright.sync_api import sync_playwright

root = Path(__file__).resolve().parent.parent
h = json.loads((root / "results/holdout.json").read_text())
base = next(b for b in h["baselines"] if b["name"].startswith("Median by product group and year"))
tokens = {
    "final": f'{h["final"]["score"]["rmsle"]:.3f}',
    "baseline": f'{base["score"]["rmsle"]:.3f}',
    "course": f'{h["course"]["validRmsle"]:.3f}',
    "sales": f'{h["fitSales"] + h["heldOutSales"]:,}',
    "n_heldout": f'{h["heldOutSales"]:,}',
}
src = root / ".github/thumbnail/thumbnail.html"
html = src.read_text()
for k, v in tokens.items():
    html = html.replace("{{" + k + "}}", v)
tmp = src.with_name(".render.html")
tmp.write_text(html)
try:
    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page(viewport={"width": 1200, "height": 627}, device_scale_factor=2)
        page.goto(tmp.as_uri())
        page.wait_for_timeout(500)
        page.screenshot(path=str(root / ".github/thumbnail.png"))
        browser.close()
finally:
    tmp.unlink()
shutil.copy(root / ".github/thumbnail.png", root / "docs/thumbnail.png")
print("Wrote .github/thumbnail.png and docs/thumbnail.png", tokens)
