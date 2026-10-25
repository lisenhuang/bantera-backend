# Practice level completion (API 1.41.0 / app 3.17.0)

Each language starts at A0 for clients that support A0. Existing progress is retained. Legacy clients without A0 support keep their A1 compatibility path.

`PracticeProgress.HighestPassedLevel` stores the highest completed level, using existing persisted indices (-1 = A0, 0 = A1, ... 5 = C2), and -2 for none. Ten correct results in the latest twelve scored exercises completes the current level, including C2. Existing first-answer, translation exclusion, deferred scoring, review exclusion, and idempotency rules remain intact.

The existing adaptive level can recommend an easier level. This no longer erases earned passes or relocks earned access. Learners can choose their highest unlocked level again; doing so starts a fresh scoring window at that level. The selected practice level and completion state are separate concepts.

The ready/preparing and level-selection responses add `levelStates` (A0–C2 mapped to `locked`, `practicing`, or `passed`) and `highestUnlockedLevel`. Saved review responses include the same metadata. Old clients ignore these additive fields. New clients tolerate older API responses, but permanent completion and C2 completion require this backend release.

## Release handoff

GO for backward-compatible deployment after the standard staging smoke checks. Migration `20261025103754_PracticeLevelCompletion` adds one integer column with a default; no endpoint or existing field is removed. It backfills levels below the existing current level as passed. Existing C2 only becomes passed when its stored 12-answer window already contains at least 10 successes. Earlier historical achievements lost before this release cannot be reconstructed reliably. No new environment variables, secrets, or dependencies are required.

Use the Oracle AU deployment runbook and take the normal database backup first. Keep the previous release for rollback; the old backend can operate with the extra column present. Do not reverse the migration during application rollback.

Smoke checks: fresh language has A0 practicing and all others locked; A0 completes with 10/12; C2 stays practicing at 9/12 and passes at 10/12; fresh retrieval preserves completion; translated/review results do not complete levels; locked selection returns 409; old-client responses and authentication still work. Verify both public domains and the API version after promotion.

Local verification includes progression boundary tests, migration/backfill and legacy-write checks on temporary PostgreSQL tables, app level-picker tests, and accessibility/layout snapshots. Production and physical-device behavior are not established by these local checks.

## Verified release, 25 October 2026

Backend 1.41.0 is live as `bantera-20261025-4` on Oracle AU. API container
`bantera-api-20261025-4` uses loopback 18159; the unchanged website 0.19.0
image runs in `bantera-website-20261025-4` on 13048; connector
`cloudflared-20261025-4` uses readiness port 12086.

The first promotion automatically rolled back after the public fixture probe
received HTTP 403 with Python's default user agent. Repeating the probe with the
same explicit verification user agent used by the existing smoke suite passed.
The second promotion completed with status SUCCESS, including the stability
window and post-release retention. Independent public checks confirmed API
1.41.0 and website 0.19.0. All 184 recorded availability samples returned 200.

A verified 8,110,852-byte PostgreSQL backup preceded the additive migration.
Candidate and public checks each passed 19 HTTP contracts, 16 level-state
presentation/lock checks, 20 admin ranking checks, and speaking leaderboard
checks. Temporary accounts were removed; no real user's progression was edited.
The level-state probe verifies persisted state presentation, while the local
progression tests establish the 10-of-12 completion boundary and exclusions.

Full local suites passed: 910 app tests; 1,163 backend tests with 21 optional
integration tests skipped. The signed iOS app 3.17.0 (457) was installed and its
inventory verified without launching or testing on the phone. Signed Android
3.17.0 (457) was built with the established upload certificate.

Cleanup retained this release and `bantera-20261025-3` as rollback; 112.6 GiB
remained free. Databases, volumes and backups were preserved. Rollback keeps the
additive migration and current writes:

```sh
python3 /home/ubuntu/releases/bantera-20261025-4/deploy.py rollback
```
