# Practice rotation and preparation

Practice selection excludes the requesting user's most recent N exercise presentations
in the same canonical language and level. N defaults to 50 and admins can change it
from 10 to 500 at `/dashboard/practice`. The setting is persisted in `app_settings`
as `practice.repeatWindow` with the administrator and update time. No environment
variable is required. Selection reads it on each new exercise; refill jobs take a
snapshot when they start. Invalid/missing stored values fall back to 50.

The eligible pool remains scoped to the requested regional locale and level.
Unseen exercises are preferred, then older eligible exercises are mixed randomly.
An active exercise, an idempotent request retry, or an explicitly opened Saved
Practice is not a new selection and is allowed to resume. Small pools wait for
preparation instead of violating the exclusion window. The existing daily limit
still applies. No user history is cleared.

Each replay receives a new exposure ID and separate attempts. Existing saved
exposure IDs continue to open their original exercises. The migration replaces
the unique user/dialogue index with a non-unique lookup index. User/request and
language/fingerprint uniqueness remain enforced. Published app request and
response contracts are unchanged. Roll back application containers if necessary;
do not roll back the database index once repeat exposures exist, and never delete
history to restore uniqueness.

Replenishment targets 20 eligible, audio-ready exercises. It reuses older audio
before generating more. Generation uses recent, locale/level-specific short
situation descriptions (3–8 English words, capped at 96 characters per description,
at most 100 descriptions) rather than full old exercises. Existing meaning keys
are reused without a paid backfill. These are coverage hints, not a ban on a broad
topic. Local similarity checks and the independent quality review remain, scoped
to recent material; globally exact normalized cue/reply fingerprints still prevent
duplicate stored content and audio generation. A0 allows useful simple-pattern
variations. New descriptions are returned by the same generation request.

Increasing N can require more generation and initial waiting, especially for
small language/level pools. Preparation remains bounded to six batches, two
consecutive empty batches, a ten-minute job deadline and the existing failure
backoff. Provider quota/availability can still delay preparation.

Verification includes configurable 30/50/100 windows, user/language/level isolation,
admin-only reads/writes, value validation, immediate setting reads, replay attempts,
request idempotency, saved-history preservation and exhaustion without early repeats.
