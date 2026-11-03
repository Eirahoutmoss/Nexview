"""Marka setinden (NexView Marka Kimliği ve Görsel Seti.png) uygulama ikonlarını üretir.
Kullanım: python tools/make_icons.py "<marka-seti.png>"   (gereksinim: pillow, numpy)"""
import sys, os
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

SRC = sys.argv[1] if len(sys.argv) > 1 else "NexView Marka Kimliği ve Görsel Seti.png"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "NexView.App", "Assets")
S = 1024

def extract_mark(sheet):
    # Sol üstteki büyük amblem (N + diyafram); koyu zemini saydamlaştır
    mark = sheet.crop((88, 40, 392, 312))
    a = np.asarray(mark).astype(float)
    mx, mn = a.max(axis=2), a.min(axis=2)
    alpha = np.clip((mx - 70) / 80, 0, 1) * np.clip((mx - mn) / 60 + 0.3, 0, 1)
    m = Image.fromarray((alpha * 255).astype("uint8")).filter(ImageFilter.GaussianBlur(0.6))
    rgba = mark.convert("RGBA")
    rgba.putalpha(m)
    return rgba.crop(rgba.getbbox())

def app_icon(mark):
    out = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(out).rounded_rectangle([24, 24, S - 24, S - 24], radius=210, fill=(10, 14, 34, 255))
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        t = y / S
        gd.line([(0, y), (S, y)], fill=(int(150 - 120 * t), int(80 + 90 * t), 250, 255))
    ring = Image.new("L", (S, S), 0)
    ImageDraw.Draw(ring).rounded_rectangle([24, 24, S - 24, S - 24], radius=210, outline=255, width=16)
    out.paste(grad, (0, 0), ring)
    w = int(S * 0.68)
    h = int(mark.height * w / mark.width)
    out.alpha_composite(mark.resize((w, h), Image.LANCZOS), ((S - w) // 2, (S - h) // 2 + 8))
    return out

if __name__ == "__main__":
    sheet = Image.open(SRC).convert("RGB")
    if sheet.size != (1536, 1024):
        sheet = sheet.resize((1536, 1024), Image.LANCZOS)
    mark = extract_mark(sheet)
    os.makedirs(OUT, exist_ok=True)
    icon = app_icon(mark)
    icon.resize((256, 256), Image.LANCZOS).save(os.path.join(OUT, "nexview-256.png"))
    icon.resize((256, 256), Image.LANCZOS).save(os.path.join(OUT, "nexview.ico"),
                                                 sizes=[(s, s) for s in (16, 20, 24, 32, 48, 64, 128, 256)])
    mh = 128
    mark.resize((int(mark.width * mh / mark.height), mh), Image.LANCZOS).save(os.path.join(OUT, "nexview-mark.png"))
    print("ok")
