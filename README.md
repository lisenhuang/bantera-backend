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

## CI — Build Check (GitHub Actions)

Every push to `main` triggers `.github/workflows/docker-publish.yml`.

**What it does:**
- Checks out the code
- Builds the Docker image to verify the build succeeds

**What it does NOT do:**
- Does not push any image to a registry
- Does not deploy anything
- Requires no credentials or secrets

If the build fails, the commit is marked red on GitHub. That's it.

---

## Production Deployment (server-side)

Deployment is managed entirely on the Ubuntu server — GitHub Actions is not involved.

### Flow

```
push to main
    ↓
GitHub Actions builds image (CI check only)
    ↓
On the server: git pull + docker compose up --build -d
```

### Deploy

SSH into the server and run:

```bash
bash /srv/bantera-backend/deploy/deploy.sh
```

This script:
1. Pulls the latest code from `main`
2. Rebuilds the Docker image locally
3. Restarts the container

### First-time server setup

```bash
git clone git@github.com:lisenhuang/bantera-backend.git /srv/bantera-backend
cd /srv/bantera-backend
docker compose up --build -d
```

### Point Cloudflare Tunnel at the container

In Cloudflare Zero Trust, set the public hostname to route to `http://localhost:8080`.

---

### Logs

```bash
docker compose logs -f
```
