# Live Markdown display tool: 1.38.0

GO for existing published apps. `deviceFormattedText` is an optional, default-off
client capability. Only supporting clients receive the `display_formatted_text`
tool declaration and guidance. Voice and live-call tool transports return a
matching device acknowledgement. Legacy HTTP replies explicitly disable it.
Changing the capability invalidates incompatible provider resume checkpoints.

No database migration, new environment variable, secret, or service registration
is required. Existing native-audio modalities and published API contracts remain
unchanged. Formatting is display data, not executable code or a web search.

The app uses a separate persisted Markdown field; the original spoken transcript
continues to drive audio resume checkpoints and listened-word counts. New app
version: 3.11.0 (441), shared by iPhone and Android.

Validation: backend build passed; 1120 regression tests passed with 19 optional
tests skipped before adding the new opt-in provider smoke test. The real
gemini-3.8-live check passed: one display tool call returned 771 Markdown
characters including bold terms and a comparison table, plus 524160 audio bytes
and a separate 201-character spoken transcript. Tool acknowledgement was
simulated; no user account, reminder, callback or other external write was used.
App validation passed 67 focused tests including persistence after navigation,
streamed voice replies, Markdown layouts and mandatory CallKit/callback checks.

Physical locked-screen audio acceptance remains with the user.

## Published release

Published 23 October 2026 as `bantera-20261023-2`, status SUCCESS. API 1.38.0
runs in `bantera-api-20261023-2` on loopback 18154 through
`cloudflared-20261023-2` (readiness 12080). Website 0.18.0 remains in
`bantera-website-20261022-4`. All 132 monitored public responses returned 200;
candidate and live HTTP, ranking and speaking checks passed. Both public version
markers were independently verified after cleanup.

Database backup verified at 6,773,598 bytes. Disk availability was 112.0 GiB
before and 112.2 GiB after cleanup. Current and previous release dependencies,
database volumes and backups were retained. Rollback preserves database writes:

```
python3 /home/ubuntu/releases/bantera-20261023-2/deploy.py rollback
```

Final full regression run: 1120 passed, 20 optional tests skipped. The new opt-in
real Google test passed separately. Signed iOS 3.11.0 (441) installed and verified
through device inventory; no physical launch or test. Android debug build passed.
No Git commit/push or store submission was performed for this release.
