# Bantera deployment on Oracle AU

Bantera **backend, website and PostgreSQL database all run on Oracle AU (Melbourne)**. Use this host for future deployment requests; do not assume Oracle SG. Deploy only when explicitly requested. Last inspected: 7 October 2026.

## Connection and routing

```bash
ssh -i ~/.ssh/oracle-melbourne.key ubuntu@168.138.25.22
```

| Component | Existing checkout / runtime |
| --- | --- |
| Backend | `/home/ubuntu/apps/bantera-backend` |
| Website | `/home/ubuntu/apps/bantera-website` |
| PostgreSQL | Docker container `bantera-postgres`, persistent database volume |
| Release snapshots | `/home/ubuntu/releases/bantera-20261006-1` |
| Active release record | `/home/ubuntu/releases/bantera-current.json` |
| Public API | `https://api.bantera.app` through Cloudflare Tunnel |
| Public website | `https://bantera.app` through Cloudflare Tunnel |

SSH keys and production settings stay outside Git. The legacy backend settings file is `apps/bantera-backend/deploy/.env.production`; website settings are `apps/bantera-website/.env.production`. Existing container runtime settings are authoritative for this release and were cloned server-side without printing values. Protected runtime snapshots contain secrets and must never be committed or downloaded into a repository.

The shared Cloudflare Tunnel also serves other domains. Preserve its tunnel identity, credentials, all unrelated ingress entries and every existing Docker network. Do not change public DNS or restart the only healthy connector to deploy Bantera.

## Release preparation

1. Fetch the relevant Git remotes and inspect local changes. Follow the repository's version/build/test requirements. Do not reset user changes or assume a push is a deployment.
2. Inspect `bantera-current.json`, Docker containers and the active connector's configuration. Container names and source checkouts alone do not establish what is live.
3. Create a new immutable release directory. Archive the reviewed source and record its Git SHA, uncommitted changes (if explicitly deploying the working tree) and archive SHA-256. Keep `.env*`, development secrets, `.git`, build caches and dependencies out of Docker build contexts.
4. Save the previous container/image/network/configuration metadata in mode-0600 server files. Take a consistent `pg_dump -Fc` backup before any migration and verify it is readable. Do not run the backup script that sends Telegram messages as part of a deployment.
5. Build uniquely tagged backend and website Docker images while the current services keep running. Retain old images and containers. Do not overwrite a moving `latest` tag as the only rollback reference.
6. Start the backend candidate using the current production environment on the existing database networks, with its host test port bound to loopback only. Startup applies migrations. Permit only backward-compatible migrations while the old application remains online.
7. Verify backend `/version`, public lesson listing, authenticated-route rejection, migration state and startup logs. Start the website candidate, then verify `/`, `/download`, `/dashboard/login`, the AI admin login redirect, and its deployment marker.

The server-only website build scaffolding uses Node 22 and pnpm, allowing build scripts for `protobufjs`, `sharp` and `unrs-resolver`. Retain the reviewed Dockerfile and dependency-build configuration in the release snapshot. Exclude production env files from the image; inject settings at runtime.

## Rolling Cloudflare Tunnel handover

- Clone the current locally managed connector configuration. Change only the `api.bantera.app` and `bantera.app` origins to the new release's unique container names.
- Attach the candidate connector to **all** networks of the old connector, including unrelated domains' networks. Mount the existing credentials read-only and the release-specific configuration read-only.
- Validate the ingress configuration before starting it. Run the same verified cloudflared image as the current connector; do not combine an infrastructure upgrade with the release.
- Start the new connector while the old one remains online. Wait for its local `/ready` endpoint to return HTTP 200.
- Gracefully stop the old connector only after the replacement is connected. Cloudflared stops accepting new requests and drains existing requests, with a default 30-second grace period. Long-lived connections may reconnect; do not claim every connection is uninterrupted.
- Verify both **public HTTPS domains**, exact backend version and website release marker. Check main/download/login pages, API lesson listing and authentication gates. Monitor repeatedly during handover and require a stable post-switch window.
- On any promotion failure, start the previous connector, wait for it to reconnect, then drain the candidate. Verify the old API version and website via their public domains. Keep previous application containers available throughout.

Source: [Cloudflare replicas](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/configure-tunnels/tunnel-availability/deploy-replicas/) and [grace-period behaviour](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/configure-tunnels/run-parameters/#grace-period).

## Commands and rollback for the October 6 release

The reviewed release-specific controller is preserved on Oracle AU:

```bash
cd /home/ubuntu/releases/bantera-20261006-1
python3 deploy.py build
python3 deploy.py stage
python3 deploy.py promote
```

These are **sequential phases, not a command to rerun blindly**. `stage` creates uniquely named candidates and applies the additive migration. It is not idempotent. Inspect existing state before retrying. `promote` automatically restores the previous connector if its public checks fail; it requires a two-minute stable verification window. Its rollout runs under `nohup` so closing SSH does not cancel the process. A file lock prevents concurrent controller runs.

Manual rollback for this specific release:

```bash
python3 /home/ubuntu/releases/bantera-20261006-1/deploy.py rollback
```

Rollback restores the original `cloudflared` connector and routes to the retained `bantera-api` and `bantera-website` containers. It does **not** restore the database: the new `ai_callbacks` table is additive and safe for the old app to ignore. Restoring the pre-release database would discard subsequent user writes and requires a separate recovery decision. Stop obsolete candidate background workers only after traffic has safely returned and active work has been considered.

This controller's expected versions, names and ports are specific to this release. For another release, create a new directory and update the plan from the then-current active record; do not reuse its old-version assumptions or reserved ports. Retain rollback artifacts until the release is accepted. `availability.jsonl`, `status`, build logs and `source-manifest.json` provide the audit trail. The automatic health rollback covers deployment, not indefinite monitoring.

## Current release scope

- Backend **1.0.160**, based on `5378d27` plus the tested local PCM/WAV changes. Those changes were uncommitted at deployment preparation.
- Website **0.1.57**, commit `2ef7151`.
- App **2.0.120+308**, installed only after the backend and website pass deployment checks and the user explicitly requests installation.
- Compatibility: **GO for existing published app clients**. AI routes are new; existing human DMs and authentication remain compatible.
- Migration: `20261006101252_AddAiCallbacks`, creates only `ai_callbacks` and associated indexes/foreign keys. No destructive schema change.
- No new required secrets. Existing Gemini keys must have Live access; scheduled iPhone callbacks use existing APNs configuration. Optional `BanteraAi__LiveModel` sets the default model.
- AI voice messages require no server ffmpeg. Existing `lame` remains for other audio generation.
- Real iPhone recording, APNs/CallKit callbacks, translation packs and nine-minute call timing still need device checks; HTTP availability does not establish those behaviours.

## Legacy scripts and automation

Do not use `apps/*/deploy/redeploy-server.sh` for the new procedure: they reset checkouts, replace live containers directly, and the website script restarts the shared tunnel. Their associated auto-deploy scripts are not the release controller above.

At inspection, there were no active Bantera deployment jobs in Ubuntu/root cron, system timers or OpenClaw schedules. Old auto-deploy logs stopped in May. The active daily Bantera DB backup schedule is separate. Recheck schedules and hooks before future pushes/deployments; do not rely on this historical observation forever.

## Verified deployment result

Release `bantera-20261006-1` completed successfully on 6 October 2026 at 23:59 NZDT (10:59 UTC). Both public domains served the expected release. The replacement connector is `cloudflared-20261006-1`; the active containers are `bantera-api-20261006-1` and `bantera-website-20261006-1`. Their loopback-only test ports are 18080 and 13001, and connector readiness is on 12000. The original containers remain available for rollback.

The first attempt encountered a Docker read-only bind-mount conflict before changing traffic. Automatic fallback completed and verified both old public domains. The corrected attempt mounts the candidate configuration at `/release.yml`, outside the read-only credentials directory. It then completed the rolling handover and stability window. No non-200 responses were observed by the public availability sampler; this does not prove every individual connection was uninterrupted.

The source archive checksums, private runtime snapshots, database backup, staged image IDs, controller, rollout logs and rollback command are preserved in the release directory. The deployed PCM/WAV backend changes and these runbook updates still require a separate explicit commit/push request.

The updated app **2.0.120 (308)** was subsequently installed on the user's iPhone 17 Pro on 7 October 2026. It includes conservative on-device AI spoken-word credit: detected mixed-language or uncertain utterances receive zero credit, and a durable event ID prevents duplicate counting. Detailed app behaviour is documented in `app/docs/ai-spoken-word-counting.md` in the workspace.

Device installation was confirmed through CoreDevice (version 2.0.120, build 308). Automatic launch was blocked because the iPhone was locked; the user must unlock and open Bantera for the physical-device smoke checks.


## Current release: 7 October 2026, 00:43 NZDT

Release **`bantera-20261007-1`** completed at 11:43:35 UTC on 6 October (00:43:35
NZDT on 7 October). Backend **1.0.161** and website **0.1.58** are live. The
active record is `/home/ubuntu/releases/bantera-current.json`; the release root is
`/home/ubuntu/releases/bantera-20261007-1`.

- API container `bantera-api-20261007-1`, loopback port **18081**.
- Website container `bantera-website-20261007-1`, loopback port **13002**.
- Connector `cloudflared-20261007-1`, readiness loopback port **12001**.
- Previous API, website and connector ending in `20261006-1` are retained.
- A verified PostgreSQL backup (3,240,717 bytes), private runtime snapshots,
  source archive hashes and all rollout logs are retained in the release directory.
- **156/156** sampled public-domain responses were HTTP 200. Home, download,
  login, public lessons, exact API version and website release marker passed;
  admin auth gates returned 401 (API) and 307 (website).
- No schema migration or new secret/configuration is required for this release.
  Published apps remain compatible. It adds the admin Live voice catalogue,
  persists the voice alongside the model atomically, and accepts 180-second WAV
  voice messages. Older model-only admin writes preserve the existing voice.
- Source includes reviewed uncommitted backend/website changes; deployment is not
  evidence of a Git commit or push.

Rollback for **this** release:

```bash
python3 /home/ubuntu/releases/bantera-20261007-1/deploy.py rollback
```

This restores `cloudflared-20261006-1` and the previous active release record,
without restoring or discarding database writes. Promotion already verified a
stable public-domain window and would have rolled back automatically on failure.

## Voice streaming release, 7 October 2026 (NZDT)

Release **bantera-20261007-2** completed at **2026-10-06 13:03:43 UTC**.
Backend **1.0.162** is live in `bantera-api-20261007-2` (loopback port 18082).
Website **0.1.58** remains in the verified `bantera-website-20261007-1` container
(port 13002). The current connector is `cloudflared-20261007-2` (readiness 12002).
The active record is `/home/ubuntu/releases/bantera-current.json`.

This release deployed the tested backend working tree; exact Git base, dirty paths
and source archive hash are in the release's `source-manifest.json`. Build, staging
and promotion scripts are preserved in `/home/ubuntu/releases/bantera-20261007-2`.
No new migration or environment variables were required. The pre-release database
backup was verified with `pg_restore --list` (3,241,677 bytes).

All **128** HTTP availability samples across the API and website returned 200
through the monitored handover. Public `/version` returned 1.0.162, the new voice
WebSocket route rejected unauthenticated requests with 401, and the website kept
its existing release marker. This establishes HTTP availability, not uninterrupted
pre-existing WebSocket calls. Real Gemini streaming was separately tested before
release, with both transcriptions, preserved context and reply audio before turn
completion.

Rollback, if needed:

```bash
python3 /home/ubuntu/releases/bantera-20261007-2/deploy.py rollback
```

It restores `cloudflared-20261007-1` and API 1.0.161; the website stays unchanged.
Old containers are retained. Application rollback does not restore the database.
The next deployment must derive its names, ports and previous connector from the
active release record rather than reusing this release controller unchanged.

## Callback confirmation release, 7 October 2026, 14:14 NZDT

Release **bantera-20261007-3** completed at **2026-10-07 01:14:45 UTC**.
Backend **1.0.163** is live in `bantera-api-20261007-3` (loopback 18083),
through `cloudflared-20261007-3` (readiness 12003). Website **0.1.58** remains
in `bantera-website-20261007-1`; there were no website code changes.

The callback scheduling voice reply now waits through the model's tool-call turn
completion for its spoken confirmation. The candidate passed staging and public
checks; all **128/128** API and website availability samples returned HTTP 200.
Public `/version`, `/download`, the website release marker and the voice route's
401 authentication gate were independently verified after promotion. This is
HTTP availability evidence, not proof of uninterrupted existing WebSocket calls.

No schema migration or new environment variable is required. **GO for published
clients**: existing API/auth contracts are unchanged. The verified PostgreSQL
backup is 3,259,827 bytes. Private runtime snapshots, source archive checksum,
tested working-tree manifest, controller and logs are retained in the release
folder. This deployment does not commit or push the source changes.

Rollback:

```bash
python3 /home/ubuntu/releases/bantera-20261007-3/deploy.py rollback
```

This restores `cloudflared-20261007-2` and backend 1.0.162, retaining the website
and current database writes. The active release record remains
`/home/ubuntu/releases/bantera-current.json`. Derive the next release from that
record, rather than reusing names/ports from this controller.


## 7 October 2026: AI greetings and callback reminders (release 4)

Release `/home/ubuntu/releases/bantera-20261007-4` completed at
02:42:03 UTC. The active record now selects:

- Backend **1.0.165**, container `bantera-api-20261007-4`, loopback port 18084.
- Website **0.1.58**, rebuilt from unchanged commit `0a3ab34` as
  `bantera-website-20261007-4`, loopback port 13003. Its public deployment
  marker is `bantera-20261007-4`.
- Connector `cloudflared-20261007-4`, readiness port 12004. Only the two
  Bantera origins changed; tunnel identity, credentials, image, networks
  and unrelated routes were preserved.

Backend source is the reviewed working tree based on `19b4ea2`, including
first-meeting greetings and required callback reminder notes. Compatibility
is **GO for existing published apps**: metadata fields are optional and
migration `20261007022443_AddAiCallbackReminder` only adds nullable
`ai_callbacks.Reminder` (`varchar(500)`). Existing callback rows remain valid.
No new production settings are required. The 3,260,911-byte pre-migration
PostgreSQL backup passed archive validation; the applied migration and column
were checked in production. Startup logs contained no error entries at staging.

Both candidates passed local checks before the rolling connector handover.
Public API version, website release marker, home/download/login pages,
admin authentication redirects/rejections and public lesson listing passed.
All **128/128** monitored HTTP samples returned 200 from 02:39:50 through
02:42:02 UTC. This verifies observed HTTP availability, not uninterrupted
individual live calls or real Gemini/APNs conversations.

The release directory retains source archive hashes, base commits and dirty
file lists in `source-manifest.json`, private runtime snapshots, database
backup, candidate verification and build/promotion/availability logs.
Source was deployed without creating commits or pushing Git branches.

Rollback for this release:

```bash
python3 /home/ubuntu/releases/bantera-20261007-4/deploy.py rollback
```

This restores `cloudflared-20261007-3`, backend **1.0.163** and website
`bantera-website-20261007-1` (**0.1.58**). Retained application containers
remain available. Leave the nullable reminder column and current database
writes intact; do not restore the backup as an application rollback.


## 7 October 2026: voice response recovery (release 5)

Prepared `/home/ubuntu/releases/bantera-20261007-5` with backend **1.0.166**
and website **0.1.58**. The backend adds stalled-reply renewal, active-call
quota recovery and noninterrupting clock updates. No new migrations or
configuration are required; old app contracts remain compatible.

Staging succeeded at 03:50:02 UTC on 7 October 2026. Containers are
`bantera-api-20261007-5` (18085) and `bantera-website-20261007-5` (13004),
with an unstarted `cloudflared-20261007-5` (12005). The first cached website
image retained the previous deployment marker and failed staging, before
traffic was changed. A fresh build of
`bantera-website:bantera-20261007-5-verified` passed the marker check; the
isolated failed candidate was stopped and renamed with `-staging-failed`.
For future release markers, set fresh file timestamps or explicitly invalidate
the build cache, and always verify the served marker.

Promotion completed at **04:18:01 UTC** after SSH access recovered. The active
record selects release 5. Both public domains serve the expected version and
marker; all **128/128** monitored HTTP samples succeeded from 04:15:49 through
04:18:01 UTC. The verified pre-release database backup is **3,261,051 bytes**.
Public home/download/login, admin redirect, public lesson listing and voice
route authentication checks passed. Local SSH/DNS access was intermittent,
while server-side rollout checks remained healthy.

Rollback:

```bash
python3 /home/ubuntu/releases/bantera-20261007-5/deploy.py rollback
```

This restores connector `cloudflared-20261007-4`, backend **1.0.165** and the
release 4 website, preserving current database writes. Runtime snapshots,
source manifests and build/staging/promotion/availability logs remain in the
release directory. No commit or push was performed.

Local validation: 571 backend tests passed (6 opt-in tests skipped), followed
by two explicit real-model voice/callback-tool tests, both passed; first audio
was approximately 1.9 and 2.1 seconds after Send. Six focused app tests and
analysis passed. Signed iOS **2.0.131+319**, built from `lib/main.dart`, was subsequently
installed on the user's iPhone 17 Pro. Device metadata confirmed version
2.0.131 and build 319. Installation did not run physical-device tests.

## 7 October 2026: streamed replies and voice reminders (release 6)

Release `/home/ubuntu/releases/bantera-20261007-6` completed at **10:11:10 UTC**.
Backend **1.0.168** and website **0.1.59** are live through
`cloudflared-20261007-6` (readiness port 12006). Application containers are
`bantera-api-20261007-6` (18086) and `bantera-website-20261007-6` (13005).
The current release record selects release 6.

The additive `20261007094604_AddAiVoiceReminders` migration preserves old
callbacks as calls. New voice reminders use `queued`, a status older callback
workers do not claim, so retaining old application containers cannot turn
voice reminders into phone calls. Streamed transcript events require an explicit
client opt-in; existing voice clients retain their response contract. No new
environment variables are required. Existing APNs credentials deliver ordinary
voice-reminder notifications; CallKit remains for explicit call requests.

A verified PostgreSQL backup of **3,263,158 bytes**, private runtime snapshots,
source archive hashes and build/staging/promotion logs remain in the release
directory. All **128/128** monitored public HTTP samples succeeded between
10:08:58 and 10:11:09 UTC. Public API version, website release marker,
home/download/dashboard login, public lessons and authentication gates passed.
The backend startup log had no error-level or unhandled-exception entries in
the checked startup window. This verifies HTTP availability, not an end-to-end
physical-device reminder delivery.

Rollback:

```bash
python3 /home/ubuntu/releases/bantera-20261007-6/deploy.py rollback
```

This restores connector `cloudflared-20261007-5`, backend **1.0.166** and
website **0.1.58**, then stops the candidate API worker after restored public
health checks pass. Keep the additive columns and current database writes;
do not restore the backup for an application rollback. Voice reminders require
release 6's worker and do not run while rolled back.

Validation: 578 backend tests passed (5 opt-in tests skipped), with explicit
real-model reminder selection and voice/callback regression checks also passed.
The Flutter feature suite passed 31 tests. Sources were deployed with their
reviewed uncommitted changes; no commit or push was performed.

Signed iOS **2.0.133+321**, built from `lib/main.dart`, was installed on the
user's iPhone 17 Pro after deployment. Device metadata confirmed version
2.0.133 and build 321. Signature verification and final controller analysis
passed. No physical-device tests were run.

## 8 October 2026: incomplete AI reply diagnostics

Release **`bantera-20261008-1`** completed at **00:24:01 NZDT** on 8 October
(7 October 11:24:01 UTC). Backend **1.0.169** runs in
`bantera-api-20261008-1` (loopback **18087**), behind
`cloudflared-20261008-1` (readiness **12007**). Website **0.1.59** remains in
`bantera-website-20261007-6` on 13005, with its original release marker.
The active release record selects this backend/website combination.

No new migration or environment variables were required. The existing
`ai_pipeline_events` table now receives technical AI chat failure diagnostics;
old clients remain compatible. An authenticated synthetic diagnostic was saved
and correlated in the production database before promotion, invalid categories
were rejected, and the synthetic row was removed. Public unauthenticated POSTs
to the new endpoint return 401. Startup error checks found no error-level or
unhandled-exception entries.

All **128/128** monitored public HTTP samples succeeded from 11:21:49 through
11:24:00 UTC. The verified pre-release database backup is **3,264,045 bytes**.
The release directory contains private runtime snapshots, source archive hashes,
staging/build/promotion logs and the diagnostics verification script. Production
conversation content was not used for the smoke test.

Rollback:

```bash
python3 /home/ubuntu/releases/bantera-20261008-1/deploy.py rollback
```

This restores connector `cloudflared-20261007-6` and backend **1.0.168**, keeps
the existing website, and stops the candidate API after restored public health
checks pass. No database restore is part of application rollback. No Git commit
or push was performed.

Signed iOS **2.0.134+322** was then installed on the user's iPhone 17 Pro.
CoreDevice confirmed version 2.0.134 and build 322. Signature verification passed;
the build uses `lib/main.dart`. No physical-device tests or diagnostic launch
were performed.

## 8 October 2026: quiet live calls and model compatibility

Release **`bantera-20261008-3`** succeeded at **00:58:02 NZDT** (7 October
11:58:02 UTC). Backend **1.1.2** runs in `bantera-api-20261008-3`, loopback
**18089**, through `cloudflared-20261008-3`, readiness **12009**. Website
**0.1.59** is unchanged in `bantera-website-20261007-6` and retains its original
website release marker. Both public domains passed checks; **128/128** HTTP
samples succeeded during handover. The previous connector and application remain
available for rollback. The verified database backup is **3,263,898 bytes**.

This release makes call clock updates non-triggering context, tunes speech
activity detection, and adds model-specific tool/response-completion handling.
There is no migration or new environment variable, and old app clients retain
the same WebSocket contract. The provider's six selectable conversation Live
models accepted call and voice-message setup; synthetic spoken replies succeeded
for all six (2.5 latest, September/December previews, 3.1, 3.8, 3.8 Extended
Thinking). Extended Thinking requires `thinkingLevel: low` and reports an explicit
`interactionStatus: IDLE`; standard 3.8 omits thinking configuration.

The earlier **`bantera-20261008-2`** candidate was not promoted: its Extended
Thinking setup lacked the required thinking level. Its API is stopped and its
connector was never started. The corrected source archive, SHA-256 and dirty Git
state are recorded in release 3. Provider probe scripts/results are retained in
release 2; no chat audio or user transcripts were used for these checks.

Rollback:

```bash
python3 /home/ubuntu/releases/bantera-20261008-3/deploy.py rollback
```

This restores `cloudflared-20261008-1` and backend **1.0.169**, keeps the current
website, and stops the new candidate API after public rollback checks pass. It
does not restore the database or discard subsequent writes. No Git push or App
Store submission was performed for this release.

Signed iOS **2.1.1 (324)** was installed afterward on the user's iPhone 17 Pro.
CoreDevice confirmed both version and build. The app disables automatic screen
lock during a real-time call and releases it when the call ends. Installation
only: no physical-device test or automated app launch was performed.

## 8 October 2026: coaching, current language and chat fixes

Release **`bantera-20261008-4`** succeeded at **01:34:11 NZDT** (7 October
12:34:11 UTC). Backend **1.2.0** runs in `bantera-api-20261008-4`, loopback
**18090**, through `cloudflared-20261008-4`, readiness **12010**. The unchanged
website **0.1.59** remains in `bantera-website-20261007-6` and keeps its original
release marker. All **128/128** sampled public HTTP responses succeeded during
handover, and both public domains passed release checks. This does not prove that
every existing WebSocket call remained uninterrupted.

The release strengthens current-profile language/accent and coaching instructions,
accepts optional Discover level metadata, and sends final response data before an
interruption event. Existing apps retain their API contracts. No new migration or
environment setting is needed. The verified database backup is **3,271,963 bytes**;
startup checks found no error-level or unhandled-exception entries. Exact working
tree source hashes, private runtime snapshots and deployment logs are preserved
in the release directory. The source changes have not been committed or pushed.

Rollback for this release:

```bash
python3 /home/ubuntu/releases/bantera-20261008-4/deploy.py rollback
```

This restores connector `cloudflared-20261008-3` and backend **1.1.2**, retains
the same website and current database writes, and stops the new candidate after
restored public-domain checks pass. The previous containers and images remain
available. No App Store submission is part of this release.

Signed iOS **2.2.0 (325)** was installed after deployment on the user's iPhone
17 Pro. CoreDevice confirmed version and build; code-signature verification
passed, and the build uses `lib/main.dart`. No physical-device test or automated
app launch was performed.

## 8 October 2026: voice reply recovery and three-minute DMs

Release **`bantera-20261008-6`** succeeded at **02:11:55 NZDT** (7 October
13:11:55 UTC). Backend **1.3.0** runs in `bantera-api-20261008-6`, loopback
**18092**, through `cloudflared-20261008-6`, readiness **12012**. Website
**0.1.59** remains in `bantera-website-20261007-6`. All **128/128** monitored
public HTTP samples returned 200. Public version, website/download, authentication
and lesson checks passed. Existing WebSocket continuity is not guaranteed by
these HTTP checks.

This release adds an explicit completed-recording instruction when recovering a
silent AI voice reply, avoids timeout replay after reply audio has begun, and
recognises explicit quota messages for key rotation. Human DM validation accepts
180 seconds; group validation remains 60 seconds. **GO for existing published
clients:** request/response contracts remain compatible, with no new migration or
environment setting. Staging found no startup error entries. The verified
database backup is **3,273,638 bytes**.

Candidate `bantera-20261008-5` failed staging before any traffic change because
its source archive mistakenly excluded `BanteraApi/appsettings.json`. Its API
is stopped and no connector was created. Archive filters must retain the tracked
default settings file while excluding environment-specific secrets. Release 6
includes and explicitly verifies that file. Both source manifests, archive hashes,
private runtime snapshots and rollout logs remain in their release directories.

Rollback for the active release:

```bash
python3 /home/ubuntu/releases/bantera-20261008-6/deploy.py rollback
```

This restores `cloudflared-20261008-4` and backend **1.2.0**, preserves the
website and current database writes, and stops the candidate after restored
public health checks. Source was deployed from the reviewed working tree;
no Git commit/push or App Store submission was performed.

Signed iOS **2.3.0 (326)** was installed after deployment on the user's iPhone
17 Pro. CoreDevice confirmed version and build. Code-signature verification
passed, and the build uses `lib/main.dart`. No physical-device test or automated
app launch was performed.


## Device search and voice recovery release, 8 October 2026, 02:49 NZDT

Release **`bantera-20261008-7`** completed at **2026-10-07 13:49:06 UTC**.
Backend **1.4.0**, source commit `b17d5a07a29e5298310caf535ee7a347baa5182d`,
is live in `bantera-api-20261008-7` (loopback **18093**) through
`cloudflared-20261008-7` (readiness **12013**). Website **0.1.59** remains in
`bantera-website-20261007-6` (loopback **13005**); no website code changed.

This release adds opt-in device web-search tool relaying for AI calls and voice
messages, bounded recovery of stalled voice replies, diagnostic counters, and
three-minute human DM audio validation. The search request itself runs in the
app. Existing clients remain compatible; no new migration or environment
variable is required. Production runtime settings and the selected Gemini model
were preserved.

- The verified PostgreSQL backup is **3,275,322 bytes**. Private runtime
  snapshots, source hash, controller, build logs and backup stay in the release
  directory. The archive includes canonical `BanteraApi/appsettings.json`.
- Candidate startup, API lesson listing, authentication gates, website pages
  and Cloudflare ingress validation passed before promotion.
- **130/130** public API and website availability samples returned HTTP 200
  during the rolling handover and stable verification window. Public API
  `/version` returned **1.4.0** and the website retained its expected marker.
  HTTP checks do not establish uninterrupted existing WebSocket calls.
- Previous API and connector `20261008-6` are retained. Promotion was guarded
  by automatic rollback. Manual rollback for this release is:

  ```bash
  python3 /home/ubuntu/releases/bantera-20261008-7/deploy.py rollback
  ```

  It restores backend **1.3.0** through the previous connector and restores
  the prior active record; it does not restore or discard database writes.

App **2.4.0 (327)**, commit `10a5e7069e266d08a70f6cee6d10c9b79cbe0ea5`,
was pushed to main and passed the iOS build, but was **not installed** as part
of this deployment. Device search and the background reply fix require that
app update. Physical iPhone background playback remains a user smoke check.

## Voice reply completion fix, 8 October 2026, 16:39 NZDT

Release **`bantera-20261008-8`** succeeded at **2026-10-08 03:39:01 UTC**.
Backend **1.4.1** runs in `bantera-api-20261008-8`, loopback **18094**, via
`cloudflared-20261008-8`, readiness **12014**. Website **0.1.59** remains in
`bantera-website-20261007-6`; its static release marker describes that website
build, so use the API `/version` endpoint for the current backend version.

This release waits for the WebSocket close acknowledgement before cancelling
the receive loop, preventing an abort from racing the final voice reply frame.
The app also processes queued completion frames before transport errors and
closes the socket independently of queued playback. Regression tests fail with
the old code and pass with the fixes: 46 focused backend tests and 15 app tests.
**GO for existing published clients:** API contracts remain unchanged, with no
new migration, configuration or secret required.

The release source is based on backend commit
`7895ee75cb5d7fbfeea2f43c56ccfcec191a95be` plus the reviewed working-tree fix.
The archive hash and dirty paths are in `source-manifest.json` on the server.
Private runtime snapshots and the verified **3,281,260-byte** PostgreSQL backup
remain in the release directory. Staging reported no startup error entries.
All **156/156** monitored public HTTP samples returned 200. Independent checks
confirmed backend 1.4.1, the website home/download pages and its existing release
marker. HTTP checks do not establish uninterrupted pre-existing WebSocket calls.

Rollback:

```bash
python3 /home/ubuntu/releases/bantera-20261008-8/deploy.py rollback
```

This restores `cloudflared-20261008-7` and backend **1.4.0**, preserves the
website and database writes, and stops the candidate after rollback health
checks. Previous application containers and the connector are retained.

Signed iOS **2.5.1 (331)** was installed afterward on the user's iPhone 17 Pro.
CoreDevice confirmed version/build. Installation only: no app launch or
physical-device test. No Git commit/push or App Store submission was performed.

## Conversation timing and travel context, 9 October 2026, 12:10 NZDT

Release **`bantera-20261009-1`** succeeded at **2026-10-08 23:10:06 UTC**.
Backend **1.5.0** is live in `bantera-api-20261009-1` (loopback **18095**)
through `cloudflared-20261009-1` (readiness **12017**). Website **0.1.61**
continues in `bantera-website-20261008-10`, loopback **13007**. Its static
release marker still describes that website build; `/version` is authoritative
for the current backend.

The release sends dated history to Gemini, derives conversational continuity
from server time, and supplies guidance for relevant future plans and changes
of device time zone. **GO for published clients:** timestamp/zone history fields
are optional; old clients remain supported. No DB migration, new configuration,
secret or persisted chat data is added. Production model/voice settings remain
unchanged. Prompt guidance does not guarantee identical model wording.

The source manifest records backend base `6b57441` plus reviewed working-tree
changes and SHA-256 `d7f7385a146ee113756fd09019b17378d78656fa33657bd4f35a39f61f9d4fb8`.
Runtime snapshots, controller and verified **3,302,029-byte** PostgreSQL backup
are retained privately in the release directory. Candidate API/authentication,
website and ingress checks passed; startup logs had no error entries.
All **144/144** public HTTP availability samples returned 200 through the
rolling handover and stability window. Independent public checks confirmed
backend 1.5.0, home, download and the retained website marker. This does not
establish uninterrupted pre-existing WebSocket calls.

Rollback:

```bash
python3 /home/ubuntu/releases/bantera-20261009-1/deploy.py rollback
```

This restores `cloudflared-20261008-10`, API **1.4.1** and the prior active
record, while retaining website 0.1.61 and current database writes. The old API
and connector remain available. Promotion would automatically roll back on
failed health checks.

Validation: **106 backend tests passed**, four optional integration tests
skipped; **35 app tests passed**, targeted Dart analysis clean, debug and signed
release iOS builds passed. Signed iOS **2.7.0 (336)** was installed on the user's
iPhone 17 Pro after deployment, without launching or testing on that phone.
No commit/push or store submission was performed.

## Private conversation history and admin search diagnostics, 9 October 2026

Release **`bantera-20261009-3`** succeeded at **2026-10-09 00:13:20 UTC**.
Backend **1.8.0** runs in `bantera-api-20261009-3` on loopback **18097**
(image `bantera-backend:bantera-20261009-3-r2`); website **0.3.0** runs in
`bantera-website-20261009-3` on **13009**. Connector
`cloudflared-20261009-3` uses readiness port **12019**. Independent public
checks confirmed both version markers; all **128/128** monitored public HTTP
samples returned 200. Existing WebSocket continuity was not measured.

**GO for published clients:** existing contracts remain compatible. No database
migration, new secret or required configuration. Historical message timing is
now private background context rather than replayed assistant dialogue; the
resumption cache identity changed to avoid reusing contaminated sessions.
Fresh and resumed real Gemini sessions both retained the seeded context.

The admin search test verified grounded Gemini 2.5 Flash results in **3,289 ms**.
Validation, per-admin rate limiting, anonymous HTTP 401 and non-admin HTTP 403
checks passed. Authentication now precedes rate limiting so authenticated user
limits do not accidentally share an IP bucket. The authenticated website rendered
the search test panel successfully. No production model settings were changed.

The release directory retains source manifests, archive hashes, protected runtime
snapshots and a validated **3,305,138-byte** database backup. The revised candidate
was rebuilt before promotion to correct middleware ordering.

Rollback:

```bash
python3 /home/ubuntu/releases/bantera-20261009-3/deploy.py rollback
```

This restores `cloudflared-20261009-2`, backend **1.7.0** and website **0.2.0**
while preserving current database writes. Previous containers are retained;
promotion automatically rolls back if health checks fail.

ChatGPT hosted-plan OAuth and GPT provider selection are **not enabled** in this
release; they require an approved hosted-app registration. The iOS **2.8.1 (338)**
legacy transcript cleanup was built locally but not installed in this deployment.
No commit/push or store submission was performed.


## Conversation memory, Live resumption and reasoning controls, 9 October 2026

Release **`bantera-20261009-2`** succeeded at **2026-10-08 23:46:00 UTC**.
Backend **1.7.0** runs in `bantera-api-20261009-2` on loopback **18096**
(image `bantera-backend:bantera-20261009-2-r2`); website **0.2.0** runs in
`bantera-website-20261009-2` on **13008**. Connector
`cloudflared-20261009-2` uses readiness port **12018**. The active release record
and independent public checks confirmed both versions, home/download HTTP 200,
and unauthenticated admin/summary HTTP 401. All **128/128** monitored public
HTTP samples returned 200. These checks do not establish uninterrupted existing
WebSocket calls.

**GO for published clients:** changes are additive. No DB migration, new secret
or required configuration. Existing model/voice selections remain unchanged.
The new summary endpoint processes context transiently; the phone stores the
rolling summary locally. Eligible Live resumption handles are bounded and kept
only in process memory. New sessions use device summary/recent history.

The release directory retains source manifests/archive hashes, protected runtime
snapshots and a validated **3,302,307-byte** database backup. The unpromoted API
candidate was rebuilt as `r2` after a summary smoke test exposed a slow primary
text model; per-model timeouts now allow the configured fallback to run. Two
successive real summary updates then passed, preserving a preference and a
cancelled-plan update. A real Gemini test separately verified session resumption
retains context without replaying the earlier user message. Admin capability and
invalid-reasoning checks passed, with production settings unchanged.

Rollback:

```bash
python3 /home/ubuntu/releases/bantera-20261009-2/deploy.py rollback
```

This restores `cloudflared-20261009-1`, backend **1.5.0**, website **0.1.61**,
and the previous release record while preserving current database writes.
Previous containers are retained; promotion automatically rolls back on failed
health checks.

Validation included 119 focused backend tests (3 optional tests skipped), a
real Live resumption test, 60 AI app tests and 16 memory/history tests repeated
after the final fragment-handling edit. Website, debug iOS and signed release
iOS builds passed. CoreDevice confirmed **2.8.0 (337)** installed on the user's
iPhone 17 Pro. Installation only, without launching or testing on that phone.
No commit/push or store submission was performed.

## Admin ChatGPT OAuth connection panel, 9 October 2026

Release **`bantera-20261009-4`** succeeded at **2026-10-09 00:39:20 UTC**.
Backend **1.9.0** runs in `bantera-api-20261009-4` on loopback **18098**;
website **0.4.0** runs in `bantera-website-20261009-4` on **13010**. Images are
`bantera-backend:bantera-20261009-4` and `bantera-website:bantera-20261009-4`.
Connector `cloudflared-20261009-4` uses readiness port **12020**.

**GO for existing apps:** only additive admin endpoints, no DB migration, no
changes to Gemini configuration or generation routing. No new configuration is
needed to keep existing functionality working. ChatGPT OAuth remains inactive:
the authenticated dashboard shows **Continue with ChatGPT** and **Setup required**.
No OpenAI account was connected or subscription inference verified.

Activation needs the approved hosted OpenAI client and `ChatGptConnection__*`
settings documented in the backend README. Before activation, mount a dedicated
persistent private credential directory into the API container; ensure future
release controllers explicitly preserve this mount and its encryption key. The
current unconfigured release does not create or use credential storage. Do not
set `PlanAccessApproved` merely to enable the button without provider approval.

The staged endpoint returned configured/connected false, rejected start with 409
and anonymous requests with 401. After public promotion, authenticated dashboard
HTML confirmed both the button and registered-callback setup guidance. All
**128/128** monitored HTTP samples returned 200; this does not prove uninterrupted
pre-existing WebSockets. A **3,306,198-byte** DB backup was validated before staging.

Twenty-nine focused backend tests passed, including signed JWT validation,
browser/admin-bound state, rejection of replay, encrypted storage, concurrent
refresh serialization and cancellation. Backend and website builds and targeted
ESLint passed. Real provider OAuth remains untested pending client registration.

Rollback:

```bash
python3 /home/ubuntu/releases/bantera-20261009-4/deploy.py rollback
```

This restores `cloudflared-20261009-3`, backend **1.8.0** and website **0.3.0**,
keeping current database writes. Prior containers remain available. Automatic
rollback was enabled during promotion. No commit/push or app installation.

## ChatGPT device-code connection, 9 October 2026

Release **`bantera-20261009-5`** uses backend **1.10.0** and website **0.5.0**.
Candidates: `bantera-api-20261009-5` on **18099**, `bantera-website-20261009-5`
on **13011**, and `cloudflared-20261009-5` on readiness port **12021**. The
controller, source archives/hashes, protected runtime snapshots and a validated
**3,306,658-byte** database backup are retained in the release directory.

**GO for existing app clients:** additive admin-only connection/model-test APIs,
no DB migrations, no change to existing Gemini model routing. Device-code login
replaces the dashboard's registered-hosted-client flow. A real OpenAI device-code
request from the staged Oracle AU API succeeded; that test attempt was cancelled
without authorising an account. Actual model and web-search access still require
the admin to sign in and run the dashboard test. Twenty-one focused connection
and provider tests passed; local backend/website builds and targeted lint passed.

Persistent credential storage is now active. The dedicated host directory
`/home/ubuntu/data/bantera-chatgpt` is mounted at `/var/lib/bantera/chatgpt` in the
API. Its encryption key/settings are kept in the owner-only server file
`/home/ubuntu/.config/bantera/chatgpt-storage.json`, injected as
`ChatGptConnection__EncryptionKey` and `ChatGptConnection__StorageDirectory`.
**Preserve that key and mount in every future release.** The release controller
now clones application mounts in addition to environments/networks. Do not print
or copy these secret values into a repository. Back up encrypted credentials and
the key separately. Prior hosted OAuth registration fields are not required for
this device-code flow.

Rollback command:

```bash
python3 /home/ubuntu/releases/bantera-20261009-5/deploy.py rollback
```

This restores release 4 (backend 1.9.0, website 0.4.0) while keeping current DB
writes and the new encrypted credential directory. The restored release will not
use the device connection. Old application containers and connector are retained.

The existing signed universal iOS **2.8.1 (338)** build was also installed on the
paired iPad Air (5th generation). CoreDevice confirmed bundle/version; the app
was not launched or tested on the physical device. No app code change, Git push,
or store submission was performed for this release.

Promotion completed at **2026-10-09 01:10:01 UTC**. The active release record and
both public domains confirmed the expected versions. Authenticated dashboard
HTML showed the device-code connection panel without the hosted-client setup
blocker. All **146/146** public availability samples returned HTTP 200, including
the stable post-switch window. This does not prove uninterrupted pre-existing
WebSocket calls. Automatic rollback remained enabled throughout promotion.

## GPT text/search routing and voice reply continuity, 9 October 2026

Release **`bantera-20261009-6`** deployed backend **1.11.0** and website **0.6.0**
at **2026-10-09 01:34:19 UTC**. API candidate **18100**, website **13012**, and
connector readiness **12022**. The active release record and both public domains
confirmed this release. All **130/130** monitored HTTP samples returned 200.
Existing WebSocket continuity is not established by these HTTP checks.

**GO for existing published clients:** changes are additive admin fields and
provider routing behind saved selections; no DB migration or new environment
variable. Existing model selections were preserved. Persistent encrypted ChatGPT
storage and its key/mount were retained. A **3,312,040-byte** database backup was
validated before staging. Runtime startup, public APIs, authentication gates and
an authenticated dashboard render passed before and after promotion.

GPT/Gemini text and search models now have independent primary/fallback choices.
GPT reasoning uses the selected account model's live capabilities. An actual
**GPT-6 Luna / max** search through the staged API completed with verified search
SSE evidence and **20 source links**. Its safe proof record is
`provider-search-proof.json` in the release directory. The invalid-reasoning API
check rejected the request before any setting changed. Browser checks verified
Luna/Astra reasoning options and the reset to model default on selection change;
unsaved test selections were discarded. Production text/search selections remain
as they were until an admin saves new choices. AI chat's on-device web search is
separate and unchanged.

Backend tests: **692 passed, 10 environment-dependent tests skipped**. Backend
build, website build and targeted ESLint passed. Existing NuGet vulnerability
warnings remain; this release does not change those dependencies. Regression
tests cover streamed search evidence with empty final output, incomplete streams,
per-model reasoning, cross-provider fallback, and short-gap greeting instructions.
Greeting behavior was not tested on a physical device. The signed universal iOS
**2.8.1 (338)** build contains the current app source; this batch changes only the
backend and website, so no new iOS build number was needed.

Rollback (preserves new database writes and connected credentials):

```bash
python3 /home/ubuntu/releases/bantera-20261009-6/deploy.py rollback
```

This restores release 5, backend **1.10.0**, website **0.5.0**, and
`cloudflared-20261009-5`. Prior applications/connector, source archives/hashes,
protected runtime snapshots and the deployment controller are retained in
`/home/ubuntu/releases/bantera-20261009-6/`. Automatic rollback was enabled during
promotion. No Git commit/push or App Store submission was performed.

CoreDevice subsequently confirmed iOS **2.8.1 (338)** installed on both **Eason's
iPad Air (5th generation)** and **Ethan's iPhone 17 Pro**. Both install receipts
and installed-app readbacks are retained locally as
`/tmp/bantera-release6-{ipad,iphone}-{install,installed-app}.json`. Neither app was
launched or tested on the physical device.

## Search restrictions, GPT timeout and chat-history retention, 9 October 2026

Release **`bantera-20261009-7`** completed at **2026-10-09 02:03:05 UTC**
(**15:03 NZDT**). Backend **1.12.0** is on loopback **18101**, website **0.7.0**
on **13013**, and connector `cloudflared-20261009-7` readiness on **12023**.
The active record and both public version markers confirm this release;
**130/130** monitored public HTTP samples returned 200. Candidate startup logs
contained no error entries. HTTP checks do not prove uninterrupted existing calls.

**GO for existing published apps:** additive admin timeout field; no database
migration or new configuration/secrets. The saved GPT timeout defaults to **180
seconds**, configurable from **30–300** in the AI dashboard. Omitted fields retain
existing settings. GPT transport and dashboard test budgets accommodate this;
caller cancellation, summary and overall lesson deadlines remain authoritative.
Gemini backend search permits only `gemini-2.5-flash` and `AIzaSy` keys; other
Gemini keys are never used as a fallback. Connected GPT choices remain available.
App AI searches continue directly on-device, separately from backend search.

The protected release directory contains source manifests/hashes, runtime
snapshots, controller and a validated **3,329,393-byte** PostgreSQL backup.
ChatGPT credential encryption key/storage mounts were preserved. The candidate
and public smoke checks verified admin gates, live catalogues, timeout default,
invalid-value rejection, search allowlist and authenticated dashboard rendering.
Validation: **714 backend tests passed**, 10 environment-dependent tests skipped;
backend/website builds and targeted lint passed. Existing NuGet warnings remain.

Rollback, preserving database writes and connected credentials:

```bash
python3 /home/ubuntu/releases/bantera-20261009-7/deploy.py rollback
```

This restores release 6 (backend **1.11.0**, website **0.6.0**) through its retained
connector. Promotion had automatic rollback enabled; old applications remain.

The app **2.8.2 (339)** adds a local checkpoint when leaving AI chat mid-reply,
retaining both visible transcripts and received reply audio. A newly opened chat
waits for pending account-specific writes. **25 focused app tests passed**,
including immediate reopen during a streamed reply; targeted analysis, debug
and signed release iOS builds passed. User requested iPhone installation only
after the iPad remained unavailable. No Git commit/push or store submission.

CoreDevice confirmed **2.8.2 (339)** installed on **Ethan's iPhone 17 Pro**.
Receipt and installed-app readback are `/tmp/bantera-release7-iphone-install.json`
and `/tmp/bantera-release7-iphone-installed-app.json` on the Mac. Installation
only: the app was not launched or tested on the physical phone.

## Voice playback deadline and GPT diagnostics, 9 October 2026

Release **`bantera-20261009-8`** completed at **2026-10-09 02:47:38 UTC**
(**15:47 NZDT**). Backend **1.12.1** is on loopback **18102**; unchanged website
**0.7.0** is redeployed on **13014**; connector readiness is **12024**. Both
public version markers and the current-release record match. **130/130** public
HTTP samples returned 200. Existing calls were not exercised during handover.
The verified PostgreSQL backup is **3,339,144 bytes**.

**GO for published clients:** no migration, new secrets or API contract change.
Voice-reply inactivity now accounts for queued 24 kHz PCM playback while awaiting
Gemini's completion event. A real missing completion still times out, and caller
cancellation/overall limits still win. Counts of generation/turn completion
signals are added to existing technical failure diagnostics.

GPT retries a transient server error once, within the same configured deadline;
quota/auth/unsupported requests and partially generated answers are not retried.
Database diagnostics retain only safe error codes, HTTP status, stage, completion
flag and character count, never response text, prompts or credentials. The admin
search test no longer imposes an unrelated 30-second cap on GPT; Gemini retains
its separate test deadline. Selected models, reasoning and timeout are unchanged.

The user's lesson `2a20d955-0544-4cdc-9a89-fd7f09c29d47` hit the configured
180-second GPT deadline. A staged real search reproduced OpenAI `server_error`
after `web_search_call.completed`, before answer text, on both attempts. This is
an unresolved upstream failure, not proof that web search is unsupported. The
configured Gemini 2.5 Flash fallback returned a verified answer with 20 sources
in **22.2 seconds** total. Evidence: `search-resilience-proof.json` in the protected
release directory; test ID `bbfcb1f4-3dd0-436e-8a35-68aa56880efd`. Both GPT failures
were verified in `ai_pipeline_events` with `searchCompleted: true` and
`stage: answer_started`.

Validation: **727 backend tests passed**, 10 environment-dependent tests skipped;
backend build, website production build and candidate/live authenticated smoke
checks passed. Existing build warnings remain. Rollback is available without
restoring the database or losing connected ChatGPT credentials:

```bash
python3 /home/ubuntu/releases/bantera-20261009-8/deploy.py rollback
```

This restores release 7 (backend **1.12.0**, website **0.7.0**). No commit/push.
The changes are server-only; iOS **2.8.2 (339)** remains the matching current build.
Requested iPhone-only reinstall was attempted but CoreDevice returned error 4016
because the phone was unavailable. Receipt: `/tmp/bantera-release8-iphone-install.json`
on the Mac. Installation remains pending a connected/unlocked iPhone. No iPad
installation, physical-device launch or testing was performed.
