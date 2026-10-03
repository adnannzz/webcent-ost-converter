# Contributing

Thank you for taking an interest in Webcent OST Converter.

The source is published under a [source-available licence](LICENSE): you can read it, build it and review it, but it is not
open-source software. Within that, here is how to help.

## Reporting problems and ideas

Open an issue. The templates ask for what we need: the app version, Windows version, the format you converted to and
what you saw. Please include the conversion log or the error report from `%LOCALAPPDATA%\OST Converter\logs` when relevant.

**Never attach real OST or PST files, or text copied from real mail.** If a particular file structure triggers a bug, describe
it, or reproduce it with invented data (`ostcli demo`, see [docs/BUILDING.md](docs/BUILDING.md)).

Security problems go to <Info@webcents.in>, not to a public issue: see [SECURITY.md](SECURITY.md).

## Code contributions

1. Open an issue first and describe the change, so we can agree it fits before you spend time on it.
2. Fork, create a branch, make the change, and keep it focused on one thing.
3. Build and run all tests; both must pass:
   ```powershell
   dotnet test "OST Converter\OstConverter.sln" -c Release
   dotnet test "OST Converter Free\OstConverterFree.sln" -c Release
   ```
4. Add or update tests for behaviour you change. Tests must use generated data only.
5. Follow the conventions in [docs/BUILDING.md](docs/BUILDING.md#conventions), and match the style of the surrounding code.
6. Open a pull request that explains what changed and why.

### Terms of contribution

Because the project is not open-source, contributions are accepted on these terms: by submitting a contribution you confirm
that you wrote it (or have the right to submit it), and you grant Webcent Solutions a perpetual, worldwide, royalty-free,
irrevocable licence to use, modify, sublicense and distribute it as part of the project, including in commercial products.
You keep the copyright in your contribution. If you are not comfortable with this, please contribute by reporting issues
instead.

### What we will not merge

- Anything that weakens privacy (uploading mail, adding telemetry) or the read-only handling of source files.
- Code that removes or bypasses licence checks or usage limits.
- Large reformatting or renaming unrelated to the change.
- Binary files, real OST/PST files, or any real personal data.
