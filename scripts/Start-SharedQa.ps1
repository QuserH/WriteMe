param([int]$Port = 8791, [string]$DataDirectory = '')
$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$serverOutput = Join-Path $projectRoot 'native/WriteMe.SyncServer/bin/Release/net10.0'
$webOutput = Join-Path $serverOutput 'wwwroot'
New-Item -ItemType Directory -Path $webOutput -Force | Out-Null
Copy-Item -Path (Join-Path $projectRoot 'dist/*') -Destination $webOutput -Recurse -Force
if ([string]::IsNullOrEmpty($DataDirectory)) { $DataDirectory = Join-Path $projectRoot ('artifacts/shared-qa/server-' + [Guid]::NewGuid().ToString('N')) }
$env:WRITEME_SYNC_DATA = $DataDirectory
$env:WRITEME_SETUP_USER = 'qa-admin'
$env:WRITEME_SETUP_PASSWORD = 'qa-only-test-password-2026'
$env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"
& dotnet (Join-Path $serverOutput 'WriteME.SyncServer.dll')
