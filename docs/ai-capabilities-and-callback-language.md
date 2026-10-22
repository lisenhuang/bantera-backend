# AI feature introduction and callback language choice

Backend 1.35.0 adds a numbered capability overview to the first spoken response across voice messages and calls. Returning learners can request it with “What can you do?” or an equivalent question in their language. Questions about one feature receive a focused answer. The overview uses the learner's level and current reply language.

The available-feature list covers conversation and word explanations, vocabulary management, review and quizzes, internet search, photos, videos, scheduled callbacks, and scheduled voice-message reminders. Device vocabulary/search flags and the app's media capability note prevent advertising unsupported tools to older clients. Phone callbacks direct search requests to the app. First-meeting state still uses the existing persistent flag and model-history fallback; reconnection does not replay the introduction.

A greeting, feature question, or example does not authorize a tool action. The prompt distinguishes a once-per-day in-chat vocabulary review suggestion from a requested timed voice reminder. It does not promise recurring background reminders, which the current scheduler does not support. Callback and reminder confirmations continue to require successful tool results.

CallKit callbacks start in the profile language, then honor a current explicit request such as “Speak English” or “Please speak Chinese.” The requested language applies to the remainder of that call, including reminders and farewells, until another explicit change. A single translated phrase does not necessarily switch the call. Old history, quoted material and accent alone do not change it. Saved profile settings remain unchanged. Ordinary in-app language/accent behavior and the CallKit search restriction are preserved.

Session context version advances from 9 to 10 so cached provider sessions cannot reuse the older instruction set after the change.

## Verification and compatibility

**GO for existing published apps.** These are prompt-only changes with no new endpoints, payload fields, database migrations, environment variables, secrets, or service registrations. No app or website build is needed. Existing audio, reminder scheduling, auth, key routing and device-tool contracts remain intact.

Backend build passed. Full suite: **1,106 passed, 16 optional tests skipped**, including isolated PostgreSQL leaderboard checks. New tests cover both first-meeting modes, returning users/history fallback, optional device capabilities, callback-only language switching, profile preservation, search handoff and resumed-session setup. Production AI wording and physical locked-screen callback audio/language switching were not exercised; code and contract tests do not establish those physical behaviors.

Release verification is recorded separately in the deployment artifacts for `bantera-20261022-3`.
