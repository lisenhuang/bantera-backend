# AI voice streaming (backend 1.0.162)

`/ws/chat/ai/voice` is an additive authenticated WebSocket route. The initial JSON
`start` contains a UUID requestId, recent local history, a device-data snapshot,
and metadata with device timezone. The server responds `ready`. Binary frames
contain mono 16-bit little-endian PCM at 16 kHz, bounded to 180 seconds in total and
32,000 bytes per frame. The server forwards them to Gemini during recording.

The client sends `commit` with fresh metadata when the learner sends the message.
Only then is Gemini's manual `activityEnd` sent. Tools with side effects and output
playback wait until commit. Disconnect/cancel stops generation without persisting
recording/history. After commit, binary 24 kHz PCM reply frames are streamed to the
client. `complete` contains both inputText and outputText. No translation runs on
the server. A generic `error` terminates an unsuccessful message.

The per-account operation lease also applies to recording sessions. Startup is
bounded to 20 seconds, recording to 190 wall-clock seconds, and reply to 100 seconds.
Input audio is held in bounded memory so a provider retry can replay the same
message; no database/R2 recording or transcript write is added.

## Quota versus expiry

- HTTP/JSON 429, structured RESOURCE_EXHAUSTED, or an explicit quota-exceeded
  provider close cools down that key for the selected model and tries the next
  unique eligible key. This applies during setup, recording and reply generation.
- `goAway`, structured DEADLINE_EXCEEDED, and the explicit provider deadline-expired
  close text produce AiLiveSessionExpiredException. A voice message retries a fresh
  session on the same key with its original context and buffered audio, at most
  twice. Expiry does not cool down keys. A bare 1011 or arbitrary ABORTED error is
  not enough evidence to classify an expiry.
- `reset` asks a streaming client to discard/clear partial audio before a retried
  response. Callback request IDs remain stable and repeated identical tool results
  are cached within the operation.
- The live-call route sends `reconnect` with reason `session_expired`. New apps
  restore recent local transcript context, preserve their original nine-minute
  countdown, and reconnect with the remaining duration. Initial calls greet first;
  reconnects do not repeat the introduction. The final 30-second farewell remains.
- General provider errors, cancellation and invalid requests do not rotate keys.
  Retries never reveal keys, provider exception text or user content in logs.

Google documents **around ten minutes per connection**, plus an audio-only
**15-minute session limit without compression**. Context compression is enabled.
Our voice-message connections are fresh per recording (up to three minutes), and
calls are capped at nine minutes. We use fresh sessions seeded with client-supplied
context for recovery, rather than persisting provider resumption handles.

References:
- https://ai.google.dev/gemini-api/docs/live-api/session-management
- https://ai.google.dev/gemini-api/docs/live-api/capabilities
- https://ai.google.dev/api/live

## Release safety and verification

**GO for existing published clients:** the existing HTTP reply and call routes,
authentication, and response shapes remain compatible. The HTTP reply path shares
the new quota/expiry recovery. No new migrations, schema changes or environment
variables. Existing configured Gemini keys, model and voice are used.

Verification: backend build; 45 focused passing tests plus two opt-in checks skipped
in the normal run. The separately enabled real Gemini streaming test passed with
both transcripts and prior-context recall: first audio 2.1 seconds after commit,
full generation 3.6 seconds for one synthetic sample. These numbers are not a
production latency guarantee. Public checks must confirm `/version` = 1.0.162,
`/ws/chat/ai/voice` rejects unauthenticated requests, and both Cloudflare domains
remain healthy after deployment. Retain the prior API and connector for rollback.

Deployed to Oracle AU as `bantera-20261007-2` on 7 October 2026 NZDT. All 128
monitored API/website HTTP checks passed. The previous API/connector are retained
for rollback; see `oracle-au-deployment.md` for exact paths and commands.

## Callback spoken-confirmation fix (1.0.163)

Production logs showed a successful callback insert immediately before a voice
stream `InvalidDataException`. A synthetic “call me back in one minute” recording
reproduced `AI returned no audio` with the real Live model. The tool-call turn
can end before the separate spoken confirmation arrives. The voice reply reader
now keeps the same connection open across that boundary, without scheduling
again, and bounds tool rounds to 16. This applies to streaming and legacy HTTP
voice replies. Safe failure categories improve diagnostics without recording
provider URLs, keys, or conversation content.

All 50 focused backend checks passed with the isolated database and two real
Gemini samples enabled. The callback sample produced its first audio 1.5 seconds
after Send and completed in 3.5 seconds; this is one sample, not a latency SLA.
Deterministic tests include tool completion both with and without spoken preamble.

Production deployment verdict: **GO** for existing published apps. No API shape,
authentication, schema, migration, or environment-variable changes. Runtime
verification requires API version 1.0.163, unauthenticated voice routes rejected,
and both public domains healthy. Retain the previous connector for rollback.


## First-meeting metadata (1.0.164)

All three AI transports accept optional boolean `metadata.hasMetBanteraAi`.
It is an account-scoped local app preference passed for inference, with no new
server persistence. A prior model turn in supplied history also identifies a
returning learner, covering old clients and stale/absent flags. The shared Live
system prompt introduces Bantera AI only on the first conversation across voice
messages and calls. Returning voice-message replies answer directly; each fresh
call is explicitly prompted to speak first with a warm, varied greeting.
Reconnections and the final farewell never restart the introduction.

Name priority is the latest explicitly supplied personal/preferred name in the
available conversation, then the profile name. Do not infer names from email
addresses or claim memories absent from supplied context. The shared prompt
emphasises friendly, level-appropriate speaking and listening practice.

Deployment compatibility: GO for existing published app clients. The metadata
field is optional and existing requests/responses stay valid. No migration, new
configuration, secret or DI dependency. Deploy the backend and update the app to
get durable first-meeting behaviour after chat history is cleared. Runtime smoke
check: first voice reply introduces once, then a new call greets first without
another introduction; subsequent voice replies answer directly. Prompt/unit
tests establish the rules and state flow, not a guarantee of model wording.


## Callback reminder topic (1.0.165)

The schedule_callback tool now requires a learner-provided reminder (1–500 trimmed
characters). For a time-only request the model must ask what the call should remind
them about and wait; it must not assume language practice. The service returns
needsReminder before any database access/write if the note is absent or invalid.
Times are still interpreted with the learner's timezone and trusted server clock.

The short note is scheduling metadata stored in ai_callbacks.Reminder, separate
from device-local chat history and audio. It follows the existing seven-day cleanup.
It is not placed in APNs payloads or lock-screen notification text. The app supplies
callbackId when answering; the server reads a reminder only for that authenticated
user and an answered callback. A fresh callback greeting states the reminder,
while connection renewal does not repeat it. Old schedules without notes retain
the normal greeting. Privacy/help copy in all 17 app locales explains the storage.

Deployment compatibility: GO with the additive AddAiCallbackReminder migration.
The Reminder column is nullable, so existing rows and old app clients remain valid.
Apply the migration before serving the new backend (normal startup migration also
handles it). No new environment variable or secret is required. Rollback to older
code can leave this optional column in place; do not drop it and lose reminders.
A new app build is needed to identify the exact callback reminder. Verify a time-only
request asks a follow-up, a time+topic creates one schedule, and answering the
callback says its reminder. Local PostgreSQL tests cover persistence, idempotency,
missing-topic rejection and owner/status isolation. Real model wording and push
arrival still require a live smoke check after release.


### Reply recovery (7 October, backend 1.0.166 / iOS 2.0.131+319)

A production voice stream reached its request deadline without completing a
reply. Direct checks of all 14 configured keys returned audio, so this did not
establish a general quota outage. Keep timeout, quota and session expiry distinct.

- A voice reply gets a 20-second audio-progress deadline after Send, never while
  recording. A stalled reply renews once with the same key and bounded recording
  and context; partial output is reset and callback tool results are memoized.
- Explicit quota failures rotate eligible keys. Active-call quota failures now
  cool down the exact connected key and send `reconnect: quota_exceeded`; session
  expiry still sends `session_expired`. A silent opening sends `response_timeout`.
  The updated app handles all three with bounded renewal and its existing timer.
- Clock notes during audio use `realtimeInput.text`. Do not send `clientContent`
  on each input transcription: Google documents that it interrupts generation.
  Initial history and the explicit call greeting still use `clientContent`.
- Stream failure logs include safe reason, commit state, byte counts and elapsed
  time, never transcript contents, keys, push tokens or raw provider exceptions.

Reference: [Google Live WebSocket message semantics](https://ai.google.dev/api/live#bidigeneratecontentclientcontent).

## Progressive voice-message bubbles (1.0.167 / app 2.0.132+320)

New apps send `streamTranscripts: true` in the initial `start` frame. After commit,
the backend forwards incremental `{type: "transcript", role: "user" | "model",
text: "..."}` frames alongside the existing binary PCM audio. Text is a delta to
append; `complete.inputText` and `complete.outputText` remain authoritative full
transcripts. Gemini's transcription events are independent of audio chunks and
must not be assumed to align word-for-word with playback.

The first model audio or transcript creates a provisional AI bubble and replaces
Sending with replying. Transcript and received-audio duration grow as data arrives.
`reset` clears partial captions and audio while keeping the same provisional bubble;
successful completion saves that bubble once. Failed or cancelled partial replies
are not written to history. Translation remains an on-device, manual action after
the reply completes.

Deployment verdict: **GO for existing published apps**. Transcript frames require
an explicit boolean opt-in because older clients reject unknown frame types.
Without opt-in, the existing wire format is unchanged. No new migration, environment
variable, secret or service registration is required by this change. Deploy the
backend and update the app for progressive captions; the new app can still show
the early audio bubble against the previous backend. Verify `/version` is 1.0.167,
both public domains remain healthy, and a short voice message displays its AI
bubble before completion. Retain the previous release for rollback.

Verification: 577 backend tests passed (six opt-in checks skipped in the normal
run), and the separately enabled real-model transcript check passed. In that
synthetic sample, first audio arrived 2,460 ms after Send, first transcript at
2,459 ms, and completion at 3,776 ms. Concatenated streamed transcripts matched
the final response. These timings are a sample, not a production latency promise.

## Voice reminders and explicit calls (1.0.168 / app 2.0.133+321)

Reminder-only requests use `schedule_reminder`, standard APNs alerts, and a voice
message in the AI conversation. Only explicit requests for a call use
`schedule_callback`, which requires `explicitCallRequested: true`. Time-only
requests still require a reminder topic. New client metadata carries a separate
`alertPushToken`; missing support/notification permission returns an unavailable
result and must never cause the model to substitute a call.

The additive `AddAiVoiceReminders` migration adds delivery kind, temporary audio,
transcript/language, and generation attempt fields to `ai_callbacks`. Existing
rows default to calls. Message reminders use `queued`, never `scheduled`, so old
callback workers retained during a rolling deploy cannot ring for them. A separate
worker claims generation atomically, retries up to three times, and checks for
cancellation before publishing a ready message. Normal message notifications carry
no reminder text. Receipt/cancellation clears audio and transcript; seven-day
schedule cleanup provides the expiry. App privacy and Usage copy explain this
temporary delivery storage in all 17 languages.

Authenticated `/api/chat/ai/reminders` lists the owner's schedules; per-ID audio,
receipt and cancellation routes enforce the same owner. The app saves audio and
text locally before acknowledging receipt and uses stable IDs for retry deduplication.
The Reminders menu lists upcoming items first with voice-message/call labels,
local date/time, status and confirmation before cancellation.

**Deployment GO for existing published apps**, after backing up and applying the
additive migration. No new env vars, secrets or DI services external to the existing
Gemini/APNs configuration. Old clients retain their wire formats and cannot request
message delivery without the new alert-token metadata. Keep the previous release
for application rollback; do not drop the new columns or restore the old database.
Public smoke checks must confirm version 1.0.168, both domains, auth rejection for
all new routes, and healthy reminder-worker startup with the migrated database.

## Incomplete reply diagnostics (1.0.169 / iOS 2.0.134+322)

Voice-stream terminal failures now persist to the existing `ai_pipeline_events`
table, alongside the console warning. HTTP voice fallback and real-time call
failures also record an event. The voice-stream request UUID correlates these
events with best-effort app reports sent to authenticated
`POST /api/chat/ai/diagnostics`. This endpoint accepts bounded technical fields,
has a 4 KiB request limit and a 20-per-10-minute rate limit. The authenticated
account supplies UserId; the client cannot select another account.

Use the admin-only `GET /api/admin/ai-pipeline/events?stage=ai_voice_server`
or `stage=ai_voice_client`, then match `detailJson.requestId`. Existing 90-day
cleanup applies. Diagnostics writes have a three-second deadline and cannot
turn a chat or generation into a failure. Client reports are best effort: an
offline phone, closed app, or old backend may prevent delivery.

Recorded information includes category, operation phase, model/voice on the
server, app version on the client, audio byte counts, elapsed time, first-audio
time, reset count, transcript character counts, committed state, numeric
provider/socket status, native error code where safe, and server method names.
Never include chat text/audio, tool arguments, reminders, prompts, learning
snapshots, raw exception messages, provider URLs, credentials or push tokens.
Quota, session expiry, stalled response, request deadline, client disconnect,
playback failure and local-save failure have distinct categories.

The app freezes a failed stream before saving its partial reply locally. The
same bubble remains, marked **Reply interrupted**, and can replay the received
audio. Partial model replies are excluded from future AI context and cannot be
submitted as a user's Retry. A playback-drain failure after a complete saved
reply does not incorrectly mark the user's message as unsent. Cleanup errors
cannot prevent the composer from leaving its busy state. A failure does not
automatically resend a committed voice message or repeat reminder side effects.

**Deployment GO for published apps:** wire formats remain compatible; the
diagnostics endpoint is additive. This patch needs no migration or new environment
variables. Before release, verify existing database availability, DI startup,
unauthenticated diagnostics rejection, and a correlated metadata-only test event.
These diagnostics cannot reconstruct a failure from before their deployment.
## Live-call pauses and incidental noise (backend 1.1.2)

Live-call clock updates use `clientContent` with `turnComplete: false`, never
`realtimeInput.text`. A microphone amplitude spike may come from chewing or
background noise; its clock update must not independently request a model reply.
Timezone and trusted server time remain in that context, and scheduling tools
still use current server time. Explicitly committed voice messages retain their
manual activity start/end protocol.

Calls use low start/end speech sensitivity with 300 ms prefix padding and a
700 ms silence window. The live-call prompt asks the model to ignore chewing,
crunching, breathing and other non-speech sounds, and to wait after each response.
The opening greeting and timed farewell remain explicit exceptions. This reduces
false turns; it is not a guarantee that every noise will be classified correctly.

The app keeps the display awake for the call's lifetime and releases the wake
lock on hang-up, startup failure or controller disposal. Manual phone locking and
background call support remain available.

Reference: [Google Live API capabilities](https://ai.google.dev/gemini-api/docs/live-api/capabilities).
Regression checks cover the non-triggering clock wire format, call VAD setup,
and preservation of the voice-message configuration. Device acceptance should
include a greeting followed by silence/crunching, then quiet speech and spoken
interruption, plus auto-lock restoration after ending the call.

Model compatibility: VAD settings and incomplete client-content clock updates use
the shared Live protocol. No affective-dialogue or proactivity flags are sent,
because supported values differ by model. Extended Thinking requires an explicit
thinking level and uses `low`; other models omit this setting. Legacy models retain
their implicit blocking tools; Gemini 3.8 Live explicitly uses blocking tools,
and 3.8 Live Extended Thinking uses non-blocking tools. Extended Thinking replies
wait for `interactionStatus` / `interaction_status` to become `IDLE`, including
standalone status frames, rather than ending on an intermediate `turnComplete`.
Other models adopt status-based completion if the server emits these signals.
Unknown future models use the baseline setup; availability and future protocol
changes cannot be guaranteed by a model name alone. The admin catalogue still
comes from the provider's live `bidiGenerateContent` model list.

Deployment GO for existing clients: these changes affect only the provider
adapter and preserve the app WebSocket contract. No migration, new environment
variables or website update is required.

### Current language, accent and Discover level

Each new call/voice-message connection reads the current learning language from the
server profile. The shared system prompt uses the complete regional locale and
explicitly prioritises it over older conversation languages. Cantonese selections
receive an explicit colloquial Cantonese instruction, distinct from Mandarin.
Both session types use speaking/listening coaching guidance, with short turns and
no extra prompts during silence. Accent quality still depends on the provider.

New clients include optional `metadata.learningLevel` using the same device-local
Discover preference: `beginner`, `intermediate`, `advanced`, or null for All levels.
The server allowlists those values and supplies vocabulary, pacing and exercise
guidance for the selected level. Missing/unknown values adapt to demonstrated
ability, preserving older-client compatibility. No DB migration or new config is
required; changing the level takes effect on the next call/voice-message session.
The device learning-profile tool also exposes that preference with its local
storage scope.

Speaking counts remain on-device transcript estimates. The full utterance must
confidently match the learning language; short low-confidence fragments do not
veto it. Confident foreign fragments or mixed-language tags in the full context
reject the whole utterance. This avoids zero credit caused by ambiguous greetings
or three-word windows, without counting only the target-language parts of detected
mixed speech. Retries retain the same speech event ID to prevent duplicate credit.

## Device web search and silent-response recovery (1.4.0)

The optional `metadata.deviceWebSearch` capability registers `search_web` only
for compatible WebSocket clients. Search network requests run in the app;
`AiWebSearchTool` and `AiVoiceDeviceTools` only declare/relay bounded tool data.
Upload-only HTTP clients never advertise it. No new DB schema, key or environment
variable is needed. Search content is not written to technical diagnostics.

A provider completion without reply PCM no longer ends a voice message while a
replacement turn may still follow. The existing post-Send timeout bounds this
wait. If the first attempt is silent but produced a user transcription, its one
fresh-session recovery commits that transcription to the **same selected Live
model** with prior context. It does not switch models or re-upload PCM in that
case. Tool mutations retain the operation's existing memoization/idempotency.
Timeouts after reply audio begins are not replayed. Quota and session-expiry
handling remain distinct.

Failure diagnostics now include counts of audio-free completions, provider
interruptions, PCM parts and other inline parts. No recordings, transcripts,
search queries/results or raw provider error messages are persisted by these
counters. Synthetic checks establish protocol behavior, not a guarantee that the
provider can never stall.
