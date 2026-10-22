# Thirty-day leaderboard release, 22 October 2026

Backend **1.33.0** is live as `bantera-20261022-1`, from the reviewed working-tree
archive. No Git commit or push is implied. `GET /api/speaking-leaderboard?days=30`
includes today and the previous 29 UTC dates, rather than the calendar month.
Omitted `days` and `days=7` preserve the published seven-day contract. Other values
return generic HTTP 400. **GO for existing published app versions:** no migration,
new required environment variables, secrets or DI dependencies.

Backend build and 1,093 tests passed; 16 optional integration tests were skipped.
Isolated PostgreSQL tests verified 30-day boundaries and legacy totals. Candidate
and live checks independently compared both windows with stored activity, checked
pagination, auth, existing admin rankings and chat receipt routes. All 132 public
handover samples succeeded. Exact API version, website marker, home page and public
lesson listing passed again after cleanup.

The website remains 0.17.1 / `bantera-website-20261021-9`, including its original
seven-day preview and backend connection. Preserve this dependency in future
releases. The retained 1.32.2 API and previous connector provide rollback:
`python3 /home/ubuntu/releases/bantera-20261022-1/deploy.py rollback`.
Rollback preserves newer database writes. Protected snapshots and the verified
backup remain server-only. Cleanup preserved current and previous releases,
volumes and database backups; disk is 43% used with 112 GiB free.

App **3.5.0 (429)** requests 30 days, orders the celebration as particles,
percentage rise, then rocket, and removes the inline translation explanation
while preserving its confirmation modal. All 17 locales use updated ranking copy.
121 focused Flutter tests passed, including callback/CallKit, word accounting,
Duo resizing, Goal replay and celebration cancellation. Analysis, unsigned debug
build, signed release build and deep signature verification passed. Both app and
widget report 3.5.0 (429), iOS 15.0 minimum and iOS 27.1 SDK. Installation and version
were confirmed on the user's iPhone without launching. Physical-device behavior,
including locked-screen callback audio, remains unverified and is left to the user.
