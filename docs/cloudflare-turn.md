# Cloudflare TURN for chat calls

`GET /api/chat/calls/ice-servers` keeps its existing authenticated API and adds
`iceTransportPolicy` (`all` or `relay`) alongside `iceServers`. The default is
direct P2P using STUN with TURN fallback. The backend generates temporary
Cloudflare TURN credentials for each request. This does not implement iOS
CallKit or VoIP push notifications.

## Admin connection mode

Dashboard > Calls (`/dashboard/calls`) exposes a **Use TURN only** switch:

- Off (default): direct P2P using STUN, with TURN available as fallback.
- On: only relay candidates are permitted on updated mobile clients. All calls
  use relay traffic. If credentials cannot be generated, return an empty server
  list with `iceTransportPolicy: relay`; do not silently allow direct connections.

Admin-only `GET/PUT /api/admin/call-settings` reads and updates the mode. PUT
accepts `{"iceTransportPolicy":"all"}` or `{"iceTransportPolicy":"relay"}`.
The setting uses the existing `app_settings` table (`chat.iceTransportPolicy`),
records the admin and update time, and needs no migration. It applies when
starting new calls. TURN-only selection requires configured TURN credentials.

**TURN-only enforcement requires app 2.0.56+ on both devices.** Older versions
ignore the new policy field and may connect directly even when the switch is on.
Deploy the backend and website, release the app, then enable TURN only once the
intended callers have updated. The default mode remains compatible with old apps.

## Configuration

Set these environment variables on the API container before restarting it:

| Variable | Value |
| --- | --- |
| `CloudflareTurn__Enabled` | `true` |
| `CloudflareTurn__KeyId` | TURN Token ID from the Cloudflare `bantera` app |
| `CloudflareTurn__ApiToken` | The matching server-only API token |
| `CloudflareTurn__CredentialTtlSeconds` | `86400` (default: 24 hours) |

Use the dedicated TURN API token, not the Workers AI `Cloudflare__ApiToken`.
Never commit or embed the permanent token in the mobile app. Production reads
environment variables; it does not read credentials from the developer's Mac.
For local development, the same settings can go in the gitignored
`BanteraApi/appsettings.Development.json` under `CloudflareTurn`.

The credential lifetime must exceed the expected call duration. Valid configured
values are 60 to 86400 seconds. Existing clients do not renew credentials during
calls; relay calls exceeding that lifetime require a future client refresh feature.

Disabled or invalid configuration, provider errors, invalid responses, and a
five-second provider timeout return the original STUN-only response in default
mode, or an empty relay-only response in TURN-only mode. Logs describe
the failure without printing credentials or provider response bodies. Caller
cancellation is propagated. Credential responses use `Cache-Control: private,
no-store` and are not shared between users.

## Deployment and verification

This change is backward-compatible, requires no database migration, and is safe
to deploy with TURN disabled. Without the environment variables, existing P2P
calls keep working but relay fallback is inactive. Set `CloudflareTurn__Enabled=false`
and restart the API to roll back TURN independently of the app. Turn off the
dashboard's TURN-only switch first, or updated clients will fail closed.

On the current production server, `deploy/redeploy-server.sh` reads
`/home/ubuntu/apps/bantera-backend/deploy/.env.production` with Docker `--env-file`.
Editing that file does not change the running container; the next manual deploy
loads the values. Keep the file private and outside Git.

After deployment:

1. Confirm `/version` reports the deployed release and unauthenticated requests to
   the ICE endpoint are rejected.
2. As a signed-in user, request ICE configuration. Confirm STUN and authenticated
   TURN/TURNS entries are present, `Cache-Control` is `private, no-store`, and the
   permanent API token is absent. Do not paste temporary credentials into logs.
3. Test an iOS/Android call on different networks. Use WebRTC candidate-pair stats
   to verify a relay path on a restrictive network, plus microphone playback in
   both directions. Successful credential issuance alone is not call verification.
4. Toggle each mode as an admin, reload the page, and confirm persistence. Confirm
   non-admin requests are rejected. In TURN-only mode, verify the updated app
   selects a relay pair even on the same Wi-Fi. Restore the desired mode afterward.

Cloudflare includes 1,000 GB/month shared with SFU usage, then charges for overages.
This integration does not impose a spending cap. Only relayed media consumes TURN
traffic; enabling TURN does not force every call through Cloudflare.

References: [credential API](https://developers.cloudflare.com/realtime/turn/generate-credentials/)
and [pricing](https://developers.cloudflare.com/realtime/sfu/platform/pricing/).
