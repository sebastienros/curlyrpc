# Security Policy

## Supported versions

CurlyRpc is pre-1.0. Security fixes are released in a new patch version of the latest minor release
only. Upgrade to the most recent [`CurlyRpc` package](https://www.nuget.org/packages/CurlyRpc) to
receive fixes.

| Version | Supported |
| --- | --- |
| Latest `0.x` release | ✅ |
| Older releases | ❌ |

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues, discussions, or pull
requests.**

Report them privately with GitHub's
[Report a vulnerability](https://github.com/sebastienros/curlyrpc/security/advisories/new) form on the
repository's Security tab. The report is visible only to you and the maintainers.

Please include as much of the following as you can:

- the affected version(s) and target framework(s);
- the type of issue (for example remote code execution, denial of service, authentication bypass,
  information disclosure);
- the affected `JsonRpcOptions` configuration (default or `CreateHardened()`), the framing used, and
  whether `InboundMiddleware` is configured;
- step-by-step reproduction, ideally a minimal program or the raw bytes a peer sends;
- the impact, including what an attacker needs (an unauthenticated peer, an authenticated peer, a
  malicious server, or a local user).

## What to expect

- An acknowledgment within **5 business days**.
- An initial assessment, including whether the report is accepted, within **14 days**.
- Fixes are developed in a private GitHub security advisory. You will be credited in the advisory and
  the release notes unless you ask not to be.
- We aim to release a fix within **90 days** of the report, and will coordinate the disclosure date
  with you.

## Scope

CurlyRpc's security assumptions, trust boundaries, and the guarantees the library does and does not
make are described in the README under
[Security and hardening](README.md#security-and-hardening). In short:

- **In scope:** issues where a remote peer can affect the host beyond what the configuration allows.
  Examples are crashing or stalling the process, bypassing `InboundMiddleware` or the handshake,
  invoking methods that were not meant to be exposed, exceeding a configured limit, or leaking
  exception details when `ExposeExceptionDetails = false`.
- **Out of scope:**
  - resource exhaustion under the default (permissive) options, which assume a trusted peer, unless
    `CreateHardened()` or the relevant limit does not protect against it;
  - the confidentiality or integrity of the transport itself (use TLS or OS access controls);
  - vulnerabilities in application handlers or in the .NET runtime.
