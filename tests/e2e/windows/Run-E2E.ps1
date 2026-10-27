<#
.SYNOPSIS
  End-to-end test of zexoro's NSIS installers: install, upgrade, downgrade refusal, running-app
  refusal, stage mode (/ZEXORO_STAGE, used by zexoro-updater) and uninstall, for the per-user and
  the per-machine scope. The installers are built with [updates]; the update round trip itself is
  tested by Run-UpdateE2E.ps1.

.DESCRIPTION
  Consumes the output of tests/e2e/pack.sh (any pack host). Runs in Windows PowerShell 5.1 and
  PowerShell 7 and needs administrator rights (per-machine installs, and trusting the throwaway
  signing certificate in LocalMachine\Root, which is removed again at the end).

  Each check prints PASS or FAIL; the script exits 1 when any check failed. It leaves no installs
  behind, also after a failure.

.PARAMETER InstallerDir
  Directory written by tests/e2e/pack.sh: user\, machine\, unsigned\, root.cer and zexoro-update.pub.

.PARAMETER Scope
  Scopes to test (default: both).

.EXAMPLE
  .\tests\e2e\windows\Run-E2E.ps1 -InstallerDir C:\temp\e2e
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallerDir,
    [ValidateSet('user', 'machine')][string[]]$Scope = @('user', 'machine'),
    [int]$TimeoutSeconds = 120
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'E2E.Common.ps1')
$StageRootVariable = 'ZEXORO_UPDATE_ROOT'

function Test-Installed($App, [string]$Version, [string]$RootSubject) {
    $appDir = Join-Path $App.Root 'app'
    $main = Join-Path $appDir 'FixtureApp.exe'
    Assert-That (Test-Path -LiteralPath $main) "$Version installed: $main exists"
    foreach ($file in 'FixtureApp.dll', 'FixtureApp.runtimeconfig.json', 'FixtureApp.deps.json', 'version.txt') {
        [void](Test-That (Test-Path -LiteralPath (Join-Path $appDir $file)) "app\$file exists")
    }
    Test-Equal (Get-Content -LiteralPath (Join-Path $appDir 'version.txt') -Raw).Trim() $Version 'app\version.txt has the installed version'
    [void](Test-That (Test-Path -LiteralPath (Join-Path $App.Root 'uninstall.exe')) 'uninstall.exe exists')
    [void](Test-That (-not (Test-Path -LiteralPath (Join-Path $App.Root 'app.old'))) 'no app.old left behind')

    $recordPath = Join-Path $App.Root 'zexoro-install.json'
    if (Test-That (Test-Path -LiteralPath $recordPath) 'zexoro-install.json exists') {
        $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
        $expected = [ordered]@{
            schema = 1; app = $App.Id; version = $Version; os = 'windows'; arch = 'x64'; format = 'nsis'
            scope = $App.Scope; strategy = $(if ($App.Scope -eq 'user') { 'replace' } else { 'reinstall' })
        }
        foreach ($name in $expected.Keys) { Test-Equal $record.$name $expected[$name] "zexoro-install.json $name" }
        Test-Equal $record.channel 'stable' 'zexoro-install.json channel'
        Test-Equal $record.feed "http://localhost:8080/$($App.Scope)/stable/feed.json" 'zexoro-install.json feed'
        Test-Equal (@($record.keys) -join ',') $script:UpdateKey 'zexoro-install.json keys'
        Test-Equal $record.main 'FixtureApp.exe' 'zexoro-install.json main'
    }
    [void](Test-That (Test-Path -LiteralPath (Join-Path $App.Root 'zexoro-updater.exe')) 'zexoro-updater.exe exists')
    $leftovers = @(Get-ChildItem -LiteralPath $App.Root -Force | Where-Object { $_.Name -match '\.(new|old)$' } | ForEach-Object { $_.Name })
    [void](Test-That ($leftovers.Count -eq 0) 'no .new or .old files in the install root' ($leftovers -join ', '))

    $entry = Get-UninstallEntry $App
    Assert-That ($null -ne $entry) "uninstall key $($App.Hive)\$UninstallPath\$($App.Id) exists"
    $uninstaller = Join-Path $App.Root 'uninstall.exe'
    Test-Equal $entry['DisplayName'] $App.Name 'DisplayName'
    Test-Equal $entry['DisplayVersion'] $Version 'DisplayVersion'
    Test-Equal $entry['Publisher'] 'zexoro e2e' 'Publisher'
    Test-Equal $entry['InstallLocation'] $App.Root 'InstallLocation'
    Test-Equal $entry['DisplayIcon'] "$uninstaller,0" 'DisplayIcon'
    Test-Equal $entry['UninstallString'] "`"$uninstaller`"" 'UninstallString'
    Test-Equal $entry['QuietUninstallString'] "`"$uninstaller`" /S" 'QuietUninstallString'
    Test-Equal $entry['NoModify'] 1 'NoModify'
    Test-Equal $entry['NoRepair'] 1 'NoRepair'
    [void](Test-That ($entry['ZexoroVersionOrder'] -match '^[0-9]+$') 'ZexoroVersionOrder is set' "got '$($entry['ZexoroVersionOrder'])'")
    [void](Test-That ([int]$entry['EstimatedSize'] -gt 0) 'EstimatedSize is positive' "got '$($entry['EstimatedSize'])'")
    $keys = @(Get-MatchingUninstallKeys $App)
    [void](Test-That ($keys.Count -eq 1) 'exactly one uninstall entry' ($keys -join ', '))

    $target = if (Test-Path -LiteralPath $App.Shortcut) { Get-ShortcutTarget $App.Shortcut } else { '' }
    Test-Equal $target $main "Start menu shortcut $($App.Shortcut) points to the main executable"

    # pack signs the bundled updater with the application's certificate too.
    if ($RootSubject) { Test-Signatures @($App.Installer[$Version], $uninstaller, $main, (Join-Path $App.Root 'zexoro-updater.exe')) $RootSubject }

    if (Test-Path -LiteralPath $DataDir) { Remove-Item -LiteralPath $DataDir -Recurse -Force }
    $code = Invoke-Program $main @()
    Test-Equal $code 0 'the installed app exits with 0'
    Test-Equal (Read-Marker) "$appDir\" 'the installed app ran (marker written)'
    return $entry
}

# Runs an installer in stage mode with ZEXORO_UPDATE_ROOT set to $Root ($null: unset), as zexoro-updater does.
function Invoke-Stage([string]$Installer, $Root) {
    $previous = [Environment]::GetEnvironmentVariable($StageRootVariable)
    [Environment]::SetEnvironmentVariable($StageRootVariable, $Root)
    try { return Invoke-Program $Installer @('/S', '/ZEXORO_STAGE') }
    finally { [Environment]::SetEnvironmentVariable($StageRootVariable, $previous) }
}

function Get-StagedItems($App) {
    return @(Get-ChildItem -LiteralPath $App.Root -Force | Where-Object { $_.Name -match '\.new$' } | ForEach-Object { $_.Name } | Sort-Object)
}

# Stage mode next to an installed 1.0.0: refused without or with a wrong root, then writes only app.new and the .new files.
function Test-Stage($App, $Before) {
    Write-Host '-- stage mode (/S /ZEXORO_STAGE)'
    $installer = $App.Installer['2.0.0']
    if ($App.Scope -eq 'machine') {
        Test-Equal (Invoke-Stage $installer $App.Root) 2 'a per-machine installer refuses stage mode (exit 2)'
        Test-Equal ((Get-StagedItems $App) -join ', ') '' 'nothing was staged'
        return
    }

    Test-Equal (Invoke-Stage $installer $null) 2 "stage mode without $StageRootVariable exits with 2"
    Test-Equal (Invoke-Stage $installer (Join-Path $env:TEMP 'not-the-install')) 2 "stage mode with another $StageRootVariable exits with 2"
    Test-Equal ((Get-StagedItems $App) -join ', ') '' 'nothing was staged by the refused runs'

    # Another case and a trailing backslash still name the install root.
    Test-Equal (Invoke-Stage $installer ($App.Root.ToUpperInvariant() + '\')) 0 'stage mode for the install root exits with 0'
    Test-Equal ((Get-StagedItems $App) -join ', ') 'app.new, uninstall.exe.new, zexoro-install.json.new, zexoro-updater.exe.new' 'stage mode wrote app.new and the .new root files'
    $staged = Join-Path $App.Root 'app.new'
    [void](Test-That (Test-Path -LiteralPath (Join-Path $staged 'FixtureApp.exe')) 'app.new\FixtureApp.exe exists')
    if (Test-Path -LiteralPath (Join-Path $staged 'version.txt')) {
        Test-Equal (Get-Content -LiteralPath (Join-Path $staged 'version.txt') -Raw).Trim() '2.0.0' 'app.new\version.txt is 2.0.0'
    } else { Write-Fail 'app.new\version.txt exists' }
    [void](Test-That (-not (Test-Path -LiteralPath (Join-Path $staged 'obsolete.txt'))) 'app.new has no obsolete.txt')
    $record = Get-Content -LiteralPath (Join-Path $App.Root 'zexoro-install.json.new') -Raw | ConvertFrom-Json
    Test-Equal $record.version '2.0.0' 'zexoro-install.json.new version'
    Test-Equal $record.main 'FixtureApp.exe' 'zexoro-install.json.new main'
    $current = Get-Content -LiteralPath (Join-Path $App.Root 'zexoro-install.json') -Raw | ConvertFrom-Json
    Test-Equal $current.version '1.0.0' 'zexoro-install.json is still 1.0.0'
    Test-Equal (Get-Content -LiteralPath (Join-Path $App.Root 'app\version.txt') -Raw).Trim() '1.0.0' 'app\version.txt is still 1.0.0'
    $entry = Get-UninstallEntry $App
    Test-Equal $entry['DisplayVersion'] '1.0.0' 'stage mode left DisplayVersion alone'
    Test-Equal $entry['ZexoroVersionOrder'] $Before['ZexoroVersionOrder'] 'stage mode left ZexoroVersionOrder alone'
    Test-Equal (Get-ShortcutTarget $App.Shortcut) (Join-Path $App.Root 'app\FixtureApp.exe') 'stage mode left the shortcut alone'
}

function Test-Scope([string]$ScopeName, [string]$RootSubject) {
    $app = New-E2EApp $ScopeName $InstallerDir
    $name = $app.Name

    Write-Host ''
    Write-Host "== $name ($ScopeName scope, $($app.Root))" -ForegroundColor Cyan
    foreach ($installer in $app.Installer.Values) { Assert-That (Test-Path -LiteralPath $installer) "installer $installer exists" }
    Clear-App $app
    $running = $null
    try {
        Write-Host '-- install 1.0.0'
        Assert-That ((Invoke-Program $app.Installer['1.0.0'] @('/S')) -eq 0) 'installer 1.0.0 /S exits with 0'
        $v1 = Test-Installed $app '1.0.0' $RootSubject
        [void](Test-That (Test-Path -LiteralPath (Join-Path $app.Root 'app\obsolete.txt')) 'app\obsolete.txt exists in 1.0.0')

        Test-Stage $app $v1

        Write-Host '-- upgrade to 2.0.0 (also removes what stage mode left)'
        Assert-That ((Invoke-Program $app.Installer['2.0.0'] @('/S')) -eq 0) 'installer 2.0.0 /S exits with 0'
        $v2 = Test-Installed $app '2.0.0' $RootSubject
        [void](Test-That (-not (Test-Path -LiteralPath (Join-Path $app.Root 'app\obsolete.txt'))) 'app\obsolete.txt is gone after the upgrade')
        [void](Test-That ([string]::CompareOrdinal($v2['ZexoroVersionOrder'], $v1['ZexoroVersionOrder']) -gt 0) 'ZexoroVersionOrder increased')

        Write-Host '-- downgrade to 1.0.0 is refused'
        Test-Equal (Invoke-Program $app.Installer['1.0.0'] @('/S')) 3 'installer 1.0.0 /S over 2.0.0 exits with 3'
        $entry = Get-UninstallEntry $app
        Test-Equal $entry['DisplayVersion'] '2.0.0' 'DisplayVersion is still 2.0.0'
        Test-Equal (Get-Content -LiteralPath (Join-Path $app.Root 'app\version.txt') -Raw).Trim() '2.0.0' 'app\version.txt is still 2.0.0'

        Write-Host '-- reinstall while the app is running is refused'
        if (Test-Path -LiteralPath $DataDir) { Remove-Item -LiteralPath $DataDir -Recurse -Force }
        $running = Start-Process -FilePath (Join-Path $app.Root 'app\FixtureApp.exe') -ArgumentList '--wait' -PassThru
        Assert-That (Wait-Until { $null -ne (Read-Marker) } 60) 'the app started with --wait'
        Test-Equal (Invoke-Program $app.Installer['2.0.0'] @('/S')) 4 'installer 2.0.0 /S while the app runs exits with 4'
        [void](Test-That (-not $running.HasExited) 'the app is still running')
        [void](Test-That (Test-Path -LiteralPath (Join-Path $app.Root 'app\FixtureApp.exe')) 'the installed files are intact')
        $running | Stop-Process -Force
        $running.WaitForExit()
        $running = $null

        Write-Host '-- uninstall'
        Assert-That (Test-Path -LiteralPath (Join-Path $DataDir 'cache\cache.txt')) 'the app wrote %LOCALAPPDATA%\ZexoroE2E\cache before the uninstall'
        # What an interrupted update leaves, and the updater's state: the uninstaller removes both.
        New-Item -ItemType Directory -Force -Path (Join-Path $app.Root 'app.new') | Out-Null
        Set-Content -LiteralPath (Join-Path $app.Root 'app.new\leftover.txt') -Value 'x'
        foreach ($name in 'zexoro-updater.exe.old', 'zexoro-install.json.new', 'uninstall.exe.old') { Set-Content -LiteralPath (Join-Path $app.Root $name) -Value 'x' }
        $state = Join-Path $env:LOCALAPPDATA "zexoro\updater\$($app.Id)"
        New-Item -ItemType Directory -Force -Path (Join-Path $state 'downloads') | Out-Null
        Set-Content -LiteralPath (Join-Path $state 'state.json') -Value '{}'
        [void](Test-That (Invoke-QuietUninstall $app) 'QuietUninstallString removed the key and the root')
        [void](Test-That ($null -eq (Get-UninstallEntry $app)) 'uninstall key is gone')
        [void](Test-That (@(Get-MatchingUninstallKeys $app).Count -eq 0) 'no uninstall entry is left in any hive')
        [void](Test-That (-not (Test-Path -LiteralPath $app.Root)) "install root $($app.Root) is gone (with the update leftovers)")
        [void](Test-That (-not (Test-Path -LiteralPath $state)) "the updater state $state is gone")
        [void](Test-That (-not (Test-Path -LiteralPath $app.Shortcut)) 'Start menu shortcut is gone')
        [void](Test-That (Test-Path -LiteralPath (Join-Path $DataDir 'ran.txt')) 'user data (%LOCALAPPDATA%\ZexoroE2E\ran.txt) is kept')
        [void](Test-That (-not (Test-Path -LiteralPath (Join-Path $DataDir 'cache'))) '[windows.uninstall] remove deleted %LOCALAPPDATA%\ZexoroE2E\cache')
    } catch {
        Write-Fail "$name aborted: $_"
    } finally {
        if ($null -ne $running -and -not $running.HasExited) { $running | Stop-Process -Force }
        Clear-App $app
    }
}

if (-not (Test-Administrator)) {
    Write-Host 'FAIL  administrator rights are required (per-machine installs and trusting the test certificate)' -ForegroundColor Red
    exit 1
}

$InstallerDir = (Resolve-Path -LiteralPath $InstallerDir).Path
Write-Host "Installers: $InstallerDir"
$script:UpdateKey = (Get-Content -LiteralPath (Join-Path $InstallerDir 'zexoro-update.pub') -Raw).Trim()

# Trust the throwaway root that issued the signing certificate for the duration of the test.
$certificate = $null
$store = $null
$certificatePath = Join-Path $InstallerDir 'root.cer'
if (Test-Path -LiteralPath $certificatePath) {
    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $certificatePath
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store -ArgumentList 'Root', 'LocalMachine'
    $store.Open('ReadWrite')
    $store.Add($certificate)
    Write-Host "Trusting $($certificate.Subject) ($($certificate.Thumbprint)) in LocalMachine\Root until the end of the test"
} else {
    Write-Fail "$certificatePath not found; signatures cannot be checked"
}

try {
    $unsigned = Join-Path $InstallerDir 'unsigned\ZexoroE2EUser-1.0.0-win-x64.exe'
    if (Test-Path -LiteralPath $unsigned) {
        Write-Host ''
        Write-Host '== unsigned installer' -ForegroundColor Cyan
        Test-Equal (Get-AuthenticodeSignature -LiteralPath $unsigned).Status 'NotSigned' 'installer without [windows.signing] is not signed'
    }
    foreach ($scopeName in $Scope) {
        Test-Scope $scopeName $(if ($certificate) { $certificate.Subject } else { '' })
    }
} finally {
    if ($store) {
        $store.Remove($certificate)
        $store.Close()
        Write-Host "Removed $($certificate.Thumbprint) from LocalMachine\Root"
    }
}

exit (Complete-Run)
