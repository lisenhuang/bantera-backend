# Backend 1.47.2 release compatibility

GO for existing published apps. Rolling `hours` and stable pagination `until` are optional additions; the existing `days` endpoints keep their calendar-day contract. Admin 24-hour access remains server-protected. Timestamped activity is owner-scoped and immutable under retries. No transcript/audio is stored in events. Historical daily counters cannot recover exact boundary hours; full interior days and the current UTC day supplement new events. All spoken languages are combined independently of the profile learning language. Version 1.47.2 fixes missing current-day daily counters in rolling windows without double-counting timestamped events. The first partial historical day still cannot be reconstructed.

Migrations `20261027021928_SpeakingEventTimestamps` and `20261027023425_MaoriPracticeEnglish` add a new table, indexes and a nullable column. Back up before applying. Application rollback preserves these additions and all newer writes. No new environment variables, secrets or DI services are required.

Māori Practice remains admin-only. `listeningLevel` creates review-mode exercises without awarding earned-level passes. English meanings are generated/stored only for Māori and map to the saved reply order. The owned translation endpoint queues background work and returns promptly. Other languages retain device translation. Existing Māori exercises are backfilled on demand.

Before installation: stage with migrations, verify public backend version, existing lesson/auth routes, rolling leaderboard authorization and both public domains. Preserve the preceding release for rollback. Local tests cover timestamp cutoffs, retries, legacy deduplication, admin restrictions, C2 listening selection and automatic English meanings. Physical-device testing remains user-controlled.


Version 1.47.2 adds optional `localDay` to the existing speaking-activity snapshot.
`20261027034039_SpeakingLocalDayWindows` creates one additive table with per-user,
per-device, per-language cumulative counts and interval bounds. No new secrets
or configuration. Old payloads remain accepted and calendar-day queries retain
their behavior. Back up before migration. New app sync is needed to populate the
bounds; do not claim that deployment alone restores local-day totals.

Only wholly contained intervals contribute. Use MAX between overlapping per-device
counts, then sum across devices/languages. Timestamped events outside the interval
are added to its candidate; events inside are already included. UTC-day fallback
remains. This is a conservative historical lower bound, not a reconstruction of
unknown first-partial-day timestamps. Repeated uploads take maximum counts and
widen bounds so older retries cannot count extra or narrow the covered interval.


Version 1.47.3 also uses existing local calendar totals for every participant,
including devices that already have UTC snapshots but no new local-day bounds.
For unknown timezones, UTC +/-14 hours conservatively bounds each local date;
already-saved counts end no later than the current refresh. Only fully contained
possible intervals contribute, via the same per-device/language MAX candidates.
This restores old-client activity without an app update or fabricated individual
timestamps. Unknown partial-date activity is still a lower bound, not an exact
historical reconstruction. No additional migration or configuration changes.
Production-shape tests cover three participants, UTC-before-midnight speech,
all three periods, duplicate sources and expiration of uncertain date bounds.
