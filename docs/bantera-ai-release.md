# Bantera AI release: backend 1.0.160, website 0.1.57, app 2.0.120 (308)

## Deployment

Deploy only on an explicit user request using the [Oracle AU deployment runbook](oracle-au-deployment.md). Stage and verify the backend before the website, retain rollback containers, and verify both public Cloudflare Tunnel domains. The backend main workflow builds a Docker image for CI only; it does not deploy.

Compatibility verdict: **GO for compatibility with the published app**, provided the existing production database and configuration pass the checks below. Human DMs, existing call signalling, authentication and released endpoints are unchanged. New AI routes require authentication; model settings require admin authorization. The model-catalog response gains an optional `liveModels` array.

The additive `20261006101252_AddAiCallbacks` migration creates only `ai_callbacks` and its indexes/foreign keys. Existing startup migration handling applies it; back up the database and confirm startup logs report success before serving the new app. No existing message/history rows are rewritten. Callback rows contain account/token references, due time, timezone, request deduplication key and status, never transcripts or audio. Completed/missed metadata is removed after seven days. Deleting the account or associated push-token row cascades to schedules.

AI voice messages are recorded on iOS/Android directly as mono, little-endian PCM16 at 16 kHz inside WAV files. The backend validates the container and reads the PCM bytes without transcoding, temporary files or `ffmpeg`. The Docker runtime retains the existing `lame` dependency. The new AI endpoint accepts WAV only; its earlier M4A implementation was never released in an app. Human DM uploads are unchanged. Recordings are limited to 60 seconds (about 1.9 MB); up to one second of recorder-stop latency is accepted and trimmed. Permit outbound HTTPS/WebSocket access to Google's Generative Language API. The existing proxy must allow authenticated WebSockets at `/ws/chat/ai` for at least nine minutes.

Configuration:

- Existing `Gemini__ApiKeys__0` (and other configured keys) must have Live API access/quota.
- Optional new `BanteraAi__LiveModel` sets the default (`gemini-3.8-live`). The admin choice at `/dashboard/bantera-ai` is persisted in existing `app_settings`, overriding this default for new sessions.
- Scheduled iPhone calls reuse existing `Apns__KeyId`, `Apns__TeamId`, `Apns__BundleId` and `Apns__PrivateKeyPem`. No new secret is introduced. Missing APNs configuration or a registered VoIP token prevents schedule confirmation.
- The website uses the existing backend base-URL environment variables. No new website configuration is needed.

## Behaviour and data boundaries

Bantera AI is an always-online local DM entry. Voice messages and audio calls use the selected Gemini Live model, learning language and accent. Every request carries the device timezone and offset; the backend supplies authoritative current UTC/local time. Live speech updates its time context. Accent fidelity remains model-dependent.

The nine-minute timer starts when the Live call is ready. At 8:30 the old microphone/generation path is stopped, queued speech is cleared, and a separate short Live farewell is generated. The app ends after playback drains, with a server/client nine-minute hard cutoff if generation or playback stalls. Calls stay within chat, with audio bubbles and input/output transcripts. Translation is exclusively Apple's on-device translation on iOS, with language-pack download/retry as needed; there is no server translation fallback.

AI chat audio, transcripts and conversation context are stored per account on the device, excluded from OS backup for this new history directory. Clearing history stops active AI work and removes these records and audio. Context is bounded: up to 80 turns (early details plus recent turns), 70,000 text characters. It is not unlimited or cross-device memory. Relevant context and learning-data snapshots/tool results pass through the backend to Gemini for inference, without being written to Bantera DB/R2. Provider processing is distinct from device storage.

Practice history and daily goals are local. Saved Media bookmarks, server-saved cues, profile/account details and synced word totals already use the server; local-only cues/media remain local. The AI is instructed to explain the distinction, not falsely claim every Bantera record disappears on reinstall. Logging into the same account alone does not restore device-only records. Clearing AI history does not clear the learning library or cancel scheduled callbacks.

Read-only learning tools access profile/language, practice progress, saved media/cues and daily-goal data with capped result sizes. Live calls request these from the device; voice-message requests provide a bounded snapshot for the same tools. No credentials or device file paths are included in tool results.

## Scheduled callbacks

An explicit callback request can schedule 10 seconds to 30 days ahead, up to ten pending callbacks per account. Relative times use server time. Ambiguous absolute times should be clarified by the AI; schedules are stored in UTC with the learner's timezone. The user can ask the AI to list or cancel callbacks.

A worker checks every two seconds and atomically claims a due schedule. It rings the requesting iPhone's registered VoIP token and publishes an in-app event. The user must answer; the app then opens Bantera AI chat and starts a new Live session with device-local context. It never opens the microphone automatically at the scheduled time. There is no Android background-push implementation in the existing app; scheduling reports unavailable without an eligible iPhone VoIP token.

Delivery is best effort. Internet access, APNs, notification settings, account status and the app's call handling must be available. Ringing expires after 45 seconds. Downtime or an unknown APNs delivery outcome results in a missed callback rather than a duplicate or late surprise ring. Acceptance is account-scoped and single-use. Concurrent AI work is limited to one operation per account per backend process; use shared coordination before scaling that limit across multiple API instances. Schedule claims themselves are safe across multiple workers.

## Verification and deployment smoke checks

Local verification includes .NET builds/tests, the additive migration on an isolated PostgreSQL database, callback idempotence/ownership, a real Gemini 3.8 Live voice-input/context/transcription test, Flutter history tests, iOS and Android debug builds, website build/lint, and an admin-form browser test against a local fixture API.

The 1.0.160 follow-up removes server transcoding. WAV validation tests cover sample preservation, native metadata/padding, invalid formats, malformed containers and duration limits. A WAV produced by Apple's native audio tooling passed the updated reader and a real Live request, including both input/output transcripts and response audio. This follow-up adds no migration or environment variable. It requires the updated, still-unreleased AI app recording path; released human-DM clients are unaffected.

After deploying:

1. Confirm `/version` reports 1.0.160, database migration succeeds and the existing published app can sign in, browse lessons, send a normal DM and make a human audio call.
2. Open `/dashboard/bantera-ai`, load the actual Live catalogue, save a model and refresh to confirm persistence. Keep a conversational Live audio model selected.
3. After installing the updated app on the user's explicit request, send two voice messages with a remembered personal fact. Verify transcript/audio replies and local iOS translations; clear history, relaunch and confirm the history is empty.
4. Make a foreground AI call: verify the AI greets first, learner accent, echo/interruption handling, transcript/audio bubbles, mute/speaker, 8:30 farewell during speech, playback-drained hangup and hard nine-minute cutoff. Check leaving chat/backgrounding and incoming human-call interruption release microphone resources.
5. Ask about practice history/saved cues/goals. Check results match the current account and storage explanations are accurate.
6. Enable call notifications, ask for a callback in one minute, background/lock the iPhone, answer and verify the AI chat opens. Repeat with decline, expired/offline notification, cancel request, logout and a different signed-in account. Confirm timezone and daylight-saving handling.

Real-device APNs/CallKit delivery, audio routing/echo, actual nine-minute timing and downloaded Apple translation languages still require the deployed device smoke checks. Builds and synthetic Live tests do not establish those hardware behaviours.

### Voice selection (1.0.161)

The admin settings API adds `voice`, `defaultVoice` and `voices`. Each catalogue
entry supplies its name, style and gender presentation from Google's documented
Live voice catalogue. PUT accepts optional `voice`; older model-only requests
preserve the saved voice. Model and voice updates commit atomically in the existing
`app_settings` table. The default is Puck. No new migration or environment variable
is required. All new replies, calls and accepted callbacks use the selected voice;
an ongoing call retains it for the farewell even if an admin changes the setting.

Deploy backend 1.0.161 before website 0.1.58. This is additive and compatible with
published app versions. Verify the admin dropdown loads, save and reload a voice,
then check a voice-message response and a fresh audio call. The website disables
saving voice settings against an older backend rather than silently ignoring them.

Voice messages now accept up to 180 seconds of mono PCM16 WAV at 16 kHz, with one second of recorder-stop tolerance trimmed to the 180-second limit. This expands the earlier 60-second limit. Deploy this backend before using three-minute recording in app 2.0.121 (309).
