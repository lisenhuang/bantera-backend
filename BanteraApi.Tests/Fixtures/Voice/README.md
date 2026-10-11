# Speech detector fixtures

`yes.pcm` and `no.pcm` are short synthetic single-word recordings generated with
macOS `say` (Samantha), converted to mono signed PCM16 little-endian at 16 kHz.
They contain no user audio and are embedded only in the test assembly.

These fixtures ensure the no-speech guard accepts meaningful replies shorter
than one second, including when the recording is quieter or split across frames.
