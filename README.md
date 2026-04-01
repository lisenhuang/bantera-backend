# Bantera Backend API

.NET 10 minimal Web API — runs in Docker, exposed via Cloudflare Tunnel.

## Endpoints

| Method | Path       | Description |
| ------ | ---------- | ----------- |
| GET    | `/`        | Hello World |
| GET    | `/swagger` | Swagger UI  |

## Run locally

```bash
dotnet run --project BanteraApi
# http://localhost:5218         → Hello World
# http://localhost:5218/swagger → Swagger UI
```

Or with Docker:

```bash
docker compose up --build
# http://localhost:8080         → Hello World
# http://localhost:8080/swagger → Swagger UI
```

---

## CI — Build & Publish to GHCR

Every push to `main` triggers the GitHub Actions workflow at `.github/workflows/docker-publish.yml`.

**What it does:**
1. Checks out the code
2. Logs in to GitHub Container Registry using the built-in `GITHUB_TOKEN` (no secrets to configure)
3. Builds the Docker image from the repo's `Dockerfile`
4. Pushes two tags to GHCR:
   - `latest` — always points to the most recent `main` build
   - `sha-<short-commit>` — immutable tag for that exact commit (e.g. `sha-a1b2c3d`)

**Image name:**
```
ghcr.io/lisenhuang/bantera-backend
```

**Published tags example:**
```
ghcr.io/lisenhuang/bantera-backend:latest
ghcr.io/lisenhuang/bantera-backend:sha-a1b2c3d
```

The image is only pushed if the Docker build succeeds. A failed build produces no new image.

---

## Deploy to Ubuntu (GHCR + Cloudflare Tunnel)

### 1. Authenticate Docker on the server

Generate a GitHub Personal Access Token (PAT) with `read:packages` scope, then:

```bash
echo <YOUR_PAT> | docker login ghcr.io -u <YOUR_GITHUB_USERNAME> --password-stdin
```

### 2. Pull and run the latest image

```bash
docker pull ghcr.io/lisenhuang/bantera-backend:latest
```

Update `docker-compose.yml` on the server to use the pre-built image instead of building locally:

```yaml
services:
  api:
    image: ghcr.io/lisenhuang/bantera-backend:latest
    container_name: bantera-api
    restart: unless-stopped
    ports:
      - "8080:8080"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
```

Then start it:

```bash
docker compose up -d
```

### 3. Update to a new release

```bash
docker pull ghcr.io/lisenhuang/bantera-backend:latest && docker compose up -d
```

Wrap this in `deploy/deploy.sh` or a cron/webhook to automate.

### 4. Point Cloudflare Tunnel at the container

In Cloudflare Zero Trust, set the public hostname to route to `http://localhost:8080`.

---

### Logs

```bash
docker compose logs -f
```
