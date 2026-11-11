<#
.SYNOPSIS
Builds one plugin and installs it into a Jellyfin data folder's plugins directory.

.DESCRIPTION
Reads plugins/<Plugin>/build.yaml (the single source of truth) and publishes the plugin project
into artifacts/deploy/<Plugin>/<Configuration> (with -SourceRoot, into a folder of this run's own; below). The publish
output must contain no *.dll other than the build.yaml artifacts (the meta.json "assemblies" allowlist): that catches
server assemblies, SQLite and other dependencies that must never ship with a plugin. Every assembly those DLLs reference
that the target Jellyfin program folder (-JellyfinDir, only read) ships must be referenced at a version no higher than the
shipped one (the reference-version check): a plugin compiled against a newer
Microsoft.Data.Sqlite than the server has passes the allowlist but can stop the whole server at startup. It then:
- copies the allowlisted DLLs and their PDBs to <DataDir>\plugins\<name>_<version>, and checks the copies by SHA-256
  against the build that was checked (taken right after the build): a difference removes the folder again and stops;
- removes every other <name>_<version> folder of this plugin (and any folder whose meta.json has its GUID);
- copies the plugin's image, plugins/<Plugin>/image.png, as image.png into the same folder and checks it by SHA-256 too
  (Resolve-PluginImage): this repository's working tree's file; with -SourceRoot, that checkout's (for -RealServer, the
  release tag's, read from git), or else the one committed at this repository's HEAD, so that a release tagged before the
  plugins had images gets one all the same. No image anywhere installs none;
- writes meta.json (UTF-8 without BOM) from build.yaml plus timestamp, status, autoUpdate=false, assemblies and, with an
  image, imagePath ("image.png": Jellyfin serves it at GET /Plugins/{id}/{version}/Image for the Plugins page's card).

Jellyfin loads plugins at startup, so restart the server afterwards.

C:\ProgramData\Jellyfin (the real server) is refused unless -RealServer is passed, the data folder is exactly
C:\ProgramData\Jellyfin\Server, and no jellyfin.exe using it is running (loaded DLLs are locked). Any other
data folder is refused while a jellyfin.exe using it is running. Use Install-RealServer.ps1 for the real server: it
backs it up first and passes -RealServer with the release tag's checkout.

-RealServer installs a release tag's build and nothing else: -SourceRoot must be the temporary worktree
Install-RealServer.ps1 made of the plugin's release tag for this run, locked with the reason -ReleaseLock names, its
HEAD the commit of the tag <plugin>/v<x.y.z> for the x.y.z.0 its build.yaml says, nothing changed, added,
ignored or hidden from git status in it; -NoBuild and a Debug build are refused. Install-RealServer.ps1 passes -RealServer
for a stopped sandbox's data folder as well, so a rehearsal takes the real path.

Paths are read as the folders they name with this script's own PowerShell drives first: .NET doesn't know a
drive made with New-PSDrive, while Test-Path and Copy-Item follow it, so the guard must see where it leads.

.PARAMETER Plugin
The folder name under plugins/, for example Bookmarks (in any case).

.PARAMETER DataDir
Jellyfin's data folder (the one holding config, data and plugins), for example .sandbox\default\data.

.PARAMETER SourceRoot
The checkout whose plugins/<Plugin> is built and whose build.yaml is installed, by default this repository's working tree.
Another checkout is published into a folder only this run uses, artifacts\builds\<Plugin>-<Configuration>-<time>-<id> in
this repository, checked and installed from there, and copied into artifacts\deploy\<Plugin>\<Configuration> (what
-NoBuild installs) only after the install; that folder is removed at the end (any other build of the
plugin in this checkout lands in artifacts\deploy and would otherwise have been installed under the release's name). The
run folder's name can be predicted, so each file in it must be the compiler's output in the checkout's
obj\<Configuration>\<framework> (inside the locked release worktree for -RealServer), whose SHA-256 is the baseline the
copies are checked against and Install-RealServer.ps1 records; a file written there after the build is refused before
anything is installed. The build ignores persistent build servers (--disable-build-servers),
so nothing keeps a file of a temporary checkout open.

.PARAMETER ReleaseLock
With -RealServer: the lock reason Install-RealServer.ps1 gave the release worktree it made for this run.
Only a worktree locked with exactly this reason is installed.

.PARAMETER PassThru
Returns what was installed: Plugin, Name, Version, Configuration, Target (the plugin folder), Files (each installed
DLL and PDB, and the image, with its SHA-256 and size) and Image (Name, From, Source, Commit, Sha256 and Bytes, or $null
without one), which Install-RealServer.ps1 records in the backup's backup.json and RESTORE.txt.

.PARAMETER JellyfinTarget
The Jellyfin the build is for: 10.11 (the default, net9.0) or 12. The Jellyfin in -JellyfinDir must be that version
(10.11 is 10.11.x, 12 is 12.x); otherwise Deploy fails before building and names -JellyfinDir.

.PARAMETER JellyfinDir
The program folder of the Jellyfin the plugin is for (the one holding jellyfin.exe and the assemblies it ships),
by default the installed C:\ProgramData\Jellyfin\Server. Only read, for the reference-version check; its jellyfin.dll
version must match -JellyfinTarget, so a -JellyfinTarget 12 deploy needs a -JellyfinDir with a Jellyfin 12.x.

.EXAMPLE
./scripts/Deploy-Plugin.ps1 -Plugin Bookmarks -DataDir .sandbox\default\data -Configuration Debug

.EXAMPLE
./scripts/Install-RealServer.ps1 -Plugin Bookmarks -Version 1.1.1
# The real server: Install-RealServer.ps1 checks bookmarks/v1.1.1 out into a worktree locked for its run and runs
# Deploy-Plugin.ps1 -RealServer -SourceRoot <that worktree> -ReleaseLock <its lock>; Deploy refuses anything else.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Plugin,

    [Parameter(Mandatory = $true)]
    [string] $DataDir,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $NoBuild,

    [ValidateSet('10.11', '12')]
    [string] $JellyfinTarget = '10.11',

    [string] $JellyfinDir = 'C:\ProgramData\Jellyfin\Server',

    [switch] $RealServer,

    [string] $SourceRoot,

    [string] $ReleaseLock,

    [switch] $PassThru
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'JellyfinSandbox.psm1') -Force

$repo = Get-RepoRoot
# PowerShell spellings (a drive of New-PSDrive's, FileSystem::, ~) read as the folders they name, with this script's own
# drives, before any check.
$DataDir = ConvertFrom-DevicePath (Resolve-FullPath (ConvertTo-FileSystemPath -Path $DataDir -SessionState $ExecutionContext.SessionState))
$JellyfinDir = ConvertFrom-DevicePath (Resolve-FullPath (ConvertTo-FileSystemPath -Path $JellyfinDir -SessionState $ExecutionContext.SessionState))
$production = Get-ProductionDataDir

# --- Where may we write?
if (Test-ProtectedPath $DataDir) {
    if (-not $RealServer) {
        throw "Refusing to deploy into $DataDir (the real Jellyfin server). Install there with Install-RealServer.ps1, which backs the server up and installs the plugin's release tag."
    }

    if ($DataDir -ine $production) {
        throw "Refusing to deploy into ${DataDir}: with -RealServer the data folder must be exactly $production."
    }
}

# Only a validated -RealServer deploy may replace plugin folders under the real server's plugins folder.
$realServerTarget = [bool]($RealServer -and $DataDir -ieq $production)

# --- What may we build? -RealServer installs a release tag's build and nothing else: the worktree of the tag that
# Install-RealServer.ps1 made for this run, built here in Release.
$source = $repo
if ($SourceRoot) { $source = ConvertFrom-DevicePath (Resolve-FullPath (ConvertTo-FileSystemPath -Path $SourceRoot -SessionState $ExecutionContext.SessionState)) }
if ($SourceRoot -and $NoBuild) {
    throw "-SourceRoot builds that checkout, and -NoBuild would install this repository's last build instead. Use one of them."
}

if ($RealServer) {
    if (-not $SourceRoot -or -not $ReleaseLock) {
        throw ("-RealServer installs only a release tag's build: run Install-RealServer.ps1, which checks the tag out into a temporary worktree locked for its run " +
            'and passes it here as -SourceRoot, with -ReleaseLock, the lock''s reason.')
    }

    if ($Configuration -ne 'Release') { throw "-RealServer installs a release, which is built in Release, not $Configuration." }
    $release = Assert-PluginReleaseCheckout -Path $source -Plugin $Plugin -RepoRoot $repo -LockReason $ReleaseLock
    $Plugin = $release.Plugin
    Write-Host "Release checkout: $source is $($release.Tag) (commit $($release.Commit)), unchanged, nothing ignored or hidden in it, locked for this run."
}
elseif ($ReleaseLock) {
    throw '-ReleaseLock names the release worktree of a -RealServer install; without -RealServer it means nothing.'
}

# --- The reference-version check's server must be the Jellyfin this build is for (10.11 -> 10.11.x, 12 -> 12.x).
$serverVersion = Assert-JellyfinTarget -JellyfinDir $JellyfinDir -JellyfinTarget $JellyfinTarget

$running = @(Get-JellyfinProcessesForDataDir -DataDir $DataDir)
if ($running.Count -gt 0) {
    throw "Jellyfin is running with data folder $DataDir (PID $(($running | ForEach-Object { $_.ProcessId }) -join ', ')). Stop it first; it locks loaded plugin DLLs."
}

if (-not (Test-Path -LiteralPath $DataDir)) {
    if ($RealServer) { throw "$DataDir does not exist." }
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
}

# --- Build: the source checkout's project. This repository's working tree goes into artifacts\deploy\<Plugin>\<Configuration>
# (what -NoBuild installs). Another checkout goes into a folder of this run's own and is checked and installed from there,
# so that a build of the plugin in this checkout that lands in artifacts\deploy meanwhile (Deploy-Plugin.ps1's
# default, dev-server.ps1 -Configuration Release, a rehearsal, another session) is never what gets installed.
$info = Get-PluginBuildInfo -Plugin $Plugin -RepoRoot $source
if (-not (Test-Path -LiteralPath $info.Project)) { throw "Plugin project not found: $($info.Project)" }

$artifacts = Join-Path $repo 'artifacts'
$shared = Join-Path $artifacts "deploy\$($info.Folder)\$Configuration"
$output = $shared
$runFolder = $null
if ($SourceRoot) {
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss', [System.Globalization.CultureInfo]::InvariantCulture)
    $runFolder = Join-Path $artifacts ('builds\{0}-{1}-{2}-{3}' -f $info.Folder, $Configuration, $stamp, [guid]::NewGuid().ToString('N').Substring(0, 8))
    $output = $runFolder
}

try {
    if (-not $NoBuild) {
        if ($runFolder) {
            if (Test-Path -LiteralPath $runFolder) { throw "$runFolder is there already; run the deploy again." }
            New-Item -ItemType Directory -Path $runFolder | Out-Null
        }
        elseif (Test-Path -LiteralPath $output) {
            Remove-SandboxItem -Path $output -Root $artifacts
        }

        $publish = @($info.Project, '-c', $Configuration, '-o', $output, '--nologo', '-v', 'quiet', "-p:JellyfinTarget=$JellyfinTarget")
        # Another checkout is usually a temporary one (a release tag's worktree, removed right after): no build server may keep
        # one of its files open.
        if ($source -ine $repo) { $publish += '--disable-build-servers' }
        # Its messages to the host, so that only -PassThru's result is this script's output.
        & dotnet publish @publish | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) { throw "Build of $Plugin failed." }
    }

    if (-not (Test-Path -LiteralPath $output)) { throw "No build output in $output; run without -NoBuild." }

    # --- DLL allowlist: nothing but build.yaml's artifacts may ship.
    $allowed = $info.Assemblies
    $unexpected = @(Get-ChildItem -LiteralPath $output -Recurse -File -Filter '*.dll' |
        Where-Object { $allowed -notcontains $_.Name } |
        ForEach-Object { $_.FullName.Substring($output.Length + 1) })
    if ($unexpected.Count -gt 0) {
        throw "The $Plugin build output contains DLLs that are not in build.yaml's artifacts (the meta.json allowlist): $($unexpected -join ', '). " +
            'Reference server packages with ExcludeAssets="runtime" instead of shipping them.'
    }

    foreach ($assembly in $allowed) {
        $path = Join-Path $output $assembly
        if (-not (Test-Path -LiteralPath $path)) { throw "build.yaml lists $assembly, but the build did not produce $path." }
    }

    # What is installed, by SHA-256, read before the checks below look at it: the copies are compared with it.
    # Another checkout's build is the compiler's output in that checkout: the run's folder
    # under artifacts\builds has a name anyone can predict, so a file written there between the publish and this line would
    # otherwise be what is checked, installed and recorded as the tag's build. Each file in it must be the one the compiler
    # wrote in the checkout's obj\<Configuration>\<framework> (for a release, inside the worktree locked for this run), and
    # that one's SHA-256 is the baseline.
    $published = New-Object System.Collections.Generic.List[object]
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($assembly in $allowed) {
        foreach ($name in @($assembly, [System.IO.Path]::ChangeExtension($assembly, '.pdb'))) {
            if ($name -ne $assembly -and -not (Test-Path -LiteralPath (Join-Path $output $name))) { continue }
            $names.Add($name)
        }
    }

    if ($runFolder) {
        $framework = @{ '10.11' = 'net9.0'; '12' = 'net10.0' }[$JellyfinTarget]
        foreach ($file in @(Assert-BuildIsCompilerOutput -OutputFolder $output -ProjectFolder (Split-Path -Parent $info.Project) -Configuration $Configuration -Framework $framework -Files $names.ToArray())) {
            $published.Add($file)
        }
    }
    else {
        foreach ($name in $names) {
            $path = Join-Path $output $name
            $published.Add([pscustomobject]@{ Name = $name; Sha256 = (Get-FileSha256 $path); Bytes = (Get-Item -LiteralPath $path).Length })
        }
    }

    # --- Reference versions: nothing newer than the target Jellyfin ships.
    $references = Assert-AssemblyReferenceVersions -AssemblyPaths @($allowed | ForEach-Object { Join-Path $output $_ }) -JellyfinDir $JellyfinDir -What $Plugin
    foreach ($missing in @($references.NotShipped | Where-Object { $allowed -notcontains ($_.Reference + '.dll') })) {
        Write-Warning "$($missing.Assembly) references $($missing.Reference) $($missing.Referenced), which neither the plugin nor $($references.JellyfinDir) has."
    }

    Write-Host "Reference check passed: $(@($references.Checked).Count) references, none newer than $($references.JellyfinDir) (Jellyfin $serverVersion) ships."

    $mainDll = Join-Path $output $allowed[0]
    $fileVersion = (Get-Item -LiteralPath $mainDll).VersionInfo.FileVersion
    if ($fileVersion -ne $info.Version) {
        throw "$($allowed[0]) has file version $fileVersion, but build.yaml says $($info.Version)."
    }

    # --- The plugin's image: the release tag's (read from git), or another checkout's, or else the one committed at this
    # repository's HEAD; this repository's own working tree's otherwise.
    if ($RealServer) {
        $image = Resolve-PluginImage -Plugin $info.Folder -RepoRoot $repo -Commit $release.Commit -CommitLabel $release.Tag -FallbackToHead
    }
    elseif ($SourceRoot) {
        $image = Resolve-PluginImage -Plugin $info.Folder -RepoRoot $repo -SourceFolder $source -FallbackToHead
    }
    else {
        $image = Resolve-PluginImage -Plugin $info.Folder -RepoRoot $repo -SourceFolder $repo
    }

    if ($image) { Write-Host "Image: $($image.Name) from $($image.From), SHA-256 $($image.Sha256)" }
    else { Write-Host "Image: none (no plugins/$($info.Folder)/$(Get-PluginImageName) to install)" }

    # --- Install
    $pluginsDir = Join-Path $DataDir 'plugins'
    New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null
    $target = Join-Path $pluginsDir ($info.Name + '_' + $info.Version)
    $versionPattern = '^' + [regex]::Escape($info.Name) + '_\d+(\.\d+){1,3}$'

    foreach ($folder in @(Get-ChildItem -LiteralPath $pluginsDir -Directory)) {
        if ($folder.FullName -ieq $target) { continue }
        $sameGuid = $false
        $meta = Join-Path $folder.FullName 'meta.json'
        if (Test-Path -LiteralPath $meta) {
            try { $sameGuid = ((Read-JsonFile $meta).guid -ieq $info.Guid) } catch { $sameGuid = $false }
        }

        if ($sameGuid -or $folder.Name -match $versionPattern) {
            Remove-SandboxItem -Path $folder.FullName -Root $pluginsDir -AllowProtected:$realServerTarget
            Write-Host "Removed $($folder.Name)"
        }
    }

    if (Test-Path -LiteralPath $target) { Remove-SandboxItem -Path $target -Root $pluginsDir -AllowProtected:$realServerTarget }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    foreach ($file in $published) {
        Copy-Item -LiteralPath (Join-Path $output $file.Name) -Destination $target -Force
    }

    if ($image) {
        [System.IO.File]::WriteAllBytes((Join-Path $target $image.Name), $image.Bytes)
        $published.Add([pscustomobject]@{ Name = $image.Name; Sha256 = $image.Sha256; Bytes = $image.Size })
    }

    # The copies are the build that was checked, file by file; otherwise nothing of them stays installed.
    $mismatches = @()
    foreach ($file in $published) {
        $copy = Join-Path $target $file.Name
        $hash = '(missing)'
        if (Test-Path -LiteralPath $copy) { $hash = Get-FileSha256 $copy }
        if ($hash -ne $file.Sha256) { $mismatches += "$($file.Name) is $hash, the build was $($file.Sha256)" }
    }

    if ($mismatches.Count -gt 0) {
        Remove-SandboxItem -Path $target -Root $pluginsDir -AllowProtected:$realServerTarget
        throw ("The installed files aren't the build that was checked (SHA-256: $($mismatches -join '; ')): something changed $output during the install. " +
            "$target was removed again, so nothing of that build is installed.")
    }

    $yaml = $info.Yaml
    $manifest = [ordered]@{
        category    = [string]$yaml['category']
        changelog   = ([string]$yaml['changelog']).TrimEnd()
        description = ([string]$yaml['description']).TrimEnd()
        guid        = $info.Guid
        name        = $info.Name
        overview    = ([string]$yaml['overview']).TrimEnd()
        owner       = [string]$yaml['owner']
        targetAbi   = [string]$yaml['targetAbi']
        timestamp   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        version     = $info.Version
        status      = 'Active'
        autoUpdate  = $false
        assemblies  = [string[]]$allowed
    }
    if ($image) { $manifest['imagePath'] = $image.Name }
    Write-JsonFile -Path (Join-Path $target 'meta.json') -Value $manifest

    Write-Host "Deployed $($info.Name) $($info.Version) ($Configuration) to $target"
    Write-Host ('Checked by SHA-256: ' + (@($published | ForEach-Object { "$($_.Name) $($_.Sha256)" }) -join ', '))

    if ($runFolder) {
        # Only now, and only so that -NoBuild (a sandbox started on this build afterwards) finds it: a copy in artifacts\deploy.
        try {
            if (Test-Path -LiteralPath $shared) { Remove-SandboxItem -Path $shared -Root $artifacts }
            New-Item -ItemType Directory -Force -Path $shared | Out-Null
            Get-ChildItem -LiteralPath $runFolder -Force | Copy-Item -Destination $shared -Recurse -Force
            Write-Host "Copied this build to $shared, where -NoBuild finds it."
        }
        catch {
            Write-Warning "Could not copy this build to ${shared} ($($_.Exception.Message)); the install itself is done."
        }
    }

    if ($PassThru) {
        [pscustomobject]@{
            Plugin        = $info.Folder
            Name          = $info.Name
            Version       = $info.Version
            Configuration = $Configuration
            Target        = $target
            Files         = @($published | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Sha256 = $_.Sha256; Bytes = $_.Bytes } })
            Image         = $(if ($image) { [pscustomobject]@{ Name = $image.Name; From = $image.From; Source = $image.Source; Commit = $image.Commit; Sha256 = $image.Sha256; Bytes = $image.Size } } else { $null })
        }
    }
}
finally {
    if ($runFolder -and (Test-Path -LiteralPath $runFolder)) {
        try { Remove-SandboxItem -Path $runFolder -Root $artifacts }
        catch { Write-Warning "Could not remove this run's build folder ${runFolder}: $($_.Exception.Message)" }
    }
}
