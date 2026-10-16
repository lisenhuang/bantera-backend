# Images attached to AI voice messages

App 2.16.0 (360), backend 1.19.0.

The AI chat composer accepts up to four images from Camera, Photos or Files. They remain removable drafts until recording starts. Images and the recording are saved together in the account-isolated local AI history. A failed send retains both for Retry. Human DMs do not accept image attachments.

Images larger than 256 KiB are downsampled on the device, limited to a 1080-pixel longest side and encoded as JPEG. Smaller JPEG/PNG images retain dimensions and format; unsupported formats such as HEIC are converted. Orientation is applied and metadata stripped. Selected originals are not modified. The bounded transport accepts up to 1 MiB per image and four images; oversized/corrupt inputs fail without sending an incomplete message.

`/ws/chat/ai/voice` accepts an optional start-frame `images` array with `mimeType` and base64 `data`. Only validated JPEG/PNG images are accepted. The ready frame adds `acceptImages: true`. New clients require this acknowledgement for image messages; they never degrade to the audio-only HTTP endpoint. Old voice-only clients continue unchanged.

Audio still streams as PCM while recording. The backend relays images to Live as video frames, spaced at least one second apart, concurrently with PCM through serialized WebSocket writes. It finishes the activity only after Send and after all image frames have been sent. Image-containing recovery attempts resend both PCM and images rather than falling back to transcription alone. Quiet recordings remain ignored.

Images are transient in backend memory and sent to the configured AI provider, not written to Bantera's database, logs or R2. Local images are not replayed into every future session: history contains an attachment marker and the dialogue text. App web/image-search requests remain on the device.

Verification: four synthetic coloured images plus a streamed synthetic voice question were recognized in order by Gemini Live 3.1. Automated coverage checks validation, pacing while audio flows, commit ordering, old-server rejection, persistence/retry and thumbnail controls. This does not establish recognition accuracy for every image or provider model.
