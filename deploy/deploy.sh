#!/usr/bin/env bash
# deploy.sh — run on the Ubuntu server to pull latest code and redeploy.
# Can be triggered manually or via a webhook/cron.
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "==> Pulling latest code..."
git -C "$REPO_DIR" pull

echo "==> Rebuilding and restarting container..."
docker compose -f "$REPO_DIR/docker-compose.yml" up --build -d

echo "==> Done. Container status:"
docker compose -f "$REPO_DIR/docker-compose.yml" ps
