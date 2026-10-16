# Contribuir com o GuiaFotos

**Português (Brasil)** | [English (US)](CONTRIBUTING.en-US.md)

Contribuições de programação, correções e testes são bem-vindos. Comece por uma issue curta com o problema ou a melhoria proposta.

## Onde trabalhar

- `src/GuiaFotos.Core`: catálogo, reconhecimento, agrupamento, comparação e backup.
- `src/GuiaFotos.Windows`: interface WPF.
- `installer/`: instalação, atualização e desinstalação.
- `tests/`: testes e ferramenta de calibração facial.

Prioridades: validar o aplicativo no Windows, melhorar a precisão em coleções reais e facilitar a revisão dos agrupamentos.

## Desenvolvimento

Use .NET SDK 8, Python 3.11+, 7-Zip e MSYS2 MINGW64 no Windows. No terminal MINGW64, instale as ferramentas e compile FFmpeg:

```bash
pacman -S --needed make nasm diffutils curl mingw-w64-x86_64-gcc mingw-w64-x86_64-pkgconf mingw-w64-x86_64-python
bash scripts/build-ffmpeg.sh
```

Depois, no PowerShell, execute `./build-windows.ps1`. As ferramentas ficam em `tools/windows/`; os fontes correspondentes do FFmpeg/x264 são gerados em `artifacts/GuiaFotos-0.2.0-fontes-dependencias.zip`.

```powershell
$env:GUIAFOTOS_TOOLS_ROOT = (Resolve-Path artifacts/windows-x64).Path
dotnet run --project tests/GuiaFotos.Tests -c Release
dotnet run --project tests/GuiaFotos.AdvancedTests -c Release
dotnet run --project tests/GuiaFotos.GroupingTests -c Release
dotnet run --project installer/tests/Setup.Tests.csproj -c Release
```

O workflow inclui verificações adicionais de interface, modelos e instalação. Para avaliar precisão facial, use `tests/GuiaFotos.FaceCorpus` com imagens rotuladas e separadas entre calibração e avaliação.

## Pull requests

Descreva o problema, a mudança e os testes executados. Mantenha cada PR focado e informe o que ainda não foi validado. Alterações visuais podem incluir capturas de tela.

Preserve os originais e as correções feitas pelo usuário. Não inclua fotos pessoais, catálogos, credenciais, modelos ou executáveis no Git; use dados sintéticos ou material autorizado em testes.
