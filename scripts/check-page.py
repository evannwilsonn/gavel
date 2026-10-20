#!/usr/bin/env python3
"""Opens the demo in Chromium at 1280px and 375px, in light and dark, and checks that the model
loads and predicts, that nothing scrolls sideways and that the console stays clean.
Screenshots go to shots/ (not committed).

usage: python3 scripts/check-page.py [url]   (default http://127.0.0.1:8822/, e.g. python3 -m http.server 8822 -d docs)
"""
import sys
from pathlib import Path
from playwright.sync_api import sync_playwright

url = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:8822/"
out = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(__file__).resolve().parent.parent / "shots"
out.mkdir(parents=True, exist_ok=True)
failures = []
with sync_playwright() as p:
    browser = p.chromium.launch()
    for scheme in ["light", "dark"]:
        for width in [1280, 375]:
            page = browser.new_page(viewport={"width": width, "height": 900}, color_scheme=scheme, device_scale_factor=1)
            errors = []
            page.on("console", lambda m: errors.append(m.text) if m.type == "error" else None)
            page.on("pageerror", lambda e: errors.append(str(e)))
            page.goto(url)
            page.wait_for_function("document.querySelector('#price').textContent.startsWith('$')", timeout=15000)
            price = page.text_content("#price")
            # change a field and make sure the estimate moves
            page.fill("#f-year", "1985")
            page.dispatch_event("#f-year", "input")
            older = page.text_content("#price")
            page.fill("#f-year", "2008")
            page.dispatch_event("#f-year", "input")
            newer = page.text_content("#price")
            sideways = page.evaluate("document.documentElement.scrollWidth - document.documentElement.clientWidth")
            bars = page.locator("#tornado rect.bar-up, #tornado rect.bar-down").count()
            page.screenshot(path=str(out / f"{scheme}-{width}.png"), full_page=True)
            page.click("#pull")
            page.wait_for_timeout(200)
            tag = page.text_content("#tag-body")
            page.locator(".tag-col").screenshot(path=str(out / f"{scheme}-{width}-tag.png"))
            print(f"{scheme:5} {width:4}px  price {price}  1985: {older}  2008: {newer}  bars {bars}  sideways {sideways}px  errors {len(errors)}")
            print(f"            tag: {' '.join(tag.split())[:150]}")
            if errors: failures.append(f"{scheme} {width}: {errors}")
            if sideways > 0: failures.append(f"{scheme} {width}: scrolls sideways by {sideways}px")
            if not price.startswith("$") or older == newer: failures.append(f"{scheme} {width}: estimate did not respond")
            if bars < 5: failures.append(f"{scheme} {width}: only {bars} bars")
            page.close()
    browser.close()
if failures:
    print("FAILED"); [print(" ", f) for f in failures]; sys.exit(1)
print("all checks passed; screenshots in", out)
