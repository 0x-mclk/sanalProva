import cv2
import numpy as np

from app.contracts import BodyZone, SegmentParams

from .base import SegmentationResult

# Giyim bölgesine göre kaba dikey aralık (görsel yüksekliğinin oranı)
_ZONE_BANDS = {
    BodyZone.UPPER: (0.20, 0.50),
    BodyZone.LOWER: (0.48, 0.95),
    BodyZone.FULL_BODY: (0.18, 0.95),
    BodyZone.WAIST: (0.44, 0.52),
    BodyZone.ACCESSORY: (0.40, 0.55),
}


class MockSegmenter:
    """Gerçek model olmadan sözleşmeyi uçtan uca denemek için bölgeye göre elips maske üretir.

    Güven değeri 0 döner: backend kalite kontrolü bu maskeyi asla "başarılı" saymamalıdır.
    """

    name = "mock"

    def segment(self, image_bgr: np.ndarray, params: SegmentParams) -> SegmentationResult:
        height, width = image_bgr.shape[:2]
        top, bottom = _ZONE_BANDS[params.zone]
        center_x = width // 2
        if params.points:
            center_x = int(np.mean([p.x for p in params.points]) * width)

        mask = np.zeros((height, width), dtype=np.uint8)
        center = (center_x, int((top + bottom) / 2 * height))
        axes = (max(1, int(width * 0.22)), max(1, int((bottom - top) / 2 * height)))
        cv2.ellipse(mask, center, axes, 0, 0, 360, 255, thickness=-1)
        return SegmentationResult(mask=mask, confidence=0.0)
