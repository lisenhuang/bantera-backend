# A0 Practice

A0 is Bantera's starting-from-zero stage, informed by Pre-A1, not an official
CEFR level. The mobile UI names it “Starting from zero” and explains that
relationship in the level help sheet.

## Compatibility and storage

- Persisted A1–C2 values stay 0–5. A0 uses -1. Never prepend to the existing
  `PracticeRules.Levels` array or renumber saved dialogues/progress.
- `LevelName` and `LevelIndex` bridge the internal values and visible labels.
- `/api/me/practice/next` and `/level` accept optional `supportsA0`, default false.
  App 3.1.0 sends true. Missing capability preserves the A1 starting level,
  filters A0 exposures out of resume, and clamps response levels to A1 or higher.
- New progress created by an A0-capable request starts at A0. Existing progress
  stays at its earned level. Selecting A0 for review preserves its score window.
- Modern deferred scoring can move between A0 and A1. Legacy scoring retains its
  A1 floor. As before, 10–12 correct in the latest 12 scored exercises advances;
  0–4 moves down, within the client-supported bounds.
- A legacy device on an account currently at A0 receives A1 material without
  overwriting its A0 progress. Its above-current-level exercises do not score.
- No database schema migration, new configuration or secret is required.

## Content and learning support

The generation and independent review prompts both define A0 explicitly:
one concrete expression, short natural utterances, no assumed literacy,
no complex grammar, abstract reasoning, implied intent or subtle distractors.
Beginner topics are drawn from introductions, food, shopping, home, help and
weather. Existing audio validation, shared-pool reuse and duplicate safeguards
remain in place.

The app automatically displays on-device translations of the cue and both
choices where supported. This is normal A0 learning support, so it does not call
the optional translation-assistance endpoint or exclude a beginner result from
scoring. Explicit translation assistance for A1–C2 remains excluded. Review
exercises at any level never change the earned score. Audio replay remains
available when device translation is unavailable.

## Release and rollback

Deploy the capable API before installing/distributing app 3.1.0. Existing
published clients remain supported by the new API. Candidate/public contract checks use disposable accounts, removed even on failure.
The separate real-audio check is recorded independently: for this release, Google
TTS rejected regular keys and the already-enabled paid fallback with quota
errors. Generation remains pending; do not describe contract checks as a passed
audio end-to-end test.

After real learners have saved A0 progress, do not roll the API back to a binary
that indexes all stored levels directly through the old A1–C2 array. Preserve
A0-aware reads in any rollback build. The rolling deployment's pre-install
rollback is safe while no released client sends the new capability and the
verification accounts have been removed. Never restore an old database backup
as an application rollback.
