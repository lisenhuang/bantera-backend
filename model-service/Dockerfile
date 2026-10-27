FROM python:3.12-slim-bookworm
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg time libgomp1 && rm -rf /var/lib/apt/lists/*
RUN pip install --no-cache-dir fastapi==0.115.12 uvicorn==0.34.2
WORKDIR /opt/service
COPY bin/ /opt/bin/
COPY notices/ /opt/notices/
COPY service.py generation.py ./
ENV RAYON_NUM_THREADS=2 OMP_NUM_THREADS=2 OPENBLAS_NUM_THREADS=1
USER 10001:10001
CMD ["uvicorn", "service:app", "--host", "0.0.0.0", "--port", "8090", "--workers", "1", "--no-access-log", "--log-level", "warning"]
