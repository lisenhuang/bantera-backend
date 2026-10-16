# Security reporting

Please report suspected vulnerabilities privately to [contact@bantera.app](mailto:contact@bantera.app), the contact listed on [Bantera Support](https://bantera.app/support). Include the affected repository and version, impact, and a minimal reproduction. Do not include real user data, access tokens, credentials, or production recordings.

Do not publish exploitable details in a public issue before the maintainer has had an opportunity to investigate. Coordinate disclosure and any testing that could affect users with the maintainer. Security reports are reviewed by the repository owner; no response-time or bounty commitment is implied.

## Scope

Development follows `main`; there is no separate long-term-support branch. Fixes should preserve compatibility with published clients wherever possible. A fix reaching `main` is not proof that it has been deployed or released in a mobile store.

Dependency updates are proposed through Dependabot. CI uses read-only repository permissions and does not receive production credentials. Never use production databases or accounts in automated tests.

## Dependency follow-up

ImageSharp 3.1.12 currently has an outstanding [BigTIFF decoding advisory](https://github.com/advisories/GHSA-wmxv-xphr-5c9g). Its patched 3.2 release requires a build-time license key ([publisher guidance](https://docs-v3.sixlabors.com/articles/imagesharp/index.html)); that key is not configured in this repository. The upgrade needs maintainer licensing/configuration work before it can be applied. NuGet also reports additional ImageSharp advisories and a transitive Microsoft.OpenApi advisory (GHSA-v5pm-xwqc-g5wc). Review the current restore output and [GitHub advisories](https://github.com/advisories) when prioritizing dependency updates. NuGet audit warnings are not suppressed. A passing CI badge reports build and test status, not a clean security audit.
