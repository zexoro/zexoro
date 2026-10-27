<#
.SYNOPSIS
  End-to-end test of automatic updates for zexoro's NSIS installers (docs/design/0005-nsis-updates.md):
  zexoro-updater check, download and apply against a local feed server, for the per-user scope
  (strategy replace) and the per-machine scope (strategy reinstall), plus the verification failures.

.DESCRIPTION
  Consumes the output of tests/e2e/pack.sh (any pack host): the installers, whose [updates] point at
  http://localhost:8080/<scope>/stable/, and the feeds pack.sh signed (feeds\<scope>\seq1, seq2 and
  other-key). The script serves them with an HttpListener on that address, so the port must be free.
  Runs in Windows PowerShell 5.1 and PowerShell 7 and needs administrator rights (per-machine
  installs; the updater runs the per-machine installer through ShellExecute, which raises no UAC
  prompt in an elevated session).

  For each scope: install 1.0.0; check against a valid feed with 1.0.0 only (up to date), a
  tampered feed, a feed signed with an untrusted key, the feed with 2.0.0 (available), the old feed
  again (replay), a download whose bytes do not match the feed; then download and apply 2.0.0 while
  the application runs (apply waits for it, then restarts it); verify the install and check again
  (up to date); uninstall.

  Each check prints PASS or FAIL; the script exits 1 when any check failed. It leaves no installs
  behind, also after a failure.

.PARAMETER InstallerDir
  Directory written by tests/e2e/pack.sh.

.PARAMETER Scope
  Scopes to test (default: both).

.EXAMPLE
  .\tests\e2e\windows\Run-UpdateE2E.ps1 -InstallerDir C:\temp\e2e
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

# The installed version as the files, the install record and the uninstall key tell it.
function Test-Version($App, [string]$Version, [string]$Context) {
    Test-Equal (Get-Content -LiteralPath (Join-Path $App.Root 'app\version.txt') -Raw).Trim() $Version "$Context`: app\version.txt is $Version"
    Test-Equal (Read-Json (Join-Path $App.Root 'zexoro-install.json')).version $Version "$Context`: zexoro-install.json version is $Version"
    Test-Equal (Get-UninstallEntry $App)['DisplayVersion'] $Version "$Context`: DisplayVersion is $Version"
    $staged = @(Get-ChildItem -LiteralPath $App.Root -Force | Where-Object { $_.Name -match '\.new$' } | ForEach-Object { $_.Name })
    [void](Test-That ($staged.Count -eq 0) "$Context`: nothing is staged in the install root" ($staged -join ', '))
}

function Get-Downloads($App) {
    $downloads = Join-Path $App.State 'downloads'
    if (-not (Test-Path -LiteralPath $downloads)) { return @() }
    return @(Get-ChildItem -LiteralPath $downloads -Force | ForEach-Object { $_.Name } | Sort-Object)
}

function Remove-State($App) {
    if (Test-Path -LiteralPath $App.State) { Remove-Item -LiteralPath $App.State -Recurse -Force }
}

# What a direct silent install of $Version writes into the uninstall key; the updater must match it.
function Get-ReferenceEntry($App, [string]$Version) {
    Assert-That ((Invoke-Program $App.Installer[$Version] @('/S')) -eq 0) "reference install of $Version exits with 0"
    $entry = Get-UninstallEntry $App
    Assert-That (Invoke-QuietUninstall $App) "reference install of $Version uninstalled"
    return $entry
}

function Test-Scope([string]$ScopeName, $Server) {
    $app = New-E2EApp $ScopeName $InstallerDir
    $fragment = Read-Json ($app.Installer['2.0.0'] + '.release.json')
    $artifact = @($fragment.release.artifacts)[0]
    $url = [Uri]$artifact.url
    $app | Add-Member -NotePropertyName Served -NotePropertyValue (Join-Path $script:WebRoot ([IO.Path]::GetDirectoryName($url.AbsolutePath.TrimStart('/'))))
    $strategy = $(if ($ScopeName -eq 'user') { 'replace' } else { 'reinstall' })

    Write-Host ''
    Write-Host "== $($app.Name) ($ScopeName scope, strategy $strategy, $($app.Root))" -ForegroundColor Cyan
    foreach ($installer in $app.Installer.Values) { Assert-That (Test-Path -LiteralPath $installer) "installer $installer exists" }
    Clear-App $app
    Remove-State $app
    New-Item -ItemType Directory -Force -Path $app.Served | Out-Null
    $running = $null
    $apply = $null
    try {
        Write-Host '-- reference: what the 2.0.0 installer itself writes'
        $reference = Get-ReferenceEntry $app '2.0.0'

        Write-Host '-- install 1.0.0'
        Assert-That ((Invoke-Program $app.Installer['1.0.0'] @('/S')) -eq 0) 'installer 1.0.0 /S exits with 0'
        Test-Version $app '1.0.0' 'installed'
        $v1 = Get-UninstallEntry $app
        Publish-Installers $app

        Write-Host '-- a valid feed without a newer version'
        Publish-Feed $app 'seq1'
        [void](Test-UpdaterRun $app 'check' $ExitUpToDate 'up-to-date' 'the feed with sequence 1 has only 1.0.0')

        Write-Host '-- verification failures (exit 4)'
        Publish-Feed $app 'seq2'
        Set-FlippedByte (Join-Path $app.Served 'feed.json')
        [void](Test-UpdaterRun $app 'check' $ExitVerificationFailed 'error' 'a feed byte was flipped' 'signature')
        Publish-Feed $app 'other-key'
        [void](Test-UpdaterRun $app 'check' $ExitVerificationFailed 'error' 'the feed is signed with an untrusted key' 'signature')

        Write-Host '-- the feed with 2.0.0'
        Publish-Feed $app 'seq2'
        $available = Test-UpdaterRun $app 'check' $ExitAvailable 'available' 'the feed with sequence 2 has 2.0.0'
        Test-Equal (Get-Field $available.Json 'installed') '1.0.0' 'check reports installed 1.0.0'
        Test-Equal (Get-Field $available.Json 'version') '2.0.0' 'check reports version 2.0.0'
        Test-Equal (Get-Field $available.Json 'size') (Get-Item -LiteralPath $app.Installer['2.0.0']).Length 'check reports the size of the 2.0.0 installer'

        Write-Host '-- replay of the older feed (exit 4)'
        Publish-Feed $app 'seq1'
        [void](Test-UpdaterRun $app 'check' $ExitVerificationFailed 'error' 'the validly signed feed with sequence 1 is served after sequence 2' 'sequence')

        Write-Host '-- an artifact that does not match its sha256 (exit 4)'
        Publish-Feed $app 'seq2'
        $served = Join-Path $app.Served (Split-Path -Leaf $app.Installer['2.0.0'])
        Set-FlippedByte $served
        [void](Test-UpdaterRun $app 'download' $ExitVerificationFailed 'error' 'the served 2.0.0 installer has one byte changed' 'SHA-256')
        Test-Equal ((Get-Downloads $app) -join ', ') '' 'nothing was kept in the downloads'
        [void](Test-UpdaterRun $app 'apply' $ExitUpToDate 'up-to-date' 'nothing is downloaded, so there is nothing to apply')
        Test-Version $app '1.0.0' 'after the verification failures'
        Test-Equal (Get-UninstallEntry $app)['ZexoroVersionOrder'] $v1['ZexoroVersionOrder'] 'after the verification failures: ZexoroVersionOrder is unchanged'
        [void](Test-That (Test-Path -LiteralPath (Join-Path $app.Root 'app\obsolete.txt')) 'after the verification failures: app\obsolete.txt is still there')

        Write-Host '-- download 2.0.0'
        Publish-Installers $app
        $server.Requests.Clear()
        $downloaded = Test-UpdaterRun $app 'download' $ExitAvailable 'downloaded' 'the 2.0.0 installer matches the feed'
        Test-Equal (Get-Field $downloaded.Json 'version') '2.0.0' 'download reports version 2.0.0'
        [void](Test-That (@($server.Requests) -contains $url.AbsolutePath) "the updater fetched $($url.AbsolutePath)" (@($server.Requests) -join ', '))
        Test-Equal ((Get-Downloads $app) -join ', ') "$(Split-Path -Leaf $app.Installer['2.0.0']), staged.json" 'the verified installer is staged in the downloads'

        Write-Host '-- apply while the application runs, then restart it'
        if (Test-Path -LiteralPath $DataDir) { Remove-Item -LiteralPath $DataDir -Recurse -Force }
        $running = Start-Process -FilePath (Join-Path $app.Root 'app\FixtureApp.exe') -ArgumentList '--wait' -WorkingDirectory $env:TEMP -PassThru
        Assert-That (Wait-Until { $null -ne (Read-Marker) } 60) 'the application started (with --wait)'
        Remove-Item -LiteralPath (Join-Path $DataDir 'ran.txt') -Force
        $lastApply = Join-Path $app.State 'last-apply.json'
        if (Test-Path -LiteralPath $lastApply) { Remove-Item -LiteralPath $lastApply -Force }
        $applyOut = Join-Path $script:WorkDir "$ScopeName-apply.out"
        $applyErr = Join-Path $script:WorkDir "$ScopeName-apply.err"
        $apply = Start-Process -FilePath (Join-Path $app.Root 'zexoro-updater.exe') -ArgumentList @('apply', '--wait-pid', $running.Id, '--restart', '--json') `
            -WorkingDirectory $env:TEMP -RedirectStandardOutput $applyOut -RedirectStandardError $applyErr -NoNewWindow -PassThru
        $null = $apply.Handle
        Start-Sleep -Seconds 3
        [void](Test-That (-not $apply.HasExited) 'apply waits while the application runs')
        Test-Equal (Get-Content -LiteralPath (Join-Path $app.Root 'app\version.txt') -Raw).Trim() '1.0.0' 'nothing changed while the application runs'
        $running | Stop-Process -Force
        $running.WaitForExit()
        $running = $null

        $finished = Wait-Until { Test-Path -LiteralPath $lastApply }
        if (-not $apply.WaitForExit($TimeoutSeconds * 1000)) { throw 'apply did not exit' }
        $apply.WaitForExit()
        Write-Host "      apply stdout: $((Read-SharedText $applyOut).Trim())"
        Write-Host "      apply stderr: $((Read-SharedText $applyErr).Trim())"
        Test-Equal $apply.ExitCode 0 'apply exits with 0'
        Assert-That $finished 'the update finished (last-apply.json written)'
        # For reinstall the copy in %TEMP% may still be finishing after it wrote the report.
        [void](Wait-Until { @(Get-Process -Name 'zexoro-updater' -ErrorAction SilentlyContinue).Count -eq 0 } 60)
        $report = Read-Json $lastApply
        Test-Equal (Get-Field $report 'status') 'applied' 'last-apply.json status is applied'
        Test-Equal (Get-Field $report 'installed') '1.0.0' 'last-apply.json installed is 1.0.0'
        Test-Equal (Get-Field $report 'version') '2.0.0' 'last-apply.json version is 2.0.0'
        Test-Equal (Get-Field $report 'exitCode') 0 'last-apply.json exitCode is 0'
        $restarted = Wait-Until { $null -ne (Read-Marker) } 60
        [void](Test-That $restarted 'the application was restarted')
        Test-Equal (Read-Marker) (Join-Path $app.Root 'app\') 'the restarted application runs from the install'

        Write-Host '-- 2.0.0 is installed'
        Test-Version $app '2.0.0' 'updated'
        [void](Test-That (-not (Test-Path -LiteralPath (Join-Path $app.Root 'app\obsolete.txt'))) 'app\obsolete.txt is gone')
        $record = Read-Json (Join-Path $app.Root 'zexoro-install.json')
        Test-Equal $record.strategy $strategy "zexoro-install.json strategy is $strategy"
        Test-Equal $record.scope $ScopeName "zexoro-install.json scope is $ScopeName"
        $entry = Get-UninstallEntry $app
        Test-Equal $entry['ZexoroVersionOrder'] $reference['ZexoroVersionOrder'] 'ZexoroVersionOrder is what the 2.0.0 installer writes'
        Test-Equal $entry['EstimatedSize'] $reference['EstimatedSize'] 'EstimatedSize is what the 2.0.0 installer writes'
        [void](Test-That ([string]::CompareOrdinal([string]$entry['ZexoroVersionOrder'], [string]$v1['ZexoroVersionOrder']) -gt 0) 'ZexoroVersionOrder increased')
        Test-Equal $entry['InstallLocation'] $app.Root 'InstallLocation is unchanged'
        Test-Equal @(Get-MatchingUninstallKeys $app).Count 1 'exactly one uninstall entry'
        foreach ($file in 'FixtureApp.exe', 'FixtureApp.dll', 'version.txt') {
            [void](Test-That (Test-Path -LiteralPath (Join-Path $app.Root "app\$file")) "app\$file exists")
        }
        foreach ($file in 'zexoro-updater.exe', 'uninstall.exe') {
            [void](Test-That (Test-Path -LiteralPath (Join-Path $app.Root $file)) "$file exists")
        }
        # The handing-off updater still runs while the swap renames it, so replace leaves
        # zexoro-updater.exe.old for the next apply (or the uninstaller) to remove.
        $allowed = $(if ($strategy -eq 'replace') { @('zexoro-updater.exe.old') } else { @() })
        $leftovers = @(Get-ChildItem -LiteralPath $app.Root -Force | Where-Object { $_.Name -match '\.(new|old)$' -and $allowed -notcontains $_.Name } | ForEach-Object { $_.Name })
        [void](Test-That ($leftovers.Count -eq 0) 'no other .new or .old items in the install root' ($leftovers -join ', '))
        Test-Equal ((Get-Downloads $app) -join ', ') '' 'the downloads were cleared'
        $copies = @(Get-ChildItem -LiteralPath $env:TEMP -Directory -Filter 'zexoro-updater-*' -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
        Write-Host "      updater copies left in %TEMP%: $($copies.Count) (removed by the next apply)"

        Write-Host '-- up to date afterwards'
        $after = Test-UpdaterRun $app 'check' $ExitUpToDate 'up-to-date' '2.0.0 is the newest release'
        Test-Equal (Get-Field $after.Json 'installed') '2.0.0' 'check reports installed 2.0.0'

        Write-Host '-- uninstall'
        [void](Test-That (Invoke-QuietUninstall $app) 'QuietUninstallString removed the key and the root')
        [void](Test-That (-not (Test-Path -LiteralPath $app.State)) "the updater state $($app.State) is gone")
    } catch {
        Write-Fail "$($app.Name) aborted: $_"
    } finally {
        if ($null -ne $running -and -not $running.HasExited) { $running | Stop-Process -Force }
        if ($null -ne $apply -and -not $apply.HasExited) { $apply | Stop-Process -Force }
        Clear-App $app
        Remove-State $app
    }
}

if (-not (Test-Administrator)) {
    Write-Host 'FAIL  administrator rights are required (per-machine installs)' -ForegroundColor Red
    exit 1
}

$InstallerDir = (Resolve-Path -LiteralPath $InstallerDir).Path
Write-Host "Installers: $InstallerDir"
foreach ($scopeName in $Scope) {
    foreach ($variant in 'seq1', 'seq2', 'other-key') {
        $feed = Join-Path $InstallerDir "feeds\$scopeName\$variant\feed.json"
        if (-not (Test-Path -LiteralPath $feed)) { throw "$feed not found; pack with the current tests/e2e/pack.sh" }
    }
}

# Every scope's installers name the same server; take its address from a release fragment.
$first = New-E2EApp $Scope[0] $InstallerDir
$firstUrl = [Uri](@((Read-Json ($first.Installer['2.0.0'] + '.release.json')).release.artifacts)[0].url)
$prefix = "$($firstUrl.Scheme)://$($firstUrl.Authority)/"
$script:WorkDir = Join-Path ([IO.Path]::GetTempPath()) ("zexoro-update-e2e-" + [Guid]::NewGuid().ToString('N'))
$script:WebRoot = Join-Path $script:WorkDir 'www'
New-Item -ItemType Directory -Force -Path $script:WebRoot | Out-Null
$server = Start-FeedServer $script:WebRoot $prefix
Write-Host "Serving $script:WebRoot at $prefix"
try {
    foreach ($scopeName in $Scope) { Test-Scope $scopeName $server }
} finally {
    Stop-FeedServer $server
    Remove-Item -LiteralPath $script:WorkDir -Recurse -Force -ErrorAction SilentlyContinue
}

exit (Complete-Run)
