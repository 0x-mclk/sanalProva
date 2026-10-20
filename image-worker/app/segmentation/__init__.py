from .base import SegmentationResult, Segmenter, SegmenterUnavailable
from .mock import MockSegmenter


def create_segmenter(backend: str) -> Segmenter:
    if backend == "mock":
        return MockSegmenter()
    if backend == "sam2":
        # Aşama 5: from .sam2 import Sam2Segmenter (PyTorch gerektirir, varsayılan kurulumda yok)
        raise SegmenterUnavailable("SAM 2 segmentasyonu henüz kurulmadı (Aşama 5).")
    raise ValueError(f"Bilinmeyen segmentasyon arka ucu: {backend}")


__all__ = ["SegmentationResult", "Segmenter", "SegmenterUnavailable", "MockSegmenter", "create_segmenter"]
