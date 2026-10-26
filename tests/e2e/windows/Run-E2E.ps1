<#
.SYNOPSIS
  End-to-end test of zexoro's NSIS installers: install, upgrade, downgrade refusal, running-app
  refusal and uninstall, for the per-user and the per-machine scope.

.DESCRIPTION
  Consumes the output of tests/e2e/pack.sh (any pack host). Runs in Windows PowerShell 5.1 and
  PowerShell 7 and needs administrator rights (per-machine installs, and trusting the throwaway
  signing certificate in LocalMachine\Root, which is removed again at the end).

  Each check prints PASS or FAIL; the script exits 1 when any check failed. It leaves no installs
  behind, also after a failure.

.PARAMETER InstallerDir
  Directory written by tests/e2e/pack.sh: user\, machine\, unsigned\ and root.cer.

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

$script:Passed = 0
$script:Failed = 0
$UninstallPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall'
$DataDir = Join-Path $env:LOCALAPPDATA 'ZexoroE2E'

function Write-Pass([string]$Message) { $script:Passed++; Write-Host "PASS  $Message" -ForegroundColor Green }
function Write-Fail([string]$Message) { $script:Failed++; Write-Host "FAIL  $Message" -ForegroundColor Red }

# Records a check; returns its result so callers can stop when later checks would be meaningless.
function Test-That([bool]$Condition, [string]$Message, [string]$Detail = '') {
    if ($Condition) { Write-Pass $Message } else { Write-Fail ($Message + $(if ($Detail) { " ($Detail)" } else { '' })) }
    return $Condition
}

function Assert-That([bool]$Condition, [string]$Message, [string]$Detail = '') {
    if (-not (Test-That $Condition $Message $Detail)) { throw "stopping: $Message" }
}

function Test-Equal($Actual, $Expected, [string]$Message) {
    [void](Test-That ("$Actual" -eq "$Expected") $Message "expected '$Expected', got '$Actual'")
}

function Wait-Until([scriptblock]$Condition, [int]$Seconds = $TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return [bool](& $Condition)
}

# Runs a program and returns its exit code; kills it after the timeout (a silent installer must never wait for input).
function Invoke-Program([string]$Path, [string[]]$Arguments) {
    $process = if ($null -ne $Arguments -and $Arguments.Count -gt 0) { Start-Process -FilePath $Path -ArgumentList $Arguments -PassThru } else { Start-Process -FilePath $Path -PassThru }
    $null = $process.Handle # keeps the handle, so ExitCode is available after exit
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        throw "$Path $Arguments did not exit within $TimeoutSeconds s"
    }
    return $process.ExitCode
}

function Get-UninstallHive([string]$ScopeName) {
    if ($ScopeName -eq 'user') {
        return [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser', 'Registry64')
    }
    return [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
}

# The app's uninstall key values as a hashtable, or $null when the key does not exist.
function Get-UninstallEntry($App) {
    $hive = Get-UninstallHive $App.Scope
    try {
        $key = $hive.OpenSubKey("$UninstallPath\$($App.Id)")
        if ($null -eq $key) { return $null }
        try {
            $values = @{}
            foreach ($name in $key.GetValueNames()) { $values[$name] = $key.GetValue($name) }
            return $values
        } finally { $key.Close() }
    } finally { $hive.Close() }
}

# Every uninstall entry, in all hives and registry views, that belongs to the app (by key name or display name).
function Get-MatchingUninstallKeys($App) {
    $found = @()
    foreach ($hiveName in 'CurrentUser', 'LocalMachine') {
        foreach ($view in 'Registry64', 'Registry32') {
            $hive = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hiveName, $view)
            try {
                $parent = $hive.OpenSubKey($UninstallPath)
                if ($null -eq $parent) { continue }
                try {
                    foreach ($name in $parent.GetSubKeyNames()) {
                        $sub = $parent.OpenSubKey($name)
                        if ($null -eq $sub) { continue }
                        try {
                            if ($name -eq $App.Id -or $sub.GetValue('DisplayName') -eq $App.Name) {
                                $found += "$hiveName/$view/$name"
                            }
                        } finally { $sub.Close() }
                    }
                } finally { $parent.Close() }
            } finally { $hive.Close() }
        }
    }
    # HKCU\Software is not redirected, so both views show the same key there; count it once.
    return @($found | ForEach-Object { $_ -replace '^CurrentUser/Registry(32|64)/', 'CurrentUser/' } | Sort-Object -Unique)
}

function Get-ShortcutTarget([string]$Path) {
    $shell = New-Object -ComObject WScript.Shell
    try { return $shell.CreateShortcut($Path).TargetPath } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }
}

function Read-Marker {
    $path = Join-Path $DataDir 'ran.txt'
    if (Test-Path -LiteralPath $path) { return (Get-Content -LiteralPath $path -Raw).Trim() }
    return $null
}

# Uninstalls through QuietUninstallString and waits until the key and the root are gone. The NSIS
# uninstaller copies itself to %TEMP% and returns at once, so its exit does not mean it finished.
function Invoke-QuietUninstall($App) {
    $entry = Get-UninstallEntry $App
    if ($null -eq $entry) { return $false }
    $command = [string]$entry['QuietUninstallString']
    if ($command -notmatch '^"([^"]+)"\s*(.*)$') { throw "unexpected QuietUninstallString: $command" }
    $code = Invoke-Program $Matches[1] @($Matches[2] -split '\s+' | Where-Object { $_ })
    if ($code -ne 0) { throw "uninstaller exited with $code" }
    return (Wait-Until { ($null -eq (Get-UninstallEntry $App)) -and -not (Test-Path -LiteralPath $App.Root) })
}

# Removes an install and test data left by an earlier, interrupted run.
function Clear-App($App) {
    Get-Process -Name FixtureApp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    if ($null -ne (Get-UninstallEntry $App)) {
        Write-Host "      removing a leftover install of $($App.Name)"
        try { [void](Invoke-QuietUninstall $App) } catch { Write-Host "      $_" }
    }
    if (Test-Path -LiteralPath $DataDir) { Remove-Item -LiteralPath $DataDir -Recurse -Force }
}

function Test-Signatures([string[]]$Paths, [string]$Thumbprint) {
    foreach ($path in $Paths) {
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        $signer = if ($signature.SignerCertificate) { $signature.SignerCertificate.Thumbprint } else { '' }
        [void](Test-That ($signature.Status -eq 'Valid' -and $signer -eq $Thumbprint) "signature valid: $(Split-Path -Leaf $path)" "$($signature.Status): $($signature.StatusMessage)")
    }
}

function Test-Installed($App, [string]$Version, [string]$Thumbprint) {
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
    }

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
    $keys = Get-MatchingUninstallKeys $App
    [void](Test-That ($keys.Count -eq 1) 'exactly one uninstall entry' ($keys -join ', '))

    $target = if (Test-Path -LiteralPath $App.Shortcut) { Get-ShortcutTarget $App.Shortcut } else { '' }
    Test-Equal $target $main "Start menu shortcut $($App.Shortcut) points to the main executable"

    if ($Thumbprint) { Test-Signatures @($App.Installer[$Version], $uninstaller, $main) $Thumbprint }

    if (Test-Path -LiteralPath $DataDir) { Remove-Item -LiteralPath $DataDir -Recurse -Force }
    $code = Invoke-Program $main @()
    Test-Equal $code 0 'the installed app exits with 0'
    Test-Equal (Read-Marker) "$appDir\" 'the installed app ran (marker written)'
    return $entry
}

function Test-Scope([string]$ScopeName, [string]$Thumbprint) {
    $title = $ScopeName.Substring(0, 1).ToUpperInvariant() + $ScopeName.Substring(1)
    $name = "Zexoro E2E $title"
    $fileName = $name -replace '[^A-Za-z0-9._-]', ''
    $app = [pscustomobject]@{
        Scope     = $ScopeName
        Id        = "dev.zexoro.e2e.$ScopeName"
        Name      = $name
        Hive      = $(if ($ScopeName -eq 'user') { 'HKCU' } else { 'HKLM' })
        Root      = $(if ($ScopeName -eq 'user') { Join-Path $env:LOCALAPPDATA "Programs\$name" } else { Join-Path $env:ProgramFiles $name })
        Shortcut  = Join-Path ([Environment]::GetFolderPath($(if ($ScopeName -eq 'user') { 'Programs' } else { 'CommonPrograms' }))) "$name.lnk"
        Installer = @{
            '1.0.0' = Join-Path $InstallerDir "$ScopeName\$fileName-1.0.0-win-x64.exe"
            '2.0.0' = Join-Path $InstallerDir "$ScopeName\$fileName-2.0.0-win-x64.exe"
        }
    }

    Write-Host ''
    Write-Host "== $name ($ScopeName scope, $($app.Root))" -ForegroundColor Cyan
    foreach ($installer in $app.Installer.Values) { Assert-That (Test-Path -LiteralPath $installer) "installer $installer exists" }
    Clear-App $app
    $running = $null
    try {
        Write-Host '-- install 1.0.0'
        Assert-That ((Invoke-Program $app.Installer['1.0.0'] @('/S')) -eq 0) 'installer 1.0.0 /S exits with 0'
        $v1 = Test-Installed $app '1.0.0' $Thumbprint
        [void](Test-That (Test-Path -LiteralPath (Join-Path $app.Root 'app\obsolete.txt')) 'app\obsolete.txt exists in 1.0.0')

        Write-Host '-- upgrade to 2.0.0'
        Assert-That ((Invoke-Program $app.Installer['2.0.0'] @('/S')) -eq 0) 'installer 2.0.0 /S exits with 0'
        $v2 = Test-Installed $app '2.0.0' $Thumbprint
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
        [void](Test-That (Invoke-QuietUninstall $app) 'QuietUninstallString removed the key and the root')
        [void](Test-That ($null -eq (Get-UninstallEntry $app)) 'uninstall key is gone')
        [void](Test-That ((Get-MatchingUninstallKeys $app).Count -eq 0) 'no uninstall entry is left in any hive')
        [void](Test-That (-not (Test-Path -LiteralPath $app.Root)) "install root $($app.Root) is gone")
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

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'FAIL  administrator rights are required (per-machine installs and trusting the test certificate)' -ForegroundColor Red
    exit 1
}

$InstallerDir = (Resolve-Path -LiteralPath $InstallerDir).Path
Write-Host "Installers: $InstallerDir"

# Trust the throwaway signing certificate for the duration of the test.
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
        Test-Scope $scopeName $(if ($certificate) { $certificate.Thumbprint } else { '' })
    }
} finally {
    if ($store) {
        $store.Remove($certificate)
        $store.Close()
        Write-Host "Removed $($certificate.Thumbprint) from LocalMachine\Root"
    }
}

Write-Host ''
if ($script:Failed -gt 0) {
    Write-Host "FAILED: $script:Failed failed, $script:Passed passed" -ForegroundColor Red
    exit 1
}
Write-Host "PASSED: $script:Passed checks" -ForegroundColor Green
exit 0
