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
# http://localhost:5218        → Hello World
# http://localhost:5218/swagger → Swagger UI
```

Or with Docker:

```bash
docker compose up --build
# http://localhost:8080        → Hello World
# http://localhost:8080/swagger → Swagger UI
```

---

## Deploy to Ubuntu (Docker + GitHub deploy key + Cloudflare Tunnel)

### 1. Add a GitHub deploy key

1. On your Ubuntu server, generate a key:
   ```bash
   ssh-keygen -t ed25519 -C "bantera-server" -f ~/.ssh/github_deploy -N ""
   cat ~/.ssh/github_deploy.pub
   ```
2. In GitHub → repo **Settings → Deploy keys**, click **Add deploy key**, paste the public key. Read-only is enough.
3. Configure SSH on the server to use it for GitHub:
   ```bash
   cat >> ~/.ssh/config <<'EOF'
   Host github.com
       IdentityFile ~/.ssh/github_deploy
       IdentitiesOnly yes
   EOF
   ```

### 2. Clone the repo on the server

```bash
git clone git@github.com:<YOUR_ORG>/bantera-backend.git /srv/bantera-backend
```

### 3. Start the container

```bash
cd /srv/bantera-backend
docker compose up --build -d
```

### 4. Point Cloudflare Tunnel at the container

In your Cloudflare Zero Trust dashboard, create a tunnel and set the public hostname to route to `http://localhost:8080`.

### 5. Deploy updates

```bash
bash /srv/bantera-backend/deploy/deploy.sh
```

This does `git pull` + `docker compose up --build -d`. Automate it with a cron job, GitHub Actions, or any webhook runner.

---

### Logs

```bash
docker compose logs -f
```
