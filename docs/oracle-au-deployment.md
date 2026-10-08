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
