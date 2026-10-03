# Architecture

Webcent OST Converter Free is a Windows desktop application. Everything that touches your mail runs in-process on your PC.
This page explains how the code is organised and why.

```
                       +-------------------------------+
   OST / PST file ---> |  OstConverter.Core            |  ---> PST, EML, MSG, MBOX, PDF, HTML, CSV
                       |   Pff reader                  |
                       |   Conversion pipeline         |
                       |   Exporters (incl. PST writer)|
                       +---------------+---------------+
                                       ^  used by
                                       |
                       OstConverterFree.App       (WPF user interface)
                       OstConverterFree.Accounts  (sign-in client)
                                       |
                                       +---- Webcent account server (sign-in only; not in this repository)
```

## The engine: `OstConverter.Core`

No UI and no account code lives here, so it can be tested with plain xUnit.

### Reading (`Pff/`)

A managed reader for the Outlook data file format (the "PFF" family: OST and PST). It is written from the published
[MS-PST](https://learn.microsoft.com/openspecs/office_file_formats/ms-pst/) specification and tested against files
produced by Outlook.

| Layer | Files | Job |
| --- | --- | --- |
| File and page structure | `PffFile.cs`, `CryptTables.cs` | Header, B-tree of nodes and blocks, block decoding (permute encryption on older files, zlib-compressed blocks on 4K-page files). Supports Unicode files only; the Outlook 97-2002 ANSI format is rejected with a clear message. |
| Property storage | `Ltp.cs`, `Lzfu.cs`, `NameMap.cs` | Heap-on-node, B-tree-on-heap, property and table contexts, compressed RTF, named-property map. |
| Object model | `PstStore.cs` | Folders, messages, recipients, attachments (including attached messages). |

The file is opened read-only and read through a `FileStream`, and a folder's items are enumerated on demand, so very large
mailboxes do not have to fit in memory. Damaged or incomplete items raise `PffFormatException` for that item only; the
pipeline records the problem and carries on.

### Converting (`Conversion/`, `Export/`)

`ConversionPipeline.Run(store, options, progress, cancellationToken)` walks the selected folders, filters by date,
hands each item to an exporter, and returns a `ConversionResult` (counts, per-item errors, output files, log path).
An `Entitlement` value passed in the options can cap the number of items per folder; the cap is enforced here, not in the
user interface, so no screen can forget to apply it. The app passes `Entitlement.Unlimited` once you are signed in.

| Exporter | Notes |
| --- | --- |
| `EmlExporter`, `MimeBuilder` | MIME messages built with MimeKit. |
| `MboxExporter` | One mbox per folder. |
| `MsgExporter`, `MsgBuilder`, `Cfb/CompoundFileWriter` | A compound-file (OLE) writer and the MS-OXMSG layout. Mail only. |
| `PdfExporter` | QuestPDF text layout. |
| `HtmlExporter` | One page per message, attachments saved beside it. |
| `CsvExporter` | Mail index, contacts, calendar and tasks. |
| `Pst/*` | A complete PST **writer**; see [PST-WRITER.md](PST-WRITER.md). |

### Diagnostics

`CrashLog` writes a plain text report to `%LOCALAPPDATA%\OST Converter\logs` when something unexpected happens. It is
never sent anywhere; users may attach it to a support request. Each conversion also writes its own log with per-item
problems (folder path and message, never message bodies).

## The app: `OstConverterFree.App`

WPF with the MVVM pattern (CommunityToolkit.Mvvm). `MainViewModel` owns the current page, which is one of three view models
(`SelectViewModel`, `OptionsViewModel`, `ConvertViewModel`), each with a matching view selected by a `DataTemplate` in
`App.xaml`. `App.xaml` also holds the brand palette and the control styles, so a colour change is a one-line edit.

### Updates

Releases are packaged with [Velopack](https://velopack.io). Installed apps check an update feed in the background, download
the new version and show "Restart to install". `tools/release.ps1` builds the installer and the update packages.

## Accounts: `OstConverterFree.Accounts`

The account code is deliberately small and runs mostly offline.

1. The user enters a name and email address; the server emails a one-time code. The first successful sign-in creates the
   account. (A support-issued one-time code can be pasted instead of an email address.)
2. The server returns a **signed token** (EdDSA JWT, valid 7 days) and a refresh secret. The client verifies the token
   **offline** against a public key compiled into the app (`LicenseConfig`); the private key exists only on the server. Only
   tokens with audience `ost-free` and plan `member` are accepted.
3. The token and secrets are stored encrypted with Windows DPAPI, tied to the Windows account.
4. The app refreshes the token about once a day. If the server cannot be reached, the last token stays valid until it
   expires, which gives a 7-day offline grace period.
5. Each account may be signed in on 2 PCs. A device is identified by a hash of machine identifiers (not reversible);
   a third PC must replace one of the first two.
6. Signed out, the app is preview-only; signed in, converting is unlimited. A suspended account is refused at sign-in and
   at refresh.

Client-side checks can always be bypassed by a determined person with a modified build. The design goal is to keep honest users
honest and the code simple, not to build copy protection.

## Tests

| Project | What it covers |
| --- | --- |
| `OstConverter.Tests` | Reader, every exporter, PST writer round trips and structural validation, compound-file writer (checked with Windows' own `StgOpenStorage`), crash log. |
| `OstConverterFree.Tests` | Account client, token verification (audience and plan), device limit, store encryption. A live-server test runs only when `OSTFREE_LIVE_*` environment variables are set. |

All tests generate their own input files; no real mailbox is used or needed.
