#!/usr/bin/env python3
"""Renders .github/thumbnail/thumbnail.html to .github/thumbnail.png (1200x627 at 2x) and writes a
1200x627 JPEG copy to docs/thumbnail.jpg for og:image. The numbers come from results/holdout.json.
Needs Python Playwright and Pillow."""
import json
from PIL import Image
from pathlib import Path
from playwright.sync_api import sync_playwright

root = Path(__file__).resolve().parent.parent
h = json.loads((root / "results/holdout.json").read_text())
base = min(h["baselines"], key=lambda b: b["score"]["rmsle"])  # the strongest baseline, not the weakest
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
png = root / ".github/thumbnail.png"
img = Image.open(png).convert("RGB")
img.save(png, optimize=True)
img.resize((1200, 627), Image.LANCZOS).save(root / "docs/thumbnail.jpg", quality=86, optimize=True, progressive=True)
print("Wrote .github/thumbnail.png and docs/thumbnail.jpg", tokens)
