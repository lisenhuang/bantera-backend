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
The server must expose native ICU for the broader spellout coverage. No service
deployment, Git push, or iPhone installation is part of this code change.
