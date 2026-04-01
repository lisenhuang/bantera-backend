#!/usr/bin/env bash
# deploy.sh — run on the Ubuntu server to pull the latest image and redeploy.
set -euo pipefail

COMPOSE_FILE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/docker-compose.yml"

echo "==> Pulling latest image from GHCR..."
docker pull ghcr.io/lisenhuang/bantera-backend:latest

echo "==> Restarting container..."
docker compose -f "$COMPOSE_FILE" up -d

echo "==> Done. Container status:"
docker compose -f "$COMPOSE_FILE" ps
