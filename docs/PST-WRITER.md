# The PST writer

Writing a PST that desktop Outlook accepts is the hardest part of this project: the format is large, and Outlook is far
stricter about what it opens than the published specification suggests. This page records how the writer is built, the
rules we found by testing against real Outlook, and how output is validated.

Source: `OST Converter/src/OstConverter.Core/Export/Pst/`.

## What it writes

- A **Unicode PST** (file version 23) with 512-byte pages, the compatible encoding Outlook itself writes (permute
  encryption), and the standard folder skeleton: root, *Top of Outlook data file*, *Search Root*, *Deleted Items*.
- Your folder tree with mail, contacts, calendar entries and tasks, recipients, attachments and attached messages.
- A copy of the source's named-property map, so custom property ids stay valid.
- **Multiple parts.** When the output reaches the size limit (default 40 GB; Outlook's own limit is 50 GB) a new numbered
  PST part is started. Each part is a complete data file you can open on its own.

## Layers

| File | Layer (MS-PST terminology) |
| --- | --- |
| `NdbWriter.cs` | Node Database: pages, the node and block B-trees, block signatures and CRCs, allocation maps. |
| `LtpWriter.cs` | Lists, Tables, Properties: heap-on-node, B-tree-on-heap, property contexts and table contexts. |
| `PstBuilder.cs` | Messaging layer: folders, messages, recipients, attachments, the store object. |
| `PstTemplates.cs` | The fixed objects every PST needs (hierarchy and contents templates, receive-folder table). |
| `PstMessageMapper.cs`, `PstExporter.cs` | Map a source item to PST properties; drive the writer and the part splitting. |
| `PstValidator.cs` | Structural checker used by the tests and the command-line tool. |

## Rules Outlook enforces

Each of these was discovered because Outlook crashed, reported database corruption, or silently showed an empty folder
for a file that passed our own checks. They are why the writer looks the way it does.

1. **Signatures.** A page or block signature is `LOWORD(x) XOR HIWORD(x)`, where `x` is the offset XOR the block id,
   truncated to 32 bits. Using only the low word happens to work below 64 KB, so small test files pass while large
   files are rejected.
2. **Block reference count.** Ordinary blocks in the block B-tree must carry a reference count of 2. A count of 1 makes
   Outlook stop with a "reference count" assertion.
3. **Padding and packing.** In tables with more than one row-matrix block, and for heap blocks, every block except the
   last is padded to the full 8,176 bytes, and heap data is packed first-fit nearly full. Looser packing makes Outlook
   show zero items once a table passes roughly 64 rows.
4. **Name-to-id map.** The streams of the named-property map (`0x0003` and `0x0004`) must live in subnodes even when tiny;
   otherwise Outlook ignores named properties and messages read as empty.
5. **Attached messages.** The attachment's data-object property is an 8-byte heap value (subnode id and size) and the
   subnode has type *normal message*. The attach-method property is `0x3705`.
6. **Template objects.** Folder and store structures are modelled on a PST that Outlook created, not on the minimum the
   specification describes.

## Validation

All of this is development tooling and is not shipped with the app.

- **Unit tests** (`PstWriterTests`) write files and read them back with the project's own reader, then run the structural
  validator.
- **`ostcli pst-validate`, `heap-check`, `pst-dump`**: command-line inspection of structure, heap integrity and properties.
- **Outlook round trip** (`tools/verify-pst.ps1`, `tools/verify-msg.ps1`): opens a *copy* of the generated file in desktop
  Outlook through COM and compares subject, recipients, body and attachment bytes with the source, reporting counts and
  hashes only so that no message text is ever printed. Windows with desktop Outlook is required.
- When Outlook rejects a file it logs the exact reason to the Windows Application event log (source *Outlook*,
  event 2000, "Database corruption..."), which is the quickest debugging aid.

If you are reading this to build your own writer: write many small files and one large one, test with a real Outlook, and
read its event log. Passing a structural checker is not enough.
