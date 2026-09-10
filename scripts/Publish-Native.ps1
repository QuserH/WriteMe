param([string]$Runtime = 'win-x64', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $projectRoot 'native/WriteMe.Desktop/WriteMe.Desktop.csproj'
$outputPath = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $projectRoot "artifacts/native/$Runtime" } else { [System.IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory)) }
& dotnet publish $projectPath --configuration Release --runtime $Runtime --self-contained true --output $outputPath --nologo -p:RestoreLockedMode=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$executable = Join-Path $outputPath 'WriteME.Native.exe'
if ($Runtime -like 'win-*' -and (Test-Path -LiteralPath $executable)) {
    $manifestDirectory = Join-Path $projectRoot 'artifacts/native'
    New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
    @{ executable = $executable; runtime = $Runtime } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $manifestDirectory 'latest.json') -Encoding UTF8
}
Write-Output "原生版已生成：$outputPath"
Write-Output '分发时请保留此目录中的所有文件。运行不需要 WebView2、Node 或另装 .NET。'
