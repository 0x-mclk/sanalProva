import base64
import json

import cv2
import numpy as np
import pytest
from fastapi.testclient import TestClient

from app.config import Settings
from app.main import create_app

KEY = "test-internal-key"


@pytest.fixture()
def client() -> TestClient:
    return TestClient(create_app(Settings(internal_key=KEY, segmentation_backend="mock", max_upload_bytes=1024 * 1024)))


def png_bytes(width: int = 60, height: int = 120, channels: int = 3) -> bytes:
    shape = (height, width, channels) if channels > 1 else (height, width)
    ok, data = cv2.imencode(".png", np.full(shape, 128, dtype=np.uint8))
    assert ok
    return data.tobytes()


def segment(client: TestClient, params: dict, key: str = KEY, image: bytes = None):
    return client.post(
        "/v1/segment",
        headers={"X-Internal-Key": key},
        files={"image": ("image", image or png_bytes(), "image/png")},
        data={"params": json.dumps(params)},
    )


def test_health_opencv_surumunu_ve_segmentasyon_arka_ucunu_doner(client: TestClient):
    body = client.get("/health").json()

    assert body["status"] == "ok"
    assert body["segmentation"] == "mock"
    assert body["open_cv_version"] == cv2.__version__


def test_ic_anahtar_olmadan_istek_reddedilir(client: TestClient):
    assert segment(client, {"zone": "Lower"}, key="yanlis").status_code == 401


def test_anahtar_tanimsizsa_v1_kapalidir():
    closed = TestClient(create_app(Settings(internal_key="", segmentation_backend="mock", max_upload_bytes=1024)))

    assert segment(closed, {"zone": "Lower"}, key="").status_code == 503


def test_mock_segmentasyon_gorselle_ayni_boyutta_maske_ve_sifir_guven_doner(client: TestClient):
    response = segment(client, {"zone": "Lower", "hint": "salvar", "points": [{"x": 0.5, "y": 0.7}]})
    body = response.json()

    assert response.status_code == 200
    mask = cv2.imdecode(np.frombuffer(base64.b64decode(body["mask_png_base64"]), np.uint8), cv2.IMREAD_GRAYSCALE)
    assert mask.shape == (120, 60)
    assert set(np.unique(mask)) <= {0, 255}
    assert body["confidence"] == 0.0  # mock maske kalite kontrolünden asla geçmemeli
    assert 0 < body["area_ratio"] < 1
    # Alt bölge maskesi görselin üst yarısına taşmaz
    assert np.count_nonzero(mask[:50]) == 0


@pytest.mark.parametrize("params", [{"zone": "Bilinmeyen"}, {"zone": "Lower", "points": [{"x": 2, "y": 0}]}])
def test_gecersiz_parametre_422_doner(client: TestClient, params: dict):
    assert segment(client, params).status_code == 422


def test_gorsel_olmayan_dosya_422_doner(client: TestClient):
    assert segment(client, {"zone": "Lower"}, image=b"bu bir gorsel degil").status_code == 422


def test_boyut_siniri_asilinca_413_doner(client: TestClient):
    assert segment(client, {"zone": "Lower"}, image=b"0" * (1024 * 1024 + 10)).status_code == 413


def test_renk_degisimi_sozlesmesi_dogrulanir_ve_henuz_501_doner(client: TestClient):
    def call(params: dict, mask: bytes):
        return client.post(
            "/v1/recolor",
            headers={"X-Internal-Key": KEY},
            files={"image": ("image", png_bytes(), "image/png"), "mask": ("mask", mask, "image/png")},
            data={"params": json.dumps(params)},
        )

    assert call({"target_hex": "kirik-beyaz"}, png_bytes(channels=1)).status_code == 422
    assert call({"target_hex": "#F5F0E6"}, png_bytes(width=10, channels=1)).status_code == 422
    assert call({"target_hex": "#F5F0E6"}, png_bytes(channels=1)).status_code == 501
