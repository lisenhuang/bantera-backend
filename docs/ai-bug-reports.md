# Consented AI bug reports

API 1.36.0 and website 0.18.0 add user-confirmed Bantera issue reporting.
The existing app 3.8.1 (435) needs no new wire protocol or client-side tool.

## Conversation flow

The AI summarises the issue, discloses that the account identity is included,
asks whether to send it, and waits for a clear affirmative answer. The server
accepts `submit_bug_report` only with `userConfirmed: true`, a nonempty
confirmation excerpt, a valid category and bounded summary/details. Interpreting
natural-language consent is the AI's responsibility; the server validates the
explicit tool fields, not the meaning of the user's audio. Complaints, silence,
old confirmations and quoted examples are explicitly not consent.

The server derives user ID from authentication, the source from the handling
endpoint, and time from its UTC clock. Source labels distinguish voice messages,
text chat, in-app live calls and CallKit callbacks. Both live WebSocket paths and
the legacy reply route dispatch this tool on the server; old apps never need to
handle an unfamiliar tool frame. The resumption context version is 11.

Reports contain only the agreed issue, category, confirmation excerpt, account
ID and submission time. They do not retain call audio or full chat history.
Identical user/content submissions on a UTC date are deduplicated, including
concurrent inserts. Different wording is a separate report. There is a limit of
20 new reports per user per rolling 24 hours. Success contains a persisted report
ID and timestamp. The AI must never promise review, a reply, a fix or delivery to
another company. Failures cannot return a successful receipt.

Admins can view newest-first, paginated reports at `/dashboard/bug-reports`.
`GET /api/admin/bug-reports` requires the Admin policy and uses private/no-store
caching. Details are rendered as escaped text. User links lead to the existing
account page. This release adds viewing, not issue triage/status management.
The earlier unsupported verbal promise did not create a report retroactively.

## Compatibility and release

GO for existing published clients: existing contracts are unchanged; one additive
migration creates `ai_bug_reports` with a user foreign key, content limits and
unique deduplication index. No new secret/configuration is required. Rollback can
keep this additive table without discarding new reports or restoring old data.
The capability overview, first-use introduction and explicit CallKit language
switching from 1.35.0 remain included.

Local checks: backend build; 1,112 tests passed, 16 optional provider tests skipped,
including isolated PostgreSQL confirmation gating, persistence, trusted metadata,
retries, account separation, failure and pagination. Website production build,
type checking and 16 tests passed; lint has 11 existing warnings and no errors.
27 app callback/audio regression tests passed. Browser preview with synthetic
reports verified details and pagination. No real user reports were created for
testing, and no physical-device call was made.

Release artifacts: `/home/ubuntu/releases/bantera-20261022-4` on Oracle AU.
Check `status`, smoke proof files and the current-release record for final
publication status. Source remains uncommitted unless separately requested.

## Verified publication

Release `bantera-20261022-4` completed successfully on 22 October 2026. API
1.36.0 and website 0.18.0 are public. All 132 monitored availability responses
were HTTP 200. Candidate and public checks verified admin-only report access,
pagination, rendered dashboard, public photo origins and an existing image.
Post-cleanup HTTP checks also passed. The verified database backup was 6,550,316
bytes. Cleanup retained current/rollback containers and their shared API dependency;
111 GiB remained available. No test reports were written to production.

The signed app 3.8.1 (435) was installed on the user's iPhone without launching
or testing it. These additions are server-driven and do not require an app code
change. Physical CallKit conversation/consent behaviour remains unverified.

Rollback: `python3 /home/ubuntu/releases/bantera-20261022-4/deploy.py rollback`.
No database restore is part of application rollback.
