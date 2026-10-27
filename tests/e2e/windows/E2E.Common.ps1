# Helpers shared by Run-E2E.ps1 and Run-UpdateE2E.ps1 (dot-sourced). The including script defines
# $TimeoutSeconds and runs with Set-StrictMode -Version 3 and $ErrorActionPreference = 'Stop'.

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

# Valid means the signature matches the file and chains to a trusted root; the issuer check ties it to the test root.
function Test-Signatures([string[]]$Paths, [string]$RootSubject) {
    foreach ($path in $Paths) {
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        $issuer = if ($signature.SignerCertificate) { $signature.SignerCertificate.Issuer } else { '' }
        [void](Test-That ($signature.Status -eq 'Valid' -and $issuer -eq $RootSubject) "signature valid: $(Split-Path -Leaf $path)" "$($signature.Status): $($signature.StatusMessage); issuer '$issuer'")
    }
}

# The fixture application of one scope, as tests/e2e/pack.sh packs it.
function New-E2EApp([string]$ScopeName, [string]$InstallerDir) {
    $title = $ScopeName.Substring(0, 1).ToUpperInvariant() + $ScopeName.Substring(1)
    $name = "Zexoro E2E $title"
    $fileName = $name -replace '[^A-Za-z0-9._-]', ''
    return [pscustomobject]@{
        Scope     = $ScopeName
        Id        = "dev.zexoro.e2e.$ScopeName"
        Name      = $name
        FileName  = $fileName
        Hive      = $(if ($ScopeName -eq 'user') { 'HKCU' } else { 'HKLM' })
        Root      = $(if ($ScopeName -eq 'user') { Join-Path $env:LOCALAPPDATA "Programs\$name" } else { Join-Path $env:ProgramFiles $name })
        Shortcut  = Join-Path ([Environment]::GetFolderPath($(if ($ScopeName -eq 'user') { 'Programs' } else { 'CommonPrograms' }))) "$name.lnk"
        State     = Join-Path $env:LOCALAPPDATA "zexoro\updater\dev.zexoro.e2e.$ScopeName"
        Installer = @{
            '1.0.0' = Join-Path $InstallerDir "$ScopeName\$fileName-1.0.0-win-x64.exe"
            '2.0.0' = Join-Path $InstallerDir "$ScopeName\$fileName-2.0.0-win-x64.exe"
        }
    }
}

function Test-Administrator {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Prints the summary and returns the script's exit code.
function Complete-Run {
    Write-Host ''
    if ($script:Failed -gt 0) {
        Write-Host "FAILED: $script:Failed failed, $script:Passed passed" -ForegroundColor Red
        return 1
    }
    Write-Host "PASSED: $script:Passed checks" -ForegroundColor Green
    return 0
}
