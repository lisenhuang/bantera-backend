# iOS incoming calls with CallKit and PushKit

The iOS app reports calls through Apple's system calling UI and uses PushKit to wake a background or cold-started app. WebRTC still carries media directly or through the configured TURN relay. Apple only delivers the incoming-call notification.

## Deployment

1. Deploy backend **1.0.138** before distributing app **2.0.59+247**.
2. Build/install the new iOS app from `app/ios/Runner.xcworkspace`. For a true cold-start test, use a signed Release or TestFlight build; Flutter debug builds have launch/debugger restrictions.
3. Open the app and sign in once, with chat notifications enabled. This registers the separate VoIP token. Existing ordinary APNs tokens continue to receive voice-message notifications.
4. The new Android APK is built from the same app version for cross-platform testing. Deploy the website to serve that APK.

No database migration or new environment variable is required. VoIP tokens use the existing `user_push_tokens.Platform` column with value `ios-voip`. The server reuses `Apns__KeyId`, `Apns__TeamId`, `Apns__BundleId`, and `Apns__PrivateKeyPem`; the configured bundle must be `bantera.lisenhuang.com`. The APNs key must authorize that application's VoIP topic. Credential presence is not proof that APNs accepted a real VoIP push; verify the delivery in the device test below.

Xcode needs Push Notifications plus the Audio, Remote notifications, and Voice over IP background modes. CallKit requires no App Store Connect switch. Automatic signing should provide the matching push entitlement for development and distribution builds.

## Compatibility and transport

- Existing alert-token registration and notification payloads remain supported for released apps.
- `PUT /api/chat/push/voip-token` registers an authenticated user's VoIP token and disables legacy incoming-call alerts for that device's regular token only after registration succeeds.
- VoIP delivery uses `apns-push-type: voip`, topic `<bundle-id>.voip`, priority 10, and expiration 0 (do not store undeliverable call invites). Voice-message alerts never use VoIP tokens.
- VoIP pushes are sent even if a background socket still appears online. Both delivery paths share one call UUID and are deduplicated natively and in Flutter.
- A headless-capable shared Flutter engine starts with the app, so Answer/Decline does not depend on opening a Flutter screen. The visible scene attaches to that same engine.
- Answer waits for backend acceptance before fulfilling the CallKit action. CallKit's audio-session activation/deactivation is forwarded to WebRTC.
- Native ringing expiry and authenticated call-status checks clear stale or cancelled calls. Declining and hanging up clear local UI even when signalling is unavailable.
- VoIP tokens are deregistered on sign-out when online. Native recipient checks also reject stale pushes for a different/signed-out account.

## Release checks requiring two physical devices

- Foreground, background, and locked iPhone: exactly one incoming-call UI, correct caller, Answer/Decline, audible audio in both directions.
- Signed Release/TestFlight app cold start: lock-screen Answer works without opening the conversation first. Do not equate this with guaranteed delivery after user force-quit or under Focus/network restrictions.
- Caller cancels while the callee rings; no answer for 45 seconds; callee declines; hang up from each side; immediately call again without reopening the DM.
- Accept on one of two signed-in devices: the other stops ringing.
- System mute and speaker/Bluetooth route; audio and video calls; incoming cellular-call interruption.
- Old released app continues to receive ordinary call alerts and voice-message notifications.
- Admin TURN-only mode with current apps on both test phones; restore normal mode afterward.

The iPhone's unlocked incoming-call presentation follows its system preferences. Locked-screen presentation is controlled by CallKit, not a custom Flutter notification screen.

## Rollback

Old mobile apps remain compatible with this backend. Before rolling the backend back to a version that does not recognize `ios-voip`, remove only rows with that platform (or retain the transport filtering patch); otherwise the old sender can mistake VoIP tokens for ordinary notification tokens. New clients require the new call-status and acceptance acknowledgement endpoints/events for CallKit answering.

## Notification toggle fix (backend 1.0.139)

Re-enabling chat notifications sends a normal test alert. The previous push sender signed a fresh APNs provider JWT for every send; Apple rejected a production request with `429 TooManyProviderTokenUpdates`. The sender now shares one provider JWT across transient HTTP clients, normal alerts and VoIP pushes, and renews it on demand after 50 minutes. Concurrent sends and renewal are covered by regression tests. The JWT remains in process memory and is never logged.

Deploy backend 1.0.139 for this fix; app 2.0.59+247 and older published clients remain compatible. No new environment variables or migration are required. After deployment, turn Bantera notifications off and on, then test an incoming audio call with the iPhone locked. Check server logs for `PushType=voip` and status 200; APNs acceptance alone does not establish that the phone rang or audio worked.

## Cloudflare STUN (backend 1.0.140)

The ICE endpoint now supplies `stun:stun.cloudflare.com:3478` for direct-connection discovery, including the fallback when TURN credentials cannot be generated. Normal mode adds the temporary Cloudflare TURN relays without duplicating STUN entries. TURN-only mode still excludes STUN and keeps the `relay` policy.

Both mobile platforms fetch ICE configuration from the backend for each call, so existing compatible apps use the new STUN address after backend deployment. No device-side configuration, new credentials or database changes are required. App 2.0.60+248 separately enables the video-call menu on Android.
