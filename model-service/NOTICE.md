# Bantera local model testing notices

Models are downloaded only from pinned revisions of the official Google MADLAD repository and the whisper.cpp maintainer's official GGML model distribution. The Whisper models are multilingual, not `.en` variants.

- Whisper Small: `ggml-small-q5_1.bin`, Q5_1; Whisper large-v3: `ggml-large-v3-q5_0.bin`, Q5_0. Distribution revision `5359861c739e955e79d9a303bcbc70fb988958b1`. OpenAI Whisper weights and whisper.cpp use the MIT licence.
- Google MADLAD-400-3B-MT: `model-q4k.gguf`, Q4_K, revision `fa184c675da0b5c9e1c8694fccd4e12e2d422094`, Apache 2.0. Google's model card credits Juarez Bochi for conversion of the original weights.
- whisper.cpp v1.9.5, exact commit recorded in `notices/whisper-runtime-commit.txt`. Built on Linux ARM64 with two CPU threads, native CPU optimisation disabled for portability, no GPU.
- Candle 0.9.1, dual MIT/Apache 2.0; see `translator/Cargo.lock` for exact dependencies. The Bantera Rust adapter is based on Candle's quantized T5 example. It adds local files only, stdin input, token bounds, greedy decoding and JSON output. It does not apply repetition penalties. Copyright 2023 Hugging Face; original notices retained.
- Rust dependency notices are retained in `notices/rust-dependencies`, with nested vendored notices (including Oniguruma) in `notices/rust-vendored-notices.tar.gz`. Python distribution licence metadata remains in the runtime image's site-packages. Debian FFmpeg, GNU time and shared library copyright/licence files remain under `/usr/share/doc` in the image. The Dockerfile records their unmodified Debian distribution source; no FFmpeg binary modifications are made.

Model cards and full primary licences are in `notices/`. Downloaded model directories also retain their model cards. Download SHA-256 hashes and byte sizes are recorded in `/models/manifest.json` and exposed to authorised administrators. Original model files are unmodified.

Benchmark recordings are existing Bantera-generated Māori Practice cues, authorised by the user for this evaluation. They are synthetic TTS, not a representative native-speaker evaluation set. No private human recording is used. Input, transcript and assessment content is never logged by the model service. Results expire after 24 hours; exported benchmark evidence is retained separately by the administrator.
