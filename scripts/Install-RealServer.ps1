<#
.SYNOPSIS
Backs up the stopped real Jellyfin server and installs one plugin's release into it, built from the release tag.
User-run only.

.DESCRIPTION
For the person who runs the server, after the sandbox checks. It never starts, stops or calls a
server.

What it installs: the plugin's release tag <plugin folder in lower case>/v<major>.<minor>.<patch> (bookmarks/v1.1.1),
-Version's or else the newest by version (compared as numbers: v1.10.0 is newer than v1.9.0) of the server's line (a
Jellyfin 10.11 server takes the releases its build.yaml describes, a Jellyfin 12 server those of build.12.yaml), never the working tree it
runs from: between releases main carries unreleased work under the released version number, which would otherwise go onto
the real server as that release. It refuses when there is no such tag, when two tags name the version, when the tag's
plugins/<Plugin>/build.yaml (build.12.yaml for a Jellyfin 12 server) doesn't say the tag's version as x.y.z.0, when no branch contains the tag's commit, and when the
temporary worktree's folder (next to the repository: ..\jfp-rel-<plugin>-<x.y.z>) is already taken. It checks the tag out
there as a detached worktree locked with this run's own reason (git worktree add --lock --reason "Install-RealServer.ps1
run <id> (PID ..., started ...)"), backs the data folder up, builds the tag in Release (Deploy-Plugin.ps1 -RealServer
-SourceRoot <the worktree> -ReleaseLock <its reason>, which installs nothing but that worktree, unchanged, nothing ignored
or hidden in it), installs that build from a folder of this run's own, checks every installed file by SHA-256 against the
build, records the hashes in backup.json and RESTORE.txt, and always removes the worktree afterwards (git worktree unlock,
git worktree remove --force, git worktree prune), also when something failed; only a worktree locked with this run's
reason is ever removed, so another run's at the same path is left alone. Afterwards the build is also in
the repository's artifacts\deploy\<Plugin>\Release, where -NoBuild finds it.

The scripts themselves run from this checkout's working tree, whichever tag is built: for the real server it refuses unless
the checkout's PowerShell scripts (scripts\*.ps1, *.psm1, *.psd1) are its HEAD's, nothing changed, added or hidden from git
status. -Plugin is matched to the plugin's folder in any case (-Plugin bookmarks is Bookmarks). Paths are
read with this script's own PowerShell drives first, so a drive made with New-PSDrive is followed to the folder it names.

-FromWorkingTree keeps the old behaviour for rehearsals: it builds the checkout's working tree (or, with -NoBuild, installs
its last Release build). It is refused for the real server's data folder in every spelling the scripts' path guard knows
(C:\ProgramData\Jellyfin\Server, its 8.3 name, \\?\ and share spellings, a junction or PowerShell drive to it, a drive
letter Windows doesn't know, ...). Without it, a stopped sandbox's data folder gets the release tag's build too, so a
rehearsal tests the real path.

1. Refuses while any jellyfin.exe uses DataDir (exit Jellyfin from its tray icon first): Jellyfin locks loaded plugin DLLs,
   and a copy of a live database isn't a reliable backup. A jellyfin.exe whose command line can't be read counts as using
   it. It also refuses a DataDir that isn't a Jellyfin data folder (no config and data folders, or a drive letter or share
   that isn't there: a Refused line too), and a BackupRoot under C:\ProgramData\Jellyfin or
   inside DataDir. All before the worktree is made or anything is copied.
2. Checks the tag out (above), then takes a cold backup to BackupRoot\<yyyyMMdd-HHmmss>-before-<Plugin>-<version>:
   config (the server's settings), root (the library definitions), plugins (every plugin's files, and
   plugins\configurations, the plugins' settings), the
   databases and small files directly in data (jellyfin.db with its -wal and -shm, device.txt, ...; not *.old, *.bak or
   *.tmp copies), the small data folders (collections, playlists, ScheduledTasks, introskipper) and the plugin's own data
   folder data\<plugin> when it is there (data\bookmarks for Bookmarks). It leaves out what Jellyfin regenerates or keeps
   for itself: metadata, cache, log, transcodes, trickplay images, subtitles, attachments and the SQLiteBackups copies.
   Every copied file is counted and measured again in the backup; a difference stops the script before anything is
   installed. backup.json lists what was copied (and the tag and commit installed), and RESTORE.txt gives the two ways
   back: A removes the plugin alone (its folder, its data folder, its settings file, an earlier version's folder put back)
   and keeps everything the server recorded since; B puts the whole backup back, which loses everything recorded since the
   backup's date (watched states, playback positions, users, library changes), and says so. For Access Roles, A starts with
   Release all locks (each user's original library access ticked) while the plugin still runs, and both move its data
   folder aside, renamed data\accessroles.removed-<yyyyMMdd-HHmmss>, instead of deleting it: the ledger and the mirror's
   PreferenceMirror records its README's recovery reads stay. For User Ratings, A starts with the exports its README's
   way back to Jellyfin Enhanced needs, while the plugin still runs, both
   move data\userratings aside the same way (the members' ratings and reviews, never deleted), and A says how to turn
   Jellyfin Enhanced's user reviews back on; the backup lists Jellyfin Enhanced's settings file and reviews.json (in
   plugins\configurations), or says they aren't there.
3. Runs Deploy-Plugin.ps1 -RealServer on the tag's worktree (with -FromWorkingTree: Deploy-Plugin.ps1 on the working tree),
   whose DLL allowlist and reference-version check read the server's program folder (-JellyfinDir), and which installs
   plugins\<Name>_<version> with its meta.json and checks the installed files by SHA-256. Their hashes go into backup.json
   ("installed") and RESTORE.txt ("Its files, ...") once the install is done.
   The plugin's image (plugins\<Plugin>\image.png, its card on the Plugins page) is installed with it: the release tag's
   when the tag has one, or else the one committed at this checkout's HEAD (read from git, not from the working tree), so
   that reinstalling a release tagged before the plugins had images gets one; with -FromWorkingTree, the working tree's.
   Which one it was, with its SHA-256, goes into backup.json ("installed": "image") and RESTORE.txt.

-WhatIf shows the checks' outcome, the tag with its commit and the version it would install, and what would be backed up
and installed, and changes nothing: no worktree, no backup, no build.

A refusal is written to the host as well ("Refused: ..."), before it is thrown, and so is a failed install after the
backup ("Stopped: ..."): a thrown error isn't captured by *> or 6>&1, so a recording of a refused run used to hold
nothing. Under powershell.exe -File the host is stdout.

.PARAMETER Plugin
The folder name under plugins/, for example Bookmarks, in any case. Its release tags are <Plugin in lower case>/v<x.y.z>.

.PARAMETER DataDir
Jellyfin's data folder, by default the real server's C:\ProgramData\Jellyfin\Server. The repository's own checks only
ever pass a stopped sandbox's data folder.

.PARAMETER JellyfinDir
The Jellyfin program folder the plugin is for (only read, for the reference-version check), by default the installed
C:\ProgramData\Jellyfin\Server.

.PARAMETER BackupRoot
Where the backup folder is made, by default ~\JellyfinBackups.

.PARAMETER Version
The release to install, as <major>.<minor>.<patch> (1.1.1 for the tag bookmarks/v1.1.1). By default the newest release tag.

.PARAMETER FromWorkingTree
Rehearsals only: builds the working tree instead of a release tag, as the script once did. Refused for the real
server's data folder.

.PARAMETER NoBuild
With -FromWorkingTree only: installs the Release build already in artifacts\deploy\<Plugin>\Release instead of building
it again. A release tag is always built.

.EXAMPLE
./scripts/Install-RealServer.ps1 -Plugin Bookmarks -Version 1.1.1 -WhatIf

.EXAMPLE
./scripts/Install-RealServer.ps1 -Plugin Bookmarks -Version 1.1.1
# The version -WhatIf showed, so a tag made in between can't change what is installed.

.EXAMPLE
./scripts/Install-RealServer.ps1 -Plugin Bookmarks -WhatIf
# The newest release tag.

.EXAMPLE
./scripts/Install-RealServer.ps1 -Plugin Bookmarks -DataDir .sandbox\je\data -BackupRoot $env:TEMP\rehearsal -FromWorkingTree
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string] $Plugin,

    [string] $DataDir = 'C:\ProgramData\Jellyfin\Server',

    [string] $JellyfinDir = 'C:\ProgramData\Jellyfin\Server',

    [string] $BackupRoot = (Join-Path $HOME 'JellyfinBackups'),

    [string] $Version,

    [switch] $FromWorkingTree,

    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'JellyfinSandbox.psm1') -Force
# CimCmdlets (Get-CimInstance, for the running-server check) loaded here, outside -WhatIf: autoloaded under -WhatIf it
# printed a "What if: ... Set Alias" line for each of its aliases before the plan, in a fresh console.
& { $WhatIfPreference = $false; Import-Module CimCmdlets }

function Stop-Refused {
    <#
    Ends the script with a refusal, written to the host first ("Refused: ..."; stdout under powershell.exe -File): a thrown
    error isn't captured by *> or 6>&1, so a recording of the run would hold nothing of it.
    The thrown message is the same text.
    #>
    param([Parameter(Mandatory = $true)] [string] $Message, [string] $Outcome = 'Nothing was backed up or installed.')
    Write-Host ("Refused: $Message $Outcome").TrimEnd()
    throw $Message
}

$repo = Get-RepoRoot
# PowerShell spellings (a drive of New-PSDrive's, FileSystem::, ~) read as the folders they name, with this script's own
# drives, before any check: .NET doesn't know such a drive, while Test-Path and Copy-Item follow it.
$DataDir = ConvertFrom-DevicePath (Resolve-FullPath (ConvertTo-FileSystemPath -Path $DataDir -SessionState $ExecutionContext.SessionState))
$BackupRoot = ConvertFrom-DevicePath (Resolve-FullPath (ConvertTo-FileSystemPath -Path $BackupRoot -SessionState $ExecutionContext.SessionState))
$JellyfinDir = ConvertFrom-DevicePath (Resolve-FullPath (ConvertTo-FileSystemPath -Path $JellyfinDir -SessionState $ExecutionContext.SessionState))
# The server's line (10.11, or 12 for a Jellyfin 12.x): its release tags, its build file (build.12.yaml) and its build. Without
# a readable Jellyfin in -JellyfinDir, 10.11; Deploy-Plugin.ps1 then refuses before building and names -JellyfinDir.
$jellyfinTarget = '10.11'
try { $jellyfinTarget = Get-JellyfinTargetOf -JellyfinDir $JellyfinDir } catch { $jellyfinTarget = '10.11' }

# --- 0. What is installed: the release tag, or for a rehearsal the working tree, never onto the real server.
if ($FromWorkingTree) {
    # First of all, before anything reads the data folder: the working tree never goes onto the real server.
    if (Test-RealServerTarget -DataDir $DataDir) {
        Stop-Refused ("-FromWorkingTree builds the working tree, which is for rehearsals on a stopped sandbox only, and $DataDir is the real server's " +
            'data folder (under C:\ProgramData\Jellyfin, or a share, device path or drive the scripts cannot tell apart from it). Leave out -FromWorkingTree to install a release tag.')
    }

    if ($Version) { Stop-Refused '-Version picks a release tag, and -FromWorkingTree builds the working tree instead. Use one of them.' }
    $release = $null
    try { $info = Get-PluginBuildInfo -Plugin $Plugin -JellyfinTarget $jellyfinTarget } catch { Stop-Refused $_.Exception.Message }
}
else {
    if ($NoBuild) {
        Stop-Refused "-NoBuild installs the working tree's last build, so it needs -FromWorkingTree (rehearsals only). A release is always built from its tag."
    }

    if ($Plugin -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') { Stop-Refused "'$Plugin' is not a plugin folder name." }

    # The tag is built, but these scripts run as the checkout has them: onto the real server only the committed ones.
    # A sandbox rehearsal may run scripts being changed.
    if (Test-RealServerTarget -DataDir $DataDir) {
        $scriptsProblem = Get-RepoScriptsProblem -RepoRoot $repo -Running @($PSCommandPath, (Join-Path $PSScriptRoot 'Deploy-Plugin.ps1'), (Join-Path $PSScriptRoot 'JellyfinSandbox.psm1'))
        if ($scriptsProblem) { Stop-Refused $scriptsProblem }
    }

    try {
        $release = Resolve-PluginRelease -Plugin $Plugin -RepoRoot $repo -Version $Version -JellyfinTarget $jellyfinTarget
        $worktreeProblem = Get-ReleaseWorktreeProblem -Path $release.WorktreePath -RepoRoot $repo
    }
    catch {
        Stop-Refused $_.Exception.Message
    }

    if ($worktreeProblem) { Stop-Refused $worktreeProblem }
    $info = $release.Info
}

$Plugin = $info.Folder   # the folder's own spelling (-Plugin bookmarks is Bookmarks)
$pluginDataFolder = $info.Folder.ToLowerInvariant()   # the plugin's own data folder: data\bookmarks

# --- 1. Checks: a Jellyfin data folder, no server using it, a backup folder outside the server's. Asked of .NET, not of
# Join-Path and Test-Path, which stop with their own error for a drive letter that isn't there:
# every refusal is a "Refused: ..." line.
$dataDrive = [System.IO.Path]::GetPathRoot($DataDir)
if (-not $dataDrive -or -not [System.IO.Directory]::Exists($dataDrive)) {
    Stop-Refused "$DataDir is not a Jellyfin data folder (its drive or share $dataDrive isn't there)."
}

foreach ($required in 'config', 'data') {
    if (-not [System.IO.Directory]::Exists([System.IO.Path]::Combine($DataDir, $required))) {
        Stop-Refused "$DataDir is not a Jellyfin data folder (it has no '$required' folder)."
    }
}

$running = @(Get-JellyfinProcessesForDataDir -DataDir $DataDir)
if ($running.Count -gt 0) {
    Stop-Refused "Jellyfin is still running with data folder $DataDir (PID $(($running | ForEach-Object { $_.ProcessId }) -join ', ')). Exit it from the tray icon (or stop the service) first."
}

if (Test-ProtectedPath $BackupRoot) {
    Stop-Refused "Refusing to put the backup in $BackupRoot, inside the real server's folder. Choose another -BackupRoot."
}

if (Test-PathUnder $BackupRoot $DataDir) {
    Stop-Refused "Refusing to put the backup in $BackupRoot, inside the data folder it backs up. Choose another -BackupRoot."
}

# --- 2. What the backup holds.
$dataRoot = Join-Path $DataDir 'data'
$items = New-Object System.Collections.Generic.List[object]
foreach ($folder in 'config', 'root', 'plugins') {
    $items.Add([pscustomobject]@{ Source = Join-Path $DataDir $folder; Relative = $folder })
}

$leftOut = New-Object System.Collections.Generic.List[string]
foreach ($file in @(Get-ChildItem -LiteralPath $dataRoot -File -Force)) {
    if ($file.Name -match '\.(old|tmp|bak\d*)$') {
        $leftOut.Add("data\$($file.Name)")
        continue
    }

    $items.Add([pscustomobject]@{ Source = $file.FullName; Relative = "data\$($file.Name)" })
}

foreach ($folder in @('collections', 'playlists', 'ScheduledTasks', 'introskipper', $pluginDataFolder) | Select-Object -Unique) {
    $items.Add([pscustomobject]@{ Source = Join-Path $dataRoot $folder; Relative = "data\$folder" })
}

function Measure-BackupItem {
    <# The files of a file or folder (recursively, hidden ones too) and their total size. #>
    param([Parameter(Mandatory = $true)] [string] $Path)
    $files = @()
    if (Test-Path -LiteralPath $Path -PathType Container) {
        $files = @(Get-ChildItem -LiteralPath $Path -Recurse -File -Force)
    }
    elseif (Test-Path -LiteralPath $Path) {
        $files = @(Get-Item -LiteralPath $Path -Force)
    }

    $bytes = [long]0
    foreach ($f in $files) { $bytes += $f.Length }
    return [pscustomobject]@{ Files = $files.Count; Bytes = $bytes }
}

$measured = @($items | Where-Object { Test-Path -LiteralPath $_.Source } | ForEach-Object {
        $m = Measure-BackupItem $_.Source
        [pscustomobject]@{ Item = $_; Files = $m.Files; Bytes = $m.Bytes }
    })
$notThere = @($items | Where-Object { -not (Test-Path -LiteralPath $_.Source) } | ForEach-Object { $_.Relative })
$totalBytes = [long]0
$totalFiles = 0
foreach ($m in $measured) { $totalBytes += $m.Bytes; $totalFiles += $m.Files }

$taken = Get-Date
$stamp = $taken.ToString('yyyyMMdd-HHmmss', [System.Globalization.CultureInfo]::InvariantCulture)
$backup = Join-Path $BackupRoot "$stamp-before-$($info.Folder)-$($info.Version)"
if ($release) {
    $tagList = "$(@($release.Tags).Count) release tags ($(@($release.Tags | ForEach-Object { $_.Name }) -join ', '))"
    $which = "the newest of $tagList"
    if (-not $release.Newest) { $which = "picked by -Version $($release.Version) from $tagList, the newest being $($release.Tags[0].Name)" }
    $branches = @($release.Branches | Select-Object -First 3) -join ', '
    if (@($release.Branches).Count -gt 3) { $branches += ', ...' }
    Write-Host "Release: $($release.Tag), commit $($release.Commit) (on $branches), $($info.Name) $($info.Version) by its $($info.YamlName), $which"
    Write-Host ("Build: Release, from a temporary detached worktree of the tag at $($release.WorktreePath) (locked for this run, removed afterwards), " +
        "into a build folder of this run's own, installed from there and checked by SHA-256, then copied to artifacts\deploy\$($info.Folder)\Release")
    $source = "$($release.Tag) (commit $($release.Commit))"
    $operation = "Back up to $backup, then install $($info.Name) $($info.Version) built from $($release.Tag) ($($release.Commit.Substring(0, 7)))"
}
else {
    $how = 'built in Release'
    if ($NoBuild) { $how = "its last Release build, artifacts\deploy\$($info.Folder)\Release" }
    Write-Host "Source: the working tree of $repo (-FromWorkingTree, a rehearsal: never the real server), $($info.Name) $($info.Version) by its $($info.YamlName), $how"
    $source = "the working tree of $repo (-FromWorkingTree)"
    $operation = "Back up to $backup, then install $($info.Name) $($info.Version) from the working tree"
}

# The plugin's image, as Deploy-Plugin.ps1 picks it: the tag's, or else the one committed at this checkout's HEAD.
try {
    if ($release) { $image = Resolve-PluginImage -Plugin $info.Folder -RepoRoot $repo -Commit $release.Commit -CommitLabel $release.Tag -FallbackToHead }
    else { $image = Resolve-PluginImage -Plugin $info.Folder -RepoRoot $repo -SourceFolder $repo }
}
catch {
    Stop-Refused "Reading the plugin's image failed: $($_.Exception.Message)"
}

if ($image) { Write-Host "Image: $($image.Name) from $($image.From), SHA-256 $($image.Sha256)" }
else { Write-Host "Image: none (no plugins/$($info.Folder)/$(Get-PluginImageName) $(if ($release) { "in $($release.Tag) or at this checkout's HEAD" } else { 'in the working tree' }))" }
Write-Host "Data folder: $DataDir (no jellyfin.exe uses it)"
Write-Host ("Backup to {0}: {1} file(s), {2:N1} MB in {3}" -f $backup, $totalFiles, ($totalBytes / 1MB), (($measured | ForEach-Object { $_.Item.Relative }) -join ', '))
if ($notThere.Count -gt 0) { Write-Host "Not there, so not backed up: $($notThere -join ', ')" }
# User Ratings imports Jellyfin Enhanced's reviews and has its user reviews switched off: its settings file and
# reviews.json are in plugins\configurations, so in the backup; said by name, so the way back can count on them.
$jeFiles = @()
if ($info.Folder -ieq 'UserRatings') {
    $jeFiles = @('plugins\configurations\Jellyfin.Plugin.JellyfinEnhanced.xml', 'plugins\configurations\Jellyfin.Plugin.JellyfinEnhanced\reviews.json' | Where-Object {
            [System.IO.File]::Exists([System.IO.Path]::Combine($DataDir, $_))
        })
    if ($jeFiles.Count -gt 0) { Write-Host "Jellyfin Enhanced's files, in plugins: $($jeFiles -join ', ')" }
    else { Write-Host 'Jellyfin Enhanced: no settings file or reviews.json in plugins\configurations, so none to back up' }
}
if ($leftOut.Count -gt 0) { Write-Host "Left out (old copies): $($leftOut -join ', ')" }

if (-not $PSCmdlet.ShouldProcess($DataDir, $operation)) {
    return
}

# The worktree is this run's only while git has it locked with this reason: Deploy-Plugin.ps1 -RealServer
# installs nothing else, and the clean-up below removes nothing else, so another run that took the same path in the moment
# between the check and the checkout keeps its worktree.
$lockReason = New-ReleaseLockReason
$worktree = $null
$worktreeTried = $false
try {
    if ($release) {
        # Free a moment ago; taken since, it isn't ours, and nothing of it is touched.
        $worktreeProblem = Get-ReleaseWorktreeProblem -Path $release.WorktreePath -RepoRoot $repo
        if ($worktreeProblem) { Stop-Refused $worktreeProblem }
        $worktreeTried = $true
        try { New-ReleaseWorktree -RepoRoot $repo -Path $release.WorktreePath -Commit $release.Commit -LockReason $lockReason } catch { Stop-Refused $_.Exception.Message }
        $worktree = $release.WorktreePath
        Write-Host "Checked out $($release.Tag) into $worktree (detached at $($release.Commit.Substring(0, 7)), locked: $lockReason)"
    }

    if (Test-Path -LiteralPath $backup) {
        Stop-Refused "$backup is already there; run the script again in a second."
    }

    New-Item -ItemType Directory -Force -Path (Join-Path $backup 'data') | Out-Null
    foreach ($m in $measured) {
        $target = Join-Path $backup $m.Item.Relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $m.Item.Source -Destination $target -Recurse -Force
    }

    # Every file copied, at the size it has, or nothing is installed.
    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($m in $measured) {
        $copy = Measure-BackupItem (Join-Path $backup $m.Item.Relative)
        if ($copy.Files -ne $m.Files -or $copy.Bytes -ne $m.Bytes) {
            $problems.Add(("{0}: {1} file(s) and {2} bytes in the data folder, {3} file(s) and {4} bytes in the backup" -f $m.Item.Relative, $m.Files, $m.Bytes, $copy.Files, $copy.Bytes))
        }
    }

    if ($problems.Count -gt 0) {
        Stop-Refused -Message "The backup in $backup doesn't match the data folder, so nothing was installed: $($problems -join '; ')." -Outcome ''
    }

    $hadPluginData = Test-Path -LiteralPath (Join-Path $dataRoot $pluginDataFolder)

    # What removing the plugin alone needs to know (RESTORE.txt, way A): its settings file (Jellyfin names it after the main
    # assembly) and the plugin's earlier version folders, which Deploy-Plugin.ps1 removes.
    $settingsFile = [System.IO.Path]::ChangeExtension($info.Assemblies[0], '.xml')
    $hadSettingsFile = Test-Path -LiteralPath (Join-Path $DataDir "plugins\configurations\$settingsFile")
    $installed = "$($info.Name)_$($info.Version)"
    # A build of the same version installed before (a working tree's, say) is replaced in place; way A puts it back.
    $hadSameVersion = Test-Path -LiteralPath (Join-Path $DataDir "plugins\$installed")
    $versionPattern = '^' + [regex]::Escape($info.Name) + '_\d+(\.\d+){1,3}$'
    $earlier = @(Get-ChildItem -LiteralPath (Join-Path $DataDir 'plugins') -Directory -ErrorAction SilentlyContinue | Where-Object {
            $folder = $_
            $sameGuid = $false
            $meta = Join-Path $folder.FullName 'meta.json'
            if (Test-Path -LiteralPath $meta) {
                try { $sameGuid = ((Read-JsonFile $meta).guid -ieq $info.Guid) } catch { $sameGuid = $false }
            }

            $folder.Name -ne $installed -and ($sameGuid -or $folder.Name -match $versionPattern)
        } | ForEach-Object { $_.Name })

    $tagName = $null
    $commit = $null
    if ($release) { $tagName = $release.Tag; $commit = $release.Commit }
    # "installed" (the plugin folder and each installed file's SHA-256) is filled in once the install is done.
    $manifest = [ordered]@{
        plugin     = $info.Name
        version    = $info.Version
        tag        = $tagName
        commit     = $commit
        dataDir    = $DataDir
        createdUtc = $taken.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ', [System.Globalization.CultureInfo]::InvariantCulture)
        files      = $totalFiles
        bytes      = $totalBytes
        items      = @($measured | ForEach-Object { [ordered]@{ path = $_.Item.Relative; files = $_.Files; bytes = $_.Bytes } })
        notThere   = @($notThere)
        leftOut    = @($leftOut)
        installed  = $null
    }
    Write-JsonFile -Path (Join-Path $backup 'backup.json') -Value $manifest
    # Two ways back: removing the plugin alone keeps everything the server recorded since; putting the
    # whole backup back (jellyfin.db among it) loses it, so that one says so, with the backup's date.
    $when = $taken.ToString('yyyy-MM-dd HH:mm', [System.Globalization.CultureInfo]::InvariantCulture)
    # Access Roles' data folder is moved aside, never deleted: it holds the ledger (each
    # managed user's library access from before) and the mirror's PreferenceMirror records, which its README's recovery reads
    # once the plugin is gone; and the locks are released first, while the plugin still runs.
    # User Ratings' too: data\userratings holds the members' ratings and reviews (userratings.db with its -wal, its
    # snapshots and its archive), which nothing else holds once Jellyfin Enhanced's user reviews are off; and the exports its
    # README's way back to Jellyfin Enhanced needs come first, while the plugin still runs.
    $isRatings = $info.Folder -ieq 'UserRatings'
    $keepsRecords = ($info.Folder -ieq 'AccessRoles') -or $isRatings
    $aside = "$DataDir\data\$pluginDataFolder.removed-<yyyyMMdd-HHmmss>"
    $moveAside = "Rename-Item -LiteralPath '$DataDir\data\$pluginDataFolder' -NewName ('$pluginDataFolder.removed-' + (Get-Date -Format yyyyMMdd-HHmmss))"
    $restore = New-Object System.Collections.Generic.List[string]
    $restore.Add("This backup was taken on $when (this computer's time), before $($info.Name) $($info.Version) was installed into $DataDir.")
    $restore.Add("What was installed: $($info.Name) $($info.Version) built from $source.")
    $restore.Add('There are two ways back. A removes the plugin and keeps everything else as it is now: use it. B puts the whole server back to the moment of this backup.')
    $restore.Add('')
    $restore.Add("A. Remove $($info.Name) $($info.Version) only.")
    $restore.Add('   Everything the server recorded since the backup stays: watched states and playback positions, users and their settings, libraries, the other plugins.')
    if ($isRatings) {
        $restore.Add("0. First, while the plugin is still enabled and the server still starts: on its page's Data tab, Export (every member's ratings and reviews, the file to import again later) and, to give them back to Jellyfin Enhanced, Export for Jellyfin Enhanced after its import's dry run and import, as its README's Going back to Jellyfin Enhanced says. Keep both files. If the server doesn't start any more, go on: step 3 keeps the database.")
    }
    elseif ($keepsRecords) {
        $restore.Add("0. First, while the plugin is still enabled and the server still starts: on its page's Overview, under Before disabling or deleting Access Roles, tick Also restore each user's original library access, press Release all locks... and then Release them. Each managed user gets back the library access from before Access Roles managed them, and each native preference access its mirror turned off is turned back on. Without it the library access stays as last synced. If the server doesn't start any more, go on: step 3 keeps the records that put it back by hand.")
    }

    $restore.Add('1. Exit Jellyfin (its tray icon: Exit).')
    $restore.Add("2. Delete $DataDir\plugins\$installed.")
    if ($hadSameVersion) {
        $restore.Add("   Copy this backup's plugins\$installed folder back into $DataDir\plugins (the build of this version installed before, which this install replaced).")
    }

    foreach ($folder in $earlier) {
        $restore.Add("   Copy this backup's plugins\$folder folder back into $DataDir\plugins (the version installed before).")
    }

    if ($keepsRecords) {
        $restore.Add("3. Move $DataDir\data\$pluginDataFolder aside, renamed with the time, never delete it: in PowerShell, $moveAside")
        if ($isRatings) {
            $restore.Add("   $aside keeps the members' ratings and reviews (userratings.db with its -wal), its snapshots and its archive: installing User Ratings again, put it back as $DataDir\data\$pluginDataFolder with Jellyfin stopped, or import the export of step 0.")
        }
        else {
            $restore.Add("   $aside keeps the ledger (each managed user's library access from before Access Roles managed them) and the PreferenceMirror records (the users whose native preference access the mirror turned off), which the plugin's README reads in its Recovery after the plugin folder was deleted with the mirror on.")
        }
        $restore.Add($(if ($hadPluginData) { "   Then copy this backup's data\$pluginDataFolder folder back into $DataDir\data: the plugin's data as it was before." } else { "   It wasn't there before: nothing is copied back." }))
    }
    elseif ($hadPluginData) {
        $restore.Add("3. Replace $DataDir\data\$pluginDataFolder with this backup's data\$($pluginDataFolder): the plugin's data as it was before (what it stored since$(if ($Plugin -eq 'Bookmarks') { ', its clips in its default folder among it,' }) is lost).")
    }
    else {
        $restore.Add("3. Delete $DataDir\data\$pluginDataFolder, what the plugin stored$(if ($Plugin -eq 'Bookmarks') { ' (and its clips, when they are in its default folder)' }). It wasn't there before.")
    }

    if ($hadSettingsFile) {
        $restore.Add("4. Copy this backup's plugins\configurations\$settingsFile back into $DataDir\plugins\configurations: the plugin's settings as they were.")
    }
    else {
        $restore.Add("4. Delete $DataDir\plugins\configurations\$settingsFile, the plugin's settings. It wasn't there before.")
    }

    $restore.Add("5. If you changed other plugins' settings along with it (its README says which), set them back on their settings pages, or copy only those plugins' files from this backup's plugins\configurations back. Copying the whole folder back also undoes every other plugin setting changed since $when.")
    if ($isRatings) {
        $restore.Add("   Jellyfin Enhanced (User Ratings' README, the switch-over): once Jellyfin has started, turn its user reviews back on (on its settings page, tick again the boxes the switch-over checklist had you untick). Its reviews.json is as you left it; the export for Jellyfin Enhanced of step 0 goes in its place as that README says. This backup's plugins\configurations\Jellyfin.Plugin.JellyfinEnhanced\reviews.json is JE's file from before User Ratings, without what was rated here since.")
    }

    $restore.Add('6. Start Jellyfin.')
    $restore.Add('')
    $restore.Add("B. Put the whole server back to the moment of this backup, $when. Only when the server itself is broken.")
    $restore.Add("   WARNING: this loses everything the server recorded since $when (data\jellyfin.db is its database): watched states and playback positions, users and their settings, library changes, and every plugin's settings and data. To remove the plugin, use A.")
    $restore.Add('1. Exit Jellyfin (its tray icon: Exit).')
    $restore.Add("2. Delete $DataDir\plugins\$installed.")
    $restore.Add("3. Copy this backup's config, root and plugins folders over the ones in $DataDir (plugins\configurations holds every plugin's settings).")
    $restore.Add("4. Copy this backup's data folder over $DataDir\data.")
    if ($keepsRecords) {
        $restore.Add($(if ($hadPluginData) { "   data\$pluginDataFolder was there before, and the backup has it as it was: first move the one there now aside, as A's step 3 does ($moveAside), so the copy puts it back as it was and its records since stay in $aside." } else { "   data\$pluginDataFolder wasn't there before: move it aside, as A's step 3 does ($moveAside), never delete it: $aside keeps its records." }))
    }
    else {
        $restore.Add($(if ($hadPluginData) { "   data\$pluginDataFolder was there before, and the backup has it as it was." } else { "   data\$pluginDataFolder wasn't there before: delete $DataDir\data\$pluginDataFolder to remove what the plugin stored (and its clips, when they are in its default folder)." }))
    }
    $restore.Add('5. Start Jellyfin.')
    Write-Utf8NoBom -Path (Join-Path $backup 'RESTORE.txt') -Text (($restore -join "`r`n") + "`r`n")
    Write-Host ("Backed up and checked: {0} file(s), {1:N1} MB." -f $totalFiles, ($totalBytes / 1MB))

    # --- 3. Install: Deploy's DLL allowlist and reference-version check against -JellyfinDir come first. A release goes
    # through Deploy-Plugin.ps1 -RealServer, which builds and installs nothing but this run's worktree of the release tag,
    # from a build folder of its own, and checks the installed files by SHA-256.
    try {
        if ($release) {
            $deployed = @(& (Join-Path $PSScriptRoot 'Deploy-Plugin.ps1') -Plugin $Plugin -DataDir $DataDir -Configuration Release -RealServer -SourceRoot $worktree -ReleaseLock $lockReason -PassThru -JellyfinDir $JellyfinDir -JellyfinTarget $jellyfinTarget)
        }
        else {
            $deployed = @(& (Join-Path $PSScriptRoot 'Deploy-Plugin.ps1') -Plugin $Plugin -DataDir $DataDir -Configuration Release -NoBuild:$NoBuild -PassThru -JellyfinDir $JellyfinDir -JellyfinTarget $jellyfinTarget)
        }

        $result = @($deployed | Where-Object { $null -ne $_ -and $_.PSObject.Properties['Files'] -and $_.PSObject.Properties['Target'] })
        if ($result.Count -ne 1) { throw 'Deploy-Plugin.ps1 said nothing about what it installed.' }
        $result = $result[0]
    }
    catch {
        # On the host as well, as the refusals above are, so a recording of the run says it.
        Write-Host "Stopped: the install failed after the backup: $($_.Exception.Message) The backup is in $backup; its RESTORE.txt gives the ways back."
        throw
    }

    # What was installed, by SHA-256, as Deploy checked the copies against the build: in backup.json and RESTORE.txt.
    $installedImage = $null
    if ($result.PSObject.Properties['Image'] -and $result.Image) {
        $installedImage = [ordered]@{ name = $result.Image.Name; source = $result.Image.Source; commit = $result.Image.Commit; from = $result.Image.From; sha256 = $result.Image.Sha256; bytes = $result.Image.Bytes }
    }

    $manifest.installed = [ordered]@{
        folder = $result.Target
        files  = @($result.Files | ForEach-Object { [ordered]@{ name = $_.Name; sha256 = $_.Sha256; bytes = $_.Bytes } })
        image  = $installedImage
    }
    Write-JsonFile -Path (Join-Path $backup 'backup.json') -Value $manifest
    $hashes = @($result.Files | ForEach-Object { "$($_.Name) $($_.Sha256)" }) -join ', '
    $restore.Insert(2, "   Its files in $($result.Target), checked after the copy against the build (SHA-256): $hashes.")
    if ($installedImage) { $restore.Insert(3, "   Its image, $($installedImage.name), came from $($installedImage.from).") }
    Write-Utf8NoBom -Path (Join-Path $backup 'RESTORE.txt') -Text (($restore -join "`r`n") + "`r`n")
    Write-Host "Recorded in backup.json and RESTORE.txt: $hashes"
}
finally {
    # The release's worktree goes whatever happened, but only this run's: one another run made at the
    # same path, locked with its own reason, is left as it is. A leftover of ours is said, with the commands that clear it.
    if ($worktreeTried) {
        $path = $release.WorktreePath
        $removal = Remove-ReleaseWorktree -RepoRoot $repo -Path $path -LockReason $lockReason
        switch ($removal.Status) {
            'Removed' { Write-Host "Removed the temporary worktree $path (git worktree unlock, git worktree remove --force, git worktree prune)." }
            'None' { }
            'NotOurs' { Write-Host "Left $path as it is: $($removal.Problem)." }
            default {
                $message = ("Could not remove the temporary worktree ${path}: $($removal.Problem). Remove it once nothing uses it: git -C `"$repo`" worktree unlock `"$path`" " +
                    "(if it is still locked), git -C `"$repo`" worktree remove --force `"$path`", then git -C `"$repo`" worktree prune.")
                Write-Host "Warning: $message"
                Write-Warning $message
            }
        }
    }
}

Write-Host ''
Write-Host 'Done. Start Jellyfin again (Start menu > Jellyfin Server, or the tray app).'
Write-Host "To roll back: $backup\RESTORE.txt"
