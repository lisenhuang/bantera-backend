# Māori model benchmark, 27 October 2026

Oracle AU, Ubuntu ARM64, four Neoverse-N1 host cores and 23.42 GiB host RAM. Model service capped at two CPU cores and 8 GiB RAM, no swap, one job at a time. Runtime binaries: whisper.cpp v1.9.5 (`d1be6fde11ac6e0407606b4e42fe72d34add8037`) and Candle 0.9.1. Each job loads its model afresh. File-system cache may be warm; these are not controlled cold-disk measurements. A separately capped one-core application build ran during part of the benchmark.

Two existing Bantera-generated Māori Practice cues were used with the user’s authorisation. They are synthetic TTS recordings, 2.184 and 1.440 seconds long. No human recording or production account data was used. This tiny set cannot establish native-speaker accuracy or general performance.

| Sample | Model | Wall seconds | RTF | CPU seconds | Average CPU | Process peak RSS MiB | Total service sampled peak MiB | WER |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| sample-1.audio | whisper-small | 18.519 | 8.479 | 36.14 | 195.1% | 459.2 | 615.0 | 100.0% |
| sample-1.audio | whisper-large-v3 | 129.191 | 59.153 | 244.18 | 189.0% | 1974.0 | 2720.9 | 100.0% |
| sample-1.audio | madlad | 9.591 | n/a | 7.0 | 73.0% | 2933.1 | 4734.7 | n/a |
| sample-2.audio | whisper-small | 20.873 | 14.495 | 35.65 | 170.8% | 459.3 | 2574.6 | 66.7% |
| sample-2.audio | whisper-large-v3 | 128.113 | 88.967 | 236.78 | 184.8% | 1973.7 | 4383.0 | 66.7% |
| sample-2.audio | madlad | 7.356 | n/a | 5.95 | 80.9% | 2935.2 | 5606.4 | n/a |

Wall time includes model load and inference, excludes queue wait and audio conversion. RTF is wall time/audio duration. CPU 100% means one core. GNU time peak RSS is the inference process high-water mark. Total service memory is cgroup memory including file cache, sampled every 100 ms; it includes prior cached model files and is not the current job’s own allocation.

## Accuracy observations

Both Whisper models performed poorly on these clips. Macrons are retained in WER; punctuation and case are ignored. A successful model response and Māori language selection do not prove acceptable transcription. WER is not pronunciation quality.

- whisper-small; input/reference: Ko wai tō ingoa?; output: kōwai to īmoa
- whisper-large-v3; input/reference: Ko wai tō ingoa?; output: Kauai to imoa.
- madlad; input/reference: Ko wai tō ingoa?; output: Who has a name?
- whisper-small; input/reference: He wai māu?; output: He wa imo?
- whisper-large-v3; input/reference: He wai māu?; output: He waimo?
- madlad; input/reference: He wai māu?; output: Who are you?

MADLAD outputs show meaning errors (for example, “Ko wai tō ingoa?” produced “Who has a name?”). No fluent-speaker quality assessment has been performed. Use a larger, consented, native-speaker dataset with human-checked references before any app integration. All three models ran within the resource limits; large-v3 was about 128–129 seconds for these very short clips and is unsuitable for a real-time experience under these settings.

## Exact model files

- **Whisper Small (multilingual)**: `ggml-small-q5_1.bin`, Q5_1, 190,085,487 bytes. Revision `5359861c739e955e79d9a303bcbc70fb988958b1`. SHA-256 `ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb`. MIT. Source: https://huggingface.co/ggerganov/whisper.cpp.
- **Whisper large-v3 (multilingual)**: `ggml-large-v3-q5_0.bin`, Q5_0, 1,081,140,203 bytes. Revision `5359861c739e955e79d9a303bcbc70fb988958b1`. SHA-256 `d75795ecff3f83b5faa89d1900604ad8c780abd5739fae406de19f23ecd98ad1`. MIT. Source: https://huggingface.co/ggerganov/whisper.cpp.
- **Google MADLAD-400-3B-MT**: `model-q4k.gguf`, Q4_K GGUF, 1,654,597,280 bytes. Revision `fa184c675da0b5c9e1c8694fccd4e12e2d422094`. SHA-256 `ea6e5531a3e95213c7f0635988d119e078a655c09306e47851e15d4c0c3f9c37`. Apache-2.0. Source: https://huggingface.co/google/madlad400-3b-mt.

Raw per-job results and provenance: [benchmark-results.json](benchmark-results.json). Licences and runtime dependency notices: [model-service/notices](../../model-service/notices). The source audio was removed from the service after each test. The separately authorised benchmark report is retained beyond the dashboard’s 24-hour job retention.

## Generated round trip, release 1.49.0

A real 10-word test used the app's shared primary text model
`chatgpt/gpt-6-luna` (3.744 seconds), then its primary Gemini TTS model
`gemini-2.5-flash-preview-tts` (5.675 seconds), Māori Practice voice `Kore`.
The configured fallbacks were `gemini-flash-latest` and
`gemini-3.8-flash-lite-tts`; neither fallback was needed. Submission returned a
job ID in 0.039 seconds. The generated MP3 was 28,032 bytes; decoded duration
was 3.504 seconds (the provider PCM duration before MP3 padding was 3.450 s).

| Stage | Wall seconds | CPU seconds | Average CPU | Process peak RSS MiB | Total service sampled peak MiB |
|---|---:|---:|---:|---:|---:|
| Whisper Small Q5_1 | 19.937 | 39.11 | 196.2% | 459.7 | 531.2 |
| MADLAD Q4_K | 10.267 | 12.60 | 122.7% | 2937.8 | 4084.1 |

Whisper real-time factor was 5.690, with 70% WER (7 edits / 10 reference words).
MADLAD received the actual Whisper transcript. Translation quality remains
unassessed. This synthetic, machine-referenced example is not evidence of
acceptable Māori accuracy. Cloud text/TTS timings measure API wall time;
their external provider CPU/RAM usage is unavailable and is not included in
local model measurements. The local limits and exact model files above are
unchanged. The complete round trip, owner-only audio playback, byte-range
seeking, non-admin rejection, assessment persistence and temporary-file cleanup
passed candidate and public verification. Generated completed-test audio is
retained privately for 24 hours.

Content-free timing/model evidence: [generated-stage-results.json](generated-stage-results.json).
Deployment and live checks: [generation-deployment-proof.json](generation-deployment-proof.json).
