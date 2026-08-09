# Security Policy

## Supported versions

Security fixes are provided for the latest published release.

| Version | Supported |
|---|---|
| 1.0.x | Yes |
| Earlier versions | No |

## Reporting a vulnerability

Do not publish exploit details in a regular GitHub issue. Use the repository's **Security** tab to submit a private vulnerability report. If private reporting is unavailable, contact the maintainer through the [GrafXP GitHub profile](https://github.com/GrafXP) and request a private reporting channel.

Please include the affected version, reproduction steps, impact, and any suggested mitigation. You should receive an acknowledgement within seven days. No bounty program is currently offered.

The project opens untrusted STL and 3MF files and recursively enumerates user-selected folders, so parser crashes, excessive resource consumption, path handling problems, and archive/XML issues are all considered security-relevant.
