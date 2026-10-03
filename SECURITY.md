# Security policy

## Reporting a vulnerability

Please report security problems **privately** by email to <Info@webcents.in> with the subject "Security". Do not open a
public issue for a vulnerability.

Useful details: the app and version (shown at the bottom right of the window), what you did, what you expected and what
happened, and a minimal way to reproduce it. We aim to acknowledge a report within 5 working days.

**Never send real mail.** If you need to share a file that triggers the problem, create it from invented data (the
command-line tool can write one: see [docs/BUILDING.md](docs/BUILDING.md)) or describe the structure instead.

## What is in scope

- The Windows applications and the conversion engine in this repository: parsing of untrusted OST/PST files, the exporters,
  the PST/MSG writers, file output paths, local storage of the sign-in token, and update handling.
- The sign-in protocol as implemented by the client code here (token verification, device handling).

Parsing hostile files is the most important surface: a crafted OST/PST should never cause code execution, writes outside the
chosen output folder, or unbounded memory use. Reports in this area are especially welcome.

## Out of scope

- Bypassing the sign-in check in a build you modified yourself. Client-side checks cannot prevent
  this by design; see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
- Findings that need a modified or already-compromised Windows account.
- The account server and the websites run by Webcent Solutions are not part of this repository; please still report
  problems you notice there to the same address.

## How the client protects you

- Mail is processed locally and never uploaded.
- Source files are opened read-only and never modified.
- The signed sign-in token is verified offline against a public key built into the app; the private key is never in the
  client or this repository.
- The stored token and refresh secret are encrypted with Windows data protection (DPAPI).
- Error reports are plain text files kept on your PC; they are never sent automatically and never contain message content.
- No secrets, keys or credentials are kept in this repository.
