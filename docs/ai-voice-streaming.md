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
