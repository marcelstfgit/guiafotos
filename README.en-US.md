# GuiaFotos

[Português (Brasil)](README.md) | **English (US)**

A photo organizer for Windows, inspired by Picasa's simplicity, focused on finding people and grouping versions of the same photo.

**Under development · 0.2.1 preview build.**

[Downloads on GitHub Releases](https://github.com/marcelstfgit/guiafotos/releases) · Windows x64 installer and portable package.

## Why it exists

Picasa made organizing photos on a computer approachable: browsing folders, rediscovering memories, and making simple edits were part of one experience. For people who cared for a personal collection, that convenience is still missed.

In 2016, Google [announced Picasa's retirement to focus on Google Photos](https://googlephotos.blogspot.com/2016/02/moving-on-from-picasa.html). GuiaFotos began with that experience as its inspiration, aiming to combine local organization with current face recognition, image comparison, and backup tools.

## What's implemented

- **People:** five selectable recognition models, SCRFD/YuNet detection, small-face search, and grouping with names, merging, splitting, and undo.
- **Photo versions:** comparison across lighting, compression, and resolution changes, side-by-side viewing, and preferred-copy selection.
- **Duplicates:** fast filtering by file size and 16 KB samples using xxHash64; candidate matches are confirmed by a full byte comparison.
- **Local library:** folder and album sidebar, thumbnail grid with zoom, search, and multiple selection.
- **Integrated editor:** double-click to open, large photo view, navigation, visual cropping, light/color adjustments, undo/redo, and exported copies.
- **Nextcloud:** backup snapshots and restoration with integrity checks.
- **Distribution:** scripts to build an `.exe` installer and a portable version for Windows x64.

Face models are downloaded when you activate each engine; face and similarity analysis runs locally. Analysis never deletes photos automatically.

## Current status

Local tests cover grouping, caching, persistence, and image variants. When a package passes CI, its report records the completed Windows checks and the tested commit. Automated checks and smoke tests do not replace evaluating the interface in real use, measuring face recognition accuracy on representative photos, or testing a production Nextcloud server.

SSCD is optional: its inclusion and export parity check are recorded in the package report. Numerical parity does not measure accuracy on photos. Detailed usage notes and limitations are available in [INSTRUCOES.txt](INSTRUCOES.txt) and [LEIA-ME-PACOTES.txt](LEIA-ME-PACOTES.txt), in Portuguese.

## Build

On Windows, install .NET SDK 8, Python 3.11+, 7-Zip, and MSYS2 MINGW64. The first build needs internet access. Prepare FFmpeg with `bash scripts/build-ffmpeg.sh` in MSYS2, following [CONTRIBUTING.en-US.md](CONTRIBUTING.en-US.md), then run in PowerShell:

```powershell
.\build-windows.ps1
```

The executable, installer, and portable ZIP are generated in `artifacts/`. The [Windows workflow](.github/workflows/windows.yml) runs the build and automated checks; publishing a release requires all checks to pass.

## Contribute

Want to help turn this idea into a reliable photo organizer? Programming contributions are welcome, especially in face recognition, similar-photo grouping, the Windows interface, and testing with real collections.

Open an [issue](https://github.com/marcelstfgit/guiafotos/issues) or submit a pull request. See [CONTRIBUTING.en-US.md](CONTRIBUTING.en-US.md) to get started.

## License

GuiaFotos code is licensed under MIT. Dependencies and models have their own terms, documented in [THIRD-PARTY.txt](THIRD-PARTY.txt) and `licenses/`.
