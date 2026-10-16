# Contributing

This repository is maintained by [@lisenhuang](https://github.com/lisenhuang). Keep changes focused and explain the problem, the approach, and how you checked it. Discuss large changes in an issue before implementing them.

## Local checks

Use the toolchain versions declared by the CI workflow and package files. From this repository root:

```sh
dotnet restore BanteraApi.Tests/BanteraApi.Tests.csproj --locked-mode
dotnet build BanteraApi.Tests/BanteraApi.Tests.csproj -c Release --no-restore
dotnet test BanteraApi.Tests/BanteraApi.Tests.csproj -c Release --no-build
docker build -t bantera-api:ci .
```

CI restores locked NuGet dependencies with the SDK in `global.json`, builds Release, runs the xUnit regression suite, uploads TRX results, and builds the production Docker image. Tests requiring a disposable PostgreSQL database or live provider credentials are explicitly skipped when those prerequisites are absent; a green badge does not claim live-provider or database integration coverage.

## Review expectations

- Add regression tests for fixes and new behavior. Prefer deterministic fixtures and mocked external services; never run tests against production data.
- Follow the surrounding code style. Keep type checking and lint rules enabled; explain any necessary local exception.
- Keep API contracts, persisted data and account boundaries compatible. Describe migrations and configuration changes explicitly.
- Include accessibility and localization checks for UI work, and screenshots when they help review.
- Update the README or relevant design documentation when behavior or setup changes. Follow the versioning rules in `CLAUDE.md`.
- Commit lockfile changes alongside dependency changes. Dependabot opens update proposals; updates still need review and passing checks.
- Do not include keys, tokens, recordings, personal data, or signing material in changes, test fixtures or CI logs. See [SECURITY.md](SECURITY.md).

## Continuous integration and delivery

[CI](.github/workflows/ci.yml) runs for pull requests to `main`, pushes to `main`, and manual dispatch. Action revisions are pinned; workflow tokens have read-only repository permissions. Test reports are retained for 14 days. The README badge links to the actual workflow result for `main`.

Passing checks are evidence for the checks listed above, not a guarantee of complete test coverage or production behavior. Document any skipped tests and manual verification. CODEOWNERS identifies the maintainer; it does not itself enforce branch protection or mandatory approval.

CI builds and tests only. Signing, store submission, and production deployment remain separate human-operated release steps.
