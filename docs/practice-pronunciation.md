# Practice pronunciation guides

API 1.40.1 adds optional aligned pronunciation to quick Practice. Supported
languages are Mandarin, Cantonese, Japanese, Korean, Thai, Hindi, Arabic,
Hebrew, Russian, Ukrainian and Greek, including the existing regional variants.

`POST /api/me/practice/{exposureId}/pronunciation` uses the existing account,
exposure ownership and Practice-consent checks. It returns `status`, `cue`
segments and two `choices` segment arrays in the same shuffled order as the
exercise. Each segment has the exact original `text` plus Latin `reading`.
The ordinary next/saved-review response includes the same optional
`pronunciation` object. Old clients can ignore it. Readings do not modify
answers, translations, review mode, progress or audio.

New exercise generation requests pronunciation in the same text-generation
response. Supported-language drafts without valid readings are rejected before
reservation/audio generation. The shared database retains validated readings.
Concatenation must exactly reproduce the source sentences, grapheme boundaries
must remain intact, and readings must use Latin letters with bounded sizes.
This checks structure and alignment, not linguistic perfection.

Turning on Pronunciation requests the current old practice. The endpoint saves
PronunciationRequestedAt and immediately wakes the worker. Only explicitly
requested old practices are eligible; there is no automatic bulk backfill. One PostgreSQL advisory lock bounds work across
replicas. It processes one exercise at a time with a 100-second deadline and
15/30/45/60/75-minute failure cooldowns. After five failures it stops automatic
retries for that row; a later authenticated request can restart it after the
cooldown. Requests are stored in the database, so restarts do not lose pending work.
It uses the existing configured text models, key health and paid-routing policy;
no new credentials or environment variables are required.

Migration 20261025091150_PracticePronunciation adds nullable PronunciationJson
and PronunciationRetryAt, PronunciationAttempts defaulting to zero, and a partial
index on pending ready rows. It is additive and compatible with the published
app and previous backend. Take a verified database backup before migration.
Rollback the application/connector only; retain the columns and user writes.

The app's optional toggle is saved per account and exact learning locale. Reads
poll every five seconds only while enabled and the Practice page is active.
Late responses are discarded after switching account/exercise. Missing/error
states keep the original text and controls usable. Word taps preserve sentence
offsets and the existing select-then-confirm interaction.

Validation: 1,137 backend tests passed before the final endpoint cooldown
addition, with 21 optional skips; the 19 focused tests including the isolated
PostgreSQL ownership/consent, persistence, swapped choices and unchanged-score
checks passed after that addition and the legacy Cantonese locale correction. Website checks, tests, lint and build passed.

Migration 20261025092857_OnDemandPracticePronunciation adds nullable
PronunciationRequestedAt. New practice generation still includes readings;
old practices are generated only when requested. Already saved readings remain.

Production deployment verified on 2026-10-25: Oracle AU release
`bantera-20261025-2`, public API `1.40.1`, website `0.19.0` (unchanged website
image stamped `bantera-20261025-1`). The staged and public HTTP/auth checks,
admin word-rankings checks and speaking aggregate proof passed. Verified backups
preceded additive migrations. The previous API container is stopped so its
superseded bulk scan cannot continue. The previous API/web/connector remain
available for rollback. Post-release cleanup preserved database volumes and
backups; disk free increased to 113 GiB, and public versions were rechecked.

## Matching correction: API 1.40.2

Japanese explicit clock numerals normalize through the existing numeric path,
including 三時/3時/fullwidth digits. Hour/minute/second associations and 午前/午後
are protected; ordinary kanji names are not converted. App/backend fixtures
are byte-identical. The new app submits all attempts to this authoritative
matcher, removing the earlier local rejection that blocked server fixes.
Full backend tests: 1,151 passed, 21 optional skips. No additional schema,
configuration or endpoint change; safe for the published app (deploy GO).
