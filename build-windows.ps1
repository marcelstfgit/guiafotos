param([string]$Output = "artifacts/windows-x64", [switch]$SkipPackages, [switch]$WithSscd)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$downloadDir = Join-Path $PSScriptRoot "artifacts/downloads"
$nativeDir = Join-Path $PSScriptRoot "tools/windows"
New-Item -ItemType Directory -Force $downloadDir, $nativeDir | Out-Null
function Get-VerifiedDownload([string]$Destination, [string[]]$Urls, [string]$Hash) {
    if ((Test-Path $Destination) -and (Get-FileHash $Destination -Algorithm SHA256).Hash.ToLowerInvariant() -eq $Hash) { return }
    $failures = @()
    foreach ($url in $Urls) {
        $temporary = "$Destination.$([guid]::NewGuid().ToString('N')).part"
        try {
            Write-Host "Baixando $(Split-Path $Destination -Leaf): $url"
            Invoke-WebRequest -Uri $url -OutFile $temporary -TimeoutSec 180 -MaximumRetryCount 2 -RetryIntervalSec 3 -UserAgent "GuiaFotos-build/0.2.0"
            if ((Get-FileHash $temporary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Hash) { throw "Checksum inválido: $(Split-Path $Destination -Leaf)" }
            Move-Item -Force $temporary $Destination
            return
        } catch {
            $failures += "$url : $($_.Exception.Message)"
            Write-Warning $failures[-1]
        } finally { Remove-Item -Force $temporary -ErrorAction SilentlyContinue }
    }
    throw "Download verificado falhou: $($failures -join ' | ')"
}
$archives = @(
    @{ Name="exiftool-13.59_64.zip"; Urls=@("https://downloads.sourceforge.net/project/exiftool/exiftool-13.59_64.zip", "https://sourceforge.net/projects/exiftool/files/exiftool-13.59_64.zip/download"); Hash="44b512b25af500724ba579d0a53c8fc5851628b692dd5e5d94ae4a15c2cba9ec" },
    @{ Name="imagemagick-7.1.2-33-x64.7z"; Urls=@("https://github.com/ImageMagick/ImageMagick/releases/download/7.1.2-33/ImageMagick-7.1.2-33-portable-Q16-x64.7z"); Hash="5c242a23c4343add3f3bbdf265e74baa28d97d565a7b2a7c2827a4c3c449c278" },
    @{ Name="ImageMagick-7.1.2-33-Windows.7z"; Urls=@("https://github.com/ImageMagick/ImageMagick/releases/download/7.1.2-33/ImageMagick-7.1.2-33-Windows.7z"); Hash="cccc7ba9e099d634d266dbe4d9d86443e39702abeaf77367cc01374a8ece91a3" }
)
foreach ($archive in $archives) {
    $destination = Join-Path $downloadDir $archive.Name
    Get-VerifiedDownload $destination $archive.Urls $archive.Hash
}
Expand-Archive -Force (Join-Path $downloadDir "exiftool-13.59_64.zip") (Join-Path $downloadDir "exiftool-unpacked")
Copy-Item -Recurse -Force (Join-Path $downloadDir "exiftool-unpacked/exiftool-13.59_64/exiftool_files") $nativeDir
Copy-Item -Force (Join-Path $downloadDir "exiftool-unpacked/exiftool-13.59_64/exiftool(-k).exe") (Join-Path $nativeDir "exiftool.exe")
$sevenZip = Get-Command 7z -ErrorAction SilentlyContinue
if ($sevenZip) { & $sevenZip.Source x -y "-o$nativeDir" (Join-Path $downloadDir "imagemagick-7.1.2-33-x64.7z") | Out-Null }
elseif (Test-Path "C:/Program Files/7-Zip/7z.exe") { & "C:/Program Files/7-Zip/7z.exe" x -y "-o$nativeDir" (Join-Path $downloadDir "imagemagick-7.1.2-33-x64.7z") | Out-Null }
else { & tar -xf (Join-Path $downloadDir "imagemagick-7.1.2-33-x64.7z") -C $nativeDir }
if ($LASTEXITCODE -ne 0) { throw "Instale 7-Zip para extrair ImageMagick." }
$ffmpegManifest = Join-Path $nativeDir "ffmpeg-build.json"
if (!(Test-Path $ffmpegManifest)) { throw "Compile FFmpeg primeiro: bash scripts/build-ffmpeg.sh em MSYS2 MINGW64. Consulte CONTRIBUTING.md." }
$ffmpegBuild = Get-Content -Raw $ffmpegManifest | ConvertFrom-Json
if ($ffmpegBuild.FfmpegCommit -ne "38b88335f9" -or $ffmpegBuild.X264Commit -ne "31e19f92f00c7003fa115047ce50978bc98c3a0d" -or $ffmpegBuild.Dav1dVersion -ne "1.5.4") { throw "Versões do FFmpeg/x264/dav1d diferentes das fontes fixadas." }
foreach ($binary in $ffmpegBuild.Binaries.PSObject.Properties) {
    if ((Get-FileHash (Join-Path $nativeDir $binary.Name) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $binary.Value) { throw "FFmpeg adulterado: $($binary.Name)" }
}
$ffmpegSources = Join-Path $PSScriptRoot "artifacts/$($ffmpegBuild.SourceArchive)"
if (!(Test-Path $ffmpegSources) -or (Get-FileHash $ffmpegSources -Algorithm SHA256).Hash.ToLowerInvariant() -ne $ffmpegBuild.SourceSha256) { throw "Fontes correspondentes do FFmpeg ausentes ou alterados." }
# CPU facial runtime, pinned to the same official npm distribution used in this build.
$faceDir = Join-Path $nativeDir "faces"
New-Item -ItemType Directory -Force $faceDir | Out-Null
$faceDll = Join-Path $faceDir "onnxruntime.dll"
if (!(Test-Path $faceDll)) {
    $onnxArchive = Join-Path $downloadDir "onnxruntime-node-1.27.0.tgz"
    if (!(Test-Path $onnxArchive)) { Invoke-WebRequest "https://registry.npmjs.org/onnxruntime-node/-/onnxruntime-node-1.27.0.tgz" -OutFile $onnxArchive }
    $sha = [Security.Cryptography.SHA512]::Create()
    $stream = [IO.File]::OpenRead($onnxArchive)
    try { $integrity = [Convert]::ToBase64String($sha.ComputeHash($stream)) } finally { $stream.Dispose(); $sha.Dispose() }
    if ($integrity -ne "QEzGwrvNBgv4uPVdnbHsOGG4G6T96mdlcFI8aAKPjMU8wOPpVocPXb6k3QGkaZagVTv2G9Bnnbo6Z3JdXr1fQw==") { throw "Integridade do runtime ONNX não confere." }
    $onnxUnpacked = Join-Path $downloadDir "onnx-unpacked"
    New-Item -ItemType Directory -Force $onnxUnpacked | Out-Null
    & tar -xf $onnxArchive -C $onnxUnpacked
    if ($LASTEXITCODE -ne 0) { throw "Falha ao extrair ONNX Runtime." }
    Copy-Item (Join-Path $onnxUnpacked "package/bin/napi-v6/win32/x64/onnxruntime.dll") $faceDll
}
if ((Get-FileHash $faceDll -Algorithm SHA256).Hash.ToLowerInvariant() -ne "273f9ef9cf755c6a0f342226a80448156c10ceb74db2ae8c8370dc98e75af0eb") { throw "DLL ONNX diferente da versão fixada." }
$faceLicenses = @{
    "onnxruntime" = "https://raw.githubusercontent.com/microsoft/onnxruntime/main/LICENSE"
    "yunet" = "https://raw.githubusercontent.com/opencv/opencv_zoo/main/models/face_detection_yunet/LICENSE"
    "uniface" = "https://raw.githubusercontent.com/yakhyo/uniface/main/LICENSE"
    "adaface" = "https://raw.githubusercontent.com/mk-minchul/AdaFace/master/LICENSE"
    "adaface-export" = "https://raw.githubusercontent.com/yakhyo/adaface-onnx/main/LICENSE"
    "edgeface" = "https://raw.githubusercontent.com/otroshi/edgeface/main/LICENSE"
    "edgeface-export" = "https://raw.githubusercontent.com/yakhyo/edgeface-onnx/main/LICENSE"
    "sface" = "https://raw.githubusercontent.com/opencv/opencv_zoo/main/models/face_recognition_sface/LICENSE"
}
foreach ($entry in $faceLicenses.GetEnumerator()) {
    $licenseFile = Join-Path $faceDir "$($entry.Key)-LICENSE.txt"
    Copy-Item -Force (Join-Path $PSScriptRoot "licenses/recognition/$($entry.Key)-LICENSE.txt") $licenseFile
}
if ($WithSscd) {
    $sscdDir = Join-Path $nativeDir "sscd"
    New-Item -ItemType Directory -Force $sscdDir | Out-Null
    python scripts/export-sscd.py --output $sscdDir
    if ($LASTEXITCODE -ne 0) { throw "Exportação/paridade SSCD falhou; o modelo não pode ser empacotado." }
    Invoke-WebRequest "https://raw.githubusercontent.com/facebookresearch/sscd-copy-detection/main/LICENSE" -OutFile (Join-Path $sscdDir "LICENSE.txt")
}
$outputPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Output))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "artifacts"))
if (!$outputPath.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Output deve ficar dentro de artifacts." }
$stage = Join-Path $artifactsRoot ("publish-" + [guid]::NewGuid().ToString("N"))
try {
    dotnet publish src/GuiaFotos.Windows/GuiaFotos.Windows.csproj -c Release -r win-x64 --self-contained true -o $stage
    if ($LASTEXITCODE -ne 0) { throw "Compilação falhou." }
    $stageTools = Join-Path $stage "tools"
    New-Item -ItemType Directory -Force $stageTools | Out-Null
    Get-ChildItem $nativeDir | Where-Object { $WithSscd -or $_.Name -ne "sscd" } | Copy-Item -Recurse -Force -Destination $stageTools
    Copy-Item INSTRUCOES.txt, LICENSE.txt, THIRD-PARTY.txt, LEIA-ME-PACOTES.txt, REVISAO-SENTINELA.txt $stage
    # NuGet runtime packs contain notices that dotnet publish does not always copy.
    $packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget/packages" }
    $runtimeNotices = @{
        "core" = "microsoft.netcore.app.runtime.win-x64"
        "desktop" = "microsoft.windowsdesktop.app.runtime.win-x64"
    }
    $licenseOutput = Join-Path $stage "licenses/dotnet"
    New-Item -ItemType Directory -Force $licenseOutput | Out-Null
    foreach ($entry in $runtimeNotices.GetEnumerator()) {
        $pack = Get-ChildItem (Join-Path $packageRoot $entry.Value) -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
        if (!$pack) { throw "Runtime pack ausente: $($entry.Value)" }
        foreach ($notice in @("LICENSE.txt", "THIRD-PARTY-NOTICES.txt")) {
            $noticeFile = Get-ChildItem $pack.FullName -File | Where-Object {
                $_.Name -ieq $notice -or ($notice -eq "LICENSE.txt" -and $_.Name -ieq "LICENSE")
            } | Select-Object -First 1
            if ($noticeFile) { Copy-Item $noticeFile.FullName (Join-Path $licenseOutput "$($entry.Key)-$notice") }
            elseif ($notice -eq "LICENSE.txt") { throw "Licença não encontrada: $($entry.Value)" }
        }
    }
    $manifest = Join-Path $stage "SHA256SUMS.txt"
    Get-ChildItem -Recurse -File $stage | Where-Object { $_.FullName -ne $manifest } | Sort-Object FullName | ForEach-Object {
        "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.FullName.Substring($stage.Length + 1).Replace('\', '/'))"
    } | Set-Content -Encoding UTF8 $manifest
    if (Test-Path $outputPath) { Remove-Item -Recurse -Force $outputPath }
    Move-Item $stage $outputPath
} finally { if (Test-Path $stage) { Remove-Item -Recurse -Force $stage } }
Write-Output "Publicação pronta: $outputPath/GuiaFotos.exe"
if (!$SkipPackages) {
    dotnet publish installer/GuiaFotos.Setup.csproj -c Release -r win-x64 --self-contained true -o artifacts/setup-stub
    if ($LASTEXITCODE -ne 0) { throw "Compilação do instalador falhou." }
    python package-windows.py --publish $outputPath
    if ($LASTEXITCODE -ne 0) { throw "Empacotamento falhou. Instale Python 3.11 ou superior." }
}
