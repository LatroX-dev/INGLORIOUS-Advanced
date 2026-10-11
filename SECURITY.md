# Security

## Supported versions

Security fixes target the latest published release. Older app and firmware combinations are not maintained as separate security branches. An experimental device label is not a security guarantee.

## Report a vulnerability

Use this repository's **Security → Report a vulnerability** form for issues involving firmware writes, backup association, malicious profiles, native tool loading or arbitrary code execution. If private vulnerability reporting is unavailable, open an issue asking for a private reporting channel without publishing exploit details, personal captures or device identifiers.

Include the app version, operating system, affected model and a minimal reproduction with synthetic data. Never attach original firmware, personal profile contents or unredacted logs. Review diagnostic files before sharing them: they may contain local paths and USB identifiers.

## Boundaries

A package checksum detects changes; it does not prove publisher identity. Current Windows packages are unsigned. Obtain app and firmware packages from the same repository release.

The app verifies known model and memory geometry, requires confirmation before firmware writes, checks backups and verifies written data. It does not authenticate a keyboard against an immutable manufacturer identity. Installation identifiers can be copied with a flash image. An interrupted write can require separate recovery.

Backups cover internal flash. They do not cover every external memory or wireless component. Do not bypass read protection or force an unrecognized device through a firmware workflow.
