import os
from dataclasses import dataclass


@dataclass(frozen=True)
class Settings:
    # Backend ile paylaşılan iç anahtar (X-Internal-Key). Tanımsızsa /v1 uç noktaları kapalıdır.
    internal_key: str
    # "mock" (varsayılan, bağımlılıksız) | "sam2" (Aşama 5, ayrı kurulum)
    segmentation_backend: str
    max_upload_bytes: int


def load_settings() -> Settings:
    return Settings(
        internal_key=os.environ.get("IMAGE_WORKER_INTERNAL_KEY", ""),
        segmentation_backend=os.environ.get("IMAGE_WORKER_SEGMENTATION", "mock").lower(),
        max_upload_bytes=int(os.environ.get("IMAGE_WORKER_MAX_UPLOAD_MB", "15")) * 1024 * 1024,
    )
