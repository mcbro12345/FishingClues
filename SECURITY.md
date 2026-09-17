# Security Policy

## Supported Versions

Only the latest released version of Fishing Clues receives security fixes. Update to the newest release before reporting an issue, in case it is already fixed.

## Reporting a Vulnerability

Report security vulnerabilities privately using GitHub's [private vulnerability reporting](https://github.com/mcbro12345/FishingClues/security/advisories/new), not a public issue. This keeps details out of view until a fix is available.

Include:

- The affected Fishing Clues version and Dalamud/API version
- Steps to reproduce
- The potential impact

Expect an initial response within a few days. Confirmed vulnerabilities will be fixed and disclosed once a patched release is available.

## Scope

Fishing Clues is a client-side Dalamud plugin that runs inside your own FINAL FANTASY XIV process. It does not run a server, accept network connections, or process input from other users. Relevant reports include things like unsafe memory access, crashes triggered by malformed local data files, or unintended data exposure such as revealing undiscovered fish or fishing-hole names through an unintended path.
