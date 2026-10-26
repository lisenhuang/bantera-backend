# AI operation lifetimes

Long audio generation, transcript correction, summaries and admin provider/search
checks execute through the `ai_operations` queue. Submission authenticates the
caller, takes an account admission lock, and saves an idempotent job before any
provider work. `Prefer: respond-async` returns 202 and a job ID immediately.
Owner-only `/api/me/ai-jobs/{id}` and `/events` expose status and saved milestones.
Published clients keep JSON responses (bounded to 105 seconds) or their existing
SSE events, with an immediate started event and 15-second heartbeats.

Two workers atomically claim queued jobs, renew a 60-second lease every ten
seconds, and have operation-specific finite budgets (3 to 11 minutes). Client
connection loss does not cancel work. Queued work survives restarts. A persisted
terminal event can be recovered after a crash. Other expired running leases are
failed as uncertain, never automatically sent to a paid provider again. Users
can explicitly start another attempt. This deliberately does not claim exactly
once execution at an external provider or resumption midway through an unknown
provider call.

Request inputs, results and progress are encrypted with AES-GCM using a
domain-separated key derived from the existing JWT secret. Inputs are removed
when work finishes. Summary jobs expire after one hour; other jobs after 24
hours. Cleanup runs in the worker. Account deletion cascades, and account status
and admin privileges are checked before execution and during the lease renewal.
JWT secret rotation renders existing encrypted payloads unreadable; affected
jobs fail safely. No new secret is needed. Conversation summaries remain
client-owned, with only temporary encrypted queue storage on the backend.

Missing audio covers and profile avatars use the same durable queue. Metadata
and job-status reads never wait for image generation. Provider model catalogues
have a five-minute cache, a ten-second refresh deadline, and at most one-day
stale fallback. The separate GPT catalogue already has a 20-second deadline.
Practice and pronunciation keep their existing background workers. Live voice
and callbacks keep their WebSocket transport and interruption behaviour; the
legacy multipart HTTP reply retains its 100-second deadline.

The Flutter summary and transcript clients use short authenticated requests,
with a stable job ID and finite polling. Audio keeps SSE plus owner-only job
recovery, including queued jobs after app reopening. Dashboard test polling runs
in the browser: server actions only submit or read status, avoiding a long
outer HTTP request to the website. A tab stores the job ID, never the prompt or
result. Closing the tab does not cancel provider work.

Validation includes a real 130-second simulated provider operation, heartbeat
receipt, disconnect recovery, same-ID duplicate POSTs, owner isolation, legacy
JSON shape, cancellation, encrypted payload isolation, and stale-lease handling
against a disposable local PostgreSQL database. The migration is additive.

## Deployment evidence: 27 October 2026

Release `bantera-20261027-2` is live with backend **1.43.0** and website
**0.22.0**. The signed iOS **3.21.0 (470)** build was installed on the paired
phone and its installed version verified; it was not launched or tested there.
Physical locked-screen callback acceptance remains unverified.

The additive migration was applied after an 8,385,879-byte PostgreSQL backup was
verified. Existing app contracts remain supported and no new secret is required.
Both staged and public checks passed for authenticated queue submission,
idempotency, owner isolation, cancellation, worker execution and legacy JSON
responses, without provider calls or retained verification accounts. All 132
public availability samples returned HTTP 200. Cleanup restored 112 GiB free
space and kept the immediately previous release for rollback.

Validation: 1,699 backend tests passed (22 optional skips); the separate three
PostgreSQL operation tests passed, including 130-second generation, heartbeats
and disconnect recovery. App callback/audio/recovery/client tests and analysis
passed; website tests (22), type checking and production build passed. A
scheduler-sensitive existing voice test initially failed during concurrent
builds; its isolated suite and the full uncontended rerun passed.

Runtime snapshots, source manifests, backup, smoke proofs and rollback controller
are retained at `/home/ubuntu/releases/bantera-20261027-2`. Rollback preserves
current database writes:

```sh
python3 /home/ubuntu/releases/bantera-20261027-2/deploy.py rollback
```

No Git commit/push or store submission was performed for this batch.
