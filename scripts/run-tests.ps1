param([Parameter(Mandatory)][string]$Project, [Parameter(Mandatory)][string]$Log)
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
New-Item -ItemType Directory -Force (Split-Path $Log -Parent) | Out-Null
dotnet run --project $Project -c Release 2>&1 | Tee-Object -FilePath $Log
$testExit = $LASTEXITCODE
if ($testExit -ne 0) {
    $details = (Get-Content $Log -Tail 80 | Out-String).Trim().Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
    Write-Host "::error title=${Project}::$details"
    exit $testExit
}
