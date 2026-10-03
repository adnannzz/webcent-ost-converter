# Changelog

All notable changes are listed here, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/).

## [1.0.0] - first release

### Added
- Reader for Unicode OST and PST files (Outlook 2003 onwards), including the 4K-page format used by recent Outlook versions.
- Export to PST, EML, MSG, MBOX, PDF, HTML and CSV, with folder selection, date range filter and cancellation.
- PST writer that Outlook opens, with automatic splitting into parts above a size limit (default 40 GB).
- Per-item error handling: damaged items are reported in a log and skipped, never stopping the job.
- Preview of folders, messages and attachments before converting.
- Free account: sign in with an emailed code (no password), unlimited converting, 2 PCs per account, offline use for up to 7 days.
- Windows installer and background updates (Velopack), crash reports kept locally.
- Webcent branding: logo and icon, navy/blue/magenta interface with a three-step progress header.
- Developer tools: `ostcli` command-line utility with a `demo` mailbox generator and PST validation commands.
