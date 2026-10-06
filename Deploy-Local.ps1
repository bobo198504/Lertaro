# Elevated deploy for local testing: replace freshly built assemblies in a running Lertaro install,
# then bring it back up. Mirrors make.bat's publish layout, so a deploy is just a copy over the install.
#
# STOPPING THE APP IS PART OF DEPLOYING, NOT OF BUILDING. DEVELOPMENT_GUIDE.md rule 2 says not to kill the
# App or Service for ordinary compilation -- the build does not conflict with a running install. This
# script does kill them, because the copy below has to overwrite DLLs the running processes hold open.
# That is the only reason, and it is why a deploy should be a deliberate act rather than a reflex after
# every build. Ask before running it against someone's live install.
#
# Usage (elevate first -- it stops the service and overwrites files in the install directory):
#   powershell -ExecutionPolicy Bypass -File Deploy-Local.ps1 -Publish -Full
#   powershell -ExecutionPolicy Bypass -File Deploy-Local.ps1 -InstallDir 'D:\App protable\Lertaro'
#   powershell -ExecutionPolicy Bypass -File Deploy-Local.ps1 -Assemblies 'Lertaro.App.dll'
#
# -Publish -Full is the "deploy this source tree" call: rebuild publish\x64\Lertaro from source and
# mirror the whole tree over the install. Without -Publish it reuses an existing publish\ output;
# without -Full it only copies the three assemblies named in -Assemblies.
#
# -NoRestart copies the files and leaves the install down for the user to start. Useful when the restart
# cannot work (see the note on Restart-App) or when the user would rather launch it themselves.
#
# NOTE (parameters under -File): powershell.exe's own command line has no notion of a [string[]], so
# `-File ... -Assemblies a.dll b.dll` binds only a.dll and hands b.dll to the next positional
# parameter -- which is how a second name silently became -InstallDir. Pass a comma-joined single
# value instead:  -Assemblies 'Lertaro.App.dll,Lertaro.Core.dll'.
#
# NOTE (paths with spaces): `Start-Process powershell -Verb RunAs -ArgumentList @('-File', 'D:\App
# protable\...')` loses the quoting and the path is cut at the space -- the elevated run then reports a
# missing install directory that plainly exists. Pass ONE pre-quoted string instead.
#
# Why a script rather than a few copy commands: the app holds its assemblies open, the service is
# elevated (a plain taskkill gets access-denied against it), and the relaunch has a trap -- see the
# note on Restart-App below. Getting any of the three wrong produces a confusing half-broken install.
param(
    # Where the published/built assemblies are.
    [string] $SourceDir = (Join-Path $PSScriptRoot 'publish\x64\Lertaro'),
    # The installed app to update. Point this at your own install location.
    [string] $InstallDir = 'D:\App protable\Lertaro',
    # Which assemblies to copy. Defaults to what a normal code change touches; narrow it with e.g.
    # -Assemblies 'Lertaro.App.dll' for a UI-only change. Under -File, join several with commas (see
    # the NOTE in the header) -- this stays [string[]] so an interactive/dot-sourced call can still
    # pass a real array.
    [string[]] $Assemblies = @('Lertaro.App.dll', 'Lertaro.Core.dll', 'Lertaro.PluginSdk.dll'),
    # Skip the relaunch (when you want to start the app yourself).
    [switch] $NoRestart,
    # Stop at the copy step without touching the service or the running processes. Use this to verify
    # the script itself (paths, assembly parsing, hashes) without disturbing a live install.
    [switch] $DryRun,
    # Mirror the WHOLE source tree over the install instead of copying $Assemblies one by one. This is
    # what a full deploy needs: listing files by name silently misses new plugins, their sub-directories
    # and their dependencies, so a "successful" deploy can leave the install running stale code.
    # Data\ (user settings, indexes, logs) is never touched -- see the exclusion below.
    [switch] $Full,
    # Rebuild $SourceDir before deploying. Needed because make.bat deletes publish\ as its last step,
    # so the default -SourceDir usually does not exist yet.
    #
    # NOTE: this must go through the shared toolchain, NOT `dotnet` from PATH. The machine-wide
    # C:\Program Files\dotnet has runtimes but no SDK at all ("No SDKs were found"), so `where dotnet`
    # succeeds and the publish then fails -- which is exactly the trap make.bat falls into.
    [switch] $Publish
)

$ErrorActionPreference = 'Continue'

# A comma-joined -Assemblies value arrives as one element; split it so both call styles work.
$Assemblies = @($Assemblies | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

# NOTE (paths): the source tree path contains non-ASCII characters, which batch/PowerShell round-trips
# mangle when handed to native tools. Keep any such path in a variable and let PowerShell quote it.
$log = Join-Path $PSScriptRoot 'deploy-local.log'

function Say($m) {
    $line = ('[' + (Get-Date -Format 'HH:mm:ss') + '] ' + $m)
    $line | Out-File -FilePath $log -Append -Encoding utf8
    Write-Host $line
}

# Bring the install back up the way portable-updater.bat does: start the SERVICE, and let it start the
# App as it comes up.
#
# This mirrors the real updater deliberately, because the two hand-offs it avoids are both traps that
# were already hit and documented there:
#
#   - An elevated Start-Process hands the App this script's elevation. An elevated Lertaro.App cannot
#     share the hook process's named pipe with the non-elevated Service (hook.log fills with
#     "HookIpcServer: Access to the path is denied"), and its Settings > Plugins page comes up empty --
#     looking exactly like a code regression when it is not.
#   - explorer.exe is not the fix for that from here either. portable-updater.bat keeps it strictly as a
#     fallback for an install with no service to lean on, and says why: UIPI drops a shell request made
#     from an elevated process, so the launch silently does nothing. In a sandboxed session it is worse
#     than silent -- explorer dies with 0xc0000142 and puts an error dialog on the user's desktop.
#
# The service is the one process that can start the App at the session's own integrity level, and it does
# that from UpdateRelaunchMarker -- a one-line note in the shared data directory, read once as the service
# starts. Writing that note is THIS SCRIPT'S JOB, and it is the step that is easy to miss: the real
# updater writes it as part of applying an update (UpdateApplyRequestHandler), so a hand-rolled copy over
# the install has nothing to trigger the relaunch and the service comes up with no App.
function Write-RelaunchNote {
    # <install>\Data\Machine for a portable copy -- DataDirectoryResolver.ResolveShared. Written as the
    # updater writes it: "utc ticks TAB session TAB exe path", and only fresh for five minutes.
    $shared = Join-Path $InstallDir 'Data\Machine'
    $exe = Join-Path $InstallDir 'Lertaro.App.exe'
    if (-not (Test-Path $exe)) { Say "cannot arm relaunch: $exe missing"; return $false }

    # The session the USER is in, not this elevated process's -- the whole point is to launch into their
    # desktop. explorer.exe is the session's own process, so its id is the answer.
    $session = (Get-Process -Name 'explorer' -ErrorAction SilentlyContinue | Select-Object -First 1).SessionId
    if (-not $session -or $session -le 0) {
        # Nothing to launch into is a real possibility over RDP/Service-account runs, and a bad session id
        # makes the service skip the note. Say so instead of writing one that cannot work.
        Say 'cannot arm relaunch: no interactive session found'
        return $false
    }

    try {
        New-Item -ItemType Directory -Path $shared -Force | Out-Null
        $note = Join-Path $shared 'update-relaunch'
        Set-Content -Path $note -Value ("{0}`t{1}`t{2}" -f [DateTimeOffset]::UtcNow.UtcTicks, $session, $exe) -NoNewline -Encoding utf8
        Say "armed relaunch: session=$session exe=$exe"
        return $true
    } catch {
        Say "could not arm the relaunch: $($_.Exception.Message)"
        return $false
    }
}

function Restart-App {
    Write-RelaunchNote | Out-Null

    Say 'starting the service (it relaunches the App at the session integrity level)'
    & sc.exe start LertaroService | Out-Null
    Start-Sleep -Seconds 10

    $svc = (& sc.exe query LertaroService | Select-String 'STATE') -join ' '
    Say "service:$svc"

    # The App is started by the service, so it is never this process's child. Whether it came up is judged
    # from ITS log rather than from a process list: the sandbox denies this process the handle it would
    # need to enumerate others (tasklist answers "Access denied"), so a null Get-Process proves nothing.
    $appLog = Get-ChildItem (Join-Path $InstallDir 'Data\Users') -Recurse -Filter 'app.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($appLog) {
        $fresh = (Get-Date) - $appLog.LastWriteTime
        Say ("app.log last written {0:N0}s ago ({1})" -f $fresh.TotalSeconds, $appLog.LastWriteTime)
        if ($fresh.TotalSeconds -lt 60) {
            Say 'App started (its log is being written).'
            return
        }
    }

    Say 'WARNING: the App does not appear to have started (its log has not moved).'
    Say "Start it by hand: $InstallDir\Lertaro.App.exe"
}

Remove-Item $log -ErrorAction SilentlyContinue
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Say "elevated=$elevated"
if (-not $elevated) { Say 'WARNING: not elevated; stopping the service and writing the install will likely fail.' }

Say "source=$SourceDir exists=$(Test-Path $SourceDir)"
Say "install=$InstallDir exists=$(Test-Path $InstallDir)"
if (-not (Test-Path $InstallDir)) { Say 'ABORT: install dir missing.'; exit 1 }

if ($Publish) {
    # Rebuild the publish tree the way make.bat does, minus the packaging half (installer, zips,
    # arm64) -- a local deploy only needs the x64 tree. make.bat itself is not used because it shells
    # out to plain `dotnet`, which on this machine resolves to the SDK-less system install.
    $dn = 'D:\Projects\Code\_tools\dotnet10\dotnet.exe'
    if (-not (Test-Path $dn)) { Say "ABORT: shared toolchain not found at $dn"; exit 1 }
    Say "PUBLISH: rebuilding $SourceDir with $dn"
    if (Test-Path $SourceDir) { Remove-Item $SourceDir -Recurse -Force }
    New-Item -ItemType Directory -Path $SourceDir -Force | Out-Null

    $env:NUGET_CONFIG_FILE = 'D:\Projects\Code\_tools\nuget.config'
    & $dn publish (Join-Path $PSScriptRoot 'Lertaro.slnx') -c Release -o $SourceDir -v minimal -m:1 -nodeReuse:false -p:NuGetAudit=false 2>&1 |
        Select-Object -Last 5 | ForEach-Object { Say "  $_" }
    if ($LASTEXITCODE -ne 0) { Say "ABORT: solution publish failed ($LASTEXITCODE)."; exit 1 }

    & $dn publish (Join-Path $PSScriptRoot 'Lertaro.Plugins.slnx') -c Release -o (Join-Path $SourceDir 'Plugins') -v minimal -m:1 -nodeReuse:false -p:NuGetAudit=false 2>&1 |
        Select-Object -Last 5 | ForEach-Object { Say "  $_" }
    if ($LASTEXITCODE -ne 0) { Say "ABORT: plugin publish failed ($LASTEXITCODE)."; exit 1 }

    # Match make.bat step 3: ship the portable helper scripts, drop the pdb files.
    foreach ($b in 'portable-updater.bat', 'install-dotnet-runtime.bat', 'portable-cleanup.bat') {
        Copy-Item (Join-Path $PSScriptRoot $b) $SourceDir -Force -ErrorAction SilentlyContinue
    }
    Get-ChildItem $SourceDir -Recurse -File -Filter '*.pdb' | Remove-Item -Force -ErrorAction SilentlyContinue
    Say ("PUBLISH: done, {0} file(s)" -f (Get-ChildItem $SourceDir -Recurse -File).Count)
}

if (-not (Test-Path $SourceDir)) { Say 'ABORT: source dir missing (run make.bat, or pass -Publish).'; exit 1 }

# Stop the service through the SCM first (a clean shutdown), then force-kill stragglers. The service
# runs elevated, so Stop-Process here is what reaches it -- a plain taskkill cannot.
#
# -DryRun returns before any of this: killing the user's running app to rehearse a copy against a
# scratch directory is exactly the accident this switch exists to prevent.
if ($DryRun) {
    Say 'DRYRUN: stopping before the service/process teardown (no files were replaced).'
    if ($Full) {
        $srcFiles = Get-ChildItem -Path $SourceDir -Recurse -File | Where-Object {
            $rel = $_.FullName.Substring($SourceDir.Length).TrimStart('\')
            -not ($rel -eq 'Data' -or $rel -like 'Data\*')
        }
        $new = 0; $diff = 0; $same = 0
        foreach ($f in $srcFiles) {
            $rel = $f.FullName.Substring($SourceDir.Length).TrimStart('\')
            $dst = Join-Path $InstallDir $rel
            if (-not (Test-Path $dst)) { $new++ }
            elseif ((Get-FileHash $f.FullName -Algorithm SHA256).Hash -eq (Get-FileHash $dst -Algorithm SHA256).Hash) { $same++ }
            else { $diff++ }
        }
        Say ("DRYRUN FULL: {0} files -- {1} new, {2} changed, {3} already identical" -f $srcFiles.Count, $new, $diff, $same)
    }
    else {
        foreach ($name in $Assemblies) {
            $src = Join-Path $SourceDir $name
            Say ("DRYRUN {0}: {1}" -f $name, $(if (Test-Path $src) { 'would copy' } else { 'SKIP (not in source)' }))
        }
    }
    Say 'done (dry run)'
    exit 0
}

& sc.exe stop LertaroService | Out-Null
Start-Sleep -Seconds 2

$names = @('Lertaro.App.exe', 'Lertaro.Service.exe', 'lff.exe')
for ($i = 0; $i -lt 10; $i++) {
    $alive = @()
    foreach ($n in $names) {
        $procs = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($n)) -ErrorAction SilentlyContinue
        if ($procs) { $alive += $procs }
    }
    if (-not $alive) { break }
    foreach ($p in $alive) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction Stop; Say "killed $($p.ProcessName) pid=$($p.Id)" }
        catch { Say "kill failed $($p.ProcessName) pid=$($p.Id): $($_.Exception.Message)" }
    }
    & taskkill.exe /F /IM Lertaro.App.exe 2>&1 | Out-Null
    & taskkill.exe /F /IM Lertaro.Service.exe 2>&1 | Out-Null
    & taskkill.exe /F /IM lff.exe 2>&1 | Out-Null
    Start-Sleep -Seconds 1
}

# Copy each assembly, retrying briefly in case a lock lingers a moment after the kill, then compare
# hashes. The hash check is the point of the exercise: a half-written or silently skipped copy is
# otherwise indistinguishable from a successful deploy until the app misbehaves at runtime.
$mismatch = @()

if ($Full) {
    # Mirror the source tree. Data\ is the one thing that must never be overwritten: it holds the
    # machine index caches, the service log and each user's settings/history, none of which exist in
    # the publish output anyway -- copying over it would clobber live state.
    Say "FULL deploy: mirroring $SourceDir"
    $srcFiles = Get-ChildItem -Path $SourceDir -Recurse -File | Where-Object {
        $rel = $_.FullName.Substring($SourceDir.Length).TrimStart('\')
        -not ($rel -eq 'Data' -or $rel -like 'Data\*')
    }
    Say ("FULL deploy: {0} source file(s) to consider" -f $srcFiles.Count)

    $copiedCount = 0; $sameCount = 0
    foreach ($f in $srcFiles) {
        $rel = $f.FullName.Substring($SourceDir.Length).TrimStart('\')
        $dst = Join-Path $InstallDir $rel
        $dstDir = Split-Path $dst -Parent
        if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }

        # Skip files already identical, so the log shows what actually changed.
        if (Test-Path $dst) {
            try {
                if ((Get-FileHash $f.FullName -Algorithm SHA256).Hash -eq (Get-FileHash $dst -Algorithm SHA256).Hash) {
                    $sameCount++
                    continue
                }
            } catch { }
        }

        $ok = $false
        for ($i = 0; $i -lt 8; $i++) {
            try { Copy-Item -Path $f.FullName -Destination $dst -Force -ErrorAction Stop; $ok = $true; break }
            catch { Start-Sleep -Seconds 1 }
        }
        if (-not $ok) { Say "FAILED $rel (locked?)"; $mismatch += $rel; continue }

        try {
            $sh = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
            $dh = (Get-FileHash $dst -Algorithm SHA256).Hash
        } catch { Say "copied $rel but hashing failed: $($_.Exception.Message)"; $mismatch += $rel; continue }

        if ($sh -eq $dh) { $copiedCount++ }
        else { Say "MISMATCH $rel  src=$($sh.Substring(0,16)) dst=$($dh.Substring(0,16))"; $mismatch += $rel }
    }
    Say ("FULL deploy: {0} copied, {1} already identical, {2} failed" -f $copiedCount, $sameCount, $mismatch.Count)
}
else {
foreach ($name in $Assemblies) {
    $src = Join-Path $SourceDir $name
    $dst = Join-Path $InstallDir $name
    if (-not (Test-Path $src)) { Say "SKIP $name (not in source)"; continue }

    $copied = $false
    for ($i = 0; $i -lt 8; $i++) {
        try { Copy-Item -Path $src -Destination $dst -Force -ErrorAction Stop; $copied = $true; break }
        catch { Start-Sleep -Seconds 1 }
    }
    if (-not $copied) { Say "FAILED $name (locked?)"; $mismatch += $name; continue }

    # Read both files after the write has closed; a hash taken from the copy call's own handle would
    # not catch a truncated destination.
    try {
        $sh = (Get-FileHash -Path $src -Algorithm SHA256 -ErrorAction Stop).Hash
        $dh = (Get-FileHash -Path $dst -Algorithm SHA256 -ErrorAction Stop).Hash
    } catch {
        Say "copied $name but hashing failed: $($_.Exception.Message)"
        $mismatch += $name
        continue
    }
    if ($sh -eq $dh) { Say ("copied $name  sha256={0}" -f $sh.Substring(0, 16)) }
    else { Say "MISMATCH $name  src=$($sh.Substring(0,16)) dst=$($dh.Substring(0,16))"; $mismatch += $name }
}
}

if (-not $NoRestart) { Restart-App }
if ($mismatch.Count -gt 0) { Say ("done with PROBLEMS: " + ($mismatch -join ', ')); exit 1 }
Say 'done'
