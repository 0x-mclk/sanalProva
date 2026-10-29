# syntax=docker/dockerfile:1
# Sanal Prova image-worker (FastAPI + OpenCV, GPU/PyTorch yok). Bağlam: ./image-worker (bkz. docker-compose.yml).
# Durumsuzdur; yalnızca iç ağdan erişilir. İç anahtar çalışma zamanında IMAGE_WORKER_INTERNAL_KEY ile gelir (imajda yok).
FROM python:3.12-slim
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1
WORKDIR /app
RUN useradd --system --uid 10001 --no-create-home app
COPY pyproject.toml ./
COPY app ./app
RUN pip install .
ENV IMAGE_WORKER_SEGMENTATION=mock
USER app
EXPOSE 8090
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8090"]
