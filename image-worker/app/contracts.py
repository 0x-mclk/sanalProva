"""Backend ile sözleşme. backend/src/SanalProva.Infrastructure/ImageWorker/ImageWorkerClient.cs ile birebir aynı tutulmalıdır.

JSON alanları snake_case. Görseller multipart olarak gelir, yanıtta base64 döner.
"""

from enum import Enum
from typing import List, Optional

from pydantic import BaseModel, Field


class BodyZone(str, Enum):
    UPPER = "Upper"
    LOWER = "Lower"
    FULL_BODY = "FullBody"
    WAIST = "Waist"
    ACCESSORY = "Accessory"


class PointPrompt(BaseModel):
    """Personelin görselde dokunduğu nokta (0–1 arası normalize)."""

    x: float = Field(ge=0, le=1)
    y: float = Field(ge=0, le=1)


class SegmentParams(BaseModel):
    zone: BodyZone
    hint: Optional[str] = Field(default=None, max_length=60)
    points: Optional[List[PointPrompt]] = Field(default=None, max_length=10)


class SegmentResponse(BaseModel):
    mask_png_base64: str
    confidence: float
    area_ratio: float


class RecolorParams(BaseModel):
    target_hex: str = Field(pattern=r"^#[0-9A-Fa-f]{6}$")
    preserve_texture: bool = True


class RecolorQualityReport(BaseModel):
    delta_e: float
    edge_leak: float
    structure_similarity: float
    pattern_preserved: bool
    passed: bool


class RecolorResponse(BaseModel):
    image_base64: str
    content_type: str
    quality: RecolorQualityReport


class HealthResponse(BaseModel):
    status: str
    segmentation: str
    open_cv_version: str
