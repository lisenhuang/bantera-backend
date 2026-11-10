# Investigating AI voice message latency

The app opens `/ws/chat/ai/voice` when recording starts and streams PCM as it
arrives. The server opens Gemini Live once it detects speech and forwards that
PCM without waiting for the user's Send. Send commits the turn and lets the
model answer. The app can replay saved PCM if its initial socket fails before
commit; Gemini can also retry an attempt on the server.

Backend 1.56.4 adds `progress.timing` to existing `ai_voice_server` completion
and failure events. All timestamps are elapsed milliseconds since the server
accepted the app's socket. No audio, transcript, tool arguments, credentials,
or new client fields are recorded.

- `firstClientPcmMs` to `lastClientPcmMs`: time over which the app's recording
  chunks arrived. A burst alone cannot distinguish a replay from a late socket
  connection that flushed buffered chunks.
- `latestAttempt.pcmBytesSentBeforeCommit`: positive means the latest Gemini
  attempt was fed audio while the app was still recording or preparing Send.
  Check `providerAttempts` and `latestAttempt.inputMode` for a retry; explicit
  text and transcript recovery send no PCM.
- `latestAttempt.startMs` to `latestAttempt.readyMs`: connection and provider
  setup. `clientCommitMs` to `latestAttempt.inputEndMs`: remaining provider
  setup, retry, or input delivery after the app's commit.
- `latestAttempt.inputEndToFirstAudioMs`: wait from final input delivery to the
  first reply audio packet available for relay, including any earlier tool work.
  `toolMs` and `toolRounds` cover completed rounds for the entire latest
  attempt; `unfinishedToolMs` shows an in-flight round when a failure is logged.
- `commitToFirstAudioMs`: server receipt of Send to the first audio packet
  sent back to the app, across all attempts. This excludes local saving before
  commit and device playback after the packet arrives. `DurationMs` includes
  recording and full reply generation; it is not time to first audio.

After deployment, send one short voice message in the published app. Match the
completion event by its time and request ID, confirm that `progress.timing` is
present, and compare its commit-to-first-audio timing with the time heard on
the phone. The wire protocol and database schema are unchanged.
