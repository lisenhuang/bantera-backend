# Live text input release

Published **1.34.0**, release **bantera-20261022-2**, on 22 October 2026. Public `/version` returned `1.34.0`. App **3.7.0 (431)** was installed and its version verified on the requested iPhone. Website **0.17.1** was reused without a frontend release.

## Compatibility verdict: GO

The existing voice WebSocket accepts an optional start field `inputMode: "text"`; omitted mode still means audio. The ready response adds `acceptText: true`. Explicit text sessions commit nonblank text up to 8,000 characters and reject PCM/images. Ordinary audio sessions retain voice activity and no-speech validation; supplying text cannot bypass them. Text is uploaded through the existing Live transcript path, preserving tool, provider, and key routing behavior.

Existing published clients retain their audio contract. There are no database migrations or new environment variables. Search still runs on the device; the backend only relays tool calls. No breaking endpoint, authentication, or error-shape change was introduced.

## Validation

- Backend build passed. Full suite: **1,098 passed, 16 skipped, zero failed**, including isolated PostgreSQL leaderboard integration checks.
- Text commit/upload without PCM, blank/oversized/mixed inputs, and audio VAD bypass rejection are covered by tests.
- Staged release and rolling connector handover completed with the previous release retained. **132 public availability samples returned HTTP 200**, with no failed samples.
- After cleanup, public API/website, auth/DI/receipt checks, admin ranking/aggregate/dashboard checks, and speaking ranking/UTC/legacy-seven-day/new-thirty-day/profile checks passed.
- These checks did not make a paid end-to-end Live provider request. Actual generated explanations and physical locked-screen callback audio remain acceptance checks, distinct from build, protocol, and installation verification.

## Release and rollback

Current API: `bantera-api-20261022-2`, port `18150`; connector: `cloudflared-20261022-2`, readiness port `12076`.

Immediate rollback API: `bantera-api-20261022-1`, version `1.33.0`, port `18149`; connector: `cloudflared-20261022-1`, readiness port `12075`.

The shared website remains `bantera-website-20261021-9`, port `13042`. Its existing internal API dependency, `bantera-api-20261021-9` on port `18148`, was retained. Host cleanup now recursively protects valid `BANTERA_API_BASE_URL` container dependencies of retained releases; the previous cleanup script was backed up before this change. This avoids removing an API still used by a shared website.

Disk space increased from approximately **112 GiB free / 43% used** before the build to **113 GiB free / 42% used** after cleanup. Current and immediate rollback paths, shared dependencies, database backups, volumes, and user data were preserved. Both public services passed checks after cleanup.

Backend source archive SHA-256: `ebb4f786145b3e94b0b05e682d5ca3aac774f11136d51b61f467a7a474109eb9`.

Local release evidence and scripts: `/tmp/bantera-word-video-release/`. Production release record: `/home/ubuntu/releases/bantera-current.json`. No secrets are included here. No Git commit or push was performed for this batch.
