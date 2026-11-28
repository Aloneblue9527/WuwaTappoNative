"""Build the four-character Tabler Icons subset (fonttools==4.61.1).

Usage: python tools/subset-pad-symbols.py
The full upstream font is downloaded to memory, never shipped with the app.
"""
from hashlib import sha256
from io import BytesIO
from pathlib import Path
from urllib.request import urlopen

from fontTools import subset
from fontTools.ttLib import TTFont

ROOT = Path(__file__).resolve().parents[1]
URL = "https://cdn.jsdelivr.net/npm/@tabler/icons-webfont@3.35.0/dist/fonts/tabler-icons-outline.ttf"
MAPPING = {0x25B3: 0xEB44, 0x25CB: 0xEA6B, 0x00D7: 0xEB55, 0x25A1: 0xEB2C}


def main():
    data = urlopen(URL).read()
    # Pin the upstream binary as well as the package version.
    assert sha256(data).hexdigest() == "c91389ecf6f8cee2bc21be8d1fdb38d9daeca8a28dfae9c765722b370c0ae441"
    font = TTFont(BytesIO(data), recalcTimestamp=False)
    cmap = font.getBestCmap()
    names = {char: cmap[icon] for char, icon in MAPPING.items()}
    options = subset.Options()
    options.recommended_glyphs = False
    options.notdef_outline = False
    options.name_IDs = [0, 1, 2, 3, 4, 5, 6, 13, 14]
    subsetter = subset.Subsetter(options=options)
    subsetter.populate(glyphs=names.values())
    subsetter.subset(font)
    for table in font["cmap"].tables:
        table.cmap = names.copy()
    for name_id, value in {
        1: "Wuwa Pad Symbols", 2: "Regular", 3: "WuwaPadSymbols-Tabler3.35.0",
        4: "Wuwa Pad Symbols", 6: "WuwaPadSymbols",
        13: "Tabler Icons, Copyright (c) 2020-2025 Paweł Kuna. MIT License.",
        14: "https://github.com/tabler/tabler-icons/blob/main/LICENSE",
    }.items():
        font["name"].setName(value, name_id, 3, 1, 0x409)
    target = ROOT / "assets/fonts/WuwaPadSymbols.ttf"
    target.parent.mkdir(parents=True, exist_ok=True)
    font.save(target)
    check = TTFont(target)
    assert set(check.getBestCmap()) == set(MAPPING)
    assert len(check.getGlyphOrder()) == 5  # Four symbols plus mandatory .notdef.
    print(f"{target.name}: {target.stat().st_size} bytes; only △ ○ × □ (+ .notdef)")


if __name__ == "__main__":
    main()
