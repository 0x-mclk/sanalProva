"""PWA PNG ikonlarını frontend/public/icons/icon.svg ile aynı motifle üretir (OpenCV, image-worker venv'i).

Kullanım:  image-worker/.venv/Scripts/python.exe scripts/generate_icons.py
Marka ikonu hazır olduğunda bu betik yerine tasarımcının PNG'leri kullanılmalıdır.
"""

from pathlib import Path

import cv2
import numpy as np

OUT = Path(__file__).resolve().parent.parent / "frontend" / "public" / "icons"
BORDO = (58, 31, 122)  # BGR #7A1F3A
GOLD = (124, 190, 217)  # #D9BE7C
CREAM = (239, 247, 251)  # #FBF7EF
SS = 4  # süper örnekleme (yumuşak kenarlar)


def render(size: int, maskable: bool) -> np.ndarray:
    s = size * SS
    img = np.zeros((s, s, 4), dtype=np.uint8)
    scale = s / 512
    # Maskable ikonlarda içerik merkezdeki %80 güvenli alanda kalmalı, zemin tam kare
    content = 0.78 if maskable else 1.0

    if maskable:
        img[:, :] = (*BORDO, 255)
    else:
        radius = int(112 * scale)
        cv2.rectangle(img, (radius, 0), (s - radius, s), (*BORDO, 255), -1)
        cv2.rectangle(img, (0, radius), (s, s - radius), (*BORDO, 255), -1)
        for cx, cy in [(radius, radius), (s - radius, radius), (radius, s - radius), (s - radius, s - radius)]:
            cv2.circle(img, (cx, cy), radius, (*BORDO, 255), -1)

    def p(x: float, y: float) -> tuple:
        return (int((256 + (x - 256) * content) * scale), int((256 + (y - 256) * content) * scale))

    cv2.circle(img, p(256, 256), int(170 * content * scale), (*GOLD, 255), int(10 * content * scale), cv2.LINE_AA)
    thick = int(16 * content * scale)
    cv2.ellipse(img, p(284, 168), (int(28 * content * scale),) * 2, 0, 180, 450, (*CREAM, 255), thick, cv2.LINE_AA)
    cv2.line(img, p(284, 196), p(256, 224), (*CREAM, 255), thick, cv2.LINE_AA)
    hanger = np.array([p(256, 224), p(368, 304), p(144, 304)], dtype=np.int32)
    cv2.polylines(img, [hanger], True, (*CREAM, 255), thick, cv2.LINE_AA)
    return cv2.resize(img, (size, size), interpolation=cv2.INTER_AREA)


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for name, size, maskable in [
        ("icon-192.png", 192, False),
        ("icon-512.png", 512, False),
        ("icon-maskable-512.png", 512, True),
        ("apple-touch-icon.png", 180, True),
    ]:
        cv2.imwrite(str(OUT / name), render(size, maskable))
        print(f"{name} ({size}px)")


if __name__ == "__main__":
    main()
