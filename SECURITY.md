# Security Policy

## Supported versions

Security fixes are applied to the latest release line and `main`.

| Version | Supported |
| --- | --- |
| 2.1.x | Yes |
| 2.0.x and older | No |

## Reporting a vulnerability

Please report vulnerabilities privately through this repository's GitHub Security Advisories. Do not open a public issue containing credentials, session identifiers, packet captures, redirect URLs, HAR files, or complete application logs.

## Secrets

Treat all of the following as secrets:

- Sony `hmac_key` and `session_key` values
- Session identifiers and complete authenticated wire captures
- OAuth access and refresh tokens
- Credential files such as `session_keys.json` or `credentials*.json`
- Browser HAR files and OAuth callback URLs

If any of these are exposed, mint a fresh Sony session-key bundle immediately, update every controller using the old bundle, and coordinate repository-history cleanup when the value was committed. Removing a secret in a later commit does not remove it from Git history.

## Local-network threat model

The soundbar control channel currently uses cleartext HTTP/2 on the local network. HMAC authentication protects supported command/request bodies, but it is not transport encryption and it does not authenticate an arbitrary server discovered only by an open TCP port.

Use the application only on a trusted private network. Never expose port `55051` to the public internet. Discovery must verify that a candidate endpoint is a compatible Sony control service before sending authenticated traffic.

## Contributor hygiene

- Never commit real credentials or captured authenticated packets.
- Use generated, deterministic synthetic values in tests.
- Keep secret scanning and push protection enabled.
- Review staged changes with `git diff --cached` before pushing.
- Sanitize logs and server response bodies before attaching them to reports.
