<#
.SYNOPSIS
  End-to-end test of zexoro's portable Windows zip (docs/design/0006-desktop-formats.md): extract,
  run, and automatic updates with zexoro-updater (strategy replace from the archive) against a local
  feed server, plus the verification failures and a folder that is not writable.

.DESCRIPTION
  Consumes the output of tests/e2e/pack.sh (any pack host): portable\ZexoroE2EPortable-{1.0.0,2.0.0}-win-x64.zip,
  whose [updates] point at http://localhost:8080/portable/stable/, the feeds pack.sh signed
  (feeds\portable\seq1, seq2 and other-key), and unsigned\ZexoroE2EUser-1.0.0-win-x64.zip (no
  [windows.signing], no [updates]). The script serves the feeds with an HttpListener on that
  address, so the port must be free. Runs in Windows PowerShell 5.1 and PowerShell 7. Extracting and
  updating need no administrator rights; with them, the throwaway root that signed the files is
  trusted in LocalMachine\Root for the run and the Authenticode signatures are checked too.

  Shared update scenario (0006 section 5): extract 1.0.0; check against seq1 (up to date); a
  tampered feed, a feed signed with an untrusted key, seq1 replayed after seq2 and a download whose
  bytes do not match the feed each exit 4 with nothing changed; then download and apply 2.0.0 while
  the application runs (apply waits for it, then restarts it); check again (up to date). Finally a
  copy in a folder the user may not write to: apply exits 7 and changes nothing.

  Each check prints PASS or FAIL; the script exits 1 when any check failed. It removes everything it
  extracted, also after a failure.

.PARAMETER InstallerDir
  Directory written by tests/e2e/pack.sh.

.EXAMPLE
  .\tests\e2e\windows\Run-ZipE2E.ps1 -InstallerDir C:\temp\e2e
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallerDir,
    [int]$TimeoutSeconds = 120
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'E2E.Common.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$ExitManualUpdate = 7

# The names of the entries of a zip archive.
function Get-ZipEntryNames([string]$Path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try { return @($zip.Entries | ForEach-Object { $_.FullName }) } finally { $zip.Dispose() }
}

# Extracts $Archive into $Parent as a user would (Expand-Archive), after checking it has the one
# top-level folder $Folder; returns the extracted root.
function Expand-Package([string]$Archive, [string]$Parent, [string]$Folder) {
    $names = Get-ZipEntryNames $Archive
    $outside = @($names | Where-Object { -not $_.StartsWith("$Folder/") })
    [void](Test-That ($outside.Count -eq 0) "$(Split-Path -Leaf $Archive) has the single top-level folder $Folder\" ($outside -join ', '))
    New-Item -ItemType Directory -Force -Path $Parent | Out-Null
    Expand-Archive -LiteralPath $Archive -DestinationPath $Parent
    return (Join-Path $Parent $Folder)
}

# The extracted layout and install record; runs the application from it.
function Test-Extracted([string]$Root, $App, [string]$Version, [bool]$WithUpdates, [string]$RootSubject) {
    $appDir = Join-Path $Root 'app'
    $main = Join-Path $appDir 'FixtureApp.exe'
    Assert-That (Test-Path -LiteralPath $main) "$Version extracted: app\FixtureApp.exe exists"
    foreach ($file in 'FixtureApp.dll', 'FixtureApp.runtimeconfig.json', 'FixtureApp.deps.json', 'version.txt') {
        [void](Test-That (Test-Path -LiteralPath (Join-Path $appDir $file)) "app\$file exists")
    }
    Test-Equal (Get-Content -LiteralPath (Join-Path $appDir 'version.txt') -Raw).Trim() $Version "app\version.txt is $Version"
    $top = @(Get-ChildItem -LiteralPath $Root -Force | ForEach-Object { $_.Name } | Sort-Object)
    $expected = @('app', 'zexoro-install.json') + $(if ($WithUpdates) { @('zexoro-updater.exe') } else { @() })
    Test-Equal ($top -join ', ') (($expected | Sort-Object) -join ', ') 'the root holds app\, the record and (with [updates]) the updater, nothing else'

    $record = Read-Json (Join-Path $Root 'zexoro-install.json')
    $fields = [ordered]@{ schema = 1; app = $App.Id; version = $Version; os = 'windows'; arch = 'x64'; format = 'zip'; scope = 'user'; strategy = 'replace' }
    foreach ($name in $fields.Keys) { Test-Equal (Get-Field $record $name) $fields[$name] "zexoro-install.json $name" }
    if ($WithUpdates) {
        Test-Equal (Get-Field $record 'channel') 'stable' 'zexoro-install.json channel'
        Test-Equal (Get-Field $record 'feed') "http://localhost:8080/$($App.Scope)/stable/feed.json" 'zexoro-install.json feed'
        Test-Equal (@(Get-Field $record 'keys') -join ',') $script:UpdateKey 'zexoro-install.json keys'
        Test-Equal (Get-Field $record 'main') 'FixtureApp.exe' 'zexoro-install.json main'
        if ($RootSubject) { Test-Signatures @($main, (Join-Path $Root 'zexoro-updater.exe')) $RootSubject }
    } else {
        [void](Test-That ($null -eq (Get-Field $record 'feed')) 'zexoro-install.json has no feed without [updates]')
        Test-Equal (Get-AuthenticodeSignature -LiteralPath $main).Status 'NotSigned' 'the main executable is not signed without [windows.signing]'
    }

    if (Test-Path -LiteralPath $DataDir) { Remove-Item -LiteralPath $DataDir -Recurse -Force }
    Test-Equal (Invoke-Program $main @()) 0 'the extracted app exits with 0'
    Test-Equal (Read-Marker) "$appDir\" 'the extracted app ran (marker written)'
    Test-Equal @(Get-MatchingUninstallKeys $App).Count 0 'nothing is registered (no uninstall entry)'
}

# The installed version as the files and the install record tell it, with nothing staged.
function Test-Version($App, [string]$Version, [string]$Context) {
    Test-Equal (Get-Content -LiteralPath (Join-Path $App.Root 'app\version.txt') -Raw).Trim() $Version "$Context`: app\version.txt is $Version"
    Test-Equal (Read-Json (Join-Path $App.Root 'zexoro-install.json')).version $Version "$Context`: zexoro-install.json version is $Version"
    $staged = @(Get-ChildItem -LiteralPath $App.Root -Force | Where-Object { $_.Name -match '\.new$' } | ForEach-Object { $_.Name })
    [void](Test-That ($staged.Count -eq 0) "$Context`: nothing is staged in the root" ($staged -join ', '))
}

# Denies the current user creating files and folders directly in $Path; returns what undoes it.
function Deny-Create([string]$Path) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls.exe $Path /deny "*$($sid):(WD,AD)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls /deny failed with $LASTEXITCODE" }
    return { & icacls.exe $Path /remove:d "*$sid" | Out-Null }.GetNewClosure()
}

function Test-Unsigned {
    $archive = Join-Path $InstallerDir 'unsigned\ZexoroE2EUser-1.0.0-win-x64.zip'
    Write-Host ''
    Write-Host '== unsigned zip without [updates]' -ForegroundColor Cyan
    if (-not (Test-Path -LiteralPath $archive)) { Write-Fail "$archive exists"; return }
    $app = New-E2EApp 'user' $InstallerDir
    [void](Test-That (-not (Test-Path -LiteralPath "$archive.release.json")) 'no release fragment without [updates]')
    $root = Expand-Package $archive (Join-Path $script:WorkDir 'unsigned') $app.FileName
    Test-Extracted $root $app '1.0.0' $false ''
}

function Test-Portable([string]$RootSubject) {
    $app = [pscustomobject]@{
        Scope     = 'portable'
        Id        = 'dev.zexoro.e2e.portable'
        Name      = 'Zexoro E2E Portable'
        FileName  = 'ZexoroE2EPortable'
        Root      = Join-Path $script:WorkDir 'my apps\ZexoroE2EPortable'
        State     = Join-Path $env:LOCALAPPDATA 'zexoro\updater\dev.zexoro.e2e.portable'
        Installer = @{
            '1.0.0' = Join-Path $InstallerDir 'portable\ZexoroE2EPortable-1.0.0-win-x64.zip'
            '2.0.0' = Join-Path $InstallerDir 'portable\ZexoroE2EPortable-2.0.0-win-x64.zip'
        }
    }
    $fragment = Read-Json ($app.Installer['2.0.0'] + '.release.json')
    $artifact = @($fragment.release.artifacts)[0]
    $url = [Uri]$artifact.url
    Test-Equal "$($artifact.os)/$($artifact.arch)/$($artifact.format)/$($artifact.scope)" 'windows/x64/zip/user' 'the release fragment describes a windows/x64/zip/user artifact'
    $app | Add-Member -NotePropertyName Served -NotePropertyValue (Join-Path $script:WebRoot ([IO.Path]::GetDirectoryName($url.AbsolutePath.TrimStart('/'))))

    Write-Host ''
    Write-Host "== $($app.Name) (zip, strategy replace, $($app.Root))" -ForegroundColor Cyan
    foreach ($archive in $app.Installer.Values) { Assert-That (Test-Path -LiteralPath $archive) "archive $archive exists" }
    Get-Process -Name FixtureApp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $DataDir) { Remove-Item -LiteralPath $DataDir -Recurse -Force }
    Remove-State $app
    New-Item -ItemType Directory -Force -Path $app.Served | Out-Null
    $running = $null
    $apply = $null
    $undo = $null
    try {
        Write-Host '-- extract 1.0.0'
        [void](Expand-Package $app.Installer['1.0.0'] (Split-Path -Parent $app.Root) $app.FileName)
        Test-Extracted $app.Root $app '1.0.0' $true $RootSubject
        [void](Test-That (Test-Path -LiteralPath (Join-Path $app.Root 'app\obsolete.txt')) 'app\obsolete.txt exists in 1.0.0')
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
        Test-Equal (Get-Field $available.Json 'size') (Get-Item -LiteralPath $app.Installer['2.0.0']).Length 'check reports the size of the 2.0.0 archive'

        Write-Host '-- replay of the older feed (exit 4)'
        Publish-Feed $app 'seq1'
        [void](Test-UpdaterRun $app 'check' $ExitVerificationFailed 'error' 'the validly signed feed with sequence 1 is served after sequence 2' 'sequence')

        Write-Host '-- an artifact that does not match its sha256 (exit 4)'
        Publish-Feed $app 'seq2'
        Set-FlippedByte (Join-Path $app.Served (Split-Path -Leaf $app.Installer['2.0.0']))
        [void](Test-UpdaterRun $app 'download' $ExitVerificationFailed 'error' 'the served 2.0.0 archive has one byte changed' 'SHA-256')
        Test-Equal ((Get-Downloads $app) -join ', ') '' 'nothing was kept in the downloads'
        [void](Test-UpdaterRun $app 'apply' $ExitUpToDate 'up-to-date' 'nothing is downloaded, so there is nothing to apply')
        Test-Version $app '1.0.0' 'after the verification failures'
        [void](Test-That (Test-Path -LiteralPath (Join-Path $app.Root 'app\obsolete.txt')) 'after the verification failures: app\obsolete.txt is still there')

        Write-Host '-- download 2.0.0'
        Publish-Installers $app
        $server.Requests.Clear()
        $downloaded = Test-UpdaterRun $app 'download' $ExitAvailable 'downloaded' 'the 2.0.0 archive matches the feed'
        Test-Equal (Get-Field $downloaded.Json 'version') '2.0.0' 'download reports version 2.0.0'
        [void](Test-That (@($server.Requests) -contains $url.AbsolutePath) "the updater fetched $($url.AbsolutePath)" (@($server.Requests) -join ', '))
        Test-Equal ((Get-Downloads $app) -join ', ') ((@((Split-Path -Leaf $app.Installer['2.0.0']), 'staged.json') | Sort-Object) -join ', ') 'the verified archive is staged in the downloads'

        Write-Host '-- apply while the application runs, then restart it'
        if (Test-Path -LiteralPath $DataDir) { Remove-Item -LiteralPath $DataDir -Recurse -Force }
        $running = Start-Process -FilePath (Join-Path $app.Root 'app\FixtureApp.exe') -ArgumentList '--wait' -WorkingDirectory $env:TEMP -PassThru
        Assert-That (Wait-Until { $null -ne (Read-Marker) } 60) 'the application started (with --wait)'
        Remove-Item -LiteralPath (Join-Path $DataDir 'ran.txt') -Force
        $lastApply = Join-Path $app.State 'last-apply.json'
        if (Test-Path -LiteralPath $lastApply) { Remove-Item -LiteralPath $lastApply -Force }
        $applyOut = Join-Path $script:WorkDir 'apply.out'
        $applyErr = Join-Path $script:WorkDir 'apply.err'
        $apply = Start-Process -FilePath (Join-Path $app.Root 'zexoro-updater.exe') -ArgumentList @('apply', '--wait-pid', $running.Id, '--restart', '--json') `
            -WorkingDirectory $env:TEMP -RedirectStandardOutput $applyOut -RedirectStandardError $applyErr -NoNewWindow -PassThru
        $null = $apply.Handle
        Start-Sleep -Seconds 3
        [void](Test-That (-not $apply.HasExited) 'apply waits while the application runs')
        Test-Equal (Get-Content -LiteralPath (Join-Path $app.Root 'app\version.txt') -Raw).Trim() '1.0.0' 'nothing changed while the application runs'
        $running | Stop-Process -Force
        $running.WaitForExit()
        $running = $null

        if (-not $apply.WaitForExit($TimeoutSeconds * 1000)) { throw 'apply did not exit' }
        $apply.WaitForExit()
        Write-Host "      apply stdout: $((Read-SharedText $applyOut).Trim())"
        Write-Host "      apply stderr: $((Read-SharedText $applyErr).Trim())"
        Test-Equal $apply.ExitCode 0 'apply exits with 0'
        Assert-That (Test-Path -LiteralPath $lastApply) 'the update finished (last-apply.json written)'
        $report = Read-Json $lastApply
        Test-Equal (Get-Field $report 'status') 'applied' 'last-apply.json status is applied'
        Test-Equal (Get-Field $report 'installed') '1.0.0' 'last-apply.json installed is 1.0.0'
        Test-Equal (Get-Field $report 'version') '2.0.0' 'last-apply.json version is 2.0.0'
        $restarted = Wait-Until { $null -ne (Read-Marker) } 60
        [void](Test-That $restarted 'the application was restarted')
        Test-Equal (Read-Marker) (Join-Path $app.Root 'app\') 'the restarted application runs from the extracted folder'

        Write-Host '-- 2.0.0 is in place'
        Test-Version $app '2.0.0' 'updated'
        [void](Test-That (-not (Test-Path -LiteralPath (Join-Path $app.Root 'app\obsolete.txt'))) 'app\obsolete.txt is gone')
        $record = Read-Json (Join-Path $app.Root 'zexoro-install.json')
        Test-Equal "$($record.format)/$($record.scope)/$($record.strategy)" 'zip/user/replace' 'zexoro-install.json is still zip/user/replace'
        foreach ($file in 'FixtureApp.exe', 'FixtureApp.dll', 'version.txt') {
            [void](Test-That (Test-Path -LiteralPath (Join-Path $app.Root "app\$file")) "app\$file exists")
        }
        [void](Test-That (Test-Path -LiteralPath (Join-Path $app.Root 'zexoro-updater.exe')) 'zexoro-updater.exe exists')
        if ($RootSubject) { Test-Signatures @((Join-Path $app.Root 'app\FixtureApp.exe'), (Join-Path $app.Root 'zexoro-updater.exe')) $RootSubject }
        # The handing-off updater still runs while the swap renames it, so zexoro-updater.exe.old
        # stays for the next apply to remove.
        $leftovers = @(Get-ChildItem -LiteralPath $app.Root -Force | Where-Object { $_.Name -match '\.(new|old)$' -and $_.Name -ne 'zexoro-updater.exe.old' } | ForEach-Object { $_.Name })
        [void](Test-That ($leftovers.Count -eq 0) 'no other .new or .old items in the root' ($leftovers -join ', '))
        Test-Equal ((Get-Downloads $app) -join ', ') '' 'the downloads were cleared'
        Test-Equal @(Get-MatchingUninstallKeys $app).Count 0 'still nothing is registered'
        [void](Wait-Until { @(Get-Process -Name 'zexoro-updater' -ErrorAction SilentlyContinue).Count -eq 0 } 60)
        Get-Process -Name FixtureApp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

        Write-Host '-- up to date afterwards'
        $after = Test-UpdaterRun $app 'check' $ExitUpToDate 'up-to-date' '2.0.0 is the newest release'
        Test-Equal (Get-Field $after.Json 'installed') '2.0.0' 'check reports installed 2.0.0'

        Write-Host '-- a 1.0.0 copy in a folder the user may not write to (exit 7)'
        Remove-Item -LiteralPath $app.Root -Recurse -Force
        [void](Expand-Package $app.Installer['1.0.0'] (Split-Path -Parent $app.Root) $app.FileName)
        [void](Test-UpdaterRun $app 'download' $ExitAvailable 'downloaded' '2.0.0 is downloaded for the read-only copy')
        $undo = Deny-Create $app.Root
        [void](Test-UpdaterRun $app 'apply' $ExitManualUpdate 'error' 'the folder is not writable' 'not writable')
        Test-Version $app '1.0.0' 'the read-only copy'
        [void](Test-That (Test-Path -LiteralPath (Join-Path $app.Root 'app\obsolete.txt')) 'the read-only copy: app\obsolete.txt is still there')
        & $undo
        $undo = $null
        Write-Host '-- the same copy once it is writable again'
        [void](Test-UpdaterRun $app 'apply' $ExitAvailable 'applied' 'the kept download is applied')
        Test-Version $app '2.0.0' 'after making the folder writable'
    } catch {
        Write-Fail "$($app.Name) aborted: $_"
    } finally {
        if ($null -ne $undo) { & $undo }
        if ($null -ne $running -and -not $running.HasExited) { $running | Stop-Process -Force }
        if ($null -ne $apply -and -not $apply.HasExited) { $apply | Stop-Process -Force }
        Get-Process -Name FixtureApp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        [void](Wait-Until { @(Get-Process -Name 'zexoro-updater' -ErrorAction SilentlyContinue).Count -eq 0 } 30)
        Remove-State $app
        if (Test-Path -LiteralPath $DataDir) { Remove-Item -LiteralPath $DataDir -Recurse -Force }
    }
}

$InstallerDir = (Resolve-Path -LiteralPath $InstallerDir).Path
Write-Host "Packages: $InstallerDir"
foreach ($variant in 'seq1', 'seq2', 'other-key') {
    $feed = Join-Path $InstallerDir "feeds\portable\$variant\feed.json"
    if (-not (Test-Path -LiteralPath $feed)) { throw "$feed not found; pack with the current tests/e2e/pack.sh" }
}
$script:UpdateKey = (Get-Content -LiteralPath (Join-Path $InstallerDir 'zexoro-update.pub') -Raw).Trim()

# With administrator rights, trust the throwaway root that issued the signing certificate for the run.
$certificate = $null
$store = $null
if (Test-Administrator) {
    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (Join-Path $InstallerDir 'root.cer')
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store -ArgumentList 'Root', 'LocalMachine'
    $store.Open('ReadWrite')
    $store.Add($certificate)
    Write-Host "Trusting $($certificate.Subject) ($($certificate.Thumbprint)) in LocalMachine\Root until the end of the test"
} else {
    Write-Host 'Not an administrator: Authenticode signatures are not checked'
}

$firstUrl = [Uri](@((Read-Json (Join-Path $InstallerDir 'portable\ZexoroE2EPortable-2.0.0-win-x64.zip.release.json')).release.artifacts)[0].url)
$prefix = "$($firstUrl.Scheme)://$($firstUrl.Authority)/"
$script:WorkDir = Join-Path ([IO.Path]::GetTempPath()) ("zexoro-zip-e2e-" + [Guid]::NewGuid().ToString('N'))
$script:WebRoot = Join-Path $script:WorkDir 'www'
New-Item -ItemType Directory -Force -Path $script:WebRoot | Out-Null
$server = Start-FeedServer $script:WebRoot $prefix
Write-Host "Serving $script:WebRoot at $prefix"
try {
    Test-Unsigned
    Test-Portable $(if ($certificate) { $certificate.Subject } else { '' })
} finally {
    Stop-FeedServer $server
    Remove-Item -LiteralPath $script:WorkDir -Recurse -Force -ErrorAction SilentlyContinue
    if ($store) {
        $store.Remove($certificate)
        $store.Close()
        Write-Host "Removed $($certificate.Thumbprint) from LocalMachine\Root"
    }
}

exit (Complete-Run)
