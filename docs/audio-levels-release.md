# Audio levels release

Backend: **1.0.143**. Flutter app: **2.0.64+252**.

Discover initially shows All levels. Beginner, Intermediate, and Advanced use the same saved device preference as Generate with AI. Selecting All levels clears the generation choice, which displays Select level and disables generation until a specific level is selected. The choice persists across app restarts. Both menus use matching native bar icons and localized labels.

Existing AI audio is returned and filtered as Intermediate. Uploaded content without a level is still visible under All levels. New AI audio stores its chosen level; dialogue complexity and speech delivery instructions use that level. Beginner uses a smaller script-length target for gentler speech. No existing recordings are regenerated.

## Deployment decision

**GO for backend deployment with the currently published app.** Changes are additive: generation requests that omit `level` still work and default to Intermediate, public-list requests without `level` retain all levels, and old clients can ignore the new response field. No new environment variables, secrets, or service registrations are required.

Migration `20260929013417_AddAudioLevel` adds nullable `user_videos.Level` (`varchar(16)`). Existing rows stay unchanged and receive the Intermediate fallback when `IsAiGenerated` is true. The API's existing startup migration process applies it. No manual backfill is required. Keep the column if rolling the server binary back.

## Human release steps

1. Commit/publish the backend changes through your normal release process, then update the server checkout or image to version 1.0.143. These changes have not been committed or pushed by the agent.
2. Deploy the backend first, preserving the server's existing secrets and environment. For a server using the repository's source-build Compose configuration, run `docker compose build api` and then `docker compose up -d api` from that server checkout. If your server uses a prebuilt image, publish/pull the updated image through your normal image release process instead.
3. Check startup logs for a successful database initialization and migration. Confirm a public-list request returns successfully before installing the new app.
4. Install/update the app to 2.0.64 (build 252). Open `app/ios/Runner.xcworkspace`, select the phone and signing team, then Run, or distribute a signed build through your usual process. A clean uninstall is unnecessary. The local no-codesign build is compiled but is not a signed installable device release.

The checked-in `deploy/deploy.sh` pulls a GHCR image while the checked-in Compose file currently uses `build: .`; ensure your server's actual release path rebuilds or selects the updated image. Do not rely on pulling an image alone to update a source-built service.

No website changes or website deployment are needed.

## Smoke checks after deployment

- With the published old app, list existing content and generate an audio without a level; generation should remain supported.
- On the new app, start with All levels, choose Beginner in Discover, and confirm Generate with AI also shows Beginner. Change to Advanced there and confirm Discover updates.
- Select All levels in Discover. Generate with AI should show Select level, offer only the three concrete levels, and keep Generate disabled until a level is chosen.
- Restart the app and verify the last choice remains. Search and scroll in each level; Intermediate should include existing AI audio.
- Generate and listen to one lesson at each level, including one custom scenario. Confirm the saved lesson has the selected level and is discoverable in that filter. Compare vocabulary and actual pace; provider-generated speech quality still needs listening validation.

## Local validation

- Backend build passed; backend suite: 346 passed, 1 unrelated local analytics integration test skipped.
- Generated migration SQL applied successfully to temporary local PostgreSQL tables. Legacy filtering, visibility, and pagination checks passed there; no production migration was run.
- 16 focused Flutter tests passed, covering shared selectors, persistence, All-level reset, long translated labels, API parameters, legacy response parsing, generation, and existing stream recovery.
- Dart analysis of changed application/test files: no issues.
- `flutter build ios --debug --no-codesign` passed; Generated.xcconfig reports 2.0.64 / 252.
- Existing backend warnings remain, including the Microsoft.OpenApi 2.4.1 advisory and existing nullable/raw-SQL warnings. Dependency upgrades are outside this change.
- No live deployment, physical-device installation, or paid AI generation was performed.
