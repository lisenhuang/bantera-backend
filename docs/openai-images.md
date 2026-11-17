# OpenAI profile and cover images

Both generated profile avatars and AI-audio lesson covers use the same
`OpenAiImageService` via the Images API at `https://api.openai.com/v1/images/generations`.

- Model: `gpt-image-2.6-flare`
- Quality: `low`
- Size: `1024x1024`, one image per request
- Output: JPEG; existing ImageSharp processing and R2 storage remain unchanged
- Complexity: API default, matching the sample comparison
- Timeout: 120 seconds; no automatic provider retries or Cloudflare generation fallback

Set **`OpenAiImages__ApiKey`** in the production container environment before deployment.
Use an OpenAI API project key with image-generation access and available API credit.
Never commit the key or expose it to the app. This is separate from the admin
ChatGPT subscription connection. Old `Cloudflare__AccountId` / `Cloudflare__ApiToken`
image-generation settings are no longer read; Cloudflare R2, TURN and Tunnel settings
are still used and must be preserved.

Existing images are not regenerated or overwritten. Existing API routes, payloads,
auth and image URLs stay compatible with published apps; no migration or app update
is required. Avatar generation continues to skip users who already have an avatar.
If cover generation fails, the existing local gradient fallback remains available.
Provider errors use generic messages; logs include status/request ID and elapsed time,
without credentials, raw provider bodies or prompts.

## Verification

`dotnet test --filter FullyQualifiedName~OpenAiImageServiceTests` covers provider
request parameters, decoding, missing configuration, error redaction, no retries,
malformed results and cancellation. Full backend tests cover surrounding contracts.
The live smoke test is opt-in via `BANTERA_TEST_OPENAI_IMAGES=true` and
`OpenAiImages__ApiKey`; it generates synthetic images only, with no production user
or database changes. Run it explicitly only when paid provider verification is authorized.

At the 17 November 2026 calculator rates, a 1024×1024 low-quality image has an
estimated output cost of **US$0.02469**, plus input tokens. This is an estimate,
not a billing guarantee. The sample API comparison measured Flare low avatar/cover
requests at 7.1/7.5 seconds; latency varies and is not an SLA.

Sources: [model](https://developers.openai.com/api/docs/models/gpt-image-2.6-flare),
[Images API](https://developers.openai.com/api/reference/resources/images/methods/generate),
[cost calculator](https://developers.openai.com/api/docs/guides/image-generation#calculating-costs).
