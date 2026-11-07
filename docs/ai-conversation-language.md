# Conversation language and accent overrides

Backend 1.56.1 deployed on 7 November 2026 after separate authorization. Source changes remain uncommitted and unpushed.

The profile learning language and accent remain the default. An explicit learner
request can override them in typed chat, voice-message replies and live calls.
The shared speech policy distinguishes an ongoing switch, an accent-only request,
a one-off translation/explanation and a request to return to the learning language.
It does not infer a switch from the learner's accent or the language of quoted text.

Previously the normal-chat identity prompt mandated the profile accent throughout
and overrode all history. Only phone callbacks allowed explicit switches. Voice
completion, reconnection and farewell instructions also repeated the profile
language. They now defer to the active conversation choice. Session-context
version 13 prevents reuse of a cached model session with the older instructions.

Saved profile language, Practice progress, input transcription and callback
lifecycle are unchanged. V1/V2/V3 tool contracts remain unchanged. No new model,
additional inference request, database migration or environment setting is needed.
Safe to deploy with existing published apps; no app update is required.

Verification: 223 focused AI, callback, voice, session, vocabulary and prompt tests
passed; 8 external-provider/database tests skipped. Backend build passed. These
checks verify prompt assembly and existing behavior, not acoustic accent fidelity
or real-provider compliance. Requested language preference is carried by available
conversation context/model session, not a new persistent profile setting. A fresh
request without that context (for example, a separately generated scheduled
reminder or fallback farewell) uses the profile default. Device-authored V3
vocabulary-review templates remain English; changing those requires app work.

Production release `bantera-20261107-2` serves 1.56.1. All 128 public availability checks passed; the unchanged website retained version 0.28.0. No physical-device behavior or provider accent fidelity was tested.
