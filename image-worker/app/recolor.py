"""OpenCV tabanlı kumaş renk değişimi — Aşama 5'te uygulanacak.

Planlanan yöntem (docs/07-ai-entegrasyon.md §5.2):
1. Nakış koruma: maske içinde yüksek kenar yoğunluğu + metalik/doygun renk kümeleri maskeden çıkarılır.
2. LAB dönüşümü: a/b kanalları hedef renge kaydırılır; L kanalı ortalaması hedefe ölçeklenir,
   varyansı korunur (kıvrım ve gölgeler kalır). Maske kenarı yumuşatılır.
3. Kalite raporu: ΔE2000, maske taşması (edge leak), L kanalında SSIM, nakış korunumu.
   Eşiklerden biri tutmazsa passed=False → backend AI düzenleme yoluna düşer.
"""

import numpy as np

from app.contracts import RecolorParams, RecolorQualityReport


class RecolorNotImplemented(NotImplementedError):
    pass


def recolor(image_bgr: np.ndarray, mask: np.ndarray, params: RecolorParams) -> "tuple[np.ndarray, RecolorQualityReport]":
    raise RecolorNotImplemented("Renk değişimi Aşama 5'te uygulanacak.")
