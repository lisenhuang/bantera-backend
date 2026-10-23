# Voice-message overlap 1.37.0

GO for existing published apps. The authenticated voice WebSocket accepts optional
`?overlap=1` for new clients. It allows at most three concurrent voice messages per
account. Calls and clients without that parameter retain the existing exclusive
session behavior. Lease release is idempotent and concurrent admission is locked;
per-account entries are removed after the last lease ends.

No database migration, new setting, secret or service registration is required.
1111 backend tests passed, with 19 optional integration/provider tests skipped.
Tests cover bounded simultaneous voice admission, release order, duplicate
release, account isolation, and mutual exclusion with calls/legacy operations.
Existing Practice shuffle tests also pass; Q2 remains independently 50/50.

The app change is 3.10.0 (438). Each voice reply has independent state, and the
chat controller outlives route removal while replies arrive. Provider/network
latency still applies; sending no longer deliberately waits for a prior answer.

Published 23 October 2026 as `bantera-20261023-1`, status SUCCESS. API container
`bantera-api-20261023-1` uses loopback 18153 and connector
`cloudflared-20261023-1` readiness 12079. Website 0.18.0 remains in its existing
`bantera-website-20261022-4` container. Public versions were independently checked
after retention cleanup. All 132 monitored public responses were HTTP 200.
Candidate/live API, ranking and dashboard smoke checks passed. Database backup
verified at 6,765,931 bytes; approximately 113 GB free after cleanup. Existing API
and connector release 20261022-4 remain available for rollback, with dependencies
of the unchanged website protected. Rollback preserves current database writes:

```
python3 /home/ubuntu/releases/bantera-20261023-1/deploy.py rollback
```

HTTP health checks do not establish uninterrupted pre-existing WebSockets or real
locked-screen callback audio. Physical iPhone acceptance remains with the user.
