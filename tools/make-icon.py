# /// script
# requires-python = ">=3.11"
# dependencies = ["pillow>=10", "numpy>=1.26", "scipy>=1.11"]
# ///
"""Erzeugt das Launcher-Icon aus assets/icon-source.png.

Weißer Hintergrund wird per Flood-Fill vom Bildrand aus freigestellt (nur zusammenhängendes
Weiß, damit helle Stellen im Motiv bleiben), Kanten werden weich ausgeblendet, darunter kommt
ein weicher Dropshadow. Ausgabe: assets/icon.png (1024 px) und assets/icon.ico.

Aufruf:  uv run tools/make-icon.py
"""
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter
from scipy import ndimage

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "assets" / "icon-source.png"
OUT_PNG = ROOT / "assets" / "icon.png"
OUT_ICO = ROOT / "assets" / "icon.ico"

SIZE = 1024
MARGIN = 0.06          # freier Rand je Seite
SHADOW_OFFSET = (0.025, 0.035)
SHADOW_BLUR = 0.025
SHADOW_ALPHA = 0.55
WHITE_TOL = 32         # Abstand zu Reinweiß, der noch als Hintergrund gilt


def cut_out(img: Image.Image) -> Image.Image:
    rgb = np.asarray(img.convert("RGB")).astype(np.int16)
    dist = np.abs(255 - rgb).max(axis=2)
    whiteish = dist <= WHITE_TOL
    labels, _ = ndimage.label(whiteish)
    border = np.unique(np.concatenate([labels[0], labels[-1], labels[:, 0], labels[:, -1]]))
    border = border[border != 0]
    bg = np.isin(labels, border)
    # Eingeschlossene Hintergrundinseln (z. B. zwischen Hals und Arm): groß und fast reinweiß.
    # Kleine helle Stellen wie Zähne bleiben erhalten.
    min_area = whiteish.size * 0.0003
    for lab in range(1, labels.max() + 1):
        if lab in border:
            continue
        region = labels == lab
        if region.sum() >= min_area and dist[region].mean() < 12:
            bg |= region
    # Weicher Übergang: Alpha aus Abstand zu Weiß an der Grenze, plus leichtes Feathering.
    fg = ~bg
    fg = ndimage.binary_opening(fg, iterations=1)
    # Randpixel, die noch hell sind, teilweise transparent machen (kein weißer Saum).
    edge = fg & ~ndimage.binary_erosion(fg, iterations=4)
    soft = np.clip((dist - 20) / 70.0, 0, 1)
    alpha = np.where(edge, soft, fg.astype(np.float32))
    alpha_img = Image.fromarray((alpha * 255).astype(np.uint8)).filter(ImageFilter.MinFilter(5)).filter(ImageFilter.GaussianBlur(1.0))
    rgba = img.convert("RGBA")
    rgba.putalpha(alpha_img)
    return rgba.crop(rgba.getbbox())


def compose(motif: Image.Image) -> Image.Image:
    inner = int(SIZE * (1 - 2 * MARGIN - max(SHADOW_OFFSET)))
    scale = inner / max(motif.size)
    motif = motif.resize((round(motif.width * scale), round(motif.height * scale)), Image.LANCZOS)
    canvas = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    x = (SIZE - motif.width) // 2 - int(SIZE * SHADOW_OFFSET[0] / 2)
    y = (SIZE - motif.height) // 2 - int(SIZE * SHADOW_OFFSET[1] / 2)

    shadow_alpha = motif.getchannel("A").point(lambda a: int(a * SHADOW_ALPHA))
    shadow = Image.new("RGBA", motif.size, (0, 0, 0, 255))
    shadow.putalpha(shadow_alpha)
    shadow_layer = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    shadow_layer.paste(shadow, (x + int(SIZE * SHADOW_OFFSET[0]), y + int(SIZE * SHADOW_OFFSET[1])), shadow)
    shadow_layer = shadow_layer.filter(ImageFilter.GaussianBlur(SIZE * SHADOW_BLUR))

    canvas = Image.alpha_composite(canvas, shadow_layer)
    layer = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    layer.paste(motif, (x, y), motif)
    return Image.alpha_composite(canvas, layer)


def main() -> None:
    src = Image.open(SRC)
    # Quelle ist klein (~355 px): vor dem Freistellen hochskalieren, damit Kanten glatter werden.
    src = src.resize((src.width * 3, src.height * 3), Image.LANCZOS)
    icon = compose(cut_out(src))
    icon.save(OUT_PNG)
    icon.save(OUT_ICO, sizes=[(s, s) for s in (16, 24, 32, 48, 64, 128, 256)])
    print(f"geschrieben: {OUT_PNG} und {OUT_ICO}")


if __name__ == "__main__":
    main()
