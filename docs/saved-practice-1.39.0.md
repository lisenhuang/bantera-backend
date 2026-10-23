# Saved practice review: API 1.39.0

Adds authenticated, consent-gated POST /api/me/practice/{id}/review and
POST /api/me/practice/{id}/review/answer. The ID must belong to the caller.
Presentation refreshes signed audio URLs and reports review mode; judging uses
the stored choice order and existing answer matcher. Review operations read the
original exposure without changing attempts, progression, translation flags or
active practice. This respects the existing unique user/dialogue constraint and
allows repeat review after the original attempt budget is exhausted.

Missing/foreign IDs return 404, retired/unready content returns 410, and answer
input retains the existing size/validation limits. Original endpoints unchanged.

GO for existing published apps: additive routes, no database migration, new
configuration, secrets or service registration. Existing storage/database
services are reused. Candidate/public startup and auth checks remain required.

An isolated local PostgreSQL HTTP test verified account ownership, consent,
fresh signed URLs, original choice order, review answer correctness, original
practice restoration and unchanged progression/attempts. It uses synthetic test
users and removes them afterward; no production data is changed by that test.

Published as bantera-20261023-3, API container bantera-api-20261023-3 on
loopback 18155, connector readiness 12081. Candidate and live checks each passed
17 HTTP contracts, 20 admin ranking checks and the speaking-ranking checks.
All 132 monitored public responses returned 200. Database backup verified:
6,775,934 bytes. Cleanup completed, preserving current/previous releases and
backups; disk free returned from 112 GB during staging to 113 GB afterward.
The public API version and unchanged website 0.18.0 were independently checked.

Backend build passed. Full suite: 1,119 passed, 21 optional skipped, one existing
subsecond streaming-deadline test failed under host load and passed when rerun
alone. The separate local PostgreSQL review HTTP test passed. No production
user data was changed by release smoke tests.

Rollback without discarding database writes:
python3 /home/ubuntu/releases/bantera-20261023-3/deploy.py rollback
