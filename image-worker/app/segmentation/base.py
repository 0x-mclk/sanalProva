from dataclasses import dataclass
from typing import Protocol

import numpy as np

from app.contracts import SegmentParams


class SegmenterUnavailable(RuntimeError):
    """Seçilen segmentasyon arka ucu bu kurulumda yok."""


@dataclass(frozen=True)
class SegmentationResult:
    mask: np.ndarray  # uint8, 0 veya 255, görselle aynı boyut (H×W)
    confidence: float

    @property
    def area_ratio(self) -> float:
        return float(np.count_nonzero(self.mask)) / float(self.mask.size)


class Segmenter(Protocol):
    name: str

    def segment(self, image_bgr: np.ndarray, params: SegmentParams) -> SegmentationResult: ...
