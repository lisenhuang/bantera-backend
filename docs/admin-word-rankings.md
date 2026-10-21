# Admin word activity rankings

The dashboard Overview ranks existing users by synced listened or spoken words.
No mobile update, database migration or new configuration is required.

`GET /api/admin/word-rankings` requires the existing Admin policy and returns
`Cache-Control: private, no-store`. Filters:

- `period`: `today` (default), `week`, or `total`.
- `metric`: `listened` (default) or `spoken`.
- `limit`: 1–100, default 20. `offset`: 0–1,000,000, default 0.

The response includes the reporting date, inclusive date range, update time,
page offset/size, `hasMore`, and users with both counts and a rank. Invalid
period/metric values return 400. Anonymous requests return 401; non-admin
requests return 403.

Today uses the Pacific/Auckland reference date. Activity buckets retain the
device-local calendar dates uploaded by existing apps; they are not UTC event
timestamps. This week runs from Monday through the reporting date. All time
includes all recorded dates, matching the app's total. The dashboard labels
these semantics and notes that offline activity appears after syncing.

The query combines legacy `user_word_activity` and language-tagged
`user_language_word_activity`, summing across devices and languages. Existing
upload endpoints already deduplicate cumulative snapshot retries. Deleted and
system accounts are excluded. Only users with a positive selected metric appear.
SQL RANK assigns equal counts equal ranks before pagination; user ID provides
stable ordering within ties. Existing app endpoints and count semantics remain
unchanged.

Validation: AdminWordRankingTests covers Auckland midnight/DST and Monday week
boundaries. Its PostgreSQL fixture covers combined legacy/language/device totals,
ties across pages, period boundaries, zero counts, excluded accounts, empty pages
and invalid filters. Set BANTERA_RANKING_TEST_DB to an isolated PostgreSQL database
named bantera_word_rankings_verify; fixtures use connection-local temporary tables.

Production smoke checks compare all six ranking combinations with independent
read-only database aggregates, verify admin access and rendered dashboard content,
and keep account data out of proof output.
