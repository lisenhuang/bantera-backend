# Phone callback search handoff

Backend 1.30.1 distinguishes phone callbacks from real-time voice calls started
inside Bantera. The callback WebSocket path sets a server-only `PhoneCallback`
marker and disables device web search, regardless of the client's advertised
search capability. This requires no mobile update.

The system prompt tells AI to direct requests for internet/image searches or
live facts such as today's weather to Chats > Bantera AI > start a voice call
inside the app. It uses the learner's language and Practice level, avoids
guessing current facts, and leaves the phone call running. Ordinary in-app
calls and voice messages retain their existing search behavior.

Phone callbacks do not declare `search_web`. If the provider nevertheless emits
it, the relay returns `in_app_call_required` without forwarding it to the phone.
Other device tools, scheduling and audio continue normally. The resume identity
includes the call mode and a new context version, preventing old search-enabled
instructions from being reused.

Validation: backend build passed; 92 focused AI, search, audio forwarding, call
health, memory, scheduling and Practice-context tests passed, with 5 optional
database/provider tests skipped. Socket regression tests verify phone searches
are blocked while audio continues and in-app searches still reach the device.
Physical locked-screen behavior and live model phrasing were not tested.

GO for existing published apps: no API shape, database schema, configuration,
secret or service-registration changes. Published as `bantera-20261021-4` on
21 October 2026 at 09:51:24 UTC. Public API 1.30.1 and website 0.15.0 were
verified after the stability window. Candidate and public checks each passed
13 HTTP contracts; 25 policy checks against the deployed assembly confirmed
callback-only search exclusion and handoff instructions across four Live models.
These checks make no provider calls and do not prove live spoken phrasing.
