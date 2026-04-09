# User avatar endpoint: duplicate requests and DB load

## Problem

`GET /api/users/{userId}/avatar` loads the creator’s profile image. The handler calls `ProfileService.GetAvatarAsync`, which used to query Postgres for `AvatarObjectKey` on **every** request.

In the Flutter app, actions that rebuild the practice screen (e.g. play/pause, toggling transcript) can trigger **multiple parallel HTTP GETs** to the same avatar URL. That produced **many identical** `SELECT "AvatarObjectKey" FROM users WHERE ...` lines in logs for a single user gesture—not because those actions are “avatar APIs,” but because the UI still displays the creator’s `NetworkImage` and the image stack can fan out requests.

## Mitigations (implemented)

### API (`BanteraApi`)

1. **`IMemoryCache`** is registered in `Program.cs` (`AddMemoryCache()`).

2. **`ProfileService.GetAvatarAsync`** uses **`memoryCache.GetOrCreateAsync`** with a per-user key (`avatar:object-key:{userId:N}`) and a **15-minute** absolute expiration. Concurrent requests for the same user share **one** DB lookup while the entry is being created.

3. **`ProfileService.UpdateAvatarAsync`** calls **`memoryCache.Remove`** for that user’s key after a successful upload so the next GET resolves the new R2 object key.

4. The avatar route sets **`Cache-Control: public, max-age=3600`** on successful responses (see `Program.cs` `MapGet` for `/api/users/{userId}/avatar`) to help HTTP caches on repeat loads.

### App (reference)

The practice player memoizes a single `NetworkImage` for the creator avatar and **`precacheImage`** after the first frame (`practice_player_screen.dart`). This reduces redundant client-side fetches; the server cache guards Postgres if multiple GETs still occur.

## Operational notes

- Cached value is the **object key string** (or empty string when the user has no avatar). Only **`UpdateAvatarAsync`** invalidates; if keys were changed outside the app, wait for TTL or restart the process.

- Multiple GETs may still open **multiple R2 downloads** until browsers/clients respect `Cache-Control`; the main log spam addressed here is **duplicate SQL** on `users` for `AvatarObjectKey` lookup.
