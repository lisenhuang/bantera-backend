# Practice numeric answer matching: 1.42.1

The existing grading matcher remains authoritative. Before comparing replies,
it now normalizes spoken numbers using ICU's locale-specific spellout rules.
Coverage includes all 61 product locales (33 languages), with regional forms
such as Belgian French and Indian English. This fixes cases such as Japanese
二十歳 versus 20歳 without an AI grading request or a transcription rewrite.

Normalization preserves the ordered values and their signs, currencies, units,
counter suffixes, clock components and explicit AM/PM periods. It recognizes
localized decimal/group separators and digits, common prices, percentages,
explicit clock expressions, numeric dates and 24-hour clock notation. Distinct
values and unit assignments remain different. Ambiguous shorthand, uncertain
speech, missing AM/PM and unrecognized linguistic forms are not guessed.

ICU parsing is bounded by input length and exact numeric range, uses a bounded
locale cache, and serializes access to each native formatter. The Linux runtime
already provides ICU as its .NET globalization dependency. If native parsing is
unavailable, the existing deterministic normalization remains usable; no new
environment variable or credential is needed. Input boundaries preserve names
and ordinary words that contain number-like syllables or characters.

The existing answer and saved-review endpoints add optional `textEquivalent`.
It is true only when the matched reply and transcript normalize exactly alike.
App 3.18.1 (459) uses this flag to suppress formatting-only mismatch highlights;
the original recognized text and low-confidence word highlights remain visible.
Old clients ignore the new field, and new clients retain their previous display
comparison when an old backend omits it. The value is display-only and cannot
change a stored attempt's outcome or choice.

Validation: 1,697 backend tests passed, with 21 optional integration tests
skipped. All 946 app tests passed, including callback/CallKit regressions.
App analysis has no errors or warnings (44 existing informational notices),
and the debug iOS build passed. A separate read-only, network-disabled Linux
container passed 621 checks using the final source, including the managed
native-ICU resolver, shared fixtures, percentages and clock notation against
the same ICU 74 family as production.

Deployment compatibility: **GO**, backward-compatible for the published app.
No endpoint removals, schema migration, new configuration or secret changes.
After deployment, smoke-test an authenticated Practice answer using numeric
spelling and verify the expected outcome, choice and `textEquivalent` field.
The server must expose native ICU for the broader spellout coverage.

## Verified deployment and installation: 26 October 2026

Release **bantera-20261026-2** completed at **11:34:34 NZDT** (25 October,
22:34:34 UTC). API **1.42.1**, source commit
`5122f79c605193d2e4ba3167e3b548beb90ec5a2`, runs in
`bantera-api-20261026-2` on loopback 18161. Its native ICU 74 library was
confirmed in the deployed runtime. The website remains code/image version
**0.20.0**, with its unchanged `bantera-20261026-1` public release marker.
Its runtime was cloned as `bantera-website-20261026-2` on loopback 13050 only
to point its internal API URL to the new backend. Connector
`cloudflared-20261026-2` uses readiness port 12088.

Candidate and public verification each passed **19 HTTP contract checks** and
**342 authenticated numeric matching cases**, including all supported languages,
Japanese ages, time notation, prices and changed-value safeguards. Each run
confirmed **168 exact text equivalences**. Verification used disposable owned
saved reviews at an impossible pool level; all synthetic records were removed.
No real user transcripts, progression or messages were changed, and no AI
provider requests were made. Candidate startup had no error markers.

All **208/208** public availability samples returned HTTP 200 during handover
and the stability window. Both public domains and expected version markers
were independently verified again after cleanup. The readable pre-release
PostgreSQL backup is **8,258,279 bytes**. Cleanup retained the current release
and one rollback release; free disk space improved from **111 GB to 112 GB**
out of 194 GB (43% used). No volumes, databases, uploads or backups were deleted.

App **3.18.1 (459)** was signed, signature-verified and installed on the iPhone
17 Pro; the installed version/build were independently confirmed. It was not
launched or tested on the physical phone. App source commit
`454ece5636fb22f7fd97b38774035dbb90a952da` and the backend source commit are
verified on their respective `origin/main` branches.

Rollback retains current database writes and restores the previous applications
and connector:

```sh
python3 /home/ubuntu/releases/bantera-20261026-2/deploy.py rollback
```

The source archive hash, database backup, private runtime snapshots, controller,
proof files and availability log are retained in the server release directory.
