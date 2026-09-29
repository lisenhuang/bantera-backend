# Cue and language grouping release

Backend **1.0.149**, app **2.0.72+260**, website **0.1.48**.

## Behavior

- New transcription-derived cues combine tiny fragments with the next phrase when all spoken words have the same known speaker. A short tail can join the preceding phrase. Different or unknown speaker tags are never merged. Punctuation annotations do not override spoken-word speaker identity.
- A cue is considered short below 3 spaced words, 4 Chinese/Japanese matching units, or 700 ms. A combination must stay within 18 spaced words or 36 Chinese/Japanese matching units and 8 seconds. These are preferences, not reasons to insert punctuation or split an unpunctuated sentence. Speaker boundaries take precedence over length and punctuation preferences.
- Validated script short cues use the same text-length preference within a single speaker line. The prompt discourages isolated reactions and greetings. Full-width Chinese commas are now accepted as punctuation boundaries.
- Existing stored cue arrays are unchanged. The reported lesson `fa600f9a-e37d-4b41-b09e-22c77cb859f0` has a 0.4-second eighteenth cue, `哇，`, but its saved cue/line data does not retain speakers. Safely repairing that saved lesson needs fresh speaker information; the code does not guess or run a new transcription job for it.
- App and web highlighting use the returned `wordTiming` units directly. A returned multi-character word highlights together; separate character records remain separate. Optional per-character `parts` no longer subdivide the displayed highlight. No dictionaries, extra segmentation, extra AI calls, or transcript rewriting are added. Existing script-aligned recordings retain their stored timing-word boundaries.
- Web browsing and the app's My Audios filter combine accents by language. Cantonese (`zh-HK` and `yue-*`) is one group with 🇭🇰. Multi-country languages show 🌐. Mainland Chinese and Taiwan Chinese remain distinct 🇨🇳/🇹🇼 groups. Accent selectors for profile and generation are unchanged.
- The public list endpoint adds optional `languageGroup` (`yue`, `zh-cn`, `zh-tw`, or a primary language code such as `en`). Group filtering occurs before sorting and pagination. Existing `languageCode`, level, visibility, and search behavior remains available to released clients.

## Deployment decision

**GO for the backend with the currently published app.** The new query parameter is optional. There are no migrations, new environment variables, secrets, dependencies, or service registrations. Existing stored media is not modified. Builds and automated checks do not prove generated speech quality or physical-device behavior.

A human must deploy the backend first, then the website. An older backend ignores the new grouping parameter, so do not release the updated website before backend 1.0.149. Build/install a signed app 2.0.72+260 to see the app changes; the local no-codesign build is not a signed distribution build. No clean uninstall is needed.

## Smoke checks

1. Confirm the backend starts normally and existing exact-accent public list requests still work.
2. Request `/api/videos/public?languageGroup=yue&mediaType=audio&limit=50&offset=0`: results may include both Hong Kong and mainland Cantonese, but no Mandarin or Taiwan Chinese. Check Mainland and Taiwan group queries separately, and `languageGroup=en` across accents.
3. On `/webapp`, confirm one Cantonese choice with 🇭🇰, English/French/etc. with 🌐, and separate Mainland/Taiwan Chinese. Existing links using `languageCode=zh-HK` or `en-NZ` should select the matching group. Check My Audios uses combined counts and filters all accents in the selected group.
4. Generate a new Cantonese lesson containing a reaction followed by another phrase from the same speaker. Confirm a tiny reaction joins that phrase, punctuation and words remain unchanged, and a different speaker's reply stays separate. Listen to check the speaker labels.
5. Use returned multi-character and single-character timing records to confirm highlights follow their original boundaries and word seeking still works. Compare a spaced language as well. No extra segmentation should run.

## Local verification

- Backend build: 0 errors, 10 existing warnings. Full suite: 458 passed, 1 local PostgreSQL analytics integration test skipped.
- App: 40 focused grouping, subtitle-token, subtitle-panel, and Discover-filter tests passed against the latest main branch. The iOS debug build without codesigning passed for 2.0.72+260. New grouping/helper files passed analysis; the practice player retains existing Radio API deprecation notices.
- Website: 4 grouping/highlighting tests and changed-file ESLint passed; the production build passed.
