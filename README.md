# ⚙️ Bantera — Backend API

> **.NET 10 REST API** powering authentication, video management, AI audio generation, and admin operations for the Bantera language learning platform.

![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-14%2B-336791?logo=postgresql)
![Docker](https://img.shields.io/badge/Docker-deployed-2496ED?logo=docker)
![EF Core](https://img.shields.io/badge/EF_Core-10-purple)

---

## 🔗 Related Repositories

| Repo | Description |
|---|---|
| [**bantera**](https://github.com/lisenhuang/bantera) | Flutter iOS app |
| **This repo** | .NET REST API backend (you are here) |
| [**bantera-website**](https://github.com/lisenhuang/bantera-website) | Next.js website + admin dashboard |

---

## 🗂️ Project Structure

```
BanteraApi/
├── Auth/              # JWT issuing, refresh token rotation, Apple Sign-In
├── Videos/            # Upload, transcription, cue alignment, search
├── Gemini/            # Google Gemini — dialogue generation, TTS, cue timing
├── RevAi/             # Rev.ai — word-level audio alignment
├── Storage/           # Cloudflare R2 (S3-compatible) object storage
├── Cloudflare/        # Cloudflare Workers AI — cover image generation
├── Profile/           # User profile & avatar management
├── Admin/             # Admin user/video/stats management
├── Account/           # Account deletion
├── Database/          # EF Core DbContext, entities, migrations
├── AiAudioDiagnostics/# Non-blocking diagnostic writer for alignment failures
└── Program.cs         # Minimal API setup & all route registrations (~2150 LOC)
```

---

## 🌐 API Surface

### Public

| Method | Route | Description |
|---|---|---|
| `GET` | `/` | Health check |
| `GET` | `/version` | API version |
| `GET` | `/api/public/learning-languages` | BCP-47 language catalog |
| `GET` | `/api/public/translation-languages` | iOS translation language codes |
| `POST` | `/api/auth/login` | Email/password login |
| `POST` | `/api/auth/apple` | Apple Sign-In |
| `POST` | `/api/auth/refresh` | Refresh token rotation |
| `GET` | `/api/videos/public` | Paginated public video feed (search + language filter) |
| `GET` | `/api/videos/{id}` | Single video metadata |
| `GET` | `/api/videos/{id}/file` | Stream video file (range requests supported) |

### Protected (Bearer token required)

| Method | Route | Description |
|---|---|---|
| `GET/PUT` | `/api/me/profile` | Fetch / update user profile |
| `POST` | `/api/me/profile-image` | Upload profile avatar |
| `POST` | `/api/me/videos` | Upload video with transcript |
| `GET` | `/api/me/videos` | List own videos |
| `DELETE` | `/api/me/videos/{id}` | Delete video |
| `POST` | `/api/me/audio/generate` | Generate AI practice audio (SSE, v1) |
| `POST` | `/api/me/audio/generate/v2` | Generate AI practice audio (SSE, v2 — with alignment) |
| `GET` | `/api/me/audio/jobs/pending` | Poll pending generation jobs |
| `POST/DELETE/GET` | `/api/me/saved/{videoId}` | Save / unsave / check saved videos |
| `GET` | `/api/me/saved` | List saved videos |
| `POST/DELETE/GET` | `/api/me/saved-cues` | Save, delete, list bookmarked transcript cues |
| `GET` | `/api/me/stats` | Upload & saved counts |
| `DELETE` | `/api/me` | Delete account |

### Admin (Bearer token + `role=admin`)

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/admin/stats` | Platform-wide stats |
| `GET/PATCH/DELETE` | `/api/admin/users/{id}` | User detail, role/status edit, deletion |
| `GET` | `/api/admin/users` | Paginated user list (search + sort) |
| `GET/DELETE` | `/api/admin/videos/{id}` | Video list + deletion |

---

## 🔑 Technical Deep Dives

### 🔐 JWT Auth with Refresh Token Rotation

Access tokens expire in 15 minutes. Refresh tokens (90-day rolling) are stored as BCrypt hashes with a separate SHA-256 lookup fingerprint — enabling fast DB lookup without exposing the hash:

```
Login
  └─► access token  (JWT, 15 min, HS256)
  └─► refresh token (64-byte random, BCrypt-hashed in DB)

Refresh
  └─► SHA-256 fingerprint lookup → BCrypt verify → issue new pair → revoke old
```

Clock skew is set to zero — tokens expire exactly on time with no grace period.

---

### 🤖 AI Audio Generation with Fallback Chain

`POST /api/me/audio/generate/v2` streams progress via **Server-Sent Events**:

```
started ──► dialogue ──► audio ──► aligning ──► done
                                              └─► error
```

Word-level cue alignment is attempted in order, falling back gracefully:

```
1. Rev.ai boundary alignment   (strictest — word boundary match)
   ↓ fails
2. Rev.ai strict alignment     (token flexibility)
   ↓ fails
3. Rev.ai tolerant alignment   (high-ratio threshold)
   ↓ fails / language unsupported
4. Gemini cue timing           (audio submitted to Gemini for timestamps)
   ↓ fails
5. Linear estimation           (distribute cues by character count)
```

All alignment failures are recorded in `ai_audio_short_cue_diagnostics` for analysis.

---

### 🗄️ Database Schema

EF Core 10 + PostgreSQL. Key tables:

```
users
  └── user_identities      (email / apple per-provider credentials)
  └── user_sessions        (refresh token tracking, per-device)
  └── user_videos          (uploaded + AI-generated audio)
        └── user_saved_videos
        └── user_saved_cues
        └── user_audio_jobs
        └── ai_audio_short_cue_diagnostics
```

Transcript data (`cues`, `dialogue lines`, `word timing`) is stored as **JSONB** in Postgres for schema flexibility without migrations.

Migrations are applied automatically on startup.

---

### ☁️ External Services

| Service | Purpose |
|---|---|
| **Google Gemini** | Dialogue text generation, TTS audio synthesis, transcript correction, cue timing fallback |
| **Rev.ai** | Word-level audio alignment (EN, FR, DE, IT, ES) |
| **Cloudflare R2** | Object storage for videos, audio, profile images, cover images |
| **Cloudflare Workers AI** | Cover image generation (Flux 1 Schnell, 512×512) |
| **Apple Sign-In** | Identity token validation via Apple's public key endpoint |
| **PostgreSQL** | Primary database |

---

## 📦 Key Dependencies

| Package | Purpose |
|---|---|
| `Microsoft.AspNetCore.Authentication.JwtBearer` | JWT bearer auth |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | EF Core PostgreSQL driver |
| `BCrypt.Net-Next` | Password hashing |
| `AWSSDK.S3` | Cloudflare R2 (S3-compatible) client |
| `SixLabors.ImageSharp` | Avatar image resizing |
| `Swashbuckle.AspNetCore` | Swagger/OpenAPI docs |

---

## 🏁 Getting Started

```bash
# Run locally
dotnet run --project BanteraApi
# http://localhost:5218         → health check
# http://localhost:5218/swagger → Swagger UI

# Or with Docker
docker compose up --build
# http://localhost:8080         → health check
# http://localhost:8080/swagger → Swagger UI
```

**Required configuration** (via `appsettings.Development.json` or env vars):

| Key | Purpose |
|---|---|
| `ConnectionStrings:Postgres` | PostgreSQL connection string |
| `Jwt:Secret` | HMAC-SHA256 signing key |
| `R2:AccountId/AccessKeyId/SecretAccessKey/BucketName` | Cloudflare R2 credentials |
| `Gemini:ApiKeys` | Google Gemini API keys |
| `RevAi:AccessToken` | Rev.ai access token |
| `Cloudflare:AccountId/ApiToken` | Cloudflare Workers AI credentials |

---

## 🚀 Deployment

Runs in Docker behind a **Cloudflare Tunnel** on an Ubuntu server. CI (GitHub Actions) builds the image on every push to `main` as a build check. Deployment is triggered manually on the server:

```bash
bash /srv/bantera-backend/deploy/deploy.sh
```

The script pulls latest from `main`, rebuilds the Docker image, and restarts the container. Cloudflare Zero Trust routes the public hostname to `http://localhost:8080`.

```bash
# Tail logs
docker compose logs -f
```

---

## 📊 Codebase Stats

| Metric | Value |
|---|---|
| Framework | .NET 10 Minimal API |
| API endpoints | ~35 |
| Database tables | 8 |
| External services | 6 |
| EF Core migrations | 20+ |

---

## 📄 License

Private — all rights reserved.

---

*README last updated: 2026-04-30*
