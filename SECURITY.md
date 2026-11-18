# Security

Please report security problems privately: use **GitHub › Security › Report a vulnerability** on this repository (private vulnerability reporting). Don't open a public issue for them.

What Glance does to keep you safe:
- It installs for your account only and never asks for administrator rights.
- Updates are downloaded from this repository's Releases and checked against the published SHA-256 before (and again right before) they run.
- Tokens you give it (Todoist) live in Windows Credential Manager, never in files or logs.
- Network requests carry no identifiers; see [docs/privacy.md](docs/privacy.md).
