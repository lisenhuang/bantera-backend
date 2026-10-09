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

## Audio duration estimates

Dialog generation learns speaking rates from saved AI audio with actual durations and non-estimated transcripts. It counts spoken text with the same language-specific counter used for new scripts (words for English, written characters for Chinese/Japanese, etc.), excluding uploaded human videos and invalid/very short samples.

For each difficulty, the planner uses the median of at least three samples, preferring the exact locale and TTS model, then the exact locale, then the broader spoken language with/without a model match. Cantonese (`zh-HK`, `yue-CN`) stays separate from Mandarin. Legacy lessons without a difficulty are intermediate; missing model attribution does not exclude a lesson from general history. Queries consider at most 300 lessons, ordered by exact locale then recency, and cache the samples for five minutes. No transcript content is sent as history to the model; only the aggregate rate and requested script length enter the prompt.

When history is insufficient, unavailable, or takes more than three seconds to load, generation uses the existing language/difficulty baseline. History lookup failures are cached for 30 seconds. Neither an out-of-range script word count nor an out-of-range audio duration causes regeneration. Other existing validation/provider error handling is unchanged.

This requires no migration or new environment variables. After deployment, generate a lesson from an existing client and check the `script_duration_checked` pipeline event for `rateSource`, `historySampleCount`, `unitsPerMinute`, and `diagnosticOnly: true`. Check `audio_duration_measured` for the actual duration and confirm the generation finishes even when a duration target is missed. Fresh samples become available after the five-minute cache expires.

## Dialogue rejection and model availability

Dialogue generation accepts neutral everyday interpretations and does not invent political connections to reject ordinary scenarios. Existing political-content restrictions remain in place. Content refusals are reconsidered up to three times on the same model; a genuine refusal does not bypass policy by rotating keys or models. Admin test sessions still make only one attempt.

Rejection events now include the model's reason and a short explanation when supplied. Final `content_rejected` events preserve those details for v1, v2, and v3. Client error payloads keep the same shape and use controlled messages for `restricted_topic`, `no_suitable_news`, and `same_gender_required`; raw provider explanations remain in admin logs. Older rejection JSON without these fields is still accepted.

HTTP 503 now skips the remaining keys for the unavailable model and immediately tries its configured fallback. If no fallback is configured, or it is also unavailable, the step fails promptly. Only HTTP 429 rotates keys and applies the existing per-key/model quota cooldown. Other errors stop key rotation and use the configured fallback if available. Confirmed invalid keys are still disabled for future calls; HTTP 503 does not disable keys or apply a key cooldown.

Backend version 1.0.158 requires no migration, new environment variables, app update, or website update. A human must deploy it using the server's existing source-build procedure and production environment. After deployment, confirm `/version`, generate the delayed-bus, supermarket, doctor, and movie-debate scenarios with both configured text models, and inspect rejection details in the admin timeline. Confirm explicitly prohibited scenarios remain rejected. Model output is nondeterministic, so passing local HTTP simulations does not establish the live refusal rate. For a naturally occurring 503, expect `model_unavailable` followed directly by `model_fallback_attempted` without attempts on the remaining primary keys.

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

Backend, website and PostgreSQL run on **Oracle AU (Melbourne)** at `ubuntu@168.138.25.22`, behind Cloudflare Tunnel. Follow the [Oracle AU deployment and rollback runbook](docs/oracle-au-deployment.md). Deploy only when explicitly requested: build and health-check candidate containers, switch connectors with an overlap, verify both public domains, and roll back automatically if promotion checks fail. The old `/srv/...` instructions and direct-replacement scripts are obsolete.

CI (GitHub Actions) checks Docker builds on pushes to `main`; a successful push is not evidence of deployment. Find the active containers in `/home/ubuntu/releases/bantera-current.json` rather than assuming the historical names still serve public traffic.

```bash
# Tail logs
docker logs -f <active-backend-container>
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

### AI conversation continuity (1.5.0)

The optional `createdAt` field in AI history turns carries an ISO-8601 timestamp
from device-owned history. Existing clients without it remain supported; absent
or materially future timestamps produce unknown timing rather than an invented
absence. Gemini receives dated history and a current gap cue at session setup and
voice-message commit, including transcript recovery. Small gaps continue directly;
hours/days permit a brief contextual welcome. These are flexible prompt cues,
not extra audio triggers; call silence and reconnect behaviour stay unchanged.
Local calendar dates use the device time zone (UTC offset fallback), while elapsed
time uses UTC across midnight and daylight-saving transitions. No DB migration,
new environment variable or persisted conversation data is added.

New messages also retain their original device time zone and UTC offset. The
coach can interpret a dated future plan and ask whether it happened, without
assuming attendance. A changed time zone is a conversational cue, not proof of
travel or location. Historical zones missing from older messages remain unknown;
clearing history removes these details. No automatic reminders are inferred.

## Bantera AI reasoning settings

The admin dashboard at `/dashboard/bantera-ai` offers model-specific reasoning controls. Gemini 3.1 Flash Live Preview supports Minimal, Low, Medium and High; 3.8 Live Extended Thinking supports Low, Medium and High; standard 3.8 Live has fixed automatic reasoning. Gemini 2.5 Native Audio Preview (September/December 2025) uses dynamic/off or token-budget presets instead. Unknown model IDs use provider defaults until their capabilities are verified in `AiLiveReasoning`.

The backend is authoritative: `GET /api/admin/bantera-ai` adds `reasoning`, `reasoningByModel` and `reasoningCapabilities`; `PUT` accepts optional `reasoning` and rejects incompatible choices. Settings are stored per canonical model in existing `app_settings` rows (`chat.ai.reasoning.` plus a SHA-256 model-name hash). Older dashboard requests without reasoning preserve saved choices. Existing installations retain their prior defaults (Low for Extended Thinking, omitted thinking configuration otherwise). No migration or new environment variables are needed.

Selections apply when a new voice-message session, live call, callback or reminder starts, including retries. Active calls retain their original setting. Higher reasoning may increase latency/cost. Explicit thinking settings use the provider's normal output allowance rather than the old 2,048-token combined cap, so reasoning does not consume the entire reply allowance; the coaching prompt still requests brief speech.

Deploy backend before website. A website connected to an older backend explains that reasoning controls need the updated backend and keeps model/voice editing available. After deployment, verify admin GET, change a compatible level, reload to confirm it, switch models to confirm different options, and smoke-test a voice reply and call with the chosen model. This change is additive for published apps.

Sources checked 9 October 2026: [Live capabilities](https://ai.google.dev/gemini-api/docs/live-api/capabilities), [Live thinking](https://ai.google.dev/gemini-api/docs/live-api/thinking), [thinking budgets](https://ai.google.dev/gemini-api/docs/generate-content/thinking), [3.1 Live migration](https://ai.google.dev/gemini-api/docs/models/gemini-3.1-flash-live-preview).

## Rolling conversation memory and Live resumption

App 2.8.0 stores one account-isolated rolling summary in its local history directory, with `generatedAt`, a covered-message/fragment checkpoint, and the original covered-message timestamp. Older messages are summarised in batches of at most 24 fragments / 24,000 characters, merging the previous summary (at most 6,000 characters). Even a single very long message is split. Each successful atomic write replaces the prior summary; failed requests preserve the checkpoint. Clearing history removes the summary and rotates the conversation identity; an in-flight result cannot restore cleared data. The latest 20 messages remain as recent context. While initial compaction catches up, the existing bounded history remains available.

Authenticated, rate-limited `POST /api/chat/ai/summary` uses the dashboard's existing text model/fallback and quota-key rotation. It processes context transiently, stores no chat/summary in the DB and logs only operational metadata. The phone calls it in the background so voice replies do not wait for summarisation. No schema or secret changes. Summaries can omit details and are not perfect memory.

Live resumption is opt-in through an optional device conversation UUID. The server keeps up to 1,024 opaque handles and hashes in process RAM for two hours after disconnection, bound to the authenticated user, device conversation, mode, model, voice, reasoning, language/accent, name and learning level. Only completed, resumable checkpoints matching the last visible reply can be reused. The original eligible API key is preferred. Rejected/expired handles get one fresh setup on the same key; explicit quota failures retain key switching. Successful resumption omits the previous system instruction and history replay; current clock/time-zone updates still go through. New sessions get the device summary and recent history. Calls and voice messages use separate session identities because their VAD/turn policies differ. Server restart, cleared history, changed settings or missing checkpoints safely start fresh. Session handles and hashes are not durable chat storage, and no raw handle/key is sent to the phone or logged.

Product vocabulary guidance teaches the conversational model the spelling/pronunciation of Bantera and distinguishes app references from unrelated similar words. It does not rewrite original transcripts or guarantee Google's independent Live input transcription recognises every occurrence. Google's dedicated transcription model supports custom vocabulary; this change keeps the selected conversational Live model.

Verified against [Google session management](https://ai.google.dev/gemini-api/docs/live-api/session-management) and [Live transcription capabilities](https://ai.google.dev/gemini-api/docs/live-api/live-transcribe), 9 October 2026.

### Admin web-search diagnostics and private history transport

`POST /api/admin/ai-settings/search-test` is admin-only and limited to five tests per minute per admin. It accepts a 3–1,000 character `query`, calls the configured primary/fallback search models with the same provider routing as lesson generation, and returns elapsed time, the actual model, sources and a diagnostic reference. A completed answer alone is not success: Gemini must return search queries and safe HTTP(S) grounding sources; ChatGPT must complete an actual web-search tool call. Streamed search events count even when the final response omits its output array. Each provider attempt has a 30-second deadline and the overall diagnostic has a 75-second deadline, leaving room for fallback. Operational events record outcome, model and test ID, not the query, response text or credentials. The dashboard presents verified, unverified, failed and timed-out outcomes; this test does not change models or publish a lesson. ChatGPT uses the connected subscription account; Gemini uses the configured search keys.

Fresh Live sessions now receive dated history as one labelled, uncompleted background user turn rather than replayed assistant speech. This avoids feeding internal timestamps into assistant transcript prefill, while retaining context across provider resumption. Old timing markers are removed from historical model text. The app also suppresses that exact legacy marker in restored/streamed model transcripts. Existing audio files are not rewritten. A real Gemini test verifies recall from fresh background context and again after resuming without history replay.

### ChatGPT subscription connection (admin OAuth)

Backend **1.10.0** and website **0.5.0** provide device-code sign-in on
`/dashboard/ai`, following the existing v2en integration. Click **Continue with
ChatGPT**, open the OpenAI page, and enter the displayed one-time code. The page
polls until authorised or until the ten-minute attempt expires. Enable Codex
device-code authentication in ChatGPT security settings if OpenAI requires it.

This is an admin provider connection to the Codex subscription transport, not a
Bantera user login or an API-key connection. It uses the public Codex client
identifier, OpenAI device-auth endpoints, and `chatgpt.com/backend-api/codex` for
model discovery and response tests. These transport details may change; do not
mix these credentials with the separate hosted SIWC/API transport. Existing
Gemini production model routing remains unchanged. Connecting alone does not
activate GPT for lesson generation or live speech.

Required server settings:

- `ChatGptConnection__EncryptionKey`: a separate random 32-byte base64 key, retained in server secret storage across releases.
- `ChatGptConnection__StorageDirectory`: an absolute persistent private directory, mounted into API release containers (production: `/var/lib/bantera/chatgpt`).

Device login does **not** require a hosted web client registration, client secret,
`PlanAccessApproved`, or a Bantera OAuth redirect URL. The older registered
hosted `/start` and `/complete` endpoints remain separate and inactive by default.

Admin-only endpoints under `/api/admin/ai-settings/chatgpt`:

- `GET /`: configured/connected account status, never credentials.
- `POST /device/start`: creates an admin/browser-bound attempt and returns a user code and fixed OpenAI URL; provider device identifiers stay encrypted server-side.
- `POST /device/poll`: honours the provider interval, validates the admin, browser cookie binding and attempt, and exchanges a single-use approval code.
- `POST /device/cancel`, `POST /disconnect`: cancel pending sign-ins; disconnect deletes stored tokens locally. Provider-side revocation is not claimed.
- `GET /models`: fetches the connected account's current model catalogue and per-model reasoning levels, using the current stable Codex version fetched from npm (six-hour version cache). No fallback model list is invented.
- `POST /test`: accepts model, reasoning, prompt and optional search. Runs a backend subscription request, requires a completed SSE response, and only verifies web search when an actual completed search tool call is present. It does not change production model selections.

Tokens and pending device credentials are AES-GCM encrypted with owner-only
permissions. Cross-process file locking and atomic replacement protect rotating
refresh tokens across containers. Browser JavaScript receives neither access nor
refresh tokens. Preserve the dedicated storage mount and encryption key during
all future deployments and rollback. Never log auth request/response bodies.
Model tests are rate-limited to five per minute, bounded to 90 seconds, and use
the connected account's allowance. A real account login and test are necessary
to establish model/tool availability; an enabled button does not prove access.

### Text and search routing (1.11.0)

The AI dashboard now independently configures primary/fallback text and web-search models. Text selections accept live Gemini catalogue IDs or `chatgpt/<account-model-id>` from the connected subscription catalogue. Search selections offer the approved Gemini model and connected GPT models. GPT reasoning choices are validated against that model’s current advertised levels; an empty choice uses the provider default. Catalogue or test responses never change production selections automatically. Gemini search is restricted to `gemini-2.5-flash`, using only keys starting with `AIzaSy`. This applies to primary and fallback searches; an empty eligible key pool never falls back to other keys. Legacy model/prefix environment overrides cannot relax the restriction. Other Gemini models remain available for non-search text generation.

Text routing covers dialogue generation, transcript correction, word alignment and incremental conversation summaries. Existing output validation and content-policy refusal handling are preserved. Quota/provider failures can fall back across providers. TTS and Live audio remain separately configured. These server search settings apply to web-assisted lesson generation and the admin test; the AI chat’s device-run `search_web` tool remains on-device.

Settings use existing `app_settings` rows (`ai.textReasoning`, `ai.fallbackTextReasoning`, `ai.searchModel`, `ai.fallbackSearchModel`, `ai.searchReasoning`, `ai.fallbackSearchReasoning`). No migration or new secret is required. Older admin requests that omit the new fields preserve search settings and retain reasoning only for an unchanged model.

Voice-message sessions now receive reply-opening rules separate from audio-call greetings. Every committed voice turn refreshes the current gap directive, including resumed sessions. Recent or unknown gaps do not trigger a return greeting; long gaps allow one naturally. Prompt revision 3 invalidates older resumption checkpoints once so their stale call-opening instructions are not retained.

GPT attempts use a saved `ai.gptTimeoutSeconds` setting (default 180; admin range 30–300 seconds), shared by GPT text, search, and subscription tests. Missing update fields preserve the saved value; null restores the default. No migration or environment variable is needed. Each fallback gets its own attempt budget, while caller cancellation, the 20-second conversation-summary budget, and the 10-minute overall lesson limit remain authoritative. Gemini request limits and on-device app search are unchanged.
