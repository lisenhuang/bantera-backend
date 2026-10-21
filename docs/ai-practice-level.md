# Bantera AI Practice level context

Backend 1.29.0 reads the authenticated user's saved Practice progress for the profile's current learning language before opening each AI message/call session or generating a scheduled voice reminder. It uses Practice's language normalization, so English accents share progress and Cantonese remains separate from Mandarin.

The effective selected level (A0–C2, including easier review) controls vocabulary, grammar, reply length and pace. Earned level remains available to distinguish review from a demotion. A0 gets absolute-beginner guidance; all levels retain the profile's language/accent and respectful coaching. These are model instructions, not a guarantee that every generated utterance matches a measured level.

Saved Practice context overrides the older Discover beginner/intermediate/advanced preference. If no valid Practice record exists, the existing Discover guidance remains the fallback; no A0 assessment or progress row is invented. This lookup does not update scores or levels.

Context is server-only and cannot be supplied in client JSON. It is refreshed on each message/new call/reconnection, not during an uninterrupted call. The provider resume identity includes earned and effective levels, preventing reuse of stale instructions after promotion, review selection or reset.

## Deployment

No endpoint/request/response changes, migrations, environment variables, secrets or new service registrations. Existing apps benefit after backend deployment; no mobile update is required. The added read uses the existing indexed Practice progress key. Verify authenticated message/call initialization against the candidate database before promotion; actual generated speech quality still needs provider/device listening checks.

Deployed as `bantera-20261021-2` on 21 October 2026 at 01:31:27 UTC. Public API 1.29.0 and the unchanged website 0.14.0 were independently verified; all 156 availability samples returned HTTP 200. Candidate checks passed 13 HTTP contracts and 11 level-context checks against the deployed assembly and production database, using synthetic records in a rolled-back transaction. Previous API 1.28.0 and its connector remain available for rollback.

## Validation

The backend build passed. AI and Practice regression tests passed: 617 passed, 8 optional tests skipped. The local PostgreSQL test verified account isolation, accent/language normalization, missing progress, promotion refresh and read-only behavior. Prompt/setup tests cover all seven levels, selected review levels, untrusted client metadata and session identity changes. Live provider speech quality was not tested.
