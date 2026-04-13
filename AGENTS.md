# Backend (`backend/`) — agent notes

## API compatibility for the published app

- The app is now published. Treat the current backend API as a public contract used by existing released app versions.
- When modifying backend behavior, prefer backward-compatible changes for endpoints, request/response shapes, auth flows, and error payloads.
- Do not remove, repurpose, or silently break existing API behavior if released app clients may still depend on it.
- If a change cannot be made backward-compatible, introduce a new versioned API surface such as `/api/v2/...` and keep the previous API available until existing app clients can migrate.

## User-facing error messages

- Do not expose Gemini, AI provider, model, key/configuration, or other raw technical backend failure details in API responses shown to users.
- Log detailed technical failures server-side / in the CLI logs, and return only generic user-facing messages unless the error is a deliberate product-level validation or policy message the user can act on.
