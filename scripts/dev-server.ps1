<#
.SYNOPSIS
Runs a sandboxed Jellyfin server with this repository's plugins.

.DESCRIPTION
Runs the installed jellyfin.exe against its own folders under .sandbox\<Name>, so the real server's
configuration, libraries and web client are never touched:

  .sandbox\<Name>\data    --datadir (config in data\config, plugins in data\plugins)
  .sandbox\<Name>\cache   --cachedir
  .sandbox\<Name>\log     --logdir
  .sandbox\<Name>\web     --webdir: a copy of the installed jellyfin-web, refreshed when the installed web
                          client changes. Without --webdir Jellyfin serves the real installation's folder, which
                          Jellyfin Enhanced and JavaScript Injector may edit on disk.
  .sandbox\<Name>\temp    TMP/TEMP for the server, so it never shares %TEMP%\jellyfin with the real server

Every run:
- refuses any data, config, cache, log, web or temp folder under C:\ProgramData\Jellyfin (junctions included),
  port 8096 and a port that is already in use;
- refuses to start while Bookmarks' data in the sandbox names a clip path (ClipRoots, a clip file, a file waiting to
  be deleted, ClipOutputPath) outside the sandbox or under C:\ProgramData\Jellyfin: Bookmarks writes clips there and
  deletes files there, stale .partial files unattended at every start (JellyfinSandbox.psm1, Test-SandboxClipPaths);
- writes network.xml: the port, 127.0.0.1 only, no auto-discovery, no UPnP, no remote access, and BaseUrl;
- builds and deploys the selected plugins with Deploy-Plugin.ps1 (with its reference-version check against
  -JellyfinDir) and removes the unselected ones; the build is -Configuration's, else with -NoBuild the configuration
  the sandbox last ran (sandbox.json's Configuration, so a restart keeps a Release sandbox on Release), else Debug;
- copies the newest version folder of each requested community plugin from the real installation (or from
  -CommunityFrom, a plugins folder such as another sandbox's data\plugins) and sets autoUpdate=false in the copy's
  meta.json (the source is only read);
- writes an empty trigger list for the scheduled tasks that would reach the internet on their own (Jellyfin's
  "Update Plugins", and Jellyfin Enhanced's "Refresh CDN Assets" and "Send Anonymous Usage Report" when it is copied),
  so they never run by themselves in the sandbox; the real installation keeps its own triggers;
- for the 'je' sandbox, before its first start, writes the pinned settings fixture (Initialize-Sandbox.ps1 -PreStart);
- clears inherited JELLYFIN_* variables and runs jellyfin.exe with every path explicit, including --ffmpeg;
- gives jellyfin.exe (only it: the variables are restored in this PowerShell session once it has started, and user and
  machine variables are never changed) HTTP_PROXY and HTTPS_PROXY pointing at a dead loopback port, http://127.0.0.1:9,
  with NO_PROXY=127.0.0.1,localhost, so an outbound HTTP request fails instead of downloading from the internet while
  requests to the sandbox itself go direct; it refuses to start while something listens on port 9.

With -Background the server starts detached, with no handle of this PowerShell but its two logs (server.out.log,
server.err.log) and NUL (Start-ProcessWithLogs: a caller reading this script's output through a pipe gets it back when
the script ends, not when the server does); its PID goes to .sandbox\<Name>\server.pid, the script waits for
/System/Info/Public and then checks the server log: every path Jellyfin reports must be in
the sandbox. Stop it with Stop-Sandbox.ps1 -Name <Name>. Without -Background it runs in this console until Ctrl+C.

Standard sandboxes (defaults for -Port, -Community and -BaseUrl):
  default  8097  our plugins + FileTransformation, PluginPages
  je       8098  our plugins + FileTransformation, PluginPages, JellyfinEnhanced, CustomTabs, JavaScriptInjector
                 (+ settings fixture)
  bare     8099  our plugins only
  baseurl  8100  our plugins + FileTransformation, PluginPages, BaseUrl /jf
  move     8101  our plugins + FileTransformation, PluginPages (Library Maintenance's moves; its libraries are its own)
  upgrade  8119  our plugins + FileTransformation, PluginPages (Bookmarks' upgrade rehearsal, with -Plugins None first)
A sandbox of another name takes them from its sandbox.json (the last run's), so a restart without them keeps them; its
first run needs -Port. -CommunityFrom is likewise kept in sandbox.json for any sandbox, so a restart without it copies
from the same folder.

.EXAMPLE
./scripts/dev-server.ps1 -Name default -Background
./scripts/Initialize-Sandbox.ps1 -Name default
./scripts/Stop-Sandbox.ps1 -Name default

.EXAMPLE
./scripts/dev-server.ps1 -Name scratch -Port 8110 -Plugins Bookmarks -Community FileTransformation -Fresh

.EXAMPLE
./scripts/dev-server.ps1 -Name pp2 -Port 8111 -Community PluginPages -CommunityFrom .sandbox\default\data\plugins
#>
[CmdletBinding()]
param(
    [string] $Name = 'default',

    [int] $Port,

    [string[]] $Plugins = @('All'),

    [ValidateSet('None', 'FileTransformation', 'PluginPages', 'JellyfinEnhanced', 'CustomTabs', 'JavaScriptInjector')]
    [string[]] $Community,

    # A plugins folder to copy the community plugins from instead of -JellyfinDir's (kept in sandbox.json).
    [string] $CommunityFrom,

    [string] $BaseUrl,

    [switch] $Fresh,

    [switch] $Background,

    [string] $JellyfinDir = 'C:\ProgramData\Jellyfin\Server',

    # Debug unless given; with -NoBuild, the configuration the sandbox last ran (Resolve-SandboxConfiguration).
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration,

    [switch] $NoBuild,

    [string] $DataDir,

    [string] $CacheDir,

    [string] $LogDir,

    [string] $WebDir,

    [int] $StartTimeoutSec = 240
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'JellyfinSandbox.psm1') -Force

Assert-SandboxName $Name
$sandbox = Get-Sandbox -Name $Name
$standard = Get-StandardSandbox -Name $Name
$previous = $sandbox.State

# --- Settings: explicit parameters, else the standard sandbox table, else (a sandbox of another name) what its last run
# recorded in sandbox.json, so a probe's restart (-Name x -Port n -Background -NoBuild) keeps its port, its community plugins
# and its BaseUrl (the JSON store hardening's review, finding 8).
if (-not $PSBoundParameters.ContainsKey('Port')) {
    if ($standard) { $Port = $standard.Port }
    elseif ($previous -and $previous.PSObject.Properties['Port']) { $Port = [int]$previous.Port }
    else { throw "Sandbox '$Name' is not a standard sandbox; pass -Port." }
}

if ($PSBoundParameters.ContainsKey('Community')) { $requestedCommunity = $Community }
elseif ($standard) { $requestedCommunity = $standard.Community }
elseif ($previous -and $previous.PSObject.Properties['Community']) { $requestedCommunity = @($previous.Community | ForEach-Object { [string]$_ }) }
else { $requestedCommunity = @() }

$communityList = [string[]]@($requestedCommunity | Where-Object { $_ -and $_ -ne 'None' } | Select-Object -Unique)

if (-not $PSBoundParameters.ContainsKey('CommunityFrom') -and $previous -and $previous.PSObject.Properties['CommunityFrom'] -and $previous.CommunityFrom) {
    $CommunityFrom = [string]$previous.CommunityFrom
}

if ($CommunityFrom) {
    $CommunityFrom = Resolve-FullPath $CommunityFrom
    if (-not (Test-Path -LiteralPath $CommunityFrom -PathType Container)) { throw "-CommunityFrom $CommunityFrom is not a folder." }
}

if (-not $PSBoundParameters.ContainsKey('BaseUrl')) {
    if ($standard) { $BaseUrl = $standard.BaseUrl }
    elseif ($previous -and $previous.PSObject.Properties['BaseUrl'] -and $previous.BaseUrl) { $BaseUrl = [string]$previous.BaseUrl }
    else { $BaseUrl = '' }
}

$BaseUrl = $BaseUrl.Trim().TrimEnd('/')
if ($BaseUrl -and -not $BaseUrl.StartsWith('/')) { $BaseUrl = '/' + $BaseUrl }
if ($BaseUrl -and $BaseUrl -notmatch '^/[A-Za-z0-9._~-]+(/[A-Za-z0-9._~-]+)*$') { throw "BaseUrl '$BaseUrl' is not a plain path such as /jf." }

# The build: -Configuration, else with -NoBuild the one this sandbox last ran (sandbox.json), so the probes' restarts
# (-NoBuild) keep a sandbox started on a Release build on it, else Debug.
$requestedConfiguration = if ($PSBoundParameters.ContainsKey('Configuration')) { $Configuration } else { '' }
$configurationChoice = Resolve-SandboxConfiguration -Requested $requestedConfiguration -NoBuild:$NoBuild -Previous $previous
$Configuration = $configurationChoice.Configuration
if ($configurationChoice.Source -eq 'sandbox.json') {
    Write-Host "Configuration: $Configuration, the build this sandbox last ran (sandbox.json, with -NoBuild). Pass -Configuration to choose."
}

if ($Port -lt 1024 -or $Port -gt 65535) { throw "Port $Port is out of range." }
if ($Port -eq 8096) { throw 'Refusing port 8096: it belongs to the real Jellyfin server.' }

# --- Paths: every one explicit and outside C:\ProgramData\Jellyfin and the Jellyfin program folder.
$JellyfinDir = Resolve-FullPath $JellyfinDir
$jellyfinExe = Join-Path $JellyfinDir 'jellyfin.exe'
$ffmpeg = Join-Path $JellyfinDir 'ffmpeg.exe'
$sourceWeb = Join-Path $JellyfinDir 'jellyfin-web'
foreach ($required in $jellyfinExe, $ffmpeg, (Join-Path $sourceWeb 'index.html')) {
    if (-not (Test-Path -LiteralPath $required)) { throw "$required not found; pass -JellyfinDir." }
}

# \\?\C:\x spellings become C:\x first: Windows PowerShell's Join-Path can't handle them. Assert-SafePath below
# still refuses shares, device paths and anything under C:\ProgramData\Jellyfin.
if ($DataDir) { $sandbox.DataDir = ConvertFrom-DevicePath (Resolve-FullPath $DataDir) }
if ($CacheDir) { $sandbox.CacheDir = ConvertFrom-DevicePath (Resolve-FullPath $CacheDir) }
if ($LogDir) { $sandbox.LogDir = ConvertFrom-DevicePath (Resolve-FullPath $LogDir) }
if ($WebDir) { $sandbox.WebDir = ConvertFrom-DevicePath (Resolve-FullPath $WebDir) }

$paths = [ordered]@{
    'sandbox folder' = $sandbox.Root
    'data folder'    = $sandbox.DataDir
    'config folder'  = (Join-Path $sandbox.DataDir 'config')
    'plugins folder' = (Join-Path $sandbox.DataDir 'plugins')
    'cache folder'   = $sandbox.CacheDir
    'log folder'     = $sandbox.LogDir
    'web folder'     = $sandbox.WebDir
    'temp folder'    = $sandbox.TempDir
}
$resolved = @{}
foreach ($entry in $paths.GetEnumerator()) {
    $resolved[$entry.Key] = Assert-SafePath -Path $entry.Value -What $entry.Key -AlsoProtect @($JellyfinDir)
}

$sandbox.DataDir = $resolved['data folder']
$sandbox.CacheDir = $resolved['cache folder']
$sandbox.LogDir = $resolved['log folder']
$sandbox.WebDir = $resolved['web folder']
$sandbox.TempDir = $resolved['temp folder']
$configDir = $resolved['config folder']
$pluginsDir = $resolved['plugins folder']

# --- Nothing of ours may already run here, and the port must be free.
$existing = Get-SandboxServerProcess -Sandbox $sandbox
if ($existing) { throw "Sandbox '$Name' is already running (PID $($existing.ProcessId)). Stop it with Stop-Sandbox.ps1 -Name $Name." }
$usingData = @(Get-JellyfinProcessesForDataDir -DataDir $sandbox.DataDir)
if ($usingData.Count -gt 0) { throw "A jellyfin.exe already uses $($sandbox.DataDir) (PID $(($usingData | ForEach-Object { $_.ProcessId }) -join ', '))." }
$listeners = @(Get-ListeningProcessIds -Port $Port)
if ($listeners.Count -gt 0) { throw "Port $Port is already in use (PID $($listeners -join ', ')). Pick another -Port." }
$proxy = Get-SandboxProxy
$proxyListeners = @(Get-ListeningProcessIds -Port $proxy.Port)
if ($proxyListeners.Count -gt 0) {
    throw "Port $($proxy.Port) is in use (PID $($proxyListeners -join ', ')). The sandbox server sends its outbound HTTP requests to $($proxy.Url), " +
        'where they must be refused; that process would receive them instead (scripts/README.md, safety rules).'
}

# --- Fresh start: remove everything but the web copy.
if ($Fresh) {
    foreach ($path in $sandbox.DataDir, $sandbox.CacheDir, $sandbox.LogDir, $sandbox.TempDir, $sandbox.BrowserDir, $sandbox.CredentialsFile,
        $sandbox.StateFile, $sandbox.PidFile, $sandbox.StdOutLog, $sandbox.StdErrLog) {
        if (Test-Path -LiteralPath $path) {
            if (Test-PathUnder $path $sandbox.Root) { Remove-SandboxItem -Path $path -Root $sandbox.Root }
            else { Write-Warning "Not deleting $path for -Fresh: it is outside $($sandbox.Root)." }
        }
    }

    Write-Host "Fresh sandbox: removed the data, cache, log, temp and browser folders and the credentials of '$Name'."
}

# --- Bookmarks' clip paths, before anything is written. Bookmarks writes clips into, and deletes
# files from, the folders its data names, and at every start it deletes stale .partial files in each recorded clip
# folder, unattended. A store copied from elsewhere (a real server's bookmarks.json as a --fixture) would point that at
# another server's folders, so every clip path must lie in this sandbox.
$clipPathProblems = @(Test-SandboxClipPaths -Root $resolved['sandbox folder'] -DataDir $sandbox.DataDir)
if ($clipPathProblems.Count -gt 0) {
    $shown = @($clipPathProblems | Select-Object -First 10)
    $more = if ($clipPathProblems.Count -gt $shown.Count) { "`n  ... and $($clipPathProblems.Count - $shown.Count) more" } else { '' }
    throw ("Refusing to start sandbox '$Name': Bookmarks' data names clip paths outside it, where Bookmarks would write clips and delete files " +
        "(stale .partial files unattended at startup):`n  " + ($shown -join "`n  ") + $more +
        "`nRewrite them into the sandbox's clip folder, or move the files aside (scripts/README.md, 'The 1.0.0 store').")
}

foreach ($folder in $sandbox.Root, $sandbox.DataDir, $configDir, $pluginsDir, $sandbox.CacheDir, $sandbox.LogDir, $sandbox.TempDir) {
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
}

# --- Web client copy, once per installed web version.
$webStamp = (Get-Item -LiteralPath $jellyfinExe).VersionInfo.FileVersion + '|' + (Get-FileSha256 (Join-Path $sourceWeb 'index.html'))
$currentStamp = if (Test-Path -LiteralPath $sandbox.WebVersionFile) { (Get-Content -LiteralPath $sandbox.WebVersionFile -Raw).Trim() } else { '' }
if ($currentStamp -ne $webStamp -or -not (Test-Path -LiteralPath (Join-Path $sandbox.WebDir 'index.html'))) {
    if (Test-Path -LiteralPath $sandbox.WebDir) {
        if (-not (Test-PathUnder $sandbox.WebDir $sandbox.Root)) { throw "The web folder $($sandbox.WebDir) holds an old copy outside the sandbox; delete it yourself." }
        Remove-SandboxItem -Path $sandbox.WebDir -Root $sandbox.Root
    }

    Copy-Item -LiteralPath $sourceWeb -Destination $sandbox.WebDir -Recurse
    Write-Utf8NoBom -Path $sandbox.WebVersionFile -Text $webStamp
    Write-Host "Copied the web client into $($sandbox.WebDir)"
}

# --- network.xml: our port, loopback only.
$networkXml = @"
<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <BaseUrl>$BaseUrl</BaseUrl>
  <EnableHttps>false</EnableHttps>
  <RequireHttps>false</RequireHttps>
  <InternalHttpPort>$Port</InternalHttpPort>
  <PublicHttpPort>$Port</PublicHttpPort>
  <AutoDiscovery>false</AutoDiscovery>
  <EnableUPnP>false</EnableUPnP>
  <EnableIPv4>true</EnableIPv4>
  <EnableIPv6>false</EnableIPv6>
  <EnableRemoteAccess>false</EnableRemoteAccess>
  <LocalNetworkAddresses>
    <string>127.0.0.1</string>
  </LocalNetworkAddresses>
</NetworkConfiguration>
"@
Write-Utf8NoBom -Path (Join-Path $configDir 'network.xml') -Text ($networkXml -replace "`r`n", "`n")

# --- Our plugins.
$allPlugins = Get-PluginFolders
if ($Plugins -contains 'All') { $selected = $allPlugins }
elseif ($Plugins -contains 'None') { $selected = @() }
else {
    $selected = @($Plugins | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    foreach ($plugin in $selected) {
        if ($allPlugins -notcontains $plugin) { throw "Unknown plugin '$plugin'. Known: $($allPlugins -join ', ')." }
    }
}

foreach ($plugin in $allPlugins) {
    if ($selected -contains $plugin) {
        & (Join-Path $PSScriptRoot 'Deploy-Plugin.ps1') -Plugin $plugin -DataDir $sandbox.DataDir -Configuration $Configuration -NoBuild:$NoBuild -JellyfinDir $JellyfinDir
    }
    else {
        $info = Get-PluginBuildInfo -Plugin $plugin
        $pattern = '^' + [regex]::Escape($info.Name) + '_\d+(\.\d+){1,3}$'
        Get-ChildItem -LiteralPath $pluginsDir -Directory | Where-Object { $_.Name -match $pattern } | ForEach-Object {
            Remove-SandboxItem -Path $_.FullName -Root $pluginsDir
            Write-Host "Removed $($_.Name) (not selected)"
        }
    }
}

# --- Community plugins: newest version folder from the real install (or -CommunityFrom), copied and pinned.
$communitySource = if ($CommunityFrom) { $CommunityFrom } else { Join-Path $JellyfinDir 'plugins' }
$communityTable = Get-CommunityPluginTable
foreach ($key in $communityTable.Keys) {
    $folderName = $communityTable[$key].Folder
    $pattern = '^' + [regex]::Escape($folderName) + '_(\d+(\.\d+){1,3})$'
    $inSandbox = @(Get-ChildItem -LiteralPath $pluginsDir -Directory | Where-Object { $_.Name -match $pattern })

    if ($communityList -notcontains $key) {
        foreach ($folder in $inSandbox) {
            Remove-SandboxItem -Path $folder.FullName -Root $pluginsDir
            Write-Host "Removed $($folder.Name) (not requested)"
        }

        continue
    }

    $candidates = @(Get-ChildItem -LiteralPath $communitySource -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match $pattern } |
        Sort-Object { [version]([regex]::Match($_.Name, $pattern).Groups[1].Value) })
    if ($candidates.Count -eq 0) { throw "Community plugin $key ('${folderName}_*') is not in $communitySource." }
    $newest = $candidates[$candidates.Count - 1]
    $destination = Join-Path $pluginsDir $newest.Name

    foreach ($folder in $inSandbox) {
        if ($folder.FullName -ine $destination) {
            Remove-SandboxItem -Path $folder.FullName -Root $pluginsDir
            Write-Host "Removed $($folder.Name) (older copy)"
        }
    }

    if (-not (Test-Path -LiteralPath $destination)) {
        Copy-Item -LiteralPath $newest.FullName -Destination $destination -Recurse
        Write-Host "Copied $($newest.Name) from $communitySource"
    }

    $metaPath = Join-Path $destination 'meta.json'
    if (Test-Path -LiteralPath $metaPath) {
        $meta = Read-JsonFile $metaPath
        $meta.autoUpdate = $false
        if ($meta.PSObject.Properties['imagePath'] -and $meta.imagePath) {
            $image = Join-Path $destination (Split-Path -Leaf ([string]$meta.imagePath))
            if (Test-Path -LiteralPath $image) { $meta.imagePath = $image } else { $meta.imagePath = $null }
        }

        Write-JsonFile -Path $metaPath -Value $meta
    }
}

# --- Scheduled tasks that would download from the internet: no triggers in the sandbox, before every start
# Jellyfin reads config\ScheduledTasks\<id>.js at startup instead of a task's default triggers.
foreach ($task in @(Set-SandboxTaskTriggersOff -ConfigDir $configDir -Tasks @(Get-SandboxDisabledTasks -Community $communityList))) {
    Write-Host "Scheduled task '$($task.Name)' ($($task.Owner)) has no triggers here: config\ScheduledTasks\$($task.Id).js"
}

# --- The je sandbox's pinned settings fixture, before its first start.
$firstStart = -not (Test-Path -LiteralPath (Join-Path $sandbox.DataDir 'data\jellyfin.db'))
if ($Name -eq 'je' -and $firstStart) {
    & (Join-Path $PSScriptRoot 'Initialize-Sandbox.ps1') -Name $Name -PreStart -DataDir $sandbox.DataDir
}

# --- Record the sandbox settings for Initialize-Sandbox, Stop-Sandbox and webcheck.
$state = [ordered]@{
    Name          = $Name
    Port          = $Port
    BaseUrl       = $BaseUrl
    Plugins       = [string[]]$selected
    Community     = $communityList
    CommunityFrom = $CommunityFrom
    Configuration = $Configuration
    JellyfinDir   = $JellyfinDir
    DataDir       = $sandbox.DataDir
    CacheDir      = $sandbox.CacheDir
    LogDir        = $sandbox.LogDir
    WebDir        = $sandbox.WebDir
    TempDir       = $sandbox.TempDir
    Pid           = $null
    StartedUtc    = $null
}

$arguments = @(
    '--datadir', ('"' + $sandbox.DataDir + '"'),
    '--configdir', ('"' + $configDir + '"'),
    '--cachedir', ('"' + $sandbox.CacheDir + '"'),
    '--logdir', ('"' + $sandbox.LogDir + '"'),
    '--webdir', ('"' + $sandbox.WebDir + '"'),
    '--ffmpeg', ('"' + $ffmpeg + '"')
) -join ' '

$url = "http://127.0.0.1:$Port$BaseUrl"
Write-Host "Starting sandbox '$Name' on $url (data: $($sandbox.DataDir))"

# Each log is compared with itself: a new day's log can make Jellyfin delete an old one at this start (it keeps three).
$startupCountsBefore = Get-StartupCompleteCounts -LogDir $sandbox.LogDir

# The server's own variables (Get-SandboxServerEnvironment): no JELLYFIN_* (they would override the paths), TMP/TEMP
# in the sandbox, and the dead proxy with loopback direct. Set in this process only while jellyfin.exe starts, so it
# inherits them, and restored right after.
$serverEnvironment = @(Get-SandboxServerEnvironment -TempDir $sandbox.TempDir)
$process = Invoke-WithChildEnvironment -Variables $serverEnvironment -Action {
    if ($Background) {
        # Not Start-Process -Redirect*: the server would inherit this PowerShell's handles, and a caller reading its output
        # to the end (spawnSync with pipes) would wait until the server exits (Start-ProcessWithLogs).
        Start-ProcessWithLogs -FilePath $jellyfinExe -Arguments $arguments -WorkingDirectory $sandbox.Root `
            -StdOut $sandbox.StdOutLog -StdErr $sandbox.StdErrLog
    }
    else {
        Start-Process -FilePath $jellyfinExe -ArgumentList $arguments -WorkingDirectory $sandbox.Root -NoNewWindow -PassThru
    }
}

Write-Utf8NoBom -Path $sandbox.PidFile -Text ([string]$process.Id)
$state.Pid = $process.Id
$state.StartedUtc = (Get-Date).ToUniversalTime().ToString('o')
Write-JsonFile -Path $sandbox.StateFile -Value $state

function Test-SandboxLogPaths {
    <# Every path the server reports must be inside the sandbox, none under C:\ProgramData\Jellyfin. #>
    $log = Get-ChildItem -LiteralPath $sandbox.LogDir -Filter 'log_*.log' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $log) { throw "No server log found in $($sandbox.LogDir)." }
    $text = Read-SharedText $log.FullName
    $expected = [ordered]@{
        'Program data path'      = $sandbox.DataDir
        'Log directory path'     = $sandbox.LogDir
        'Config directory path'  = $configDir
        'Cache path'             = $sandbox.CacheDir
        'Temp directory path'    = $sandbox.TempDir
        'Web resources path'     = $sandbox.WebDir
    }
    $problems = @()
    foreach ($entry in $expected.GetEnumerator()) {
        $found = [regex]::Matches($text, [regex]::Escape($entry.Key) + ': (.+?)\r?\n')
        if ($found.Count -eq 0) { $problems += "the log has no '$($entry.Key)' line"; continue }
        $value = $found[$found.Count - 1].Groups[1].Value.Trim().Trim([char]34)
        if (Test-ProtectedPath $value) { $problems += "$($entry.Key) is $value, under C:\ProgramData\Jellyfin" }
        elseif (-not (Test-PathUnder $value $entry.Value)) { $problems += "$($entry.Key) is $value, expected $($entry.Value)" }
    }

    return $problems
}

if ($Background) {
    try {
        $info = Wait-SandboxReady -BaseUrl $url -ProcessId $process.Id -TimeoutSec $StartTimeoutSec -LogDir $sandbox.LogDir -StartupCountsBefore $startupCountsBefore
    }
    catch {
        foreach ($file in $sandbox.StdErrLog, $sandbox.StdOutLog) {
            if (Test-Path -LiteralPath $file) { Get-Content -LiteralPath $file -Tail 20 | ForEach-Object { Write-Warning $_ } }
        }

        if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) { Stop-VerifiedProcess -ProcessId $process.Id -Marker $sandbox.DataDir | Out-Null }
        throw
    }

    $problems = @(Test-SandboxLogPaths)
    if ($problems.Count -gt 0) {
        Stop-VerifiedProcess -ProcessId $process.Id -Marker $sandbox.DataDir | Out-Null
        throw "Stopped the sandbox: $($problems -join '; ')."
    }

    Write-Host "Sandbox '$Name' is up: $url (Jellyfin $($info.Version), PID $($process.Id), wizard completed: $($info.StartupWizardCompleted))."
    Write-Host 'Log check passed: data, config, cache, log, temp and web paths are all inside the sandbox.'
    if ($firstStart -or -not $info.StartupWizardCompleted) { Write-Host "Next: ./scripts/Initialize-Sandbox.ps1 -Name $Name" }
    Write-Host "Stop it with: ./scripts/Stop-Sandbox.ps1 -Name $Name"
}
else {
    try {
        $process.WaitForExit()
    }
    finally {
        if (Test-Path -LiteralPath $sandbox.PidFile) { Remove-Item -LiteralPath $sandbox.PidFile -Force }
    }
}
