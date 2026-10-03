# Webcent OST Converter engine

The conversion engine and developer tools behind [Webcent OST Converter Free](../README.md).

| Path | What it is |
| --- | --- |
| `src/OstConverter.Core` | The engine: OST/PST reader, exporters (PST, EML, MSG, MBOX, PDF, HTML, CSV), PST writer, conversion pipeline. No UI, no account code. |
| `src/OstConverter.Cli` | Developer command-line tool: inspect and validate files, convert, write a demo mailbox. |
| `tests/OstConverter.Tests` | xUnit tests; all input files are generated. |
| `tools/` | Icon generation and the Outlook validation scripts. |

```powershell
dotnet test OstConverter.Engine.sln -c Release
dotnet run --project src\OstConverter.Cli
```

Documentation for the whole project is in [../docs](../docs): [user guide](../docs/USER-GUIDE.md),
[architecture](../docs/ARCHITECTURE.md), [building](../docs/BUILDING.md) and [PST writer notes](../docs/PST-WRITER.md).

Copyright (c) 2026 Webcent Solutions. All rights reserved; see [../LICENSE](../LICENSE).
