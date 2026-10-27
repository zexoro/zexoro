# Helpers shared by Run-E2E.ps1, Run-UpdateE2E.ps1 and Run-ZipE2E.ps1 (dot-sourced). The including
# script defines $TimeoutSeconds and $InstallerDir and runs with Set-StrictMode -Version 3 and
# $ErrorActionPreference = 'Stop'. The update helpers expect $App.Root, $App.Scope (the feed
# directory under feeds\), $App.Served, $App.State and $App.Installer (version -> package path).

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

# ---- Updates (Run-UpdateE2E.ps1, Run-ZipE2E.ps1) ----

# Exit codes of zexoro-updater (docs/design/0005-nsis-updates.md section 3).
$ExitAvailable = 0
$ExitUpToDate = 1
$ExitVerificationFailed = 4

# A property of an object from ConvertFrom-Json, or $null (StrictMode forbids reading a missing one).
function Get-Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Read-Json([string]$Path) {
    return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
}

# Reads a file that another process may still hold open for writing.
function Read-SharedText([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    $stream = New-Object System.IO.FileStream -ArgumentList $Path, 'Open', 'Read', 'ReadWrite'
    try {
        $reader = New-Object System.IO.StreamReader -ArgumentList $stream
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}

#region Feed server

# Serves the files under $Root at $Prefix (http://localhost:<port>/) from a background runspace and
# records every request path in $Requests. The test changes what is served by replacing the files.
function Start-FeedServer([string]$Root, [string]$Prefix) {
    $listener = New-Object System.Net.HttpListener
    $listener.Prefixes.Add($Prefix)
    try { $listener.Start() } catch { throw "cannot listen on $Prefix (is the port in use?): $_" }
    $requests = [Collections.ArrayList]::Synchronized((New-Object Collections.ArrayList))
    $serve = {
        param($Listener, $Root, $Requests)
        while ($Listener.IsListening) {
            try { $context = $Listener.GetContext() } catch { break }
            try {
                $path = [Uri]::UnescapeDataString($context.Request.Url.AbsolutePath)
                [void]$Requests.Add($path)
                $file = [IO.Path]::GetFullPath([IO.Path]::Combine($Root, $path.TrimStart('/').Replace('/', '\')))
                if ($file.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase) -and [IO.File]::Exists($file)) {
                    $bytes = [IO.File]::ReadAllBytes($file)
                    $context.Response.ContentType = 'application/octet-stream'
                    $context.Response.ContentLength64 = $bytes.Length
                    $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                } else {
                    $context.Response.StatusCode = 404
                }
            } catch {
                # The client went away; serve the next request.
            } finally {
                try { $context.Response.Close() } catch { }
            }
        }
    }
    $powershell = [PowerShell]::Create()
    [void]$powershell.AddScript($serve).AddArgument($listener).AddArgument($Root).AddArgument($requests)
    $handle = $powershell.BeginInvoke()
    return [pscustomobject]@{ Listener = $listener; PowerShell = $powershell; Handle = $handle; Requests = $requests; Prefix = $Prefix }
}

function Stop-FeedServer($Server) {
    if ($null -eq $Server) { return }
    try { $Server.Listener.Stop(); $Server.Listener.Close() } catch { }
    try { [void]$Server.PowerShell.EndInvoke($Server.Handle) } catch { }
    $Server.PowerShell.Dispose()
}

# Publishes one of the feeds pack.sh signed (seq1, seq2, other-key) as <scope>/stable/feed.json(.sig).
function Publish-Feed($App, [string]$Variant) {
    $source = Join-Path $InstallerDir "feeds\$($App.Scope)\$Variant"
    foreach ($name in 'feed.json', 'feed.json.sig') {
        Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $App.Served $name) -Force
    }
}

# Publishes both installers as they are, as pack.sh's fragments describe them.
function Publish-Installers($App) {
    foreach ($installer in $App.Installer.Values) {
        Copy-Item -LiteralPath $installer -Destination (Join-Path $App.Served (Split-Path -Leaf $installer)) -Force
    }
}

# Flips the lowest bit of the byte in the middle of a file: same size, different content.
function Set-FlippedByte([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $index = [int]($bytes.Length / 2)
    $bytes[$index] = $bytes[$index] -bxor 1
    [IO.File]::WriteAllBytes($Path, $bytes)
}

#endregion

#region Updater

# Runs the installed zexoro-updater and waits for it: @{ ExitCode; Json (parsed --json output); Output }.
function Invoke-Updater($App, [string[]]$Arguments) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = Join-Path $App.Root 'zexoro-updater.exe'
    $info.Arguments = ($Arguments + '--json') -join ' '
    $info.WorkingDirectory = $env:TEMP
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stderr = $process.StandardError.ReadToEndAsync()
        $stdout = $process.StandardOutput.ReadToEnd()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill()
            throw "zexoro-updater $($info.Arguments) did not exit within $TimeoutSeconds s"
        }
        $process.WaitForExit()
        $json = $null
        $line = @($stdout -split "`r?`n" | Where-Object { $_.Trim().StartsWith('{') }) | Select-Object -Last 1
        if ($line) { $json = $line | ConvertFrom-Json }
        $output = ($stdout.Trim() + ' ' + $stderr.Result.Trim()).Trim()
        Write-Host "      zexoro-updater $($info.Arguments) -> $($process.ExitCode): $output"
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Json = $json; Output = $output }
    } finally { $process.Dispose() }
}

# Runs check or download and verifies exit code, status and (for errors) a phrase of the message.
function Test-UpdaterRun($App, [string]$Command, [int]$ExitCode, [string]$Status, [string]$Message, [string]$ErrorPhrase = '') {
    $result = Invoke-Updater $App @($Command)
    Test-Equal $result.ExitCode $ExitCode "$Command exits with $ExitCode`: $Message"
    Test-Equal (Get-Field $result.Json 'status') $Status "$Command reports status '$Status'"
    if ($ErrorPhrase) {
        $reported = [string](Get-Field $result.Json 'error')
        [void](Test-That ($reported.IndexOf($ErrorPhrase, [StringComparison]::OrdinalIgnoreCase) -ge 0) "$Command error names the cause ('$ErrorPhrase')" "got '$reported'")
    }
    return $result
}

#endregion

function Get-Downloads($App) {
    $downloads = Join-Path $App.State 'downloads'
    if (-not (Test-Path -LiteralPath $downloads)) { return @() }
    return @(Get-ChildItem -LiteralPath $downloads -Force | ForEach-Object { $_.Name } | Sort-Object)
}

function Remove-State($App) {
    if (Test-Path -LiteralPath $App.State) { Remove-Item -LiteralPath $App.State -Recurse -Force }
}
