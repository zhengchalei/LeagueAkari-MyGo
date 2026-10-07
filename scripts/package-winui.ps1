param([string]$Version = '', [string]$BuildDirectory = 'winui/build')

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $Version) { $Version = (Get-Content -LiteralPath (Join-Path $taskRoot 'package.json') -Raw | ConvertFrom-Json).version }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'Invalid package version' }
$taskBuild = [IO.Path]::GetFullPath((Join-Path $taskRoot $BuildDirectory))
$taskAllowed = [IO.Path]::GetFullPath((Join-Path $taskRoot 'winui/build'))
if ($taskBuild -ne $taskAllowed -and -not $taskBuild.StartsWith($taskAllowed + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'WinUI output must stay inside winui/build' }
$taskRelease = Join-Path $taskBuild "LeagueAkari-WinUI-$Version-win-x64"
$taskZip = "$taskRelease.zip"
if (Test-Path -LiteralPath $taskRelease) { Remove-Item -LiteralPath $taskRelease -Recurse -Force }
New-Item -ItemType Directory -Path $taskRelease -Force | Out-Null

Push-Location $taskRoot
try {
    & dotnet publish winui/LeagueAkari.WinUI/LeagueAkari.WinUI.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $taskRelease
    if ($LASTEXITCODE -ne 0) { throw 'WinUI publish failed' }
    # The unpackaged WinUI publish target omits the app PRI/XBF despite copying
    # the framework resources. Without them App.InitializeComponent fail-fasts.
    $taskCompiled = Join-Path $taskRoot 'winui/LeagueAkari.WinUI/bin/x64/Release/net10.0-windows10.0.22621.0/win-x64'
    Copy-Item -LiteralPath (Join-Path $taskCompiled 'LeagueAkari.WinUI.pri') -Destination $taskRelease -Force
    foreach ($taskXbf in (Get-ChildItem -LiteralPath $taskCompiled -Recurse -Filter '*.xbf' -File)) {
        $taskRelative = $taskXbf.FullName.Substring($taskCompiled.Length).TrimStart('\', '/')
        $taskTarget = Join-Path $taskRelease $taskRelative
        New-Item -ItemType Directory -Path (Split-Path -Parent $taskTarget) -Force | Out-Null
        Copy-Item -LiteralPath $taskXbf.FullName -Destination $taskTarget -Force
    }
    Push-Location (Join-Path $taskRoot 'mygo')
    try {
        & go test -tags winui_backend ./...
        if ($LASTEXITCODE -ne 0) { throw 'WinUI Go backend tests failed' }
        & go build -tags winui_backend -trimpath '-ldflags=-s -w -H=windowsgui' -o (Join-Path $taskRelease 'LeagueAkari.Backend.exe') .
        if ($LASTEXITCODE -ne 0) { throw 'Go backend build failed' }
        $taskMyGoPath = & go list -m -f '{{.Dir}}' github.com/egoist/mygo
        if ($LASTEXITCODE -ne 0) { throw 'Cannot locate MyGo license' }
        Copy-Item -LiteralPath (Join-Path $taskMyGoPath.Trim() 'LICENSE') -Destination (Join-Path $taskRelease 'MyGo-LICENSE.txt') -Force
    } finally { Pop-Location }
    Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE') -Destination (Join-Path $taskRelease 'LICENSE.txt') -Force
    Copy-Item -LiteralPath (Join-Path $taskRoot 'desktop/LICENSE') -Destination (Join-Path $taskRelease 'LeagueAkari-LICENSE.txt') -Force
    Copy-Item -LiteralPath (Join-Path $taskRoot 'THIRD_PARTY_NOTICES.md') -Destination $taskRelease -Force
    Copy-Item -LiteralPath (Join-Path $taskRoot 'winui/README.md') -Destination $taskRelease -Force
    [IO.File]::WriteAllText((Join-Path $taskRelease 'league-akari-winui.portable'), 'LeagueAkari-WinUI portable v1')
    New-Item -ItemType Directory -Path (Join-Path $taskRelease 'docs') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/WINUI3_PARITY.md') -Destination (Join-Path $taskRelease 'docs') -Force
    $taskAssets = Get-Content -LiteralPath (Join-Path $taskRoot 'winui/LeagueAkari.WinUI/obj/project.assets.json') -Raw | ConvertFrom-Json
    $taskPackageRoot = $taskAssets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
    $taskLicenseRoot = Join-Path $taskRelease 'licenses'
    New-Item -ItemType Directory -Path $taskLicenseRoot -Force | Out-Null
    foreach ($taskLibrary in $taskAssets.libraries.PSObject.Properties) {
        if ($taskLibrary.Value.type -ne 'package') { continue }
        $taskPackagePath = Join-Path $taskPackageRoot $taskLibrary.Value.path
        $taskLicenseFiles = Get-ChildItem -LiteralPath $taskPackagePath -File | Where-Object { $_.Name -match '^(license|notice|third-party-notices)' }
        if ($taskLicenseFiles) {
            $taskLicenseDirectory = Join-Path $taskLicenseRoot ($taskLibrary.Name.Replace('/', '-'))
            New-Item -ItemType Directory -Path $taskLicenseDirectory -Force | Out-Null
            foreach ($taskLicenseFile in $taskLicenseFiles) { Copy-Item -LiteralPath $taskLicenseFile.FullName -Destination $taskLicenseDirectory -Force }
        }
    }
    $taskRuntimeConfig = Get-Content -LiteralPath (Join-Path $taskRelease 'LeagueAkari.WinUI.runtimeconfig.json') -Raw | ConvertFrom-Json
    $taskRuntimeVersion = ($taskRuntimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }).version
    $taskRuntimePackage = Join-Path $taskPackageRoot "microsoft.netcore.app.runtime.win-x64/$taskRuntimeVersion"
    foreach ($taskNotice in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) { Copy-Item -LiteralPath (Join-Path $taskRuntimePackage $taskNotice) -Destination (Join-Path $taskLicenseRoot "NET-$taskNotice") -Force }
    & dotnet run --project winui/tests/HistoryFilterTests/HistoryFilterTests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'History filter tests failed' }
    & dotnet run --project winui/tests/NativeDataContracts/NativeDataContracts.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Native data contract tests failed' }
    foreach ($taskSuite in @('PlayerProfileTests', 'PlayerDataSourceTests', 'HistoryLoadingTests', 'ChampionConfigTests', 'AutomationMiscTests', 'ToolkitSendFriendsTests', 'ToolkitPresetTests', 'EncounterTests', 'LocalizationCheck', 'DebugRouteTests', 'ProfileBackgroundTests', 'GameLookupTests', 'OpggSessionTests', 'OpggDataTests', 'SettingsStorageTests', 'CDTimerRosterTests', 'OngoingCardTests', 'MainRouteTests', 'GlobalSearchTests', 'ToolkitClientLobbyTests', 'HistorySummaryTests', 'HistoryJungleTests', 'NativeMatchThemeTests', 'NativeShellTests', 'ConnectionDataTests', 'PlayerTabsTests', 'HostNotificationTests', 'ApplicationSettingsTests', 'RewardDataTests', 'ToolkitProcessTests')) {
        & dotnet run --project "winui/tests/$taskSuite/$taskSuite.csproj" -c Release
        if ($LASTEXITCODE -ne 0) { throw "$taskSuite tests failed" }
    }
} finally { Pop-Location }

$taskRequired = @('LeagueAkari.WinUI.exe', 'LeagueAkari.Backend.exe', 'LeagueAkari.WinUI.dll', 'LeagueAkari.WinUI.deps.json', 'LeagueAkari.WinUI.runtimeconfig.json', 'LeagueAkari.WinUI.pri', 'App.xbf', 'coreclr.dll', 'hostfxr.dll', 'Microsoft.UI.Xaml.dll', 'Microsoft.WindowsAppRuntime.dll', 'league-akari-winui.portable')
foreach ($taskFile in $taskRequired) { if (-not (Test-Path -LiteralPath (Join-Path $taskRelease $taskFile) -PathType Leaf)) { throw "Missing portable runtime: $taskFile" } }
Compress-Archive -Path (Join-Path $taskRelease '*') -DestinationPath $taskZip -Force
Write-Output "Local acceptance package (not published): $taskZip"
