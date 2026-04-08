# Cloudflare AI Cover Images

Bantera uses Cloudflare Workers AI to generate cover images for AI-generated audio.

## Current setup

- Model: `@cf/black-forest-labs/flux-1-schnell`
- Current request shape:

```json
{
  "prompt": "...",
  "num_steps": 50,
  "width": 512,
  "height": 512
}
```

At `512x512`, this is 1 tile.

## Estimated paid-plan cost

- Tile cost: `$0.0000528`
- Step cost: `50 x $0.0001056 = $0.00528`
- Total: about `$0.0053328` per image

That is roughly half a US cent per generated cover.

## Estimated free-plan usage

Cloudflare's free Workers AI allocation is `10,000 neurons/day`.

For this model and size:
- Tile usage: `4.8 neurons`
- Step usage: `50 x 9.6 = 480 neurons`
- Total: about `484.8 neurons` per image

That means the current `512x512`, `50-step` setup is about:
- `10,000 / 484.8 ≈ 20` free generations per day

The free allocation resets daily at `00:00 UTC`.

## Important note

Cloudflare's current docs for `flux-1-schnell` describe the parameter as `steps`, with default `4` and max `8`, not `num_steps: 50`.

If Cloudflare enforces the documented limit instead, the practical upper-bound estimate becomes:
- Paid cost at `8` steps: about `$0.0008976` per image
- Free usage at `8` steps: about `81.6 neurons` per image
- Free generations/day at `8` steps: about `122`

## Practical takeaway

- If `50` steps are accepted: about `$0.00533` per image, about `20` free images/day
- If Cloudflare enforces the current documented max `8` steps: about `$0.00090` per image, about `122` free images/day
