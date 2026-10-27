# Private Māori model test service

The website uses `/api/dashboard/model-tests/*`, which forwards the existing administrator's bearer token to `/api/admin/model-tests/*`. Every backend route requires the Admin policy and a current active, non-deleted admin account in PostgreSQL. Only the backend can join the private inference network; the worker also requires a service key and backend-supplied owner UUID. Jobs, status, cancellation and assessments are owner-scoped. The website rejects cross-origin writes.

The worker has one durable SQLite queue and one worker thread. An exclusive process lock prevents accidentally running two workers against the same state. Queued/running jobs are failed on restart; they are not silently replayed. Inference runs in a new process group with a deadline and is killed/reaped on cancellation or shutdown. Docker `--init` reaps descendants. Quotas are checked atomically. SQLite secure deletion is enabled; results/assessments expire after 24 hours. Temporary audio is removed on every terminal path and at startup. The source audio is deleted immediately after conversion, before inference.

## Limits and measurements

- Two CPU cores, 8 GiB memory including file cache, no swap; 128 PIDs. One inference job globally.
- Audio: at most 8 MiB and 0.2–60 decoded seconds. FFmpeg is restricted to file/pipe protocols and bounded to 61 decoded seconds; inputs exceeding 60 seconds are rejected. Conversion deadline 30 seconds, one FFmpeg thread.
- Translation: 1–1,000 characters, at most 256 input and 128 output tokens. Target `<2en>` is supplied by the service. Greedy decoding; input control tokens rejected.
- Two pending jobs/admin, six globally; 20 jobs/hour/admin and 60 globally. Inference deadline 900 seconds. Output cap is reported explicitly.
- GNU time measures per-process peak RSS and CPU seconds. Wall time includes cold model loading and inference, excludes queueing/conversion. Average CPU uses 100% per core. Cgroup memory is total container memory including file cache; its per-job maximum is sampled every 100 ms and is not an isolated job allocation or a guaranteed instantaneous maximum.
- WER uses Unicode NFC, lowercase, punctuation removal with apostrophes retained, and preserves macrons. It is Levenshtein word edits/reference word count, can exceed 100%, and is not a pronunciation assessment.

## Build and installation

Follow the main Oracle AU runbook and take the deployment/build lock. Recheck capacity first. Use `build-runtime.sh` inside a disposable `rust:1.90-bookworm` ARM64 container capped at two CPUs/4 GiB; its `/work` mount is this directory. Keep the generated Cargo.lock. Download via `download-models.py` in a separate 0.5 CPU/512 MiB Python container with `/models` mounted to the dedicated model directory. Exact revisions, sizes and SHA-256 values are recorded in the manifest.

`Dockerfile` expects the two built executables under `bin/`. Retain all notices. Build a uniquely tagged runtime image and use `start-service.py` as the reviewed initial Oracle installation definition. It refuses to overwrite an existing named service. The worker is non-root, read-only except its state directory/tmpfs, with all capabilities dropped, no new privileges and no public ports. The internal Docker network has no external gateway.

Set only on the backend, before staging:

- `LocalModelTests__BaseUrl=http://bantera-model-service:8090`
- `LocalModelTests__ServiceKey` from the server-only 0600 `service.env` file.

Do not attach the website, tunnel or unrelated containers to `bantera-model-private`. Do not print service keys or production runtime snapshots. The API uses a 20-second gateway deadline; inference is independent and polled by job ID. The website tells administrators to check history if submission acknowledgement is lost.

Run `test_service.py` in an isolated Linux test container with FastAPI, httpx, FFmpeg and GNU time. Tests use temporary SQLite/model fixtures and never production model files or account data. Backend tests: `dotnet test --filter FullyQualifiedName~LocalModelTestTests`.

Application rollback restores the previous backend/website connector without restoring databases. Since previous releases have no model-testing routes, the private model service may remain idle or be stopped separately; never remove the model/state directories as routine build cleanup. Pin and retain the verified runtime image and one working rollback image when upgrading it.

## Generated round-trip tests (service 1.1.0 / API 1.49.0)

Admins may request 5–60 Māori words and choose either multilingual Whisper model.
The API's hosted worker uses the same shared text settings, fallbacks, provider
adapters, key policy and Māori Practice TTS method as the app. It validates the
word count (up to three text attempts), records configured and actual models,
and hands generated audio to the private queue. Whisper explicitly uses `mi`;
MADLAD translates its transcript, not the original reference. AI-generated
references and synthetic audio are not human ground truth.

SQLite has additive kind/stage/generation/claim/lease columns. Only the backend
can use private `/generation/*` handoffs; public gateways expose none of them.
Claims bind the owner and a random token, renew every two seconds, expire after
60 seconds without a heartbeat and have a ten-minute total generation deadline.
Current admin status is checked during generation. Cancellation invalidates the
claim and cancels provider requests; late responses cannot restart a job. One
provider-generation job and one local inference may run concurrently; local
models remain serial under the existing 2 CPU / 8 GiB limit.

Successful generated audio is retained in `/data/audio` for the job's 24-hour
retention period, served only through authenticated owner-checked endpoints.
Failure/cancellation/restart and retention expiry remove audio. Uploaded and
normalised inference audio remain temporary and are deleted at every terminal
state. Partial Whisper results survive a later translation failure. Model
resource measurements cover local inference, not the external text/TTS provider.

Tests: `python -m unittest test_generation` in an isolated Linux container with
httpx 0.28.1. This includes inherited baseline tests and generated-workflow tests.
To rebuild from scratch use the main Dockerfile; deployment 1.1.0 reuses the
verified 1.0.1 binaries/libraries and changes only the Python service files.
On application rollback, keep service 1.1.0: manual API requests stay compatible.
Never run two service containers against the same SQLite directory. Retain a
SQLite backup before upgrading, but do not restore it over newer jobs blindly.
