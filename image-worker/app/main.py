"""Sanal Prova görsel işleme servisi.

Durumsuzdur: görselleri yalnızca bellekte işler, diske yazmaz ve loglamaz.
Yalnızca iç ağdan erişilmeli; /v1 uç noktaları X-Internal-Key ile korunur.
"""

import base64
import hmac
import json
from typing import Optional

import cv2
import numpy as np
from fastapi import Depends, FastAPI, File, Form, Header, HTTPException, UploadFile, status
from pydantic import ValidationError

from app.config import Settings, load_settings
from app.contracts import HealthResponse, RecolorParams, SegmentParams, SegmentResponse
from app.recolor import RecolorNotImplemented, recolor
from app.segmentation import SegmenterUnavailable, create_segmenter


def create_app(settings: Optional[Settings] = None) -> FastAPI:
    settings = settings or load_settings()
    app = FastAPI(title="Sanal Prova Image Worker", version="0.1.0", docs_url=None, redoc_url=None)

    def require_internal_key(x_internal_key: Optional[str] = Header(default=None)) -> None:
        if not settings.internal_key:
            raise HTTPException(status.HTTP_503_SERVICE_UNAVAILABLE, "IMAGE_WORKER_INTERNAL_KEY tanımlı değil.")
        if not x_internal_key or not hmac.compare_digest(x_internal_key, settings.internal_key):
            raise HTTPException(status.HTTP_401_UNAUTHORIZED, "Geçersiz iç anahtar.")

    async def read_image(upload: UploadFile, flags: int = cv2.IMREAD_COLOR) -> np.ndarray:
        data = await upload.read(settings.max_upload_bytes + 1)
        if len(data) > settings.max_upload_bytes:
            raise HTTPException(status.HTTP_413_REQUEST_ENTITY_TOO_LARGE, "Görsel boyutu sınırı aşıyor.")
        image = cv2.imdecode(np.frombuffer(data, dtype=np.uint8), flags)
        if image is None:
            raise HTTPException(status.HTTP_422_UNPROCESSABLE_CONTENT, "Görsel çözümlenemedi.")
        return image

    def parse(model, raw: str):
        try:
            return model.model_validate(json.loads(raw))
        except (json.JSONDecodeError, ValidationError) as exc:
            raise HTTPException(status.HTTP_422_UNPROCESSABLE_CONTENT, "Geçersiz parametreler.") from exc

    @app.get("/health", response_model=HealthResponse)
    def health() -> HealthResponse:
        return HealthResponse(status="ok", segmentation=settings.segmentation_backend, open_cv_version=cv2.__version__)

    @app.post("/v1/segment", response_model=SegmentResponse, dependencies=[Depends(require_internal_key)])
    async def segment(image: UploadFile = File(...), params: str = Form(...)) -> SegmentResponse:
        segment_params = parse(SegmentParams, params)
        image_bgr = await read_image(image)
        try:
            result = create_segmenter(settings.segmentation_backend).segment(image_bgr, segment_params)
        except SegmenterUnavailable as exc:
            raise HTTPException(status.HTTP_501_NOT_IMPLEMENTED, str(exc)) from exc

        ok, png = cv2.imencode(".png", result.mask)
        if not ok:
            raise HTTPException(status.HTTP_500_INTERNAL_SERVER_ERROR, "Maske kodlanamadı.")
        return SegmentResponse(
            mask_png_base64=base64.b64encode(png.tobytes()).decode("ascii"),
            confidence=result.confidence,
            area_ratio=round(result.area_ratio, 4),
        )

    @app.post("/v1/recolor", dependencies=[Depends(require_internal_key)])
    async def recolor_endpoint(image: UploadFile = File(...), mask: UploadFile = File(...), params: str = Form(...)):
        recolor_params = parse(RecolorParams, params)
        image_bgr = await read_image(image)
        mask_gray = await read_image(mask, cv2.IMREAD_GRAYSCALE)
        if mask_gray.shape[:2] != image_bgr.shape[:2]:
            raise HTTPException(status.HTTP_422_UNPROCESSABLE_CONTENT, "Maske ile görsel boyutu farklı.")
        try:
            recolor(image_bgr, mask_gray, recolor_params)
        except RecolorNotImplemented as exc:
            raise HTTPException(status.HTTP_501_NOT_IMPLEMENTED, str(exc)) from exc
        raise HTTPException(status.HTTP_500_INTERNAL_SERVER_ERROR, "Beklenmeyen durum.")

    return app


app = create_app()
