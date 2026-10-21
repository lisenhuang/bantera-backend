# Admin-managed regular Gemini keys

Backend 1.30.0 and website 0.15.0 add `/dashboard/gemini-keys`.

Admins can add a key and remarks, edit remarks, enable/disable either a saved or environment key, and remove dashboard keys. The API never returns full keys. Duplicate keys and the separately configured paid fallback key cannot be added. A key's Google project determines billing; Bantera does not verify its tier. Multiple keys in one project share quota.

`GET/POST /api/admin/ai-settings/regular-keys` and `PUT/DELETE /api/admin/ai-settings/regular-keys/{sha256-id}` require the existing Admin policy and return `Cache-Control: no-store`. Mutations return 204. Remarks are limited to 1,000 characters and the dashboard pool to 200 keys.

Keys and environment-key metadata are encrypted in the existing `app_settings` table under `ai.gemini.regularKeys.v1`, using the existing `ChatGptConnection__EncryptionKey` (32 bytes, base64). No migration or new configuration is required when encrypted paid-key storage is already configured. Keep that encryption key stable. No plaintext keys are included in read responses or diagnostic output. Admin writes use the same PostgreSQL advisory transaction lock as paid-fallback edits, preventing lost updates across instances.

The shared Gemini key plan merges enabled saved keys with environment keys before deduplication, random ordering, paid-access filtering, model cooldowns and invalid-key exclusion. This applies to Practice and Dialogues audio, text/transcription, live calls, and admin audio tests. Search retains its existing allowed-key-prefix restriction. Existing paid fallback settings and retry rules are unchanged. The health panel includes enabled saved keys. Admin audio-test diagnostics redact the merged pool.

Changes apply when a new request builds its key plan. In-flight requests/calls can finish with already selected keys. Environment secrets cannot be deleted from the dashboard; disable them here or remove them from the environment. If an environment key later moves into the dashboard, stale metadata is replaced by its new remarks and enabled state.

## Validation

- `dotnet build BanteraApi/BanteraApi.csproj`
- Focused key-pool, key-health, paid-fallback and diagnostic-redaction tests.
- PostgreSQL integration/API test uses disposable local database `bantera_regular_keys_verify` via `BANTERA_REGULAR_KEYS_TEST_DB`. It exercises anonymous/non-admin rejection for every endpoint, encrypted persistence, masked responses, duplicate and paid-key rejection, concurrent edits, fresh-service reload, disabling and removal.
- Website typecheck, focused ESLint and production build; browser checks against a local synthetic fixture for adding, editing, disabling and reloading the dashboard.

## Release safety

GO for existing published apps: API changes are additive and there are no schema changes or new secrets. Published on Oracle AU as `bantera-20261021-3` on 21 October 2026. Backend 1.30.0 and website 0.15.0 were verified through both public domains, including authenticated dashboard rendering, masked key responses, access rejection and existing AI key-health checks. Candidate and public smoke checks passed; release status is SUCCESS. Existing mobile apps need no update.

Rolling back to the prior backend preserves the encrypted setting but ignores dashboard keys and enable/disable metadata; prior environment configuration becomes authoritative again. Restoring the new backend reads the saved pool without data conversion.
