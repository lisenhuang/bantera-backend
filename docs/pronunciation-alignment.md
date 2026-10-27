# Pronunciation alignment contract v2

Backend 1.50.0 introduces structural alignment validation and additive message-notification sender metadata. Existing endpoints and payload fields retain their meaning. `alignmentVersion: 2` is an additive pronunciation response field. Existing status values remain `ready`, `preparing`, `unavailable`, and `unsupported`.

## Confirmed failure and repair

The old validation accepted up to 80 source characters per segment regardless of language. Chinese phrases could therefore be stored as `ready`, and non-null saved JSON was excluded from the worker queue. The old app rendered source and reading as independently wrapping blocks and trusted cached `ready` data indefinitely.

Validation now checks exact source reconstruction, grapheme boundaries, contextual-unit structure and language-specific limits. Chinese/Cantonese must have one Han character per mapped segment; Korean must have one syllable block. Word-based languages reject splits within words. Japanese rejects overlong groups and common phrase patterns; Thai rejects multiple vowel nuclei and broad groups. Numbers/time strings remain one unit and are not split by digit count. Invalid generation is not repaired by dividing romanization or assigning syllables by count.

These checks establish structural alignment, not a proof of phonetic accuracy or perfect lexical boundaries. Model generation still supplies contextual pronunciation. Japanese kana groups, Thai syllables and inferred Arabic/Hebrew vowels need language-quality review beyond these structural tests.

## Database and background operation

Migration `20261027080246_PronunciationAlignmentVersion` adds one non-null integer column with default 0 to `practice_dialogues`. It neither deletes nor rewrites practice text, audio, exposure, attempts used for level scoring, or user progress. It is safe for the previous API/app to ignore this column.

`PracticePronunciation:RepairExisting` defaults to false. With `PracticePronunciation__RepairExisting=true`, the worker scans at most 16 legacy saved mappings per cycle, restricted to ready/current-version/supported practices. Valid JSON is retained byte-for-byte and marked checked. Invalid JSON is cleared, queued, and given one new bounded repair budget. The version marker persists so process restarts do not repeatedly reset that budget.

One worker holds the existing PostgreSQL pronunciation advisory lock. Each generation attempt has a 100-second budget. At most five attempts are allowed, with increasing 15-minute backoff. Exhaustion returns `unavailable`, and API polling does not reset the budget. Requested legacy invalid mappings are also queued when bulk sweeping is off. Newly generated practice text/audio is retained when its pronunciation is invalid, and pronunciation is queued separately.

Invalid stored readings are hidden immediately by response validation, including before the sweep reaches them. Published old apps can consume valid per-unit arrays unchanged; their already-cached invalid data may persist until a fresh response. The new app validates and refreshes those caches.

## Production impact and rollback

Before deployment, a read-only aggregate audit found 188 saved pronunciation records. The repair sweep may use the existing AI providers for invalid records, so completion is asynchronous and may consume provider quota. Correct records do not invoke generation. Only pronunciation fields and the alignment marker change. No new secret is required.

The release takes a verified `pg_dump -Fc` backup before startup migrations. Pronunciation worker processing is briefly paused with the existing advisory lock across old/new API handover, preventing an old worker from writing legacy mappings into already-checked rows. Other API operations remain available. The queue resumes after the old API stops; the active API has bulk repair enabled.

Application rollback uses the retained previous containers and connector. Do not restore the database backup automatically, because doing so would lose newer writes. The additive column can remain in place.

## Verification

- Full backend suite: 1,723 passed, 29 environment-dependent tests skipped.
- Shared app/backend fixture validation and repair tests passed after the final validator refinements.
- Local isolated PostgreSQL: all migrations, including the additive column, applied successfully. Focused database/endpoint tests verified invalid data is hidden and queued, repair state survives reload, retry budgets do not reset, and practice progress remains unchanged.
- App: 160 focused tests, including pronunciation, cache refresh, whole-word selection, mic stability, callback, CallKit and audio regressions; unsigned iOS device build and simulator installation passed.
- Live APNs delivery and physical CallKit audio were not tested. Message avatar payloads are additive and preserve original alerts; only a newly signed app with the communication-notification extension can display the avatar presentation.

Deploy compatibility verdict: **GO for currently published clients**, with the additive migration and the repair costs/limits described above. No endpoint removal, authentication change or new required secret.

## Verified release

Backend 1.50.0 was deployed as `bantera-20261027-12`. The additive migration, public API version, website availability, authentication gates and post-release cleanup checks passed. The existing website image remains 0.25.0. At the first aggregate repair check, all 188 old saved mappings had been inspected, with 162 accepted readings and 26 pending regeneration/retry. The background process is intentionally allowed to continue after deployment; bounded failures remain unavailable rather than displaying incorrect mappings.
