# Paid fallback for generated audio

Backend 1.27.0 and website 0.13.0 add a separate Dialogues audio checkbox to
Dashboard → Bantera AI → Paid Gemini key. The existing Practice checkbox is retained.

- Both switches apply to every user, including users absent from individual access.
- Each switch controls only its own audio generation. Off means regular keys only,
  including users with individual paid-only or fallback access.
- Regular keys are tried before the paid key for each configured audio model;
  cooldown and invalid-key exclusions still apply. Content/safety rejections do not
  gain additional paid retries.
- Permission is checked again immediately before using the paid key. Disabling the
  switch or replacing/removing the key blocks subsequent paid attempts, but cannot
  cancel a provider request already sent.
- Text generation, transcription, chats and explicit admin single-attempt diagnostics
  retain individual user permissions. Text and audio model fallback settings remain
  unchanged.

## Compatibility and rollout

The optional `dialogueAudioFallback` admin response/request field is additive.
Older admin payloads omit it and preserve the saved value. Existing encrypted
settings default this new flag to false; the Practice flag is preserved.
Individual app preference saves preserve both global switches.

No database migration, new environment variable or dependency is required. The
existing encrypted paid-key storage and saved key are reused. Deploy backend first,
then website; no mobile app update is needed for these controls. Existing app API
contracts continue working. On rollout, Dialogues paid audio is intentionally off
until the admin enables it, even for previously allowlisted users.

Production smoke check: authenticate as admin, read the masked status, save and
reload both switches, and confirm that an ordinary account's next eligible audio
attempt follows the enabled switch after regular-key failure. This can incur paid
provider usage and was not performed against production during local verification.

Local validation: backend build and 1,017 passing tests (14 optional integration
skips); website tests, targeted lint and production build passed. No deployment or
production setting changes were made as part of this change.
