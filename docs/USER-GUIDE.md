# User guide

This guide covers **Webcent OST Converter Free**.

## Before you start

- Windows 10 or 11, 64-bit. Outlook does not need to be installed.
- Close Outlook if you are converting a file it is using; Outlook keeps its OST open while it runs. If a file will not open,
  close Outlook or copy the file somewhere else first. The app never modifies the file you open.
- Make sure the output drive has enough free space. A PST made from a large mailbox can be the same size as the source.

## The three steps

The bar at the top of the window shows where you are.

### 1. Select a file

- **Drop** an `.ost` or `.pst` file onto the window, or click **Browse for a file...**.
- Under **Found on this PC** the app lists Outlook data files it finds in Outlook's default folders; click **Open** next to one.
- You can also pass a data file to the program on the command line, or use *Open with* from Explorer.

### 2. Choose what to convert

- **Folders** (left): tick the folders to export. **All** and **None** are shortcuts; *Show hidden system folders* reveals
  Outlook's internal folders.
- **Items** (middle, top) and **reading pane** (middle, bottom): click a folder to list its messages and a message to read it.
- **Convert to** (right): choose a format. A short description under the list explains what you will get.
- **Split into files of** (PST only): the size at which a new PST part is started. The default of 40 GB is safe for Outlook.
- **Date range** (optional): only convert items within the dates.
- **Save to**: the output folder. The app creates files below it and never overwrites your source.

Click **Convert**. It is available once you are signed in; until then a banner offers **Create account / sign in** (see below).

### 3. Convert

A progress bar shows folders and items as they are processed. When it finishes you can:

- **Open output folder**
- **View log** (every item that was skipped or incomplete, with its folder path and a reason)
- **Change options** to run the same file again with different settings
- **Convert another file**

Items that could not be fully converted appear under **Needs attention**. This is usually because the source file itself is
missing their data; the rest of your mailbox is unaffected.

## Output formats

| Format | Result | Open it with |
| --- | --- | --- |
| PST | One Outlook data file (several if large) containing your folder structure | Outlook: *File > Open & Export > Open Outlook Data File* |
| EML | `Folder/00001 Subject.eml`, one per message | Outlook, Thunderbird, Apple Mail and others |
| MBOX | One `.mbox` per folder | Thunderbird, Apple Mail, many import tools |
| MSG | One `.msg` per message (mail only) | Double-click; opens in Outlook |
| PDF | One PDF per message: header, attachment list, text | Any PDF viewer |
| HTML | One web page per message, attachments beside it | Any browser |
| CSV | Spreadsheets for mail index, contacts, calendar, tasks | Excel and similar |

Formats other than PST and CSV only apply to mail. Contacts, calendar items and tasks are counted as *not applicable* in
the result summary; use PST or CSV to keep them.

## Your account

- Opening and previewing files needs no account.
- To convert, click **Account** (or **Create account / sign in** on the options screen), enter your **name** and **email**, click
  **Send code**, and type the 6-digit code we email you. The first sign-in creates your account; there is no password.
- After that, converting is unlimited. One account works on **2 PCs**; **Account** lists them and can sign any of them out.
- The optional **Buy me a coffee** button appears if the developer has set up a tip page. The app is free either way.

### Signing in on a third PC

If your account is already on two PCs, the app asks which one to sign out so the new PC can take its place.

### Offline use

The app refreshes your sign-in about once a day. If you are offline it keeps working for up to 7 days on the last sign-in it
received.

### If an account is suspended

An account can be suspended for abuse, such as sharing it publicly. The app then says so and shows the support address.

## Troubleshooting

| Problem | What to try |
| --- | --- |
| "The file could not be opened" | Close Outlook or copy the file and open the copy. Only Unicode OST/PST files from Outlook 2003 or later are supported. |
| Some items are listed under *Needs attention* | Open the log. Items whose data is missing from the source cannot be recovered by any converter. |
| "Can't reach the account server" | Check your internet connection. Conversion itself works offline; only sign-in and refresh need the network. |
| The code email does not arrive | Check spam. Codes are valid for 10 minutes. If you still cannot sign in, contact support; they can issue a one-time login code. |
| Windows SmartScreen warns about the installer | The installer is not yet code-signed. Choose *More info*, then *Run anyway*. |

## Getting help

Email <Info@webcents.in> with what you did and what you saw. If the app showed an error, attach the text file from
`%LOCALAPPDATA%\OST Converter\logs` (it contains the error and stack trace, never your mail). **Please do not send real OST or
PST files or message contents.**
