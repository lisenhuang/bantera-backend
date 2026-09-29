# Transcription language audit and release

Backend **1.0.146**, audited 2026-09-29 against [Google Gemini 3.5 Transcribe documentation](https://ai.google.dev/gemini-api/docs/transcribe?hl=en#supported-languages).

All 61 Bantera learning locales were checked: 34 exact supported codes, 3 explicit aliases, and 24 regional variants without a documented exact match. The provider allowlist contains all 83 unique codes in the published table. Matching ignores casing and trims whitespace.

The mapping only affects requests to speech-to-text. Public API locale identifiers, stored lesson metadata, TTS accent selection, script generation, and the script-alignment setting remain unchanged. Verbatim mode, speaker labels, and word timestamps remain enabled. No script is sent to STT.

`zh-HK` and `yue-CN` use `yue-Hant-HK`, the documented Cantonese hint; mainland Cantonese may consequently be transcribed using Traditional characters. `zh-CN` uses `cmn-Hans-CN`. For unlisted regions or scripts, including Taiwan Mandarin, use Google's documented `language_codes: []` automatic detection rather than assert a different dialect or writing system. Automatic detection does not guarantee a particular output script or perfect dialect fidelity. A provider rejection of a known language hint retains the existing automatic-detection retry, now logged explicitly.

This ensures request-code compliance, not verified transcription quality in every language. The Cantonese recording supplied by the user was tested live with `yue-Hant-HK`: it returned “你決定咗食咩未？” and timed 咗 at 19.900–20.100 seconds, with no script alignment. Other locales were audited and tested at the request boundary, without paid live transcription in every language. Existing recordings are not retranscribed.

## Complete catalog

| Lesson language | Bantera code | STT hint |
| --- | --- | --- |
| English (United States) | `en-US` | `en-US` |
| English (United Kingdom) | `en-GB` | `en-GB` |
| English (Australia) | `en-AU` | Automatic detection (`[]`) |
| English (Canada) | `en-CA` | Automatic detection (`[]`) |
| English (India) | `en-IN` | `en-IN` |
| English (New Zealand) | `en-NZ` | Automatic detection (`[]`) |
| English (Ireland) | `en-IE` | Automatic detection (`[]`) |
| English (Singapore) | `en-SG` | Automatic detection (`[]`) |
| English (South Africa) | `en-ZA` | Automatic detection (`[]`) |
| English (Philippines) | `en-PH` | Automatic detection (`[]`) |
| English (United Arab Emirates) | `en-AE` | Automatic detection (`[]`) |
| English (Indonesia) | `en-ID` | Automatic detection (`[]`) |
| English (Saudi Arabia) | `en-SA` | Automatic detection (`[]`) |
| Spanish (Mexico) | `es-MX` | Automatic detection (`[]`) |
| Spanish (Spain) | `es-ES` | Automatic detection (`[]`) |
| Spanish (Latin America) | `es-419` | `es-419` |
| Spanish (United States) | `es-US` | `es-US` |
| Spanish (Colombia) | `es-CO` | Automatic detection (`[]`) |
| Spanish (Chile) | `es-CL` | Automatic detection (`[]`) |
| French (France) | `fr-FR` | `fr-FR` |
| French (Canada) | `fr-CA` | Automatic detection (`[]`) |
| French (Belgium) | `fr-BE` | Automatic detection (`[]`) |
| French (Switzerland) | `fr-CH` | Automatic detection (`[]`) |
| German (Germany) | `de-DE` | `de-DE` |
| German (Austria) | `de-AT` | Automatic detection (`[]`) |
| German (Switzerland) | `de-CH` | Automatic detection (`[]`) |
| Italian (Italy) | `it-IT` | `it-IT` |
| Italian (Switzerland) | `it-CH` | Automatic detection (`[]`) |
| Chinese, Mandarin (China mainland) | `zh-CN` | `cmn-Hans-CN` |
| Chinese, Mandarin (Taiwan) | `zh-TW` | Automatic detection (`[]`) |
| Cantonese (Hong Kong) | `zh-HK` | `yue-Hant-HK` |
| Cantonese (China mainland) | `yue-CN` | `yue-Hant-HK` |
| Japanese (Japan) | `ja-JP` | `ja-JP` |
| Korean (South Korea) | `ko-KR` | `ko-KR` |
| Portuguese (Brazil) | `pt-BR` | `pt-BR` |
| Portuguese (Portugal) | `pt-PT` | `pt-PT` |
| Arabic (Saudi Arabia) | `ar-SA` | Automatic detection (`[]`) |
| Arabic (United Arab Emirates) | `ar-AE` | Automatic detection (`[]`) |
| Russian (Russia) | `ru-RU` | `ru-RU` |
| Hindi (India) | `hi-IN` | `hi-IN` |
| Indonesian (Indonesia) | `id-ID` | `id-ID` |
| Vietnamese (Vietnam) | `vi-VN` | `vi-VN` |
| Thai (Thailand) | `th-TH` | `th-TH` |
| Turkish (Türkiye) | `tr-TR` | `tr-TR` |
| Dutch (Netherlands) | `nl-NL` | `nl-NL` |
| Dutch (Belgium) | `nl-BE` | Automatic detection (`[]`) |
| Polish (Poland) | `pl-PL` | `pl-PL` |
| Swedish (Sweden) | `sv-SE` | `sv-SE` |
| Danish (Denmark) | `da-DK` | `da-DK` |
| Norwegian Bokmål (Norway) | `nb-NO` | `nb-NO` |
| Finnish (Finland) | `fi-FI` | `fi-FI` |
| Ukrainian (Ukraine) | `uk-UA` | `uk-UA` |
| Greek (Greece) | `el-GR` | `el-GR` |
| Czech (Czechia) | `cs-CZ` | `cs-CZ` |
| Slovak (Slovakia) | `sk-SK` | `sk-SK` |
| Hungarian (Hungary) | `hu-HU` | `hu-HU` |
| Romanian (Romania) | `ro-RO` | `ro-RO` |
| Croatian (Croatia) | `hr-HR` | `hr-HR` |
| Hebrew (Israel) | `he-IL` | `he-IL` |
| Malay (Malaysia) | `ms-MY` | `ms-MY` |
| Catalan (Spain) | `ca-ES` | `ca-ES` |

## Validation

Backend build passed. Full backend suite: 431 passed, 1 existing PostgreSQL analytics integration test skipped. Request-capture tests cover all 61 catalog entries, canonical casing, unknown locales, unchanged verbatim/timestamp settings, and the existing provider-rejection fallback. A separate comparison verified that the allowlist exactly matches all 83 unique codes in the live Google table. Existing dependency-advisory and nullable/raw-SQL warnings remain outside this change.

## Human deployment

**GO: backward compatible with the currently published app.** No new schema migration, environment variables, secrets, or service registrations are required by this change or the included 1.0.145 MCP level follow-up. If upgrading from before audio levels, the existing additive `20260929013417_AddAudioLevel` migration still applies through normal startup.

1. Pull the backend main commit containing 1.0.146 on the server through the normal release process.
2. For the repository's source-build Compose setup, run `docker compose build api` followed by `docker compose up -d api`. If the server uses a prebuilt image, use its normal image release procedure instead.
3. Confirm successful startup/database initialization and public lesson listing with the published app.
4. Generate Cantonese with script alignment disabled and verify actual spoken wording and word highlighting. Check one unlisted regional variant, such as en-NZ, succeeds using automatic detection. The existing alignment setting is not changed automatically.
5. Refresh the MCP connection for the included optional publication `level` parameter; verify an authored Advanced lesson reports Advanced in submission and lookup results.

No app rebuild or reinstall is needed. Deploying does not repair existing lesson transcripts. The GitHub main workflow only builds a Docker image for CI; pushing does not deploy the API.
