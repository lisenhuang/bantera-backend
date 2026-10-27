#!/bin/bash
set -euo pipefail
export CARGO_BUILD_JOBS=2
apt-get update -qq
apt-get install -y -qq --no-install-recommends cmake git clang libclang-dev
cd /work
if [ ! -d whisper ]; then git clone --depth 1 --branch v1.9.5 https://github.com/ggml-org/whisper.cpp whisper; fi
cmake -S whisper -B whisper/build -DCMAKE_BUILD_TYPE=Release -DGGML_NATIVE=OFF -DGGML_OPENMP=ON -DBUILD_SHARED_LIBS=OFF -DWHISPER_BUILD_TESTS=OFF
cmake --build whisper/build --target whisper-cli -j2
cd /work/translator
cargo build --release
mkdir -p /work/bin
cp target/release/bantera-translate /work/bin/
cp /work/whisper/build/bin/whisper-cli /work/bin/
