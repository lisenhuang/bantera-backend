# Backend (`backend/`) — agent notes

## Version bumps

Every time any code is modified in this codebase, bump the version in `BanteraApi/BanteraApi.csproj`:

```
<Version>1.0.37</Version>  →  <Version>1.0.38</Version>
```

- Increment the patch segment (third number)
- Do this as part of the same edit batch — not only before commits
- After changing the version, run `dotnet build` to verify the build succeeds before finishing

## API compatibility for the published app

- The app is now published. Treat the current backend API as a public contract used by existing released app versions.
- When modifying backend behavior, prefer backward-compatible changes for endpoints, request/response shapes, auth flows, and error payloads.
- Do not remove, repurpose, or silently break existing API behavior if released app clients may still depend on it.
- If a change cannot be made backward-compatible, introduce a new versioned API surface such as `/api/v2/...` and keep the previous API available until existing app clients can migrate.

## Deployment & server configuration

- The backend is **published** — it runs in production (Docker on the server, behind a Cloudflare Tunnel). Treat it as live: merged changes reach real users on the next deploy.
- **Production config and secrets are supplied as environment variables on the server**, using .NET's `Section__Key` convention (double underscore `__` maps to the config `:` separator) — e.g. `Jwt__Secret`, `GoogleSignIn__ClientSecret`, `GoogleSignIn__ClientId`. Env vars override `appsettings.json` at runtime.
- The committed `appsettings.json` holds non-secret defaults and **placeholders** for secrets; `appsettings.Development.json` is local-dev only and is **not** loaded in production.
- When you add a config key or secret that production needs: add it to `appsettings.json` (a placeholder for secrets, the real value for non-secrets like client IDs) so structure + local dev work, and call out in your hand-off that the matching `Section__Key` env var must be set on the server — otherwise the feature is inert in prod.

## User-facing error messages

- Do not expose Gemini, AI provider, model, key/configuration, or other raw technical backend failure details in API responses shown to users.
- Log detailed technical failures server-side / in the CLI logs, and return only generic user-facing messages unless the error is a deliberate product-level validation or policy message the user can act on.
