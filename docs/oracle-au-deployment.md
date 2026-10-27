# Bantera deployment on Oracle AU

## Speaking leaderboard: 22 October 2026

Release **bantera-20261021-9** publishes API **1.32.2** and website **0.17.1**.
It follows the same-day initial leaderboard releases 1.32.0/1.32.1. API runs in
`bantera-api-20261021-9` (loopback 18148), website in
`bantera-website-20261021-9` (loopback 13042), with
`cloudflared-20261021-9` (readiness 12074).

The Goal entry opens a paginated Speaking leaderboard. Rankings combine spoken
words across languages for today's UTC calendar date plus the preceding six
calendar days. Each row shows the current profile name/photo, current learning
language and exact total. Ties share a rank. The owner's ordinary row says You;
there is no pinned own row, language breakdown, eye control or hiding dialog.
Names/photos are always shown. Users can change them through Edit Profile.
The authenticated dashboard includes a matching seven-day preview, separate
from the existing Today / This week / All time admin rankings.

**GO for existing published apps:** the leaderboard API and UTC activity sync
are additive; previous APIs remain unchanged. The initial leaderboard migration
added three tables. This correction needs no further migration, configuration
or secret. The now-unused visibility table is retained rather than dropped.
The pre-release mobile client's visibility route remains authenticated, accepts
show requests without writing, and returns 409 for unsupported hide requests.
Current mobile code has no visibility caller.

Historical limitation: earlier app versions saved local dates without timestamps.
Their original date buckets remain included and explicitly labelled; new activity
uses UTC. Upgraded-device snapshots replace the old source atomically, preventing
double counting. Changing the selected language never removes previous counts.

Local validation: backend build and 1,088 tests passed (16 optional skips),
including isolated PostgreSQL tests for aggregation, ties, pagination, UTC source
replacement and visible profiles despite old hidden preferences. Website type
checking, build and 14 tests passed; lint had no errors (13 existing warnings).
App 3.3.2 (425) passed 60 selected tests, including leaderboard layouts across
17 locales, callback/CallKit and audio regressions. Focused analysis was clean;
whole-app analysis reported 44 existing informational notices. Light/dark golden
mockups are under app/docs/design/speaking-leaderboard. No physical-device testing
or launch was performed; installation alone does not verify locked-screen audio.

Release status **SUCCESS** at 2026-10-21 11:16:42 UTC. Both public version
markers and authenticated dashboard rendering passed. Candidate and live checks
each passed 13 existing HTTP contracts, 20 admin ranking checks and 11 speaking
checks, including independent read-only SQL comparison of every returned name,
photo presence, language and total. No test data was written to production.
All **160/160** monitored public responses returned 200. Verified database backup:
**6,232,364 bytes**. Post-release cleanup completed; **111 GB of 194 GB** remains
available (44% reported usage), with current and one rollback release retained.
Both public services were rechecked after cleanup.

Signed iOS **3.3.2 (425)** was installed on Ethan’s iPhone 17 Pro and verified
through the installed-app inventory. The app was not launched or tested on the
physical phone.

Rollback retains database writes and the additive tables:

```bash
python3 /home/ubuntu/releases/bantera-20261021-9/deploy.py rollback
```

The preceding release is API 1.32.1 / website 0.17.0 in bantera-20261021-8.
Source manifests, backups and verification proofs remain on the server. No Git
commit/push, App Store submission or Google Play submission was performed.

## Admin word activity rankings: 21 October 2026

Release **bantera-20261021-5** is published with status **SUCCESS**. Backend
**1.31.0** runs in `bantera-api-20261021-5` (loopback 18144), website
**0.16.0** in `bantera-website-20261021-5` (loopback 13038), and connector
`cloudflared-20261021-5` uses readiness port 12070.

The Overview page now ranks users by listened or spoken words for Today,
This week and All time, with pagination and links to user details. Activity
uses existing device-local date buckets; the reference date is Pacific/Auckland
and weeks start Monday. Counts combine legacy and language-tagged device
snapshots. Offline activity appears after syncing.

**GO for existing published apps:** additive admin-only endpoint, no migration,
no new secret, and no mobile update. The latest phone callback search handoff
from API 1.30.1 is preserved. The website's existing BANTERA_API_BASE_URL setting
now targets the candidate API internally; both containers share the dedicated
internal network `bantera-app-20261021-5`, retaining their existing networks.
The first staged dashboard check caught separate Docker networks before
promotion; after adding that private connection, all checks passed.

Validation: backend build and 1,083 tests passed (16 optional skips); website
lint had zero errors, type checking/build passed, and all 14 tests passed.
Candidate and public checks each passed 13 existing HTTP contracts and 20
ranking checks, including all six filters against independent read-only SQL
aggregates, admin authorization, pagination and rendered dashboard content.
Live browser checks confirmed period and metric controls. All 130 monitored
public responses returned 200. Verified PostgreSQL backup: **6,185,226 bytes**.
No test data was written to production.

Previous API 1.30.1, website 0.15.0 and connector remain available for rollback
without restoring the database:

```bash
python3 /home/ubuntu/releases/bantera-20261021-5/deploy.py rollback
```

Source hashes, runtime snapshots, backup and verification proofs remain in the
release directory. Post-release retention completed with approximately 111 GB
available. No App Store or Google Play release was required.

## Phone callback search handoff: 21 October 2026

Release **bantera-20261021-4** completed at **2026-10-21 09:51:24 UTC**, status
**SUCCESS**. Backend **1.30.1** runs in `bantera-api-20261021-4` (loopback
18143), with `cloudflared-20261021-4` (readiness 12069). Website **0.15.0**
remains in `bantera-website-20261021-3` (loopback 13036). Both public versions
were independently verified. Database backup: **6,184,993 bytes**.

**GO for existing published apps:** no endpoint/schema/configuration/secret or
service-registration changes, and no mobile update required. Phone callbacks
direct web/image searches and live weather requests to a real-time voice call
started inside Chats > Bantera AI. The server removes callback search tools
and blocks invented search calls without interrupting audio. In-app voice
calls retain device-side search. Resume identities prevent old search-enabled
callback instructions from being reused.

The build and 92 focused AI regressions passed (5 optional database/provider
tests skipped). Candidate and public smoke checks each passed 13 HTTP contracts.
The actual candidate assembly passed 25 search policy/tool checks across four
Live models. No provider calls or database writes were made by that policy
probe. Physical locked-screen behavior and live spoken phrasing remain untested.

The previous API 1.30.0 and its connector remain available. Rollback preserves
database writes and the shared website:

```bash
python3 /home/ubuntu/releases/bantera-20261021-4/deploy.py rollback
```

Post-deployment retention removed three obsolete containers; approximately
111 GB of 194 GB remains available (43% usage). Source hashes, private runtime
snapshots, database backup, controller, availability samples and verification
proofs remain in the release directory.

## Gemini key dashboard and disk retention: 21 October 2026

Release **bantera-20261021-3** is published with status **SUCCESS**. Backend
**1.30.0** runs in `bantera-api-20261021-3` (loopback 18142), website
**0.15.0** in `bantera-website-20261021-3` (loopback 13036), and connector
`cloudflared-20261021-3` uses readiness port 12068. Both public version markers
and authenticated `/dashboard/gemini-keys` rendering were verified. Candidate
and public checks each passed 13 HTTP contracts, plus 12 candidate and 14 public
key-dashboard checks. PostgreSQL backup: **6,035,687 bytes**.

**GO for existing published apps:** additive admin endpoints, existing encrypted
settings storage, no migration or new secret. No mobile update is required.
The previous API 1.29.0, website 0.14.0 and their connector remain available for
rollback without restoring the database:

```bash
python3 /home/ubuntu/releases/bantera-20261021-3/deploy.py rollback
```

Disk cleanup completed at **2026-10-21 02:44:56 UTC**. Available capacity rose
from **3.5 GB / 194 GB (99% reported usage)** to **112 GB / 194 GB (43%)**.
Across cleanup passes, 169 obsolete Bantera containers and 103 unused Bantera
image tags were removed, along with disposable build caches, old extracted
release copies and package download caches. Databases, volumes, uploads,
backups, model caches and unrelated application containers were preserved.
The first pass stopped on a root-owned generated build file; the corrected,
bounded cleanup completed on retry. Logs and `cleanup-proof.json` remain in
the release directory.

The hourly `server-disk-guard.timer` is enabled and active; a manual service run
returned success. Cleanup starts at 80% usage or below 30 GiB available.
Builds require at least max(20 GiB, 10% capacity). New Bantera containers have
10 MB x 3 log limits; the host journal is capped at 512 MB and 14 days. Current
and one rollback release are protected. See [disk policy](server-build-retention.md).
The guard records actionable failures in the journal; external alerts are not
configured. Other applications must adopt the shared build lock and log policy
in their future deployment procedures.

After cleanup, both Bantera public versions returned 200; Evervault and V2en
also returned 200. The unchanged shared route `cc.asxp.eu.org` returned 502
and `tv.aify.nz` returned 404 at `/`; neither had a pre-cleanup HTTP baseline,
so these results do not establish a new regression or a pre-existing failure.
Their routing and application containers were preserved.

No Git commit/push or store submission was performed.

## AI Practice level context: 21 October 2026

Release **bantera-20261021-2** completed at **2026-10-21 01:31:27 UTC**.
Backend **1.29.0** runs in `bantera-api-20261021-2` (loopback 18141),
through `cloudflared-20261021-2` (readiness 12067). Website **0.14.0** remains
in `bantera-website-20261021-1`. Both public versions were independently
verified; **156/156** monitored HTTP responses returned 200.

**GO for existing published apps:** unchanged API contracts, no migrations,
new settings, secrets or service registrations. Saved Practice level is read
per authenticated account and learning language for AI messages, calls and
reminders. Selected review level guides replies while preserving earned level.
Session identity changes prevent resuming stale level instructions.

Build and 617 AI/Practice tests passed (8 optional skips). Candidate runtime
checks passed 13 HTTP contracts and 11 level-context checks against the actual
candidate assembly/database, including A0 prompts, review selection, language
and account isolation, fresh saved levels and resume identity. Synthetic records
were enclosed in a rolled-back transaction. The same 13 HTTP contracts passed
publicly. No provider speech quality or physical callback check was performed.

Verified PostgreSQL backup: **5,949,920 bytes**. Source hashes, reviewed dirty
paths, private runtime snapshots, proofs and controller remain in the release
directory. The unchanged signed iOS **3.1.0 (421)** was installed successfully
on the iPhone 17 Pro (CoreDevice sequence 2872), without launching or testing
on the phone. Signature validation passed and no app source was newer than
the signed build. No new app version is needed for the backend feature.

During rollout, Japanese A0 waiting was diagnosed independently: one approved
draft, zero ready exercises and zero saved audio clips. Six correlated audio
runs failed with HTTP 429; the already-enabled paid fallback was attempted six
times and also entered quota cooldown each time. This predates and persists
across the release; no provider settings or billing were changed. The app polls
every four seconds for up to three minutes; the worker retries failed demand
after 90 seconds. This release does not resolve that provider-capacity issue.

Rollback preserves current writes and restores A0-aware API 1.28.0:

```bash
python3 /home/ubuntu/releases/bantera-20261021-2/deploy.py rollback
```

No Git commit/push or store submission was performed.

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


## 2026-10-09 release 9: call clock and readable historical context

Explicit user deployment authorised staging and promotion of the local backend
working tree as `bantera-20261009-9`. Public API version **1.12.3** and website
version **0.7.0** were verified after the connector handover. Website source was
unchanged; its deployment marker identifies release 9.

The call relay consumes legacy client `clock` updates as metadata without sending
a Gemini `clientContent` turn, which could interrupt the AI's own reply. The
current clock remains available through the time tool and fresh setup/resumption
context. Historical timing JSON uses readable punctuation. Existing app versions
remain compatible. No database migration, new configuration, provider-model
change or credential change was required.

Release directory: `/home/ubuntu/releases/bantera-20261009-9`. Candidate API/web
ports are **18103/13015**; tunnel readiness port **12025**. Protected source
manifests, runtime snapshots, a **3,376,669-byte** verified database dump,
`deploy.py`, `prepare.py`, `smoke.py`, build logs, promotion log and availability
samples are retained there. Existing encrypted ChatGPT storage mount and its
configuration were cloned without printing credentials. Both Bantera origins
were replaced while preserving other tunnel ingress routes and networks.

Candidate and post-handover authenticated checks passed, including connected AI
accounts, fetched model/reasoning choices, search allowlist, timeout settings and
authentication gates. The two-minute stability window passed. All **130** public
availability samples returned HTTP **200**; this establishes sampled availability,
not a guarantee that every individual request experienced zero downtime. Focused
backend validation passed **61 tests**, with **4** environment-dependent tests
skipped. The deployment controller rolls back automatically on promotion failure.

Manual application rollback, without restoring the database or losing new writes:

```bash
python3 /home/ubuntu/releases/bantera-20261009-9/deploy.py rollback
```

This restores release 8 (backend **1.12.1**, website **0.7.0**). Previous application
containers and `cloudflared-20261009-8` remain available. This deployment used the
local source archives and did not commit or push Git changes.


The accompanying normal iOS release **2.10.0 (344)** was installed on the user's
iPhone and read back through CoreDevice. Receipt files on the Mac:
`/tmp/bantera344-iphone-install.json` and
`/tmp/bantera344-iphone-installed-app.json`. No physical-device launch or tests
were performed. The signed release explicitly targets `lib/main.dart`; temporary
simulator diagnostic entrypoints were not installed on the phone.

App verification included native audio/CallKit configuration tests (**7 passed**),
Dart regression tests, **12** repeated simulator navigation cycles with 2,000
synthetic messages, and real on-device-executed simulator image searches that
retrieved JPEG previews from two non-Wikimedia websites. The exact intermittent
half-transition reported by the user was not conclusively reproduced; route
coordination and deferred/lazy history rendering address observed code races.
Web image search is app-local and required no additional backend changes.


## 10 October 2026: latest voice turn, resumption and explicit search

User-authorised release `bantera-20261010-1` completed at **2026-10-09
21:03:13 UTC (10:03 NZDT on 10 October)**. Public API **1.12.4** and the
website **0.7.0** release marker were verified. The website source was unchanged;
its candidate was redeployed alongside the backend. The API/web loopback ports
are **18104/13016**, with connector readiness on **12026**.

**GO for existing published apps:** optional `resumeText` is additive and older
clients retain checkpoint compatibility. No migration, new secret, configured
model/voice change or authentication-contract change. Fresh Gemini sessions get
summary/history once as private setup reference, never an unfinished learner
turn. Successful resumption sends neither old setup instructions nor history.
Current committed recordings retain trusted time and device-zone/continuity
metadata. Prompt revision 4 replaces checkpoints with the old policy. Historical
questions/tool requests are completed, and device search requires a current
explicit request rather than an ordinary question.

Validation: **733 backend tests passed**, **11** environment-dependent tests
skipped; **30 app regressions passed**; targeted Dart analysis, .NET build and
signed normal iOS release build succeeded. A real three-round test on production's
selected `gemini-3.1-flash-live-preview` verified two resumed follow-ups, no
search for either ordinary question, and a search tool invocation for an explicit
search request. A separate synthetic PCM recording exercised streaming upload
and confirmed that the newest topic was answered without repeating the older
festival request. No physical-phone launch or test was performed.

Candidate/admin and post-handover checks passed. All **130** monitored public
HTTP samples returned **200** during promotion and the stability window. This
records sampled availability, not guaranteed continuity for every existing call.
Startup logs showed no error entries. Protected source hashes, runtime snapshots,
build/controller files and a validated **3,380,719-byte** database dump are retained
in `/home/ubuntu/releases/bantera-20261010-1`. Existing connected ChatGPT storage,
keys, Docker networks and other Cloudflare Tunnel routes were preserved.

Rollback retains current database writes:

```bash
python3 /home/ubuntu/releases/bantera-20261010-1/deploy.py rollback
```

This restores release 9 through `cloudflared-20261009-9`, whose applications remain
available. Promotion had automatic rollback on failed public checks. Source was
published from reviewed working-tree archives; no Git commit/push or store
submission was performed for this fix.

CoreDevice confirmed normal iOS **2.10.1 (345)** installed on the user's iPhone.
Install and app-version receipts are `/tmp/bantera345-iphone-install.json` and
`/tmp/bantera345-iphone-installed-app.json` on the Mac. Installation only; the app
was not launched or tested on the phone.

## 10 October 2026: current-turn focus and dated summary provenance

User-authorised release `bantera-20261010-2` completed at **2026-10-09
21:19:19 UTC (10:19 NZDT on 10 October)**. Public API **1.12.5** and
website **0.7.0** release marker were verified. Website source is unchanged.
Candidate API/web ports are **18105/13017**; connector readiness is **12027**.
All **130** monitored public HTTP samples were 200 and startup/error log checks
found no error entries. The previous applications and connector remain available.

**GO for published apps:** no migration or new environment variables/secrets.
The optional `contextKind` field is additive. The summary endpoint still returns
a `summary` string; its new internal version-2 format carries source timestamps
per item, which existing clients preserve as background text. Source-message time
and planned-event time are separate. Legacy undated memory remains unknown.
Current voice metadata explicitly identifies the new recording and trusted
current date/time/timezone. Summary and completed messages remain private setup
reference only for fresh sessions. A checkpoint received before reply completion
cannot be saved as if it includes that completed reply. Policy version 5 renews
older checkpoints once. Current production model/voice settings are unchanged.

Validation: **735** full-suite backend tests passed with **11** environment-only
skips, followed by **21** focused timing tests after adding one more regression;
**17** app history/memory tests passed. .NET build, targeted Dart analysis, debug
and signed normal iOS release builds passed. Real streamed PCM tests on
`gemini-3.1-flash-live-preview` exercised a barbecue preference, a mild-spice
follow-up, and a change to mango sorbet. Two follow-ups resumed successfully;
responses addressed new details and made no unsolicited searches. Staged real
text-provider summary generation and incremental merge retained source dates and
a later timezone change. These were synthetic inputs, not physical-phone tests.

Source archives/hashes, runtime snapshots, build logs, smoke proof, and validated
**3,381,694-byte** database backup are in
`/home/ubuntu/releases/bantera-20261010-2`. Existing connected-account storage,
secrets, networks and unrelated Cloudflare routes were preserved. Promotion had
automatic rollback. Manual rollback preserves current DB writes:

```bash
python3 /home/ubuntu/releases/bantera-20261010-2/deploy.py rollback
```

This restores `bantera-20261010-1` (API 1.12.4). No Git commit/push or store
submission was performed. Normal iOS **2.10.2 (346)** was installed on the user's
iPhone without launching it; CoreDevice receipts are
`/tmp/bantera346-iphone-install.json` and
`/tmp/bantera346-iphone-installed-app.json` on the Mac.


## 11 October 2026: locked-screen callback startup diagnostics

Release `bantera-20261011-1` passed promotion on 10 October at 14:10:53 UTC
(11 October, 03:10:53 NZDT). Backend **1.12.6**, website **0.7.0**. The
website source is unchanged; its release marker identifies this deployment.

- API: `bantera-api-20261011-1`, loopback port **18106**.
- Website: `bantera-website-20261011-1`, loopback port **13018**.
- Tunnel: `cloudflared-20261011-1`, readiness port **12028**.
- Release directory: `/home/ubuntu/releases/bantera-20261011-1`.
- Previous release `bantera-20261010-2` remains available. Rollback with
  `python3 /home/ubuntu/releases/bantera-20261011-1/deploy.py rollback`.
- Compatibility: **GO** for existing released apps. New allowlisted diagnostic
  codes/phase are additive; no schema changes or new required secrets.
- Local backend build and tests passed (739 passed, 11 integration/live tests
  skipped). Staged API, authenticated dashboard, configuration validation,
  public domains, and the two-minute post-promotion window passed.
- Companion app **2.10.3 (347)** starts native duplex I/O in CallKit activation,
  retains early activation until Dart readiness, and avoids deactivating the
  system-owned session during handoff. Native compilation and callback
  event-order tests passed. Physical locked-screen audio is unverified.
- The signed iPhone build is ready. Installation was blocked by CoreDevice 4016
  because the phone was unavailable; no device launch or test was performed.

Private runtime snapshots, source hashes, verified database backup, stage and
promotion logs are retained in the release directory. Automatic rollback covers
failed promotion; it does not restore an old database or discard new user writes.

## 12 October 2026: silent voice messages and fresh callback sessions

User-authorised release **`bantera-20261012-1`** completed at **2026-10-11
21:42:13 UTC (12 October, 10:42:13 NZDT)**. Public API **1.12.7** and
website **0.7.0** were verified through both Cloudflare Tunnel domains. Website
source is unchanged; its release marker identifies this deployment.

- API: `bantera-api-20261012-1`, loopback port **18107**.
- Website: `bantera-website-20261012-1`, loopback port **13019**.
- Connector: `cloudflared-20261012-1`, readiness port **12029**.
- Release directory: `/home/ubuntu/releases/bantera-20261012-1`.
- Active record: `/home/ubuntu/releases/bantera-current.json`.

**GO for existing published clients:** no migrations, new secrets or changed
model/voice configuration. Speech detection runs on streamed PCM before Gemini
connection, avoiding fabricated replies to empty recordings and tested noise.
New clients explicitly opt into the `no_speech` WebSocket frame and HTTP
`{ "noSpeech": true }` result; older clients receive the existing error shape.
Each initial scheduled callback starts a fresh Gemini session, while a reconnect
within that callback can resume. Shared keys, encrypted ChatGPT credentials,
database contents, Docker networks and unrelated tunnel routes were preserved.

Validation: **762 backend tests passed**, **11** external/provider tests skipped;
.NET build succeeded. Candidate and post-switch authenticated admin checks
verified model catalogues, reasoning validation, search restrictions and timeout
configuration. The staged server rejected empty, 20 ms and three-second silent
WebSocket inputs without audio/transcripts; legacy WebSocket compatibility and
an empty WAV HTTP upload also passed. These synthetic tests did not contact
Gemini or read user conversations. Speech-like background audio can still pass
voice activity detection; it is not an intelligibility guarantee.

All **130/130** public HTTP samples returned **200** during handover and the
two-minute stability window. Public API version, website release marker,
home/download/login pages, public lessons and authentication gates passed.
Candidate API logs contained zero fail/critical entries and website logs zero
error lines. This records observed HTTP availability, not uninterrupted existing
WebSocket calls or physical iPhone acoustic/CallKit behaviour.

Source archive hashes, reviewed working-tree manifest, controller scripts,
`voice-proof.json`, build/stage/promotion logs and a validated **3,400,226-byte**
PostgreSQL backup are retained in the release directory. Prior application
containers remain running and the previous connector is stopped but retained.
Automatic rollback was armed throughout promotion and was not needed.

Rollback for this release preserves subsequent database writes:

```bash
python3 /home/ubuntu/releases/bantera-20261012-1/deploy.py rollback
```

This restores `cloudflared-20261011-1`, backend **1.12.6** and the previous
website release marker. Deployment used reviewed working-tree archives; it did
not create a Git commit, push a branch or submit a store release.


## 12 October 2026: model-call generation timeline

User-authorised release **`bantera-20261012-2`** completed at **2026-10-11
22:16:50 UTC (12 October, 11:16:50 NZDT)**. Backend **1.13.0** and website
**0.8.0** are live and their source commits are pushed to `main`:
backend `b7aa93ec6fa089e632c7d610ebc54b291ba1c26f`, website
`65f1fd383bfec7790496bafe785755a7392de67f`.

- API: `bantera-api-20261012-2`, loopback **18108**.
- Website: `bantera-website-20261012-2`, loopback **13020**.
- Connector: `cloudflared-20261012-2`, readiness **12030**.
- Release directory: `/home/ubuntu/releases/bantera-20261012-2`.
- Active record: `/home/ubuntu/releases/bantera-current.json`.

The video detail page now groups recorded model requests, GPT progress, retries,
responses, validation and fallbacks into a numbered timeline. Each new GPT and
Gemini request has a call ID and recorded outcome. Older runs explicitly disclose
missing history; current model settings are never used to invent an older run's
model. Provider response success is distinguished from subsequent validation.

**GO for existing published apps:** existing API/auth contracts remain compatible;
no migration, new secret or environment setting is required. Runtime environment,
persistent mounts, Docker networks, encrypted ChatGPT credentials and unrelated
Cloudflare ingress routes were preserved. Model/voice settings were unchanged.

Validation: **161 focused backend tests**, **4 timeline tests**, **8 existing
website audio-browsing tests**, backend and website production builds, TypeScript
and focused lint passed. Authenticated checks verified both requested historical
video pages before promotion and through the public domains afterwards. One
synthetic Luna request with low reasoning returned two answer characters in
**1.554 seconds**; persisted events included start, accepted, created,
answer-started, answer-streaming and success. This proves a short Luna request and
its event persistence, not reliability of long dialogue/high-reasoning requests.

The first promotion automatically rolled back because the new public smoke test
used Python's default User-Agent, which received HTTP 403. The same authenticated
request passed with `Bantera-release-verification`; the verification script now
sets that identity for all API and website requests. No application code change
was needed. Both attempts and their verification scripts/logs are retained.
The second promotion passed all checks and the two-minute stability window:
**140/140** public API/homepage availability samples returned HTTP 200
(**192/192** across both attempts). Candidate logs had no fail/critical entries;
a missing GSSAPI-library notice also exists in the previous release. Existing
NuGet package warnings remain. HTTP sampling does not establish uninterrupted
individual long-lived connections.

The release retains source hashes, private runtime snapshots, a validated
**3,428,931-byte** PostgreSQL backup, `timeline-proof.json`,
`timeline-live-proof.json`, `model-trace-proof.json`, and build/promotion logs.
Previous application containers remain running; the old connector is stopped
and retained. Rollback preserves new database writes:

```bash
python3 /home/ubuntu/releases/bantera-20261012-2/deploy.py rollback
```

This restores `cloudflared-20261012-1`, backend **1.12.7**, website **0.7.0**,
and the previous active release record without restoring the database.

## 12 October 2026: fresh sessions for all new audio calls

User-authorised release **`bantera-20261012-3`** completed at **2026-10-11
23:22:14 UTC (12 October, 12:22:14 NZDT)**. Backend **1.13.1** is live;
the website remains **0.8.0**, with an updated deployment marker. Every initial
in-app or CallKit call ignores previous Live checkpoints and supplies the saved
device summary/recent history once. Reconnects preserve the ongoing call's
remaining duration. Voice-message resumption is unchanged.

**GO for existing apps:** no API shape, database migration, new configuration or
secret change. Validation: **771 backend tests passed**, **11 external tests
skipped**, backend build and **17 app history/memory tests** passed. Staged and
public authenticated dashboard/API checks passed, as did **130/130 HTTP samples**
during handover and the stability window. A client disconnect produced two log
entries for the same close-handshake exception in the unchanged general chat
WebSocket handler; the pre-existing missing GSSAPI library notice also remains.
HTTP checks do not prove uninterrupted WebSockets or a physical nine-minute call.

Runtime environments, mounts and unrelated tunnel routes were retained. The
release directory contains source archive hashes, working-tree manifest, verified
**3,439,789-byte** database backup, scripts and verification logs. Ports are API
**18109**, website **13021**, connector readiness **12031**. Prior application
containers and connector are retained. Rollback preserves subsequent DB writes:

```bash
python3 /home/ubuntu/releases/bantera-20261012-3/deploy.py rollback
```

Rollback restores `cloudflared-20261012-2`, backend **1.13.0**, website **0.8.0**
and the prior release marker. Deployment used the reviewed working tree without
a Git commit or push. The unchanged signed app **2.10.4 (348)** was reinstalled on
the requested iPhone and its installed version verified with CoreDevice. The app
was not launched or physically tested; this session-policy fix is server-side.

## 12 October 2026: on-device AI vocabulary

User-authorised release **`bantera-20261012-4`** completed at **2026-10-12
00:53:57 UTC**. Backend **1.14.0** is live; website source remains unchanged at
**0.8.0**, with an updated release marker. The new `manage_vocabulary` tool is
relayed to opted-in app clients during voice messages and realtime calls.
Vocabulary is stored on the device; requested tool results are returned to AI.
App web/image searches continue to execute on the device.

**GO for existing apps:** the capability defaults off, preserving older clients.
No database migration, new secret or environment setting is required. Runtime
environment, persistent mounts, Docker networks, encrypted provider credentials
and unrelated Cloudflare ingress routes were preserved.

Validation: **777 backend tests passed**, **11 external/environment tests skipped**;
the final focused vocabulary/search suite passed **12 tests**. App analysis,
vocabulary persistence, both tool transports, UI/localisation and voice lifecycle
tests passed, along with debug and signed release iOS builds. Synthetic requests
to the configured **gemini-3.1-flash-live-preview** model successfully exercised
add, list/latest/count, update and remove with spoken responses. This verifies
provider tool behaviour, not physical microphone performance. Evidence is in
`vocabulary-provider-proof.json` in the release directory.

Staged and public authenticated checks passed. **132/132** public API/homepage
availability samples returned HTTP 200 during promotion and the two-minute
stability window. Both public release markers were verified. HTTP sampling does
not establish uninterrupted individual WebSockets. A validated **3,451,515-byte**
database backup, source hashes, runtime snapshots and promotion logs are retained.
Ports are API **18110**, website **13022**, connector readiness **12032**.

Previous application containers and the stopped connector remain available.
Rollback preserves subsequent database writes:

```bash
python3 /home/ubuntu/releases/bantera-20261012-4/deploy.py rollback
```

This restores `cloudflared-20261012-3`, backend **1.13.1**, website **0.8.0** and
the previous active release record without restoring the database.

Signed app **2.11.0 (351)** was installed on the requested iPhone and verified
through CoreDevice. It includes the local vocabulary notebook and the previously
accepted call echo protection changes. The app was not launched or physically
tested during installation.

## 12 October 2026: live call readiness and silent-response recovery

User-authorised release **`bantera-20261012-5`** completed at **2026-10-12
01:38:04 UTC**. Backend **1.14.1** is live; unchanged website source **0.8.0**
was staged with the new release marker. **GO for existing apps:** no wire-format
break, migration, new environment setting or secret is required.

Calls now monitor recognised input awaiting a reply and stalled response audio,
with bounded recovery and generic user-facing failures. Quiet learner pauses do
not cause a timeout. Technical diagnostics record counts, timing, model and a
call correlation ID, never audio, transcripts or tool arguments. These remain
account-linked operational records under the existing 90-day retention policy.
The reported 136-second call ended with a normal client close; logs contained no
quota event and did not establish the cause of its earlier silence.

New-call countdown and farewell timing begin with the first forwarded AI audio;
renewals retain the remaining budget. App **2.11.1 (352)** shows Connecting until
that audio arrives. Native VoiceProcessingIO readiness still precedes the Gemini
connection; a regression test verifies that the controller awaits native start.

Validation: the backend full suite passed **788 tests**, with **11 environment or
external-service skips**; final focused checks passed **18**, with **1 DB skip**.
App focused checks passed **9 tests**, followed by **2 passing transport tests**
including the added startup-order assertion. Targeted analysis, debug and signed
release builds passed. Existing unrelated analyzer/NuGet warnings remain.

Staged and public authenticated checks passed. **132/132** availability samples
returned HTTP 200, and both public release markers were verified. HTTP sampling
does not establish uninterrupted individual WebSockets or physical acoustics.
A validated **3,456,303-byte** database backup, source hashes, runtime snapshots
and promotion logs remain in the release directory. Runtime environments,
mounts, credentials and unrelated Cloudflare routes were retained. Ports are API
**18111**, website **13023**, connector readiness **12033**.

Previous containers and connector are retained. Rollback preserves new DB writes:

```bash
python3 /home/ubuntu/releases/bantera-20261012-5/deploy.py rollback
```

This restores release **bantera-20261012-4**, backend **1.14.0**, website **0.8.0**
and its connector. Signed app **2.11.1 (352)** was installed on the requested
iPhone; it was not launched or physically tested. Deployment used the working
tree without committing or pushing it.

## 12 October 2026: per-user paid Gemini key routing

Release `bantera-20261012-6` deployed backend **1.15.0** and website **0.9.0** from the reviewed working trees. The app was unchanged and needs no reinstall. The AI dashboard now supports an encrypted paid key, explicit user access, regular-then-paid fallback, and paid-only testing. Default access remains regular keys only. No paid key or user access was enabled by deployment.

Compatibility: **GO for published app versions**. The admin API additions are additive; no database migration or new environment variable was required. The existing `ChatGptConnection__EncryptionKey` was verified and preserved, along with container mounts and all tunnel routes. Backup `database-before.dump` was verified readable (3,470,653 bytes).

Validation: full backend suite 802 passed / 11 skipped; final Gemini-focused suite 180 passed; website TypeScript, targeted ESLint and production build passed. Staged and public checks verified encrypted-storage readiness, masked admin responses, 401/403 access control, invalid-setting rejection, dashboard rendering and existing generation timelines. Public version and deployment marker matched; the handover and stability sampler recorded 132/132 HTTP-200 checks. Paid provider calls remain untested until an admin supplies a key and allows a user.

Runtime: `bantera-api-20261012-6` (loopback 18112), `bantera-website-20261012-6` (13024), and `cloudflared-20261012-6` (readiness 12034). The prior release 5 applications and connector remain available for rollback. Controller, hashes, protected runtime snapshots and smoke-test proofs are in `/home/ubuntu/releases/bantera-20261012-6`. If rollback becomes necessary, run its `deploy.py rollback`; do not restore the database. A rollback to release 5 also removes paid-key routing support.

## 12 October 2026: paid AQ key save correction

Release `bantera-20261012-7` successfully deployed backend **1.15.1** and website **0.9.1**. Corrected paid-key validation to accept AQ. authorization keys and lengths up to 4096; the password field retains the typed value on failed saves and clears it only after successful persistence. Static admin errors now distinguish malformed keys from invalid users and conflicting remove/replace choices. No mobile changes or installation.

Compatibility: **GO for published apps**. Migration `20261012025341_ExpandEncryptedAppSettings` widens `app_settings.Value` from varchar(2000) to text; the database confirmed the new type and migration entry. Existing values and old applications remain compatible. No new secrets or environment configuration. Database backup was verified readable (3,471,869 bytes); application rollback must not reverse the migration or restore the database.

Validation: 809 backend tests passed, 11 optional tests skipped. A disposable local PostgreSQL test saved/read a 3003-character synthetic AQ. key encrypted, verified blank-key updates preserve it, and verified explicit removal. Website TypeScript, targeted ESLint and production build passed. Staged and public admin checks verified specific invalid-key errors, authentication gates and the 4096-character form. Public release markers matched and the availability sampler recorded 132/132 HTTP-200 checks. No real user key was copied, logged or used for a provider request.

Runtime: `bantera-api-20261012-7` (18113), `bantera-website-20261012-7` (13025), `cloudflared-20261012-7` (12035). Release 6 containers/connector remain for rollback. Controller, source hashes, protected backup and verification artifacts: `/home/ubuntu/releases/bantera-20261012-7`; use its `deploy.py rollback` only when needed.

## 12 October 2026: voice-message callback capability correction

Release `bantera-20261012-8` deployed backend **1.15.2** with unchanged website **0.9.1**. Voice-message and live-call prompts explicitly support scheduling CallKit callbacks; reminder-only requests remain messages. Missing notification registration returns delivery-specific guidance, and idempotency cannot report a previously scheduled message as a successful call. Callback tool attempts/outcomes now produce privacy-safe technical diagnostics. The session context revision invalidates older restored instructions.

Compatibility: **GO for published apps**. No mobile changes, new migration or configuration; no reinstall needed. Backend build and 814 tests passed (9 optional tests skipped). Synthetic Gemini 3.1 Live tests using the configured paid credential invoked `schedule_callback` and produced audio, including a recorded-audio test with an earlier refusal in history. Arguments were validated and tool results were simulated: no actual callback was scheduled or phone tested. Existing logs did not establish the precise reason for the original refusal.

The initial staged startup reached the host inotify instance limit. Ten unrouted API/website containers for releases `20261012-1` through `20261012-5` were stopped after checking active tunnel routes; containers/images remain recoverable. Release 7 stayed live and release 6 stayed available during recovery. The candidate then passed startup, authenticated callback-service resolution, admin and public checks. Backup was verified readable (3,483,920 bytes). The promotion recorded **132/132 HTTP-200** availability checks and public markers confirmed the new release.

Runtime: `bantera-api-20261012-8` (18114), `bantera-website-20261012-8` (13026), `cloudflared-20261012-8` (12036). Release 7 remains the immediate rollback. Deployment controller, source hashes, backup and proofs are under `/home/ubuntu/releases/bantera-20261012-8`; its `deploy.py rollback` restores release 7 without restoring the database. Source was deployed from the working tree; no commit or push.


## 12 October 2026: device-rendered AI emojis

Release `bantera-20261012-9` deployed backend **1.16.0** at **09:19:51 UTC**, reusing the unchanged website **0.9.1** container and release marker `bantera-20261012-8`. New app **2.12.0 (354)** opts into `send_emojis` for voice messages and live calls. Unicode is rendered and persisted in the current reply bubble without downloads or new packages. Existing clients do not advertise the tool.

Compatibility: **GO for published apps**. Request metadata and tool declarations are additive; no migration, environment variable or new secret. A verified readable **3,506,363-byte** database backup and protected runtime snapshots remain in the release directory. Source archive SHA-256: `22b136b45fee4182dde5b23b335f2f1c04767b3d6f90c34258a893e37cf2e281`.

Validation: initial focused backend checks passed 110 with 4 optional/provider skips. A synthetic recorded-voice trial caught a missing device dispatch whitelist entry; after correcting it, 55 focused tests passed, including emoji tool dispatch with and without audio preamble. Backend build, Flutter targeted analysis and tests, iOS debug/signed release builds and signed Android bundle build passed. A synthetic Gemini voice request delivered Ireland and India flags through the device bridge and returned 270,750 bytes of audio. This did not test physical phone acoustics. Public authenticated checks and both domains passed; the stability sampler recorded **128/128 HTTP-200** responses.

Runtime: `bantera-api-20261012-9` (18115), unchanged `bantera-website-20261012-8` (13026), and `cloudflared-20261012-9` (12037). Previous release 8 is retained for rollback via `/home/ubuntu/releases/bantera-20261012-9/deploy.py rollback`, without restoring the database. Website routes, unrelated Cloudflare origins and runtime settings were preserved.

Signed app **2.12.0 (354)** was installed on the requested iPhone and verified through CoreDevice; it was not launched or physically tested. Android bundle **354 (2.12.0)** was uploaded using the user's Chrome and submitted for production full rollout with all 18 release-note locales. Play Console showed Changes in review with automated quick checks still running; managed publishing is off. This is submission evidence, not publication. Source changes remain uncommitted and unpushed.

## 14 October 2026: authorised app key preferences

Release `bantera-20261014-1` deployed backend **1.17.0** and website **0.10.0**, and app **2.13.0 (355)** was installed on the iPhone without launching it. The paid Gemini key panel is now at `/dashboard/bantera-ai`. Allowed users can select regular-only, paid fallback or paid-only from app Settings. The key remains encrypted on the backend; the app receives only permission and mode. All Gemini calls resolve the job owner or authenticated request user, including summaries, corrections and admin background audio tests. Other providers and on-device searches are unchanged.

Compatibility: **GO for existing published apps**. Additive endpoint and encrypted-policy field; no migration or new environment variable. A verified readable **3,584,528-byte** database backup is preserved. Validation: 165 focused backend tests passed (including disposable PostgreSQL persistence/access checks), three app widget tests passed, targeted Flutter analysis, iOS debug/signed release builds, website TypeScript/ESLint and production build passed. Staged and public checks verified the new panel location, all three mode choices, denied-user 403s, unauthenticated 401s and unchanged production paid-key configuration. Promotion completed at **02:55:53 UTC**, with **130/130 HTTP-200** availability samples.

Runtime: `bantera-api-20261014-1` (18116), `bantera-website-20261014-1` (13027), `cloudflared-20261014-1` (12038). Source manifests, protected runtime snapshots, backup and proofs are in `/home/ubuntu/releases/bantera-20261014-1`.

After successful promotion, 30 idle, unrouted old API containers were stopped to prevent old background workers from processing queued work under outdated paid-key rules. Active routes, connections and claimed background audio jobs were checked first. Containers/images were retained, not deleted. The release controller's rollback starts and health-checks the retained previous API before restoring `cloudflared-20261012-9`; the prior website remains available. Use this release's `deploy.py rollback`, without restoring the database. Older backend versions do not enforce the new regular-only preference, so treat policy behavior as a rollback limitation. Future deployments should retire drained prior backend workers after promotion, retaining restartable rollback containers rather than leaving old workers active indefinitely.

Deployment used working trees; no commit or push was performed.

## 14 October 2026: repeat callback request hardening

Release `bantera-20261014-2` deployed backend **1.17.1** at **09:22:13 UTC**, reusing website **0.10.0** (`bantera-20261014-1`). Investigation found no recent scheduler-tool attempts for the reported missing requests; prior callbacks had been answered. Existing privacy-safe logs did not capture the exact cause. A synthetic recorded request also worked before this change, so this is not evidence of a reproduced paid-key failure.

The prompt now distinguishes a new explicit callback request from replay of an old historical request, requires scheduling before spoken confirmation, and applies this distinction in each voice-message time context and retry instruction. A session revision refreshes restored instructions. Completed voice replies record request ID, model, elapsed time, byte counts and scheduling-attempt count, without transcripts, audio, tool arguments or credentials. This makes a completed reply with no scheduler invocation visible in diagnostics.

Compatibility: **GO for existing published apps**. No endpoint/wire change, database migration or new setting/secret. Backend build passed; 137 focused tests passed, four optional tests skipped. Local PostgreSQL tests verified distinct recordings create distinct schedules while retries remain idempotent, and checked the diagnostic field allowlist. Paid-key synthetic provider checks invoked `schedule_callback` for two successive identical-topic requests and no scheduling tool for ordinary conversation. Tool results were simulated: no real call was scheduled by these tests.

The verified database backup is **3,596,301 bytes**. Staged and public smoke checks passed; availability sampling recorded **144/144 HTTP-200** responses. API: `bantera-api-20261014-2` on loopback **18117**; unchanged website on **13027**; connector `cloudflared-20261014-2` on readiness **12039**. Release artifacts are under `/home/ubuntu/releases/bantera-20261014-2`. Its `deploy.py rollback` starts and checks the retained previous API before restoring the previous connector; it does not restore the database. The idle unrouted previous API was stopped after verifying no active non-database connections or claimed background audio jobs. Its container and image remain retained.

No mobile source changes were needed. At the user's explicit request, signed **2.13.0 (355)** was reinstalled on the iPhone and launched, with installed version and launch confirmed by CoreDevice. No physical callback test was performed. These changes remain uncommitted pending explicit push authorization.

## Verified DM delivery: 16 October 2026

Release **`bantera-20261016-1`** completed at **02:44:21 UTC** on 16 October.
Backend **1.18.0** is live at `https://api.bantera.app/version`; the website remains
on `bantera-20261015-1` (0.11.1). The active release record is
`/home/ubuntu/releases/bantera-current.json`.

- API: `bantera-api-20261016-1`, loopback **18118**.
- Connector: `cloudflared-20261016-1`, readiness **12041**.
- Website: unchanged `bantera-website-20261015-1`, loopback **13028**.
- A verified pre-migration PostgreSQL custom-format backup, private runtime
  snapshots, reviewed source archive/checksum and working-tree manifest are in
  `/home/ubuntu/releases/bantera-20261016-1`.
- Migration `20261016022900_AddChatAudioIntegrity` adds nullable hash/byte-count
  fields only. **Compatibility GO**: old API contracts remain available; old
  unverified receipts retain seven days, and v3 verified receipts trigger immediate
  R2-then-database cleanup. No new environment variables or secrets are required.
- Local backend suite: **823 passed, 12 skipped** (external/provider or separately
  configured integrations). A real isolated PostgreSQL test exercised failure and
  pagination behavior with fake storage. Staged and public API checks each passed
  11 HTTP checks plus migration verification, including authenticated receipt
  validation and synthetic missing-message idempotency. No real user message or R2
  object was used for deployment tests. Startup had no observed unhandled
  exceptions or failed database commands.
- Promotion preserved all shared connector networks and unrelated ingress. Both
  public domains passed the two-minute stability window (128 sampled HTTP responses,
  all 200). The previous idle API
  was stopped after active connection/background-work checks; its container and
  previous connector remain available for rollback.

Rollback, if needed, uses the release-specific controller:
`python3 /home/ubuntu/releases/bantera-20261016-1/deploy.py rollback`.
It restores API **1.17.1** and `cloudflared-20261015-1`, retaining the additive
columns and subsequent user writes. It does not restore the backup. Delivered
messages already removed from R2/live PostgreSQL cannot be recreated by an
application rollback.

See [verified DM delivery](direct-message-delivery.md) for the receipt boundary,
legacy clients, retries, local-history behavior and infrastructure-backup scope.

App **2.15.0 (359)** was subsequently installed on the iPhone 17 Pro, confirmed
through CoreDevice's installed-app inventory. It was not launched or physically
tested. App verification included 12 passing delivery/history/API tests, clean
analysis, unsigned debug and signed release builds, and strict code-signature
verification. A cached local recording remains playable if the receipt response
is lost and metadata cannot be fetched again; this fallback sends no unverified
receipt. Changes remain uncommitted pending an explicit push request.

## October 16, 2026: AI voice image attachments

Release `bantera-20261016-2` deployed backend **1.19.0** and retained website **0.11.1** (`bantera-website-20261015-1`). Promotion completed at **03:18:16 UTC**. Candidate and public authentication/DM smoke checks passed, followed by the two-minute public stability window. The previous API/connector remain available for rollback.

- API: `bantera-api-20261016-2`, loopback **18119**.
- Connector: `cloudflared-20261016-2`, readiness **12042**.
- Previous connector: `cloudflared-20261016-1`.
- Release snapshot and database backup: `/home/ubuntu/releases/bantera-20261016-2`.
- Compatibility: **GO**. Optional image start payload and additive ready capability; old voice-only clients continue unchanged. No new migration or required configuration. The prior additive DM integrity migration remains included.
- Verification: 98 focused backend tests passed (three unrelated opt-in tests skipped); a separate real Gemini Live 3.1 test recognized four synthetic images with streamed audio. See [image protocol](ai-voice-images.md).
- Mobile **2.16.0 (360)** also restores iOS media playback mode before each practice cue after a DM call. Native simulator tests verify the category/mode reset and protection of active call audio; physical route listening is left to the user.

Mobile installation verified: **Bantera 2.16.0, build 360** (`bantera.lisenhuang.com`) installed on the connected iPhone 17 Pro. The app was not launched or physically tested. Both native cue-routing simulator tests passed; 57 focused Flutter tests and the 12-test WSS suite passed. Targeted analysis of the changed AI code/tests found no issues. The production runtime setting was confirmed as `gemini-3.1-flash-live-preview`, matching the successful four-image voice test. The idle previous API was stopped after checking for active connections and background work; its container remains available for rollback. No Git commit or push was performed.

## October 16, 2026: Language-relevant online discovery

Release `bantera-20261016-3` completed at **04:02:15 UTC**, publishing backend
**1.20.0**. The website remains **0.11.1** (`bantera-website-20261015-1`).
Discovery now requires a shared learning language or a learner/native match in
either direction. Existing DM partners retain their chats and online status.
Mobile adds an account-specific, default-off “Hide people with my native
language” option under the Chats overflow menu.

- Compatibility: **GO for existing apps**. Unchanged response contracts, no new
  migration, secrets or runtime configuration.
- API `bantera-api-20261016-3`: loopback **18120**; connector
  `cloudflared-20261016-3`: readiness **12043**.
- Protected release artifacts and verified **3,693,234-byte** database backup:
  `/home/ubuntu/releases/bantera-20261016-3`.
- Verification: **33 backend tests** passed, including isolated PostgreSQL
  discovery/DM-preservation checks; **6 Flutter tests** passed; targeted analysis
  and unsigned debug/signed release builds passed. Candidate and public smoke
  checks each passed 13 HTTP checks plus migration verification. Public stability
  sampling recorded **136/136 HTTP-200** responses. No real messages were changed
  by smoke tests.
- The idle previous API was stopped after active-work/connection checks. Its
  container and previous connector remain available through this release's
  `deploy.py rollback`, which restores backend **1.19.0** without restoring the DB.
- Signed **2.18.0 (363)** was installed on the iPhone 17 Pro and independently
  confirmed through CoreDevice's installed-app inventory. No physical testing or
  launch was performed. No Git commit or push was performed.

## October 18, 2026: Explicit dialogue proficiency requirements

Release **`bantera-20261018-2`** completed at **09:00:55 UTC** (22:00:55 NZDT),
publishing backend **1.20.3**. Generated dialogue prompts explicitly require
Beginner **A1**, Intermediate **B1**, and Advanced **C2** vocabulary and grammar.
For a language or regional variety without a formal CEFR mapping, the prompt
requires complexity comparable to English at the selected level while keeping
the dialogue in the selected spoken language and regional variety. Existing
Cantonese wording, regional rules and speech pacing remain in place.

- Compatibility: **GO for published apps**. Shared text-prompt change only; no
  API/schema changes, new migrations, configuration or secrets. No app changes
  or iPhone reinstall were needed. The installed app uses the new prompt for new
  dialogue generation.
- Verification: backend build and **196 prompt/duration tests** passed, including
  captured outbound A1/B1/C2 and equivalent-difficulty instructions. Candidate and
  public smoke checks each passed **13 HTTP checks** plus existing migration
  verification. The stability window recorded **128/128 HTTP-200** samples across
  both public domains, with no observed fatal startup/database-command errors.
- API `bantera-api-20261018-2`: loopback **18122**; connector
  `cloudflared-20261018-2`: readiness **12045**. Website **0.11.1** remains in
  `bantera-website-20261015-1` on **13028**. Other tunnel routes were preserved.
- The protected release directory contains the verified **3,814,503-byte** database
  backup, runtime snapshots, source manifest, staged/public proofs and rollout
  logs. Source archive SHA-256:
  `aa2dc7f74fdb8863799003d92a68bd5cc343a4b02a5a61daf6045929f6ad913d`.
- The previous idle API was stopped after checking active connections and
  background work; its container/image and connector remain available. Rollback:
  `python3 /home/ubuntu/releases/bantera-20261018-2/deploy.py rollback` restores
  backend **1.20.2**, retaining the explicit CEFR levels but omitting the added
  equivalent-difficulty instruction. It does not restore the database.

This release includes the user's follow-up during the initial **1.20.2** rollout
(`bantera-20261018-1`). Deployment used the tested working tree; no Git commit or
push was performed.

## October 19, 2026: Device vocabulary counts and quizzes

Release **`bantera-20261019-1`** completed at **03:04:28 UTC**, publishing backend
**1.21.0**. App **2.19.0 (370)** advertises vocabulary capability version 2,
enabling `stats`, `latest` and device-selected random `quiz` actions. Existing
list/search, mutation and review tools remain available. Vocabulary storage stays
on the device; requested tool results are relayed to the model.

- Compatibility: **GO for published apps**. Clients without capability version 2
  retain the previous action schema. The capability participates in the session
  resumption identity. No new migration, secret or runtime configuration.
- API `bantera-api-20261019-1`: loopback **18123**; connector
  `cloudflared-20261019-1`: readiness **12046**. Website **0.11.1** remains in
  `bantera-website-20261015-1` on **13028**; other tunnel routes are preserved.
- Verification: **108 backend tests passed**, three optional provider/database
  tests skipped; **42 focused Flutter tests passed**, clean targeted analysis,
  unsigned debug and signed release builds. Provider transport in these tests is
  mocked, not a real-model conversation. Candidate and public smoke checks each
  passed 13 HTTP checks plus migration verification. Availability monitoring
  recorded **130/130 HTTP-200** responses; no fatal/startup/database error markers
  were observed in the new API logs.
- Protected release artifacts include a verified **3,875,696-byte** database
  backup and source manifest. Source archive SHA-256:
  `5c4a67a8e57b7e5f8807f32fa831994225ea144ac812ac6f9bd29f951ef27a49`.
- The idle previous API was stopped after active-work/connection checks. Its
  container/image and connector remain available for rollback:
  `python3 /home/ubuntu/releases/bantera-20261019-1/deploy.py rollback` restores
  backend **1.20.3** without restoring the database. Deployment used the tested
  working tree; no Git commit or push was performed.

## October 19, 2026: Spoken reply Practice and daily-goal celebration

Release **`bantera-20261019-2`** completed at **09:20:36 UTC**, publishing backend
**1.22.0**. App **2.20.0 (371)** adds spoken reply Practice, hidden adaptive
difficulty, shared generated material with per-user seen tracking, listening and
speaking word counts, and a shared daily-goal celebration followed by an eligible
native review request. Apple controls whether the review prompt appears.

- Compatibility: **GO for published apps**. New authenticated Practice endpoints
  and four additive tables; existing API contracts remain unchanged. No new
  environment variables or secrets. The migration was verified in production.
- API `bantera-api-20261019-2`: loopback **18124**; connector
  `cloudflared-20261019-2`: readiness **12047**. Website **0.11.1** remains in
  `bantera-website-20261015-1` on **13028**. Both public domains passed promotion
  and stability checks.
- Verification: **847 backend tests passed, 14 skipped**; focused Flutter tests,
  callback/CallKit and audio regression tests passed. Unsigned debug and signed
  release iOS builds succeeded. UI golden checks cover light/dark Practice,
  large text and the goal celebration. Targeted analysis had two formatting-only
  informational diagnostics and no errors.
- Candidate and public smoke checks each passed 13 HTTP checks. A separate live
  Practice smoke test verified actual generation, all three audio files, consent,
  neutral unclear speech, correct/incorrect intent, idempotent retries, account
  isolation, shared library reuse and per-user seen exclusion. Synthetic test
  accounts were removed; generated shared lessons remain reusable.
- A verified **3,900,399-byte** database backup and source manifest are retained
  in the protected release directory with the smoke proofs and deployment logs.
  The idle previous API was stopped after checking active work/connections;
  its container, image and connector remain available for rollback:
  `python3 /home/ubuntu/releases/bantera-20261019-2/deploy.py rollback` restores
  backend **1.21.0** without restoring the database. Practice requires 1.22.0.
- Signed app **2.20.0 (371)** was installed and launched on the iPhone 17 Pro.
  Physical microphone interaction and locked-screen callback audio remain
  unverified; installation is not evidence of those behaviours. Deployment used
  the tested working tree; no Git commit, push or App Store submission occurred.

## October 19, 2026: Practice single-speaker correction

Release **`bantera-20261019-3`** targets backend **1.22.1** and app
**2.20.1 (372)**. A stored cue 1 contained an invented second speaker answering
its question. The verified transcript reproduced this defect. Practice now uses a
single-speaker TTS configuration for each utterance, with explicit verbatim-only
instructions for preview models and separate style metadata for structured models.
Discover retains its existing two-speaker generation. Text generation and review
also reject self-answered cues and embedded conversations.

- Compatibility: **GO for published apps**. Additive `ContentVersion` column on
  Practice dialogues, defaulting existing rows to version 1. New generation is
  version 2; obsolete rows cannot be selected or resumed. Existing API routes,
  auth and other features remain compatible. No new configuration or secrets.
- The content refresh tolerates stale previous IDs and releases an old request's
  unique ID before replacing its retired exercise. New app builds refresh a
  retired answer automatically. Old app builds can advance with Next.
- API loopback **18125**; connector readiness **12048**. Existing website
  `bantera-website-20261015-1` / **0.11.1** and other tunnel routes are retained.
- Local verification: **857 backend tests passed, 14 skipped**, including ten
  new single-speaker/primary-fallback tests; **32 Flutter checks passed**, including
  callback/CallKit and audio regression tests. Targeted analysis is clean; unsigned
  debug and signed release builds succeeded. Physical microphone and locked-screen
  callback audio remain unverified.
- The staged release passed 13 API smoke checks, actual generation and all Practice
  flow checks, including replacement of a cached legacy exercise. Independent
  transcription of a new cue and both choices found one speaker in each clip and
  no invented response. Recognizer contraction variations were normalized for this
  check; original transcriptions are retained in `single-speaker-audit.json`.
- Protected artifacts include a verified **3,966,000-byte** database backup, source
  manifest, smoke proofs, audio audit and promotion log. Authorized cleanup removes
  version-1 Practice rows and their audio, cascading only their Practice exposures
  and attempts. Word totals and proficiency levels are retained; affected evidence
  windows are cleared. Cleanup counts are recorded in `practice-cleanup-proof.json`.
- Rollback: `python3 /home/ubuntu/releases/bantera-20261019-3/deploy.py rollback`
  restores backend **1.22.0** without restoring the database. This would also restore
  its flawed Practice generator; use only for an urgent wider incident. Deployment
  does not commit/push Git or submit an App Store version.

Release outcome: **SUCCESS at 09:43:44 UTC**. Both public domains passed the
post-promotion checks. The idle old API was stopped and retained for rollback.
Cleanup verified removal of **31 obsolete dialogues and 93 audio objects**, with
**zero** version-1 rows remaining and **8** regenerated exercises ready. Installed
inventory confirmed **2.20.1 (372)** on the iPhone 17 Pro.

## October 19, 2026: Practice answers without AI verification

Release **`bantera-20261019-4`** targets backend **1.22.2** and app **2.20.2 (373)**.
The app matches recognized speech against both displayed choices locally. Ambiguous
speech immediately receives a neutral retry. Clear matches use the existing answer
endpoint, whose deterministic matcher checks the stored correct answer and records
idempotent progress. The AI judgement method has been removed entirely, with no
AI fallback. Shared exercise generation and audio generation still use AI.

- Compatibility: **GO for published apps**. Same authenticated request and response
  contract, including older transcript-only clients. No new migration, configuration
  or secrets. Existing content format 2, audio, word counts and proficiency remain.
- Matching normalizes punctuation/case, common English contractions and numbers;
  a narrowly limited internal spelling error can match a longer word. Negation
  omissions, number changes, short near-matches, uncertain distinguishing words and
  ambiguous choices return unclear. Free-form paraphrases may require repeating
  the displayed reply. Forty identical phone/server fixtures cover these cases and
  representative non-Latin scripts.
- **900 backend tests passed, 14 skipped**; **72 Flutter checks passed**, including
  matcher, Practice UI, callback/CallKit and audio tests. Targeted analysis is clean;
  unsigned debug and signed release builds succeeded. Physical microphone and
  locked-screen callback testing remain unverified.
- Staged API and Practice flow checks passed. Nine synthetic answer requests had
  **10.5 ms median / 19.7 ms maximum loopback latency**, with **zero Practice model
  calls** recorded during the checks. This measurement excludes phone transcription
  and network latency. Test identities/content were removed. Proof is retained in
  `no-ai-answer-proof.json`.
- API `bantera-api-20261019-4` uses loopback **18126**; connector
  `cloudflared-20261019-4` readiness **12049**. Website **0.11.1** and its existing
  container/other tunnel routes are retained. Protected source manifest and a
  verified **3,988,803-byte** database backup accompany the release.
- Rollback: `python3 /home/ubuntu/releases/bantera-20261019-4/deploy.py rollback`
  restores **1.22.1**, including its AI answer verification. It does not restore
  the database. No Git commit/push or App Store submission is performed.

Release outcome: **SUCCESS at 10:02:46 UTC**. Both public domains passed final
checks; the idle previous API was stopped and retained for rollback. CoreDevice
confirmed app **2.20.2 (373)** installed on the iPhone 17 Pro. Basic matching is
language-independent; English contraction/number normalization is additional, so
other languages may require more neutral retries. The multilingual fixtures verify
representative cases, not exhaustive linguistic equivalence across every locale.

## Practice tolerant choice matching: 19 October 2026, release 5

Release **bantera-20261019-5** succeeded at **2026-10-19 10:25:41 UTC**.
Backend **1.22.3** is live in `bantera-api-20261019-5` (loopback **18127**),
with connector `cloudflared-20261019-5` (readiness **12050**). Website **0.11.1**
remains in `bantera-website-20261015-1`, loopback **13028**. Both public domains,
API version, website marker, lesson listing and authentication gates passed the
rolling handover and stable verification window. The controller automatically
restores the prior connector if promotion fails.

The release deploys the tested uncommitted backend working tree. Source hashes,
Git base and changed paths, private runtime snapshots, verified 3,991,068-byte
PostgreSQL backup and smoke evidence are retained in the release directory.
**GO for published clients:** same endpoint and response contracts; no new schema
migration, environment variable or secret. Existing app clients remain compatible,
although older Practice apps retain their stricter local matching until updated.

Both phone screenshot transcripts passed the authenticated candidate endpoint.
Fifteen synthetic answer requests had a 10.1 ms loopback median and 19.6 ms maximum;
no Practice AI model calls were recorded during those checks. These timings exclude
speech recognition and the phone's network. Local tests passed 932 backend cases
(14 existing skips) and 116 Flutter checks, including callbacks and CallKit. Device
microphone and locked-screen callback acceptance remain unverified.

App **2.20.4 (375)** was installed and confirmed in CoreDevice's installed app list.
The first automatic launch was blocked while the phone required its passcode.
No App Store submission or Git commit/push was performed.

Rollback for this release:

```bash
python3 /home/ubuntu/releases/bantera-20261019-5/deploy.py rollback
```

It restores backend **1.22.2** through `cloudflared-20261019-4` and leaves the
current database intact. The previous image and container are retained; the
controller starts the previous API if it is stopped.

The previous API was retained running after the retirement guard detected active
background work. No running job was interrupted to clean up the old release.

After the phone was unlocked, CoreDevice successfully launched **2.20.4 (375)**.
All **128/128** public-domain availability samples during promotion returned HTTP 200.

## Visible Practice level: 20 October 2026 (NZDT), release 6

Release **bantera-20261019-6** succeeded at **2026-10-19 14:55:26 UTC**
(**20 October, 03:55 NZDT**). Backend **1.22.4** is live in
`bantera-api-20261019-6`, loopback **18128**, through
`cloudflared-20261019-6`, readiness **12051**. Website **0.11.1** remains
`bantera-website-20261015-1`, loopback **13028**. Public API version and both
public domains passed the controller's promotion checks and stable window.

**GO for existing published clients:** ready Practice responses gain only the
string `level` (A1, A2, B1, B2, C1 or C2), sourced from the displayed dialogue.
Existing response fields, progression, auth and answer matching are unchanged.
No migration, new config, secret or content reset is required. All six levels
passed authenticated ready/resume checks; existing Practice, audio, consent,
isolation and deterministic-answer smoke checks also passed. Fifteen synthetic
answer requests produced zero Practice AI calls. Local checks: 107 Flutter tests
including callback/CallKit regression tests; 932 backend tests with 14 existing
skips; unsigned debug and signed release iOS builds and signature verification.

App **2.20.5 (376)** is built but **not installed**: the iPhone became unavailable.
The user was asked to reconnect and unlock it. Physical microphone and locked-screen
callback acceptance remain unverified. No Git commit/push or App Store submission.

The release directory retains source hashes, working-tree manifest, private runtime
snapshots, a verified 4,068,837-byte DB backup and smoke proofs. Earlier containers
remain available for rollback. The SSH connection dropped intermittently during
transfer and verification; the detached controller subsequently confirmed SUCCESS.

Rollback (does not restore the database):

```bash
python3 /home/ubuntu/releases/bantera-20261019-6/deploy.py rollback
```

This restores the retained backend **1.22.3** through `cloudflared-20261019-5`.

Installation follow-up: after the user reconnected and unlocked the iPhone,
CoreDevice confirmed **2.20.5 (376)** installed and successfully launched
`bantera.lisenhuang.com`. This supersedes the pending-installation status above;
physical microphone and locked-screen callback acceptance remain unverified.

## Optional Practice translation: 20 October 2026 (NZDT)

Release `bantera-20261020-1` targets backend **1.23.0** and app **2.23.0 (383)**.
**GO for existing published clients:** `deferLevelScoring` on Next defaults false,
so old clients retain immediate first-clear scoring. New clients defer until Next,
allowing a persisted translation hint to exclude the whole exercise, even if it
is revealed after an answer. `/{id}/translation` is authenticated, account-scoped
and idempotent; ready responses add `deferredScoring` and `translationUsed`.

Migration `PracticeTranslationAssistance` adds two non-null boolean columns with
false defaults to `practice_exposures`. Existing data and old inserts remain valid.
No new environment variable, secret or AI dependency is required. Historical
already-scored legacy exercises are preserved and replaced when a new client
resumes; unscored active exercises can adopt deferred scoring.

Local validation: 938 backend tests passed with 14 existing skips, 145 Flutter
tests passed, changed-file analysis clean, debug and signed iOS builds passed.
Staged checks passed API health/auth gates, legacy Practice and deterministic
matching, all six visible levels, and assistance before/after correct and wrong
answers, resume persistence, first-clear scoring, duplicate Next and isolation.
The existing no-AI-answer smoke suite recorded zero Practice model calls.

The release uses loopback API **18129** and connector readiness **12052**, retaining
website **0.11.1** and its container. A verified **4,166,703-byte** DB backup,
source hash/working-tree manifest and private runtime snapshots are retained.
Rollback controller: `python3 /home/ubuntu/releases/bantera-20261020-1/deploy.py rollback`.
It restores backend **1.22.4** through `cloudflared-20261019-6` without restoring
the database. App translation hints then fail closed instead of exposing a hint
that could be scored. No Git commit/push or App Store submission is performed.

Deployment completed successfully at **21:10:32 UTC on 19 October** (**10:10:32
NZDT on 20 October 2026**). Both public domains and expected versions passed the
stable verification window and independent final HTTP checks. The prior API is
retained running because the idle guard detected background work; nothing was
interrupted for cleanup. App **2.23.0 (383)** was verified installed on the iPhone
17 Pro; automatic launch initially failed because iOS reported the phone locked.
After unlocking, CoreDevice confirmed a successful launch of 2.23.0 (383).

## Practice Chinese/Cantonese answer formatting: 20 October 2026 (NZDT)

Release **bantera-20261020-2** completed successfully at **2026-10-19 22:16:31 UTC**
(**20 October, 11:16 NZDT**). Backend **1.23.1** runs in
`bantera-api-20261020-2`, loopback **18130**, through
`cloudflared-20261020-2`, readiness **12053**. Website **0.11.1** remains in
`bantera-website-20261015-1`; unrelated tunnel routes were preserved. Both public
domains and exact release versions passed the stable handover window and an
independent final HTTP check.

**GO for existing published clients:** deterministic matcher changes only, with
unchanged endpoints, request/response shapes and authentication. No new migration,
environment variable, secret or runtime dependency. Existing older clients may
still reject numeral formatting locally until updated; new app **2.25.0 (388)**
uses the same normalization and was verified installed and launched on iPhone
17 Pro. No Git commit/push or App Store submission was performed.

The shared fixtures cover $300/三百元, $30/三十蚊, 5分鐘/五分鐘, and exact
Cantonese replies with phrase-level low recognition confidence. Different amounts,
currencies, negation and ambiguous choices retain retry behavior. 960 backend tests
passed, 14 existing optional tests skipped. The candidate passed 13 HTTP smoke
checks, existing Practice/level/translation flows, and 22 authenticated new matcher
cases. The existing 15-request answer check recorded zero Practice AI calls.

Protected release artifacts include a verified **4,203,840-byte** database backup,
source archive hash and working-tree manifest, runtime snapshots and smoke proofs.
Old application containers remain available; no background work was interrupted
for cleanup. Rollback restores **1.23.0** without restoring the database:

```bash
python3 /home/ubuntu/releases/bantera-20261020-2/deploy.py rollback
```

Live read-only model-settings verification confirmed Practice generation and
content validation use `chatgpt/gpt-6-luna` with high reasoning, falling back to
`gemini-flash-latest`. This release does not alter that configuration. User-answer
matching remains deterministic with no AI judgement.

## Practice ahead preparation: 20 October 2026 (NZDT)

Backend **1.23.2**, release candidate `bantera-20261020-3`, targets eight unseen,
fully voiced exercises per locale/level pool and requests refills on ready/resume,
Next and answer paths. Reachable adjacent levels are warmed up to four scored
results before a boundary; a deferred current answer is projected without changing
score or marking additional material seen. Two consumers and nonblocking database
pool locks avoid blocking the entire preparation queue behind another pool/replica.
Speech synthesis is serialized within this worker. Approved drafts and completed
clips survive failures and are resumed before generating more text. At most two
text batches are generated per demand. Existing prompts, duplicate validation,
model fallback, answer matching and public contracts remain unchanged.

**GO for published clients:** no endpoint/schema/auth changes, new migrations,
environment variables or secrets. Local build passed (existing warnings only);
967 backend tests passed with 14 existing optional skips. App **2.25.2 (390)**
passed 72 focused Practice/callback/CallKit/audio tests, targeted analysis, debug
and signed release builds, signature validation, installation and launch on iPhone
17 Pro. Only correct answers trigger the particles/rocket; all recognized speaking
and listening still count. Physical microphone and locked-screen callback audio
remain unverified.

Candidate verification passed 13 HTTP/migration checks, existing Practice flows,
all six levels, translation-assistance scoring, and 22 Chinese/Cantonese fixtures.
Fifteen deterministic answer checks recorded zero Practice AI calls (11.2 ms
loopback median, 335 ms maximum). Cached B1-to-B2 advancement returned ready in
12.3 ms and all three audio objects were playable. These timings exclude phone
networking and speech recognition.

The forced-empty-pool test verified next-level approved text is prepared before
Next without early scoring/seen exposure. Live speech requests hit provider quota
limits: new next-level audio completion was **not verified**. Approved drafts
survived repeated failures and no new text model calls occurred during the final
90-second retry window. Unit tests cover preserving completed clips and generating
only missing clips on retry. This provider limitation can still cause waits when
a user's unseen audio buffer is empty; the release does not promise zero waits.
The earlier unpromoted concurrent-audio candidate was retained stopped as
`bantera-api-20261020-3-rejected`; it never received public traffic.

Protected artifacts are in `/home/ubuntu/releases/bantera-20261020-3`, including
source manifest/archive, private runtime snapshots, verified **4,258,715-byte**
backup and smoke proofs with the provider limitation explicitly recorded. Candidate
API loopback is **18131**, connector readiness **12054**. The website remains
**0.11.1** in `bantera-website-20261015-1`. Rollback restores backend **1.23.1**
without restoring the database:

```bash
python3 /home/ubuntu/releases/bantera-20261020-3/deploy.py rollback
```

No Git commit/push or App Store submission was performed.

Deployment completed successfully at **2026-10-19 23:00:05 UTC** (**20 October,
12:00:05 NZDT**). The active record names `bantera-20261020-3`, API
`bantera-api-20261020-3` and connector `cloudflared-20261020-3`. All **128/128**
public availability samples returned HTTP 200. Independent HTTPS checks confirmed
API **1.23.2** and the unchanged website release. Previous application containers
and connector remain available for rollback. The speech-provider limitation above
remains outstanding and was not treated as proof of fresh-audio completion.

## Practice diagnostics: 20 October 2026 (NZDT)

Backend **1.23.3**, release **bantera-20261020-4**, completed at
**2026-10-19 23:38:11 UTC**. API `bantera-api-20261020-4` uses loopback
18132; connector `cloudflared-20261020-4` readiness is 12055. Website
0.11.1 remains unchanged. Public domains/version markers passed, with
**156/156 HTTP 200** availability samples and retained rollback containers.

**GO for existing apps:** additive admin query filters and internal logging only;
no new schema, environment variables or secrets. Build passed; 969 backend tests
passed (14 existing optional skips). Candidate checks covered existing Practice,
all levels, assisted scoring, 22 number/recognition cases and 15 deterministic
answer requests with no AI calls. Diagnostics verification retrieved 32 correlated
events from an actual candidate preparation run through the admin API. An initial
English-only test missed events from multilingual fixtures; the corrected check
uses candidate-start time and verifies the actual run's locale consistently.

Text, review, audio, upload and save stages record start/completion/failure with
run ID, locale, level, dialogue/clip ID and elapsed time. Exceptions retain safe
technical type/status/frame details. Nested model/key/fallback events share the
run ID; raw utterances, audio and credentials are not added to these diagnostics.
Admin `/api/admin/ai-pipeline/events` supports `jobId` and `endpoint` filters.
Existing event retention is 90 days. Live audio logs confirmed HTTP 429 responses
across 14 regular keys for primary/fallback TTS, subsequent cooldown skips, and
one successful clip. Shared pool routing still does not use per-user paid access.

Backup verified: **4,345,180 bytes**. Rollback restores backend 1.23.2 without
restoring the database:

```bash
python3 /home/ubuntu/releases/bantera-20261020-4/deploy.py rollback
```

## Paid Practice audio fallback and ASR formatting: 20 October 2026

Release **bantera-20261020-6** completed at **2026-10-20 00:04:53 UTC**.
Backend **1.24.1** (`bantera-api-20261020-6`, loopback 18134) and website
**0.12.0** (`bantera-website-20261020-6`, loopback 13030) are routed by
`cloudflared-20261020-6` (readiness 12057). Both public version markers verified;
**132/132** public availability samples returned HTTP 200. Database backup:
**4,396,124 bytes**. Previous production 1.23.3/website 0.11.1 remains available.

**GO for existing published apps:** additive nullable admin request/status field;
existing encrypted paid-key storage; no new migration, environment variable or
secret. The paid Practice audio switch defaults off and remained off after
verification. An admin must enable it and save. Shared Practice audio tries
regular keys first for each configured TTS model, then the separately saved paid
key when enabled; cooldown/unusable keys are skipped. Existing per-user call modes
remain independent. Real paid-provider usage was not exercised or enabled by the
release tests. English currency/whole-hour normalization matches the phone's
shared fixtures while protecting differing amounts, currencies and negation.

Verification: 996 backend tests passed, 14 existing optional skips; website lint,
type checking, Node 24 production build and 12 tests passed. Runtime checks cover
13 HTTP contracts, all six levels, translation-assisted scoring, shared library
reuse across new users, audio objects, seen exclusion, 44 number/recognition
fixtures, and 15 deterministic answer requests with zero model calls. Paid settings
were checked through the authenticated public API/dashboard (regular-user rejection,
admin access, no raw key, default-off checkbox and help text). The public probe
needed the release-verification user agent; the default Python probe did not pass
the edge response checks. No production setting was changed.

Candidate 5 was never promoted. Candidate 6 initially stopped during staging due
to a stale candidate website container name; this was corrected before any public
handover. Its backend was rebuilt as `bantera-backend:bantera-20261020-6-final`
with the final time-format fixtures; previous candidate source/container retained.
One old smoke expectation for an unrecognized currency spelling was corrected to
neutral retry, consistent with protected currency matching. All checks then passed.
The website includes the previously prepared AI-consent privacy copy from its
current working tree. No Git commit/push or store submission was performed.

Rollback preserves database writes:

```bash
python3 /home/ubuntu/releases/bantera-20261020-6/deploy.py rollback
```

## Selected Practice review levels: 20 October 2026

Backend **1.25.0**, release **bantera-20261020-7**, completed at
**2026-10-20 00:54:00 UTC**. API `bantera-api-20261020-7` uses loopback
18135 and final image `bantera-backend:bantera-20261020-7-final`. Connector
`cloudflared-20261020-7` readiness is 12058. Website **0.12.0** remains in
`bantera-website-20261020-6`, with its previous public release marker. Both
public domains were independently verified; **156/156** sampled responses were
HTTP 200. The verified pre-migration database backup is **4,571,980 bytes**.

**GO for existing published apps:** `/api/me/practice/level` and response metadata
are additive; `useSelectedLevel` is optional and defaults false. Older clients
retain earned-level delivery and existing unfinished-exercise resume behavior.
Migration `20261020004018_PracticeReviewLevels` adds nullable SelectedLevel to
practice_progress and default-false ReviewMode to practice_exposures. No required
new environment variables or secrets. Current earned level is the server-enforced
ceiling; historical higher levels do not stay unlocked after a downgrade.
Review selection preserves the scoring window, persists per account/language,
and never scores review exposures, even if the earned level later changes.

Local build passed; **1,005** backend tests passed, with 14 existing optional
skips. Runtime checks passed: 13 HTTP/migration checks, shared library/audio reuse,
all six levels, translation assistance, 44 number/recognition fixtures, 15 answer
requests with zero model calls, and 10 review-selection/account/consent/legacy
compatibility checks. Review checks reused real ready shared material using
short-lived verification accounts, without introducing fake ready dialogues.
An initial candidate restricted legacy resume to the current level; the existing
six-level runtime check caught this. The corrected final candidate preserves the
legacy behavior and passed all checks before promotion. Initial image/source and
stopped candidate containers are retained; they never received public traffic.

Rollback retains current database writes and restores backend 1.24.1:

```bash
python3 /home/ubuntu/releases/bantera-20261020-7/deploy.py rollback
```

The prior connector and application containers remain available. No Git
commit/push, website change, paid-key setting change, or store submission was
performed for this release.

## Māori profile language selections: 20 October 2026

Backend **1.26.0**, release **bantera-20261020-8**, completed at
**2026-10-20 01:34:29 UTC**. API `bantera-api-20261020-8` uses loopback
18136, connector `cloudflared-20261020-8` readiness 12059. Website **0.12.0**
remains in release 6. Both public domains verified; **128/128** sampled public
responses were HTTP 200. Verified database backup: **4,698,890 bytes**.

**GO for existing published apps:** additive `mi-NZ` catalog selection and
language-name resolution; existing languages and contracts preserved. No new
migration, environment variable or secret. Profile native/learning fields save
and reload Māori. Speaking Practice explicitly returns 422 with
`practice_language_unsupported` for Māori, before queueing preparation or creating
progress. Current device/TTS language lists do not establish Māori speech support;
selecting a profile language is not a claim of fully supported voice learning.

Build and 1,008 backend tests passed (14 existing optional skips). Candidate
checks passed for catalog/profile saving, unsupported speech/consent, 13 existing
HTTP contracts, shared library/audio reuse, all six levels, assistance, 44
recognition fixtures, 15 deterministic answers without model calls and 10 review
checks. Public Māori checks also passed with an ephemeral verification account,
removed in a finally block. The initial public probe's default Python user agent
was rejected by the edge; the probe was corrected to use the existing release
verification user agent. No application change was required for that probe.

Previous API/connector retained. Rollback keeps database writes:

```bash
python3 /home/ubuntu/releases/bantera-20261020-8/deploy.py rollback
```

No Git commit/push, paid-key configuration change or store submission performed.

### Prepared, not deployed: Māori audio-dialogue restriction

Backend **1.26.1** is prepared locally after the user's decision to withhold Māori
audio dialogues. All three audio generation routes and admin audio-test creation
reject `mi`, `mi-NZ` and case/separator variants with 422 before creating a job.
Native/learning profile selection remains available. App **2.27.1 (400)** disables
full dialogue generation for that language with the existing localized message.

**GO for existing apps:** existing supported language behavior is unchanged;
only the newly added unsupported Māori generation path is rejected. No migrations,
configuration or secrets required. Build passed; 1,013 tests passed and 14 existing
optional skips. Production remains backend 1.26.0/release 8 until separately deployed.
The isolated live provider probe and physical iPhone capability findings are
recorded in `app/docs/maori-language-support.md` in the sibling app repository.

### Superseding prepared change: withdrawn Māori option

Backend **1.26.2** removes Māori from the language catalog. Existing generation
rejection protects stale clients; Practice uses its existing unknown-locale
validation. App **2.27.2 (401)** removes it from both pickers and filters older
cached catalogs. No profile data is rewritten. Native-only Cantonese filtering
is in the app; both Cantonese variants remain in the learning catalog.

**GO for deployment:** existing supported languages and request shapes unchanged;
no migrations or configuration. The withdrawn locale is intentionally no longer
offered. Build and 1,012 tests passed (14 optional skips). This version has not
been deployed; production remains 1.26.0/release 8.

## Māori withdrawal and native Cantonese filtering delivered

Backend **1.26.2**, release **bantera-20261020-9**, completed on
**20 October 2026 at 02:11:01 UTC**. API container `bantera-api-20261020-9`
uses loopback 18137; connector `cloudflared-20261020-9` readiness 12060.
Website 0.12.0 remains in release 6. Both public domains verified;
**136/136** availability samples returned HTTP 200. Verified backup:
**4,780,071 bytes**. Previous release 8 API/connector retained for rollback.

Candidate and public tests confirm Māori absent from the catalog, both Cantonese
learning options retained, retired Practice requests rejected, and all three
full audio generation endpoints rejecting Māori variants without creating jobs,
progress or exposures. Existing HTTP, shared-pool/audio, level, review,
translation-assistance and recognition regression checks passed. No new migrations,
configuration, paid-key setting changes, Git commit/push or store submission.

App **2.27.2 (401)** was installed and launched on the physical iPhone 17 Pro.
Its native-only filter hides mainland Cantonese, while learning choices retain
both Cantonese variants. Māori is excluded from device, API and cached picker
sources. The signed release APK was built and verified. Physical locked-screen
callback testing remains unverified; automated callback regressions passed.

Rollback preserves database writes:

```bash
python3 /home/ubuntu/releases/bantera-20261020-9/deploy.py rollback
```

## Global paid audio controls delivered: 20 October 2026

Release **bantera-20261020-10** completed at **2026-10-20 03:03:29 UTC**.
Backend **1.27.0** (`4a095cd`) and website **0.13.0** (`d9d5449`) are live.
API `bantera-api-20261020-10` uses loopback 18138; website
`bantera-website-20261020-10` uses 13031; connector
`cloudflared-20261020-10` readiness uses 12061. Both public versions were
independently verified. All **156/156** sampled HTTP responses returned 200.

**GO for existing published apps:** the new `dialogueAudioFallback` admin field
is additive; omitted writes preserve it. No migration, new environment variable
or secret is required. Both audio switches apply globally to all users. The
existing Practice setting remained enabled; new Dialogues fallback remained off.
No paid-key setting was changed by deployment checks.

Candidate checks covered 13 HTTP contracts, authentication and DI, shared Practice
library reuse and audio URLs, all six levels, translation-assisted eligibility,
44 recognition/number fixtures and 15 answer requests with zero AI model calls.
Authenticated admin checks confirmed both dashboard controls, masked API settings,
and rejection of ordinary users. Actual paid-provider failure routing was not
forced in production; previously passing backend unit tests cover that policy.

A verified **4,874,606-byte** PostgreSQL backup, immutable source archives/hashes,
private runtime snapshots, staged images and logs are retained in the release
folder. Previous API, website and connector remain available. No Git commit/push
or App Store submission was performed.

Rollback preserves current database writes:

```bash
python3 /home/ubuntu/releases/bantera-20261020-10/deploy.py rollback
```

## Cantonese clock matching and iOS 3.0.0: 20 October 2026

Backend **1.27.1**, release **bantera-20261020-14**, completed at
**2026-10-20 10:08:32 UTC**. API `bantera-api-20261020-14` uses loopback
18139; connector `cloudflared-20261020-14` readiness uses 12065.
Website **0.13.3** remains in `bantera-website-20261020-13` on 13034.
Both public domains and exact release versions were verified; all **160/160**
availability samples returned HTTP 200 during handover and the stability window.

The matcher accepts equivalent explicit Chinese half-past times (`6:30` and
`六點半`) and the opening Cantonese affirmative `得啊` / `得呀`. Clock
hour/minute order stays protected; different times, omitted times, negation and
ambiguous or wholly uncertain speech remain rejected. Expanded clock characters
do not count as additional reliable speech.

**GO for existing published apps:** request/response shapes and authentication
are unchanged. No database migration, new environment variable, secret or
startup dependency is introduced. Build and **222 backend practice tests** passed.
The candidate passed **138 authenticated matching/input-limit cases** and **13
HTTP contract checks**. The public API passed all **24 newly added matching
cases** and the same HTTP checks. Synthetic verification accounts and dialogues
were removed in finally blocks; no real user's transcript or answer was changed.
Candidate startup logs contained no error markers.

Source is the reviewed working tree based on `00c58e6`, with only the matcher,
normalizer, version and regression fixtures/tests changed. The immutable source
archive checksum, dirty-file manifest, private runtime snapshots, verified
**5,282,302-byte** database backup, controller and proof logs are retained in the
release folder. No Git commit/push or paid-provider setting change was performed.

The signed iOS app **3.0.0 (414)** was installed and launched on the physical
iPhone 17 Pro. It includes the Goal tab progress ring, retargeted celebration,
direct goal setup when no goal is saved, and matching normalization. Debug and
release builds, signature verification, analysis and **176 app tests** passed,
including callback/CallKit regressions. Physical locked-screen callback audio
remains unverified.

Previous API/connector remain available. Rollback preserves current database
writes and restores API 1.27.0 with the same website:

```bash
python3 /home/ubuntu/releases/bantera-20261020-14/deploy.py rollback
```

## A0 beginner Practice: 21 October 2026

Release **bantera-20261021-1** completed at **2026-10-20 23:58:00 UTC**
(21 October NZDT). Backend **1.28.0** and website **0.14.0** are live in
`bantera-api-20261021-1` (18140) and `bantera-website-20261021-1` (13035),
through `cloudflared-20261021-1` (12066). Exact public versions and the A0 FAQ
were independently verified. All **134/134** monitored HTTP samples returned 200.

**GO for existing published app clients:** optional `supportsA0` gates the new
level, existing A1–C2 stored numbers remain unchanged, and existing progress is
preserved. No schema migration, new environment variable or secret. The database
backup was verified at **5,831,909 bytes**. Runtime snapshots, source archives,
hashes, build logs and proof files are retained in the release directory.

Builds and **1,052 backend tests** passed (14 existing optional skips); website
build and 12 tests passed. App practice, matching and required callback tests
passed after correcting a stale `0.0%` expectation to the already-shipped `0%`.
Candidate and public checks each passed 13 HTTP contracts and 13 A0 state/API
contracts, including fresh A0 start, locked higher levels, legacy fallback,
review selection and preserving/restoring earned progress.

**Audio limitation:** the separate real A0 exercise/audio test did not pass.
Google TTS returned quota errors for regular keys and the already-enabled paid
fallback. A0 text was generated, but its audio remained pending. No key,
provider/model setting or paid-fallback policy was changed. Contract proofs
explicitly record `a0AudioReady: false`; they do not establish audio playback or
end-to-end A0 answer/Next progression. Disposable verification accounts were
removed on both success and interrupted generation checks. Existing per-language
pools remain available; new generation depends on provider quota recovering.

Signed iOS **3.1.0 (421)** passed debug/release builds and signature validation,
and was installed on the iPhone 17 Pro (CoreDevice database sequence 2864).
The app was not launched or tested on the phone. The user's confirmed boundary
is automatic translated meanings at A0, counting toward A1; A1–C2 translations
remain optional and exclude the exercise from level changes. Easier review never
changes an already-earned level. Device translation availability still applies.

Previous containers and connector are retained. **After real A0 progress is
saved, rollback requires an A0-aware API build**; do not blindly restore 1.27.1,
which indexes stored levels through the old six-level array. See
[the compatibility and rollback notes](practice-a0.md). Never restore the old
database as an application rollback. No Git commit/push or store submission was
performed.

## 27 October 2026: AI reminder cleanup and vocabulary instructions

Release **bantera-20261027-3**, backend **1.44.0**, completed successfully at
00:28:50 UTC. Adds authenticated reminder-history listing/removal and bulk
removal. Pending schedules and generated media are removed atomically; worker
conditional updates cannot revive deleted entries. Already-ringing calls remain
visible. Existing reminder cancellation endpoints are unchanged. Vocabulary tool
instructions request very simple definitions and a short example sentence.

**GO for published apps:** additive routes, no migration, no new configuration
or secrets. Website source/image remains 0.22.0, with a cloned container pointed
at the new backend. API 18163, web 13054, connector readiness 12092. A verified
8,620,509-byte database backup preceded staging. Local backend validation:
535 AI tests passed, 14 optional integration tests skipped; the isolated local
PostgreSQL removal/race/account-isolation test passed. Candidate and live checks
passed 12 contracts each; all 138 monitored public responses returned 200.
Post-release retention preserved the current and previous release, and left
112 GB free. Public checks passed again after cleanup. No Git push occurred.

Signed app 3.25.0 (482) was installed and verified on the user's iPhone without
launching it. 114 app tests passed, including cleanup, localized layout,
leaderboard, audio, memory, vocabulary and callback/CallKit tests. Locked-screen
physical CallKit acceptance remains unverified.

Rollback preserves database writes:
`python3 /home/ubuntu/releases/bantera-20261027-3/deploy.py rollback`

## 27 October 2026: Admin location and simplified user list

Release **bantera-20261027-4**, backend **1.45.0** and website **0.23.0**,
completed at **01:18:14 UTC**. Admin responses add latest known country and
catalogue language flags to user lists, and country/city/activity date to user
details. No raw IP address is exposed. Activity recording now updates the
location tuple together so a country-only visit cannot retain an earlier city
from another country. Missing location headers preserve the known tuple.

**GO for existing published apps:** response fields are additive; no database
migration, new environment variable, secret or DI dependency. City depends on
the existing Cloudflare visitor-location headers; missing data is displayed as
unavailable. Recording remains periodic, so this is an approximate last recorded
location, not a live tracking guarantee.

Backend build and 31 focused tests passed, including isolated PostgreSQL tests
for user isolation, country/flag projections and location updates. Website build,
focused lint and 25 tests passed. Candidate and live smoke checks each passed
14 contracts. All **130/130** monitored public responses returned 200. No
authenticated production dashboard session or real-user impersonation was used.

API loopback 18164, website 13055 and connector readiness 12093. Database backup
verified at **8,598,539 bytes**. Source archives, hashes and rollout proofs remain
in the release directory. Retention preserved release 4 and rollback release 3;
disk free space was 112 GB before building and 111 GB after cleanup. Public
checks passed again after cleanup. No Git commit/push occurred.

Rollback preserves database writes and restores backend 1.44.0 plus the prior
website image (whose public marker remains `bantera-20261027-2`):
`python3 /home/ubuntu/releases/bantera-20261027-4/deploy.py rollback`
