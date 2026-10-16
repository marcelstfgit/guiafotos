# Contributing to GuiaFotos

[Português (Brasil)](CONTRIBUTING.md) | **English (US)**

Programming contributions, fixes, and testing are welcome. Start with a short issue describing the problem or proposed improvement.

## Where to work

- `src/GuiaFotos.Core`: catalog, recognition, grouping, comparison, and backup.
- `src/GuiaFotos.Windows`: WPF interface.
- `installer/`: installation, updates, and uninstallation.
- `tests/`: tests and the face recognition calibration tool.

Priorities: validate the app on Windows, improve accuracy on real collections, and make reviewing groups easier.

## Development

Use .NET SDK 8, Python 3.11+, 7-Zip, and MSYS2 MINGW64 on Windows. In the MINGW64 terminal, install the tools and build FFmpeg:

```bash
pacman -S --needed make nasm diffutils curl mingw-w64-x86_64-gcc mingw-w64-x86_64-pkgconf mingw-w64-x86_64-python
bash scripts/build-ffmpeg.sh
```

Then run `./build-windows.ps1` in PowerShell. Tools are placed in `tools/windows/`; corresponding FFmpeg/x264 sources are generated in `artifacts/GuiaFotos-0.2.0-fontes-dependencias.zip`.

```powershell
$env:GUIAFOTOS_TOOLS_ROOT = (Resolve-Path artifacts/windows-x64).Path
dotnet run --project tests/GuiaFotos.Tests -c Release
dotnet run --project tests/GuiaFotos.AdvancedTests -c Release
dotnet run --project tests/GuiaFotos.GroupingTests -c Release
dotnet run --project installer/tests/Setup.Tests.csproj -c Release
```

The workflow includes additional interface, model, and installation checks. To evaluate face recognition accuracy, use `tests/GuiaFotos.FaceCorpus` with labeled images kept separate for calibration and evaluation.

## Pull requests

Describe the problem, the change, and the tests you ran. Keep each PR focused and state what has not been validated yet. Visual changes can include screenshots.

Preserve original files and user corrections. Do not commit personal photos, catalogs, credentials, models, or executables; use synthetic data or authorized material in tests.
