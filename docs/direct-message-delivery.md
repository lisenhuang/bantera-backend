# Verified human DM delivery

Backend 1.18.0 and app 2.15.0 (359) introduce immediate server cleanup after a verified recipient receipt. This applies to human direct-message audio, not groups, AI voice replies, or scheduled AI reminders.

## Receipt boundary

1. The backend records the original upload's SHA-256 and byte count. Existing undelivered objects receive this metadata lazily on download.
2. The app checks the received bytes against the original metadata, writes through its existing temporary-file/flush/rename path, and commits the local message reference with SQLite `synchronous=FULL`.
3. The app confirms the committed reference and reads the saved file back to verify its byte count and SHA-256. Only then does it send `POST /api/v3/chat/messages/{id}/received` with `{sha256, byteCount}`. A corrupt or incomplete copy never receives a verified acknowledgement.
4. The backend checks recipient membership, original hash and byte count, then commits an immediate expiry marker. It attempts R2 deletion before deleting the message row; receipt rows cascade with the message. Missing-message acknowledgements are idempotent.
5. An R2 failure leaves the database message and expiry marker for retry. A database failure after R2 deletion leaves the already committed marker; repeating the idempotent R2 deletion permits cleanup to finish. The background cleanup worker checks every ten seconds. Outages can delay physical deletion beyond that interval.

The expiry event preserves saved local chat history. It is distinct from explicit message deletion. The thread's last activity timestamp remains the actual conversation activity time.

## Synchronization and compatibility

The app's DM background sync uses `GET /api/v3/chat/threads/{id}/messages` and an opaque cursor containing the oldest returned creation timestamp and message ID. It does not use shifting offsets while receipt processing deletes earlier pages. Legacy listing and receipt endpoints remain available. An old server can still serve the new app through the legacy listing fallback.

Old released apps send unverified legacy receipts and retain the previous seven-day policy. They must upgrade to use immediate verified cleanup. Nullable integrity fields and the additive `20261016022900_AddChatAudioIntegrity` migration are compatible with old clients and backend rollback. No new environment settings are required.

Undelivered DMs still have no automatic expiry. After successful verified delivery, another device or fresh installation cannot retrieve the server copy. Existing infrastructure database backups have their own retention policy; this change deletes the live database message and R2 object, not historical backup snapshots.

## Verification

`ChatDeliveryTests` uses a dedicated local PostgreSQL database (`BANTERA_DM_TEST_DB`, restricted to localhost and the `bantera_dm_delivery_verify_` prefix) and fake object storage. It covers original upload hashes, invalid/sender/outsider receipts, R2 failure, SQL failure after R2 deletion, retries, legacy receipts, old-object metadata, groups, undelivered messages, and 205 equal-timestamp messages deleted across pages. No real users or R2 objects are used in these tests.

App `chat_delivery_test.dart` covers byte/file integrity, the committed-file receipt boundary, HTTP headers/receipt bodies, and cursor/legacy request behavior. Existing local retention tests cover history preservation and account isolation. HTTP deployment checks verify authentication, service resolution, migration state, receipt validation and missing-message idempotency; they do not establish physical-device delivery behavior.
