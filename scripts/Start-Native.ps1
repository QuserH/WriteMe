$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifest = Join-Path $projectRoot 'artifacts/native/latest.json'
$executable = Join-Path $projectRoot 'artifacts/native/win-x64/WriteME.Native.exe'
if (Test-Path -LiteralPath $manifest) {
    $release = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    if ($release.executable -and (Test-Path -LiteralPath $release.executable)) { $executable = $release.executable }
}
if (-not (Test-Path -LiteralPath $executable)) { throw '尚未生成原生版，请先运行 npm run native:release。' }
Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable)
