$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)
$install = Join-Path $env:LOCALAPPDATA "Programs/GuiaFotos"
if (Test-Path $install) { throw "Já existe uma instalação. Execute em usuário/runner de teste limpo." }
function Run-Checked([string]$Executable, [string]$Argument) {
    $process = Start-Process $Executable -ArgumentList $Argument -PassThru
    if (!$process.WaitForExit(120000)) { $process.Kill(); throw "Timeout: $Executable" }
    if ($process.ExitCode -ne 0) { throw "Falha ($($process.ExitCode)): $Executable" }
}
$dataRoot = Join-Path $env:LOCALAPPDATA "GuiaFotos"
$sentinel = Join-Path $dataRoot ("installer-test-" + [guid]::NewGuid().ToString("N") + ".txt")
$portable = Join-Path $env:TEMP ("GuiaFotos-portable-test-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $dataRoot | Out-Null
Set-Content $sentinel "preservar catálogo"
try {
    Run-Checked (Resolve-Path "artifacts/GuiaFotos-0.2.0-Setup.exe").Path "--install-silent"
    if (!(Test-Path (Join-Path $install "GuiaFotos.exe"))) { throw "EXE instalado ausente" }
    if (!(Test-Path "HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/GuiaFotos")) { throw "Registro de desinstalação ausente" }
    $menu = Join-Path ([Environment]::GetFolderPath("StartMenu")) "Programs/GuiaFotos"
    if (!(Test-Path (Join-Path $menu "GuiaFotos.lnk")) -or !(Test-Path (Join-Path $menu "Desinstalar GuiaFotos.lnk"))) { throw "Atalhos no menu Iniciar ausentes" }
    if (Test-Path (Join-Path $install "portable.marker")) { throw "Instalação não deve estar no modo portátil" }
    Run-Checked (Join-Path $install "GuiaFotos.exe") "--smoke-test"
    $unknown = Join-Path $install "arquivo-do-usuario.txt"
    Set-Content $unknown "não foi instalado pelo GuiaFotos"
    Run-Checked (Join-Path $install "Desinstalar.exe") "--uninstall-silent"
    $limit = (Get-Date).AddSeconds(120)
    while (((Test-Path (Join-Path $install ".guiafotos-install.json")) -or (Test-Path "HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/GuiaFotos")) -and (Get-Date) -lt $limit) { Start-Sleep -Milliseconds 300 }
    if (Test-Path (Join-Path $install ".guiafotos-install.json")) { throw "Desinstalação não terminou" }
    if ((Test-Path (Join-Path $install "GuiaFotos.exe")) -or !(Test-Path $unknown) -or !(Test-Path $sentinel)) { throw "Desinstalação não preservou os limites de remoção" }
    if (Test-Path "HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/GuiaFotos") { throw "Registro não removido" }
    if ((Test-Path (Join-Path $menu "GuiaFotos.lnk")) -or (Test-Path (Join-Path $menu "Desinstalar GuiaFotos.lnk"))) { throw "Atalhos não removidos" }
    Remove-Item $unknown
    if (!(Get-ChildItem $install -Force)) { Remove-Item $install }
    Expand-Archive "artifacts/GuiaFotos-0.2.0-portatil-windows-x64.zip" $portable
    Run-Checked (Join-Path $portable "GuiaFotos/GuiaFotos.exe") "--smoke-test"
    if (!(Test-Path (Join-Path $portable "GuiaFotos/Data/catalog.sqlite"))) { throw "Portátil não criou catálogo em Data" }
    Write-Output "PASS instalação, atalhos/registro, app instalado, desinstalação e dados preservados; portátil inicia com Data local."
} finally {
    Remove-Item -Force $sentinel -ErrorAction SilentlyContinue
    if (Test-Path $portable) { Remove-Item -Recurse -Force $portable }
}
