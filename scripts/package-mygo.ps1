$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskVersion = (Get-Content -LiteralPath (Join-Path $taskRoot 'package.json') -Raw | ConvertFrom-Json).version
$taskBuild = Join-Path $taskRoot 'mygo/build'
$taskRelease = Join-Path $taskBuild "LeagueAkari-MyGo-$taskVersion-win-x64"
$taskZip = "$taskRelease.zip"
$taskFiles = @('LeagueAkari-MyGo.exe', 'LICENSE.txt', 'LeagueAkari-LICENSE.txt', 'MyGo-LICENSE.txt', 'THIRD_PARTY_NOTICES.md', 'README.md')
New-Item -ItemType Directory -Path $taskRelease -Force | Out-Null
foreach ($taskFile in $taskFiles) {
    Copy-Item -LiteralPath (Join-Path $taskBuild $taskFile) -Destination (Join-Path $taskRelease $taskFile) -Force
}
Compress-Archive -LiteralPath ($taskFiles | ForEach-Object { Join-Path $taskRelease $_ }) -DestinationPath $taskZip -Force
Write-Output "Packaged: $taskZip"
