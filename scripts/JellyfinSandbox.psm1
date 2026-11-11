<#
Shared helpers for the repository scripts: path guards that keep every sandbox away from the
real server, the sandbox layout under .sandbox/<Name>, build.yaml reading, process checks and a small
Jellyfin API client that only talks to loopback sandbox ports.

Keep this file ASCII: Windows PowerShell 5.1 reads BOM-less scripts in the ANSI code page.
#>

Set-StrictMode -Version 2.0

# Windows PowerShell 5.1 started from PowerShell 7 (a pwsh prompt, a GitHub Actions pwsh step, a test run started from
# either) inherits pwsh's PSModulePath, whose PowerShell 7 module folders come before 5.1's own. 5.1 then autoloads 7's
# Microsoft.PowerShell.Utility, which it can't load, and Get-FileHash "is not recognized" (7's CimCmdlets, Archive and
# Security would shadow 5.1's the same way). So in 5.1 those folders are dropped from this process's PSModulePath, which
# the commands still to be autoloaded and every child process read; 5.1's own folders and any others stay.
if ($PSVersionTable.PSEdition -eq 'Desktop' -and $env:PSModulePath) {
    $env:PSModulePath = @($env:PSModulePath -split ';' | Where-Object {
            $_ -and $_.TrimEnd('\') -notmatch '\\PowerShell\\(\d[^\\]*\\)?Modules$|\\WindowsApps\\Microsoft\.PowerShell[^\\]*\\Modules$'
        }) -join ';'
}

$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:ProductionDataDir = 'C:\ProgramData\Jellyfin\Server'
$script:ProductionPort = 8096
$script:ProtectedRootCache = $null

# Standard sandboxes. dev-server.ps1 takes its defaults from here.
$script:StandardSandboxes = @{
    'default' = @{ Port = 8097; Community = @('FileTransformation', 'PluginPages'); BaseUrl = '' }
    'je'      = @{ Port = 8098; Community = @('FileTransformation', 'PluginPages', 'JellyfinEnhanced', 'CustomTabs', 'JavaScriptInjector'); BaseUrl = '' }
    'bare'    = @{ Port = 8099; Community = @(); BaseUrl = '' }
    'baseurl' = @{ Port = 8100; Community = @('FileTransformation', 'PluginPages'); BaseUrl = '/jf' }
    # 'move' keeps 8101; 'upgrade' (Bookmarks' upgrade rehearsal), which once ran on 8101 with an explicit -Port, moved
    # to 8119 when 'move' took 8101. Their libraries and plugins are each check's own; the table gives the port and the
    # community plugins.
    'move'    = @{ Port = 8101; Community = @('FileTransformation', 'PluginPages'); BaseUrl = '' }
    'upgrade' = @{ Port = 8119; Community = @('FileTransformation', 'PluginPages'); BaseUrl = '' }
}

# Scheduled tasks that download from the internet on their own. Every sandbox keeps them without triggers, so they
# never run by themselves: dev-server.ps1 writes an empty trigger list for each one before
# every start, and Initialize-Sandbox.ps1 asserts through the API that they have no triggers and haven't run since.
# Type is the task's full type name, from which Jellyfin derives the task id and so the trigger file's name.
# - Jellyfin's "Update Plugins" fetches every plugin repository's manifest 3 s after each start. A new sandbox still
#   has the default repository (repo.jellyfin.org, added by a setup migration) until Initialize-Sandbox empties the list.
$script:SandboxDisabledTasks = @(
    @{ Owner = 'Jellyfin'; Type = 'Emby.Server.Implementations.ScheduledTasks.Tasks.PluginUpdateTask'; Key = 'PluginUpdates'; Name = 'Update Plugins' }
)

# The sandbox server's outbound proxy: a loopback port nothing listens on,
# so every outbound HTTP request the server makes through .NET's default proxy is refused before it leaves the machine,
# whatever made it (a task started by hand, Jellyfin Enhanced's on-demand CDN proxy, a task missing from the table
# above). Loopback requests go direct. dev-server.ps1 refuses to start while something listens on the port.
$script:SandboxProxyPort = 9
$script:SandboxNoProxy = '127.0.0.1,localhost'

# Community plugins that may be copied from the real installation, by the folder-name prefix Jellyfin gives them.
# DisabledTasks: the plugin's own scheduled tasks that reach the internet on their own (see SandboxDisabledTasks above).
# - Jellyfin Enhanced 12.8's "Refresh CDN Assets" fetches about 57 assets (about 600 KB) from jsDelivr, flagcdn,
#   Wikimedia and others into plugins/configurations/Jellyfin.Plugin.JellyfinEnhanced/cdn-cache 3 s after each start
#   and every 24 hours (measured in a sandbox).
# - Its "Send Anonymous Usage Report" registers the install and uploads usage statistics to JE's analytics backend
#   (a supabase.co host) when the setting AnalyticsEnabled is on. JE leaves it off and the je fixture pins it off, but
#   the task keeps an IntervalTrigger, first due an hour after a start, so without this entry a sandbox would be safe
#   only because of a setting.
# JE's other tasks that call out (Seerr user import and watchlist syncs, MDBList ratings, *arr tag sync) skip without
# a URL or API key, which no sandbox has; scripts/README.md (safety rules) says what the guarantee rests on.
$script:CommunityPlugins = [ordered]@{
    'FileTransformation' = @{ Folder = 'File Transformation'; Guid = '5e87cc92-571a-4d8d-8d98-d2d4147f9f90' }
    'PluginPages'        = @{ Folder = 'Plugin Pages'; Guid = '5b6550fa-a014-4f4c-8a2c-59a43680ac6d' }
    'JellyfinEnhanced'   = @{
        Folder        = 'Jellyfin Enhanced'
        Guid          = 'f69e946a-4b3c-4e9a-8f0a-8d7c1b2c4d9b'
        DisabledTasks = @(
            @{ Owner = 'Jellyfin Enhanced'; Type = 'Jellyfin.Plugin.JellyfinEnhanced.ScheduledTasks.RefreshCdnAssetsTask'; Key = 'JellyfinEnhancedRefreshCdnAssets'; Name = 'Refresh CDN Assets' }
            @{ Owner = 'Jellyfin Enhanced'; Type = 'Jellyfin.Plugin.JellyfinEnhanced.ScheduledTasks.AnalyticsReportTask'; Key = 'JellyfinEnhancedAnalyticsReport'; Name = 'Send Anonymous Usage Report' }
        )
    }
    'CustomTabs'         = @{ Folder = 'Custom Tabs'; Guid = 'fbacd0b6-fd46-4a05-b0a4-2045d6a135b0' }
    'JavaScriptInjector' = @{ Folder = 'JavaScript Injector'; Guid = 'f5a34f7b-2e8a-4e6a-a722-3a216a81b374' }
}

function Get-RepoRoot {
    return $script:RepoRoot
}

function Get-ProductionDataDir {
    return $script:ProductionDataDir
}

function Get-StandardSandbox {
    param([Parameter(Mandatory = $true)] [string] $Name)
    if ($script:StandardSandboxes.ContainsKey($Name)) { return $script:StandardSandboxes[$Name] }
    return $null
}

function Get-CommunityPluginTable {
    return $script:CommunityPlugins
}

function Get-SandboxDisabledTasks {
    <# The scheduled tasks a sandbox with these community plugins keeps without triggers: Jellyfin's own, then each plugin's. #>
    param([string[]] $Community = @())
    $tasks = @($script:SandboxDisabledTasks)
    foreach ($key in $script:CommunityPlugins.Keys) {
        if ($Community -notcontains $key) { continue }
        $entry = $script:CommunityPlugins[$key]
        if ($entry.ContainsKey('DisabledTasks')) { $tasks += @($entry.DisabledTasks) }
    }

    return $tasks
}

# ------------------------------------------------------------------------------------------------ paths

function ConvertTo-FileSystemPath {
    <#
    The file system path a PowerShell spelling names, as Windows and .NET read it: a path on a PowerShell
    drive (New-PSDrive Q -PSProvider FileSystem -Root C:\ProgramData\Jellyfin, then Q:\Server), a provider-qualified one
    (FileSystem::C:\x), ~\x and a drive-relative one (C:x, against PowerShell's location on that drive) become the path that
    Test-Path, New-Item and Copy-Item act on, which .NET can't see. Drives are looked up in SessionState: a script passes its
    own ($ExecutionContext.SessionState), so the drives of its scope and its callers' count; by default this module's, which
    sees the global ones only. A share or device path (\\server\share, \\?\C:\x) and a plain relative path (Resolve-FullPath
    resolves it) come back as they are, and so does a drive letter neither PowerShell nor Windows knows, which
    Test-ProtectedPath counts as the real server's. A path of another provider (HKLM:\, Env:\), and a drive name or
    provider PowerShell doesn't know here (a longer name than a letter, such as another scope's drive), are refused.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [System.Management.Automation.SessionState] $SessionState = $ExecutionContext.SessionState
    )
    if ($Path -match '^[\\/]{2}' -or ($Path -notmatch ':' -and -not $Path.StartsWith('~'))) { return $Path }
    $provider = $null
    $drive = $null
    try {
        $resolved = $SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path, [ref]$provider, [ref]$drive)
    }
    catch {
        if ($Path -match '^[A-Za-z]:') { return $Path }
        $why = $_.Exception.Message
        if ($_.Exception.InnerException) { $why = $_.Exception.InnerException.Message }
        throw "$Path names a PowerShell drive or provider that isn't known here, so it can't be read as a file system path ($why)."
    }

    if ($null -eq $provider -or $provider.Name -ne 'FileSystem') {
        $kind = 'another provider'
        if ($null -ne $provider) { $kind = $provider.Name }
        throw "$Path is not a file system path (PowerShell reads it as a path of $kind)."
    }

    return $resolved
}

function Resolve-FullPath {
    <#
    Absolute, normalised path without a trailing separator. A PowerShell spelling (a PowerShell drive, FileSystem::, ~) is
    read as the path it names first (ConvertTo-FileSystemPath, with the drives this module sees); relative paths resolve
    against the current location.
    #>
    param([Parameter(Mandatory = $true)] [string] $Path)
    $Path = ConvertTo-FileSystemPath -Path $Path
    if (-not [System.IO.Path]::IsPathRooted($Path)) {
        $Path = Join-Path (Get-Location -PSProvider FileSystem).ProviderPath $Path
    }

    $full = [System.IO.Path]::GetFullPath($Path)
    if ($full.Length -gt 3) { $full = $full.TrimEnd('\', '/') }
    return $full
}

function Test-PathUnder {
    <# True when Path is Root or inside it (case-insensitive). #>
    param([Parameter(Mandatory = $true)] [string] $Path, [Parameter(Mandatory = $true)] [string] $Root)
    $p = (Resolve-FullPath $Path).TrimEnd('\') + '\'
    $r = (Resolve-FullPath $Root).TrimEnd('\') + '\'
    return $p.StartsWith($r, [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-ProtectedRoots {
    if ($null -ne $script:ProtectedRootCache) { return $script:ProtectedRootCache }

    $roots = New-Object System.Collections.Generic.List[string]
    $roots.Add('C:\ProgramData\Jellyfin')
    $commonData = [Environment]::GetFolderPath('CommonApplicationData')
    if ($commonData) { $roots.Add((Join-Path $commonData 'Jellyfin')) }

    # 8.3 short forms (C:\PROGRA~3\Jellyfin) of the roots that exist.
    try {
        $fso = New-Object -ComObject Scripting.FileSystemObject
        foreach ($root in @($roots)) {
            if (Test-Path -LiteralPath $root) { $roots.Add($fso.GetFolder($root).ShortPath) }
        }
    }
    catch {
        Write-Verbose "Could not resolve short path names: $_"
    }

    $script:ProtectedRootCache = @($roots | Select-Object -Unique)
    return $script:ProtectedRootCache
}

function ConvertFrom-DevicePath {
    <# \\?\C:\x and \\.\C:\x become C:\x, \\?\UNC\server\share becomes \\server\share; other paths are unchanged. #>
    param([Parameter(Mandatory = $true)] [string] $Path)
    if ($Path -match '^[\\/]{2}[?.][\\/]UNC[\\/](.*)$') { return '\\' + $Matches[1] }
    if ($Path -match '^[\\/]{2}[?.][\\/]([A-Za-z]:([\\/].*)?)$') { return $Matches[1] }
    return $Path
}

function Test-LocalDrivePath {
    <# True for a path on a drive letter (C:\x), also when spelled \\?\C:\x. UNC shares and other device paths are not. #>
    param([Parameter(Mandatory = $true)] [string] $Path)
    return ((ConvertFrom-DevicePath (Resolve-FullPath $Path)) -match '^[A-Za-z]:([\\/]|$)')
}

function Get-FinalPath {
    <#
    The final path of an existing file or folder as Windows resolves it (GetFinalPathNameByHandle): junctions,
    symbolic links, subst and mapped drives followed, 8.3 names expanded. $null when it can't be opened.
    Only opens a handle without read or write access.
    #>
    param([Parameter(Mandatory = $true)] [string] $Path)
    if (-not ('JellyfinSandbox.NativePath' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace JellyfinSandbox
{
    public static class NativePath
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder buffer, uint length, uint flags);

        public static string GetFinalPath(string path)
        {
            // No access rights (attributes only), share read/write/delete, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS (folders).
            using (SafeFileHandle handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                if (handle.IsInvalid) { return null; }
                StringBuilder buffer = new StringBuilder(512);
                uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                if (length >= buffer.Capacity)
                {
                    buffer = new StringBuilder((int)length + 1);
                    length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                }

                return (length == 0 || length >= buffer.Capacity) ? null : buffer.ToString();
            }
        }
    }
}
'@
    }

    return [JellyfinSandbox.NativePath]::GetFinalPath($Path)
}

function Test-ProtectedPath {
    <#
    True when Path is under C:\ProgramData\Jellyfin in any spelling: 8.3 names, '..', \\?\ and \\.\ prefixes,
    a junction, symbolic link, subst or mapped drive on the way, a PowerShell drive this module sees. UNC
    and other non-drive paths count as protected (fail safe): C$ admin shares reach the same folder, and no sandbox needs a
    network path. So does a drive letter Windows doesn't know: a PowerShell drive of a script's own scope, which this
    module can't follow while Test-Path and Copy-Item in that script do (scripts resolve their paths with their own drives
    first, ConvertTo-FileSystemPath), or no drive at all. And so does a path that can't be read as a file system path at
    all (another scope's drive with a longer name, an NTFS stream spelling): where it leads can't be checked.
    #>
    param([Parameter(Mandatory = $true)] [string] $Path, [int] $Depth = 0)
    if ($Depth -gt 8) { return $true }

    try { $full = ConvertFrom-DevicePath (Resolve-FullPath $Path) } catch { return $true }
    if ($full -notmatch '^[A-Za-z]:([\\/]|$)') { return $true }
    if (-not [System.IO.Directory]::Exists($full.Substring(0, 2) + '\')) { return $true }
    foreach ($root in Get-ProtectedRoots) {
        if (Test-PathUnder $full $root) { return $true }
    }

    # Where Windows really puts it: resolve the deepest existing folder on the way and re-check.
    $existing = $full
    while ($existing -and -not (Test-Path -LiteralPath $existing)) {
        $parent = Split-Path -Parent $existing
        if (-not $parent -or $parent -eq $existing) { $existing = $null; break }
        $existing = $parent
    }

    if ($existing) {
        $final = $null
        try { $final = Get-FinalPath $existing } catch { Write-Verbose "Could not resolve the final path of ${existing}: $_" }
        if ($final) {
            $final = ConvertFrom-DevicePath $final
            if ($final -notmatch '^[A-Za-z]:([\\/]|$)') { return $true }
            $rest = $full.Substring($existing.Length).TrimStart('\')
            $redirected = if ($rest) { Join-Path $final $rest } else { $final }
            foreach ($root in Get-ProtectedRoots) {
                if (Test-PathUnder $redirected $root) { return $true }
            }
        }
    }

    $current = $full
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                $targets = @()
                if ($item.PSObject.Properties['Target'] -and $item.Target) { $targets = @($item.Target) }
                foreach ($target in $targets) {
                    if (-not $target) { continue }
                    $targetPath = [string]$target
                    if (-not [System.IO.Path]::IsPathRooted($targetPath)) {
                        $targetPath = Join-Path (Split-Path -Parent $current) $targetPath
                    }

                    $rest = $full.Substring($current.Length).TrimStart('\')
                    $redirected = if ($rest) { Join-Path $targetPath $rest } else { $targetPath }
                    if (Test-ProtectedPath -Path $redirected -Depth ($Depth + 1)) { return $true }
                }
            }
        }

        $parent = Split-Path -Parent $current
        if (-not $parent -or $parent -eq $current) { break }
        $current = $parent
    }

    return $false
}

function Assert-SafePath {
    <# Throws when Path is under C:\ProgramData\Jellyfin (or an extra protected root such as the Jellyfin program folder). #>
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $What,
        [string[]] $AlsoProtect = @()
    )
    $full = Resolve-FullPath $Path
    if (-not (Test-LocalDrivePath $full)) {
        throw "Refusing to continue: the $What ($full) is not on a local drive. Sandbox folders need a path such as C:\..., not a share or device path."
    }

    $full = ConvertFrom-DevicePath $full
    if (Test-ProtectedPath $full) {
        throw "Refusing to continue: the $What ($full) is under C:\ProgramData\Jellyfin, which belongs to the real Jellyfin server."
    }

    foreach ($extra in $AlsoProtect) {
        if ($extra -and (Test-PathUnder $full $extra)) {
            throw "Refusing to continue: the $What ($full) is inside $extra."
        }
    }

    return $full
}

function Assert-UnderRoot {
    <#
    Throws unless Path is strictly inside Root, and (without -AllowProtected) outside C:\ProgramData\Jellyfin.
    Used before deleting anything. Only Deploy-Plugin.ps1 -RealServer passes -AllowProtected.
    #>
    param([Parameter(Mandatory = $true)] [string] $Path, [Parameter(Mandatory = $true)] [string] $Root, [switch] $AllowProtected)
    $full = Resolve-FullPath $Path
    $rootFull = Resolve-FullPath $Root
    if ($full -ieq $rootFull -or -not (Test-PathUnder $full $rootFull)) {
        throw "Refusing to touch ${full}: it is not inside $rootFull."
    }

    if (-not $AllowProtected -and (Test-ProtectedPath $full)) {
        throw "Refusing to touch ${full}: it is under C:\ProgramData\Jellyfin."
    }

    return $full
}

function Remove-TreeNoFollow {
    <# Deletes a file or folder tree without ever descending into a junction or symbolic link (those are unlinked). #>
    param([Parameter(Mandatory = $true)] [string] $Path)
    $attributes = [System.IO.File]::GetAttributes($Path)
    $isDirectory = ($attributes -band [System.IO.FileAttributes]::Directory) -ne 0
    if ($attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        if ($isDirectory) { [System.IO.Directory]::Delete($Path, $false) } else { [System.IO.File]::Delete($Path) }
        return
    }

    if ($isDirectory) {
        foreach ($child in [System.IO.Directory]::GetFileSystemEntries($Path)) { Remove-TreeNoFollow -Path $child }
        [System.IO.Directory]::Delete($Path, $false)
    }
    else {
        if ($attributes -band [System.IO.FileAttributes]::ReadOnly) { [System.IO.File]::SetAttributes($Path, [System.IO.FileAttributes]::Normal) }
        [System.IO.File]::Delete($Path)
    }
}

function Remove-SandboxItem {
    <# Deletes a file or folder that must be inside Root. Junctions and symbolic links are unlinked, never followed. #>
    param([Parameter(Mandatory = $true)] [string] $Path, [Parameter(Mandatory = $true)] [string] $Root, [switch] $AllowProtected)
    $full = Assert-UnderRoot -Path $Path -Root $Root -AllowProtected:$AllowProtected
    if (-not (Test-Path -LiteralPath $full)) { return }
    Remove-TreeNoFollow -Path $full
}

# --------------------------------------------------------------------------------------------- sandbox

function Assert-SandboxName {
    param([Parameter(Mandatory = $true)] [string] $Name)
    if ($Name -notmatch '^[a-z0-9][a-z0-9-]{0,30}$') {
        throw "Sandbox name '$Name' must be lower-case letters, digits and dashes."
    }
}

function Get-SandboxRoot {
    return (Join-Path $script:RepoRoot '.sandbox')
}

function Get-Sandbox {
    <# The layout of .sandbox/<Name>. Paths recorded in sandbox.json by dev-server.ps1 take precedence. #>
    param([Parameter(Mandatory = $true)] [string] $Name)
    Assert-SandboxName $Name
    $root = Join-Path (Get-SandboxRoot) $Name
    $sandbox = [ordered]@{
        Name            = $Name
        Root            = $root
        DataDir         = Join-Path $root 'data'
        CacheDir        = Join-Path $root 'cache'
        LogDir          = Join-Path $root 'log'
        WebDir          = Join-Path $root 'web'
        TempDir         = Join-Path $root 'temp'
        BrowserDir      = Join-Path $root 'browser'
        PidFile         = Join-Path $root 'server.pid'
        StateFile       = Join-Path $root 'sandbox.json'
        CredentialsFile = Join-Path $root 'credentials.json'
        WebVersionFile  = Join-Path $root 'web.version'
        StdOutLog       = Join-Path $root 'server.out.log'
        StdErrLog       = Join-Path $root 'server.err.log'
        State           = $null
    }

    if (Test-Path -LiteralPath $sandbox.StateFile) {
        $state = Read-JsonFile $sandbox.StateFile
        $sandbox.State = $state
        foreach ($key in 'DataDir', 'CacheDir', 'LogDir', 'WebDir', 'TempDir') {
            if ($state.PSObject.Properties[$key] -and $state.$key) { $sandbox[$key] = [string]$state.$key }
        }
    }

    $sandbox.ConfigDir = Join-Path $sandbox.DataDir 'config'
    $sandbox.PluginsDir = Join-Path $sandbox.DataDir 'plugins'
    return $sandbox
}

function Resolve-SandboxConfiguration {
    <#
    The build configuration dev-server.ps1 deploys, and where it came from: Requested (-Configuration) when given
    ('parameter'); with -NoBuild, the configuration the sandbox last ran, from its sandbox.json ('sandbox.json'), so a
    restart deploys the build the sandbox was started on (a Release build stays Release; before
    this, the probes' restarts deployed Debug over it); otherwise Debug ('default').
    #>
    param([string] $Requested, [switch] $NoBuild, $Previous)
    $known = @('Debug', 'Release')
    if ($Requested) {
        $match = @($known | Where-Object { $_ -ieq $Requested })
        if ($match.Count -eq 0) { throw "Configuration '$Requested' is neither Debug nor Release." }
        return [pscustomobject]@{ Configuration = $match[0]; Source = 'parameter' }
    }

    if ($NoBuild -and $null -ne $Previous -and $Previous.PSObject.Properties['Configuration'] -and $Previous.Configuration) {
        $match = @($known | Where-Object { $_ -ieq [string]$Previous.Configuration })
        if ($match.Count -gt 0) { return [pscustomobject]@{ Configuration = $match[0]; Source = 'sandbox.json' } }
    }

    return [pscustomobject]@{ Configuration = 'Debug'; Source = 'default' }
}

function Get-SandboxBaseUrl {
    param([Parameter(Mandatory = $true)] $Sandbox)
    if (-not $Sandbox.State) { throw "Sandbox '$($Sandbox.Name)' has no sandbox.json; start it with dev-server.ps1 first." }
    $baseUrl = ''
    if ($Sandbox.State.PSObject.Properties['BaseUrl'] -and $Sandbox.State.BaseUrl) { $baseUrl = [string]$Sandbox.State.BaseUrl }
    $url = 'http://127.0.0.1:' + [int]$Sandbox.State.Port + $baseUrl
    Assert-SandboxUrl $url
    return $url
}

function Get-SandboxServerProcess {
    <#
    The sandbox's running jellyfin.exe, or $null. The PID comes from server.pid and is trusted only when that
    process is jellyfin.exe and its --datadir is this sandbox's data folder.
    #>
    param([Parameter(Mandatory = $true)] $Sandbox)
    if (-not (Test-Path -LiteralPath $Sandbox.PidFile)) { return $null }
    $text = (Get-Content -LiteralPath $Sandbox.PidFile -Raw).Trim()
    $processId = 0
    if (-not [int]::TryParse($text, [ref]$processId)) { return $null }
    $process = Get-CimInstance Win32_Process -Filter "ProcessId = $processId" -ErrorAction SilentlyContinue
    if (-not $process) { return $null }
    if (-not (Test-SandboxServerCommandLine -Process $process -Sandbox $Sandbox)) {
        Write-Warning "PID $processId from $($Sandbox.PidFile) is not this sandbox's server (the PID was reused); ignoring it."
        return $null
    }

    return $process
}

function Test-SandboxServerCommandLine {
    param([Parameter(Mandatory = $true)] $Process, [Parameter(Mandatory = $true)] $Sandbox)
    if ($Process.Name -ne 'jellyfin.exe' -or -not $Process.CommandLine) { return $false }
    $dataDir = Get-DataDirFromCommandLine $Process.CommandLine
    if (-not $dataDir) { return $false }
    if (Test-ProtectedPath $dataDir) { return $false }
    return ((Resolve-FullPath $dataDir) -ieq (Resolve-FullPath $Sandbox.DataDir))
}

# ----------------------------------------------------------------------------------------------- processes

function Get-DataDirFromCommandLine {
    <# The --datadir (or -d) value of a jellyfin.exe command line, or $null. #>
    param([string] $CommandLine)
    if (-not $CommandLine) { return $null }
    $patterns = @(
        '(?:^|\s)(?:--datadir|-d)(?:=|\s+)"([^"]+)"',
        '(?:^|\s)(?:--datadir|-d)(?:=|\s+)([^\s"]+)'
    )
    foreach ($pattern in $patterns) {
        $match = [regex]::Match($CommandLine, $pattern)
        if ($match.Success) { return $match.Groups[1].Value }
    }

    return $null
}

function Get-JellyfinProcessesForDataDir {
    <#
    jellyfin.exe processes that use DataDir. A process whose command line can't be read, or that has no
    --datadir and runs from inside DataDir, counts as using it (fail safe).
    #>
    param([Parameter(Mandatory = $true)] [string] $DataDir)
    $target = Resolve-FullPath $DataDir
    $result = @()
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name = 'jellyfin.exe'" -ErrorAction SilentlyContinue)) {
        if (-not $process.CommandLine) { $result += $process; continue }
        $dataDir = Get-DataDirFromCommandLine $process.CommandLine
        if ($dataDir) {
            if ((Resolve-FullPath $dataDir) -ieq $target) { $result += $process }
        }
        elseif ($process.ExecutablePath -and (Test-PathUnder $process.ExecutablePath $target)) {
            $result += $process
        }
    }

    return $result
}

function Get-ListeningProcessIds {
    param([Parameter(Mandatory = $true)] [int] $Port)
    return @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
}

function Get-SandboxProxy {
    <# The dead proxy every sandbox server gets: Port (nothing may listen there), Url and NoProxy (loopback goes direct). #>
    return [pscustomobject]@{ Port = $script:SandboxProxyPort; Url = "http://127.0.0.1:$($script:SandboxProxyPort)"; NoProxy = $script:SandboxNoProxy }
}

function Get-SandboxServerEnvironment {
    <#
    The process variables of a sandbox's jellyfin.exe, as Name/Value pairs in the order they are set (a $null Value
    removes the variable), for Invoke-WithChildEnvironment:
    - every JELLYFIN_* variable this process has is removed: they would override the explicit paths;
    - TMP and TEMP are the sandbox's temp folder, so the server never shares %TEMP%\jellyfin with the real server;
    - HTTP_PROXY and HTTPS_PROXY are the dead proxy (Get-SandboxProxy) and NO_PROXY is loopback. Jellyfin's clients
      (SocketsHttpHandler) and Jellyfin Enhanced's (IHttpClientFactory) set no proxy of their own, and .NET's default
      proxy reads these variables first on Windows. The lower-case spellings are listed too, for readers that look for
      http_proxy; Windows keeps one variable per name whatever its case, so both spellings read the same value.
    #>
    param([Parameter(Mandatory = $true)] [string] $TempDir)
    $proxy = Get-SandboxProxy
    $pairs = @()
    foreach ($variable in @(Get-ChildItem Env: | Where-Object { $_.Name -like 'JELLYFIN_*' })) {
        $pairs += [pscustomobject]@{ Name = $variable.Name; Value = $null }
    }

    foreach ($name in 'TMP', 'TEMP') { $pairs += [pscustomobject]@{ Name = $name; Value = $TempDir } }
    foreach ($name in 'HTTP_PROXY', 'http_proxy', 'HTTPS_PROXY', 'https_proxy') { $pairs += [pscustomobject]@{ Name = $name; Value = $proxy.Url } }
    foreach ($name in 'NO_PROXY', 'no_proxy') { $pairs += [pscustomobject]@{ Name = $name; Value = $proxy.NoProxy } }
    return $pairs
}

function Invoke-WithChildEnvironment {
    <#
    Runs Action with Variables (Name/Value pairs; a $null Value removes the variable) set in this process, so a process
    started inside Action inherits them, and afterwards puts every variable it touched back as it was, also when Action
    fails. Only the process scope is changed, never the user's or the machine's variables, so the PowerShell session that
    ran the script keeps its own environment. Returns Action's output.
    #>
    param([Parameter(Mandatory = $true)] [AllowEmptyCollection()] [object[]] $Variables, [Parameter(Mandatory = $true)] [scriptblock] $Action)
    $saved = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($variable in $Variables) {
        if (-not $saved.ContainsKey($variable.Name)) { $saved[$variable.Name] = [Environment]::GetEnvironmentVariable($variable.Name, 'Process') }
    }

    try {
        foreach ($variable in $Variables) { [Environment]::SetEnvironmentVariable($variable.Name, $variable.Value, 'Process') }
        & $Action
    }
    finally {
        foreach ($entry in $saved.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
    }
}

function Start-ProcessWithLogs {
    <#
    Starts FilePath with Arguments (one command-line string, quoted as it should reach the program) in WorkingDirectory,
    without a window, its standard output and error written to the files StdOut and StdErr (each created or emptied) and
    NUL as its input, and returns its System.Diagnostics.Process. The program inherits this process's environment (so
    Invoke-WithChildEnvironment applies) and no handle of it but those three.

    Why not Start-Process -RedirectStandardOutput (a trap): Start-Process then calls CreateProcess
    with bInheritHandles true, so the program inherits every inheritable handle of the PowerShell process, among them the
    pipes of whatever started that PowerShell. A caller that reads them to the end (Node's spawnSync with piped output, a
    CI runner) then waits until the server exits, not until the script does. Here CreateProcessW gets
    EXTENDED_STARTUPINFO_PRESENT with PROC_THREAD_ATTRIBUTE_HANDLE_LIST naming only the three handles opened for it,
    lpEnvironment null and CREATE_NO_WINDOW. (Win32_Process.Create would also cut the handles, but starts the program
    from the WMI service, with the service's environment, not this process's.) scripts/tests/dev-server-launch.test.mjs
    has the trap and this way past it.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $FilePath,
        [Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $Arguments,
        [Parameter(Mandatory = $true)] [string] $WorkingDirectory,
        [Parameter(Mandatory = $true)] [string] $StdOut,
        [Parameter(Mandatory = $true)] [string] $StdErr
    )
    if (-not ('JellyfinSandbox.LoggedProcess' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace JellyfinSandbox
{
    public static class LoggedProcess
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public int bInheritHandle;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, ref SecurityAttributes security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint ShareAll = 7; // read, write, delete: the logs stay readable while the server runs
        private const uint OpenExisting = 3;
        private const uint CreateAlways = 2;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateNoWindow = 0x08000000;
        private const int StartfUseStdHandles = 0x00000100;
        private static readonly IntPtr ProcThreadAttributeHandleList = new IntPtr(0x00020002);

        private static SafeFileHandle Open(string path, uint access, uint disposition)
        {
            // Inheritable, so it can be named in the handle list; nothing else of this process is.
            SecurityAttributes security = new SecurityAttributes();
            security.nLength = Marshal.SizeOf(typeof(SecurityAttributes));
            security.bInheritHandle = 1;
            SafeFileHandle handle = CreateFileW(path, access, ShareAll, ref security, disposition, 0x80, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Can't open " + path);
            }

            return handle;
        }

        public static Process Start(string filePath, string arguments, string workingDirectory, string stdOut, string stdErr)
        {
            using (SafeFileHandle input = Open("NUL", GenericRead, OpenExisting))
            using (SafeFileHandle output = Open(stdOut, GenericWrite, CreateAlways))
            using (SafeFileHandle error = Open(stdErr, GenericWrite, CreateAlways))
            {
                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                IntPtr list = Marshal.AllocHGlobal(size);
                IntPtr handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
                bool initialized = false;
                try
                {
                    if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList");
                    }

                    initialized = true;
                    Marshal.WriteIntPtr(handles, 0, input.DangerousGetHandle());
                    Marshal.WriteIntPtr(handles, IntPtr.Size, output.DangerousGetHandle());
                    Marshal.WriteIntPtr(handles, IntPtr.Size * 2, error.DangerousGetHandle());
                    if (!UpdateProcThreadAttribute(list, 0, ProcThreadAttributeHandleList, handles, new IntPtr(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute");
                    }

                    StartupInfoEx startup = new StartupInfoEx();
                    startup.StartupInfo.cb = Marshal.SizeOf(typeof(StartupInfoEx));
                    startup.StartupInfo.dwFlags = StartfUseStdHandles;
                    startup.StartupInfo.hStdInput = input.DangerousGetHandle();
                    startup.StartupInfo.hStdOutput = output.DangerousGetHandle();
                    startup.StartupInfo.hStdError = error.DangerousGetHandle();
                    startup.lpAttributeList = list;
                    StringBuilder commandLine = new StringBuilder("\"" + filePath + "\"" + (arguments.Length > 0 ? " " + arguments : string.Empty));
                    ProcessInformation info;
                    if (!CreateProcessW(filePath, commandLine, IntPtr.Zero, IntPtr.Zero, true, ExtendedStartupInfoPresent | CreateNoWindow, IntPtr.Zero, workingDirectory, ref startup, out info))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Can't start " + filePath);
                    }

                    try
                    {
                        // While its handle is open the process id can't be reused, so this is the process just started.
                        return Process.GetProcessById(info.dwProcessId);
                    }
                    finally
                    {
                        CloseHandle(info.hThread);
                        CloseHandle(info.hProcess);
                    }
                }
                finally
                {
                    if (initialized)
                    {
                        DeleteProcThreadAttributeList(list);
                    }

                    Marshal.FreeHGlobal(list);
                    Marshal.FreeHGlobal(handles);
                }
            }
        }
    }
}
'@
    }

    return [JellyfinSandbox.LoggedProcess]::Start($FilePath, $Arguments, $WorkingDirectory, $StdOut, $StdErr)
}

function Stop-VerifiedProcess {
    <# Stops one process after checking again that its command line contains Marker. Never stops by name. #>
    param([Parameter(Mandatory = $true)] [int] $ProcessId, [Parameter(Mandatory = $true)] [string] $Marker)
    $process = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction SilentlyContinue
    if (-not $process) { return $false }
    if (-not $process.CommandLine -or $process.CommandLine.IndexOf($Marker, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        Write-Warning "Not stopping PID ${ProcessId}: its command line does not contain $Marker."
        return $false
    }

    Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
    return $true
}

function Test-ProcessRunning {
    <#
    Whether ProcessId is still a running process: listed and not exited, and, with StartTime (when the process meant was
    created, from Win32_Process.CreationDate), started at that time, so a reused id counts as gone. A process Windows still
    lists while it exits but reports as exited counts as gone too.
    #>
    param([Parameter(Mandatory = $true)] [int] $ProcessId, [Nullable[datetime]] $StartTime)
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $process) { return $false }
    try { if ($process.HasExited) { return $false } } catch { $null = $_ }
    if ($null -ne $StartTime) {
        try {
            if ([Math]::Abs(($process.StartTime - [datetime]$StartTime).TotalSeconds) -gt 2) { return $false }
        }
        catch { $null = $_ }
    }

    return $true
}

function Wait-ProcessExit {
    <#
    Waits until ProcessId has exited (Test-ProcessRunning), looking every PollMs for up to TimeoutSec, and returns whether
    it did. Stop-Sandbox.ps1 polls rather than trusting one look: right after a graceful shutdown the server was sometimes
    still there for a moment ("still running" in 2 of about 14 stops, gone a moment later).
    #>
    param(
        [Parameter(Mandatory = $true)] [int] $ProcessId,
        [Nullable[datetime]] $StartTime,
        [int] $TimeoutSec = 30,
        [int] $PollMs = 250
    )
    $deadline = [DateTime]::UtcNow.AddSeconds([Math]::Max(0, $TimeoutSec))
    while ($true) {
        if (-not (Test-ProcessRunning -ProcessId $ProcessId -StartTime $StartTime)) { return $true }
        if ([DateTime]::UtcNow -ge $deadline) { return $false }
        Start-Sleep -Milliseconds ([Math]::Max(10, $PollMs))
    }
}

# ------------------------------------------------------------------------------------------------ files

function Write-Utf8NoBom {
    param([Parameter(Mandatory = $true)] [string] $Path, [Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $Text)
    $Path = Resolve-FullPath $Path
    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)] [string] $Path)
    $text = [System.IO.File]::ReadAllText((Resolve-FullPath $Path), [System.Text.Encoding]::UTF8)
    return ($text | ConvertFrom-Json)
}

function Write-JsonFile {
    param([Parameter(Mandatory = $true)] [string] $Path, [Parameter(Mandatory = $true)] $Value)
    Write-Utf8NoBom -Path $Path -Text ((($Value | ConvertTo-Json -Depth 20) -replace "`r`n", "`n") + "`n")
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)] [string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-BuildIsCompilerOutput {
    <#
    Checks the files a build of another checkout published into this run's folder against the compiler's output in that
    checkout, and returns each one's Name, Sha256 and Bytes: the compiler output's. dotnet
    publish copies every assembly (and its PDB) the compiler wrote into obj\<Configuration>\<framework> of its project, so a
    file of the run's folder (artifacts\builds\<Plugin>-<Configuration>-<time>-<id>, a name anyone can predict) that
    differs from it was written by something else between the build and the checks, and is refused before anything is
    installed. For a release the project is inside the worktree locked for this run, so the baseline is the tag's own
    build, never the run folder's word.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $OutputFolder,
        [Parameter(Mandatory = $true)] [string] $ProjectFolder,
        [Parameter(Mandatory = $true)] [string] $Configuration,
        [Parameter(Mandatory = $true)] [string] $Framework,
        [Parameter(Mandatory = $true)] [string[]] $Files
    )

    $compiled = Join-Path (Join-Path (Join-Path $ProjectFolder 'obj') $Configuration) $Framework
    foreach ($name in $Files) {
        $source = Join-Path $compiled $name
        $copy = Join-Path $OutputFolder $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "The compiler's output $source isn't there, so the build in $OutputFolder can't be checked against it; nothing was installed."
        }

        $expected = Get-FileSha256 $source
        $actual = '(missing)'
        if (Test-Path -LiteralPath $copy -PathType Leaf) { $actual = Get-FileSha256 $copy }
        if ($actual -ne $expected) {
            throw ("$name in $OutputFolder isn't what the compiler wrote (SHA-256 $actual; the compiler's output $source is $expected): something wrote into " +
                "the run's build folder after the build, so nothing of it was installed.")
        }

        [pscustomobject]@{ Name = $name; Sha256 = $expected; Bytes = (Get-Item -LiteralPath $source).Length }
    }
}

# ------------------------------------------------------------------------------------------ scheduled tasks

function Get-JellyfinTaskId {
    <#
    The id Jellyfin gives a scheduled task: the MD5 of the task type's full name in UTF-16 as a Guid, as
    ScheduledTaskWorker.Id and BaseExtensions.GetMD5 compute it (v10.11.5). The task's trigger file is
    <config>\ScheduledTasks\<id>.js and its last result <data>\ScheduledTasks\<id>.js, with the id in the "D" format.
    #>
    param([Parameter(Mandatory = $true)] [string] $TypeName)
    $md5 = [System.Security.Cryptography.MD5]::Create()
    try {
        $hash = $md5.ComputeHash([System.Text.Encoding]::Unicode.GetBytes($TypeName))
    }
    finally {
        $md5.Dispose()
    }

    return (New-Object System.Guid (, $hash)).ToString('D')
}

function Set-SandboxTaskTriggersOff {
    <#
    Writes an empty trigger list ("[]") for each task into ConfigDir\ScheduledTasks\<id>.js, which Jellyfin reads at
    startup instead of the task's default triggers, so the task never runs by itself (it can still be started by hand
    from the dashboard). Only for sandbox config folders: ConfigDir must be outside C:\ProgramData\Jellyfin.
    Returns one object per task with the file written.
    #>
    param([Parameter(Mandatory = $true)] [string] $ConfigDir, [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [object[]] $Tasks)
    $ConfigDir = Assert-SafePath -Path $ConfigDir -What 'config folder'
    $folder = Join-Path $ConfigDir 'ScheduledTasks'
    $written = @()
    foreach ($task in $Tasks) {
        $id = Get-JellyfinTaskId -TypeName $task.Type
        $file = Join-Path $folder ($id + '.js')
        Write-Utf8NoBom -Path $file -Text '[]'
        $written += [pscustomobject]@{ Owner = $task.Owner; Name = $task.Name; Key = $task.Key; Type = $task.Type; Id = $id; File = $file }
    }

    return $written
}

function ConvertTo-UtcTime {
    <#
    A time from Jellyfin's API or sandbox.json as a UTC DateTime: an ISO 8601 string (with Z, with an offset, or with
    neither, which is taken as UTC, like every *Utc field Jellyfin writes) or a DateTime (PowerShell 7 parses JSON dates;
    Unspecified is taken as UTC, Local is converted). $null or empty gives $null.
    #>
    param($Value)
    if ($null -eq $Value -or ([string]$Value).Length -eq 0) { return $null }
    if ($Value -is [DateTime]) {
        if ($Value.Kind -eq [DateTimeKind]::Unspecified) { return [DateTime]::SpecifyKind($Value, [DateTimeKind]::Utc) }
        return $Value.ToUniversalTime()
    }

    if ($Value -is [DateTimeOffset]) { return $Value.UtcDateTime }

    return [DateTime]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AdjustToUniversal -bor [Globalization.DateTimeStyles]::AssumeUniversal)
}

function Test-SandboxTasksOff {
    <#
    Evaluates what GET /ScheduledTasks returned (Tasks) against the task table. Every task a
    sandbox with these community plugins keeps without triggers (Get-SandboxDisabledTasks) must be listed under its key,
    with the id its trigger file is named after, no triggers, Idle, and no run that started at or after StartedUtc (the
    server's start, sandbox.json); with Jellyfin Enhanced, CdnCacheDir (its cdn-cache folder) must hold no file written
    since StartedUtc. Nothing but that folder is read. Returns Problems (empty when all hold) and Report (one line per
    check, for the passing message). Initialize-Sandbox.ps1 calls it and fails on any problem.
    #>
    param(
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [object[]] $Tasks,
        [Parameter(Mandatory = $true)] $StartedUtc,
        [string[]] $Community = @(),
        [string] $CdnCacheDir
    )
    $started = ConvertTo-UtcTime $StartedUtc
    if ($null -eq $started) { throw 'StartedUtc is empty: the server start time is needed to tell a run since the start.' }

    $problems = @()
    $report = @()
    foreach ($expected in @(Get-SandboxDisabledTasks -Community $Community)) {
        $label = "$($expected.Owner)'s task '$($expected.Name)'"
        $task = $Tasks | Where-Object { $_ -and $_.PSObject.Properties['Key'] -and $_.Key -eq $expected.Key } | Select-Object -First 1
        if (-not $task) {
            $problems += "$label (key $($expected.Key)) is not listed. If it was renamed, update the task table in scripts\JellyfinSandbox.psm1."
            continue
        }

        # The API gives the id in the "N" format; the trigger file is named in the "D" format (Get-JellyfinTaskId).
        $id = (Get-JellyfinTaskId -TypeName $expected.Type).Replace('-', '')
        $actualId = if ($task.PSObject.Properties['Id']) { [string]$task.Id } else { '' }
        if ($actualId -ne $id) {
            $problems += "$label has the id $actualId, but its trigger file is written for $id ($($expected.Type)). The task type was renamed: update scripts\JellyfinSandbox.psm1."
        }

        # A task without a Triggers property is not proof of no triggers.
        if (-not $task.PSObject.Properties['Triggers']) {
            $problems += "$label is listed without a Triggers property, so its triggers can't be checked."
        }
        else {
            $triggers = @($task.Triggers | Where-Object { $null -ne $_ })
            if ($triggers.Count -gt 0) {
                $types = @($triggers | ForEach-Object { if ($_.PSObject.Properties['Type']) { $_.Type } else { '?' } }) -join ', '
                $problems += "$label has $($triggers.Count) trigger(s) ($types). Restart the sandbox with dev-server.ps1, which writes its empty trigger list."
            }
        }

        $state = if ($task.PSObject.Properties['State']) { [string]$task.State } else { '' }
        if ($state -ne 'Idle') { $problems += "$label is $(if ($state) { $state } else { 'in no state' }), not Idle." }

        $lastStart = $null
        if ($task.PSObject.Properties['LastExecutionResult'] -and $task.LastExecutionResult -and $task.LastExecutionResult.PSObject.Properties['StartTimeUtc']) {
            $lastStart = ConvertTo-UtcTime $task.LastExecutionResult.StartTimeUtc
        }

        if ($lastStart -and $lastStart -ge $started) {
            $problems += "$label ran at $($lastStart.ToString('o')), after the server started at $($started.ToString('o'))."
        }

        $history = if ($lastStart) { "last run $($lastStart.ToString('yyyy-MM-dd HH:mm:ss'))Z, before this start" } else { 'never run' }
        $report += "'$($expected.Name)': no triggers, $history"
    }

    if ($Community -contains 'JellyfinEnhanced') {
        if (-not $CdnCacheDir) { throw "With Jellyfin Enhanced, pass -CdnCacheDir (its plugins\configurations\Jellyfin.Plugin.JellyfinEnhanced\cdn-cache folder)." }
        $files = @()
        if (Test-Path -LiteralPath $CdnCacheDir) { $files = @(Get-ChildItem -LiteralPath $CdnCacheDir -Recurse -File -Force) }
        $recent = @($files | Where-Object { $_.LastWriteTimeUtc -ge $started })
        if ($recent.Count -gt 0) {
            $root = (Resolve-FullPath $CdnCacheDir).TrimEnd('\') + '\'
            $examples = @($recent | Select-Object -First 3 | ForEach-Object { $_.FullName.Substring($root.Length) }) -join ', '
            $problems += "Jellyfin Enhanced wrote $($recent.Count) file(s) into its CDN cache since the server started, downloaded from the internet ($examples). " +
                'A browser that opened a Jellyfin Enhanced page asked /JellyfinEnhanced/cdn/ for them; webcheck.mjs blocks that route.'
        }

        $report += "Jellyfin Enhanced's CDN cache: $($files.Count) file(s), none written since this start"
    }

    return [pscustomobject]@{ Problems = [string[]]$problems; Report = [string[]]$report; StartedUtc = $started }
}

# -------------------------------------------------------------------------------------- assembly references

function Get-AssemblyReferenceList {
    <#
    The assemblies a .NET assembly references (its AssemblyRef table), as objects with Name and Version. Read in a
    new Windows PowerShell process: the reflection-only context holds one assembly per identity for the life of a
    process, so a rebuilt plugin with an unchanged version could not be read again in the same session.
    #>
    param([Parameter(Mandatory = $true)] [string] $Path)
    $full = Resolve-FullPath $Path
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "$full not found." }
    $literal = $full.Replace("'", "''")
    $command = "`$ErrorActionPreference = 'Stop'; " +
        "[System.Reflection.Assembly]::ReflectionOnlyLoad([System.IO.File]::ReadAllBytes('$literal')).GetReferencedAssemblies() | " +
        "ForEach-Object { `$_.Name + '|' + `$_.Version }"
    $encoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($command))
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $lines = @(& $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded)
    if ($LASTEXITCODE -ne 0) { throw "Could not read the assembly references of $full (exit code $LASTEXITCODE)." }

    $references = @()
    foreach ($line in $lines) {
        $text = ([string]$line).Trim()
        if (-not $text) { continue }
        if ($text -notmatch '^([^|]+)\|(\d+(\.\d+){1,3})$') { throw "Unexpected output while reading the references of ${full}: $text" }
        $references += [pscustomobject]@{ Name = $Matches[1]; Version = [version]$Matches[2] }
    }

    return $references
}

function Test-AssemblyReferenceVersions {
    <#
    Compares what the given assemblies (a plugin's DLLs) reference with what the Jellyfin
    program folder JellyfinDir ships (only read). Every referenced assembly that JellyfinDir ships as <Name>.dll must be
    referenced at a version no higher than the shipped one; a higher one passes the DLL allowlist but can't bind at run
    time, and when a DI-built constructor touches it the whole server stops at startup (seen in a sandbox). References that
    JellyfinDir doesn't ship (the plugin's own assemblies, or files it lacks) are listed under NotShipped.
    #>
    param([Parameter(Mandatory = $true)] [string[]] $AssemblyPaths, [Parameter(Mandatory = $true)] [string] $JellyfinDir)
    $JellyfinDir = Resolve-FullPath $JellyfinDir
    if (-not (Test-Path -LiteralPath $JellyfinDir -PathType Container) -or
        -not ((Test-Path -LiteralPath (Join-Path $JellyfinDir 'jellyfin.dll')) -or (Test-Path -LiteralPath (Join-Path $JellyfinDir 'jellyfin.exe')))) {
        throw "$JellyfinDir is not a Jellyfin program folder (no jellyfin.dll or jellyfin.exe); pass -JellyfinDir with the target server's program folder."
    }

    $checked = @()
    $newer = @()
    $notShipped = @()
    $shippedCache = @{}
    foreach ($assemblyPath in $AssemblyPaths) {
        $assemblyName = Split-Path -Leaf $assemblyPath
        foreach ($reference in @(Get-AssemblyReferenceList -Path $assemblyPath)) {
            if (-not $shippedCache.ContainsKey($reference.Name)) {
                $shipped = $null
                $file = Join-Path $JellyfinDir ($reference.Name + '.dll')
                if (Test-Path -LiteralPath $file -PathType Leaf) {
                    try {
                        $identity = [System.Reflection.AssemblyName]::GetAssemblyName($file)
                        if ($identity.Name -ieq $reference.Name) { $shipped = $identity.Version }
                    }
                    catch {
                        # Not a .NET assembly (a native DLL of that name): nothing to compare with.
                        $shipped = $null
                    }
                }

                $shippedCache[$reference.Name] = $shipped
            }

            $shippedVersion = $shippedCache[$reference.Name]
            if ($null -eq $shippedVersion) {
                $notShipped += [pscustomobject]@{ Assembly = $assemblyName; Reference = $reference.Name; Referenced = $reference.Version }
                continue
            }

            $entry = [pscustomobject]@{ Assembly = $assemblyName; Reference = $reference.Name; Referenced = $reference.Version; Shipped = $shippedVersion }
            $checked += $entry
            if ($reference.Version -gt $shippedVersion) { $newer += $entry }
        }
    }

    return [pscustomobject]@{ JellyfinDir = $JellyfinDir; Checked = $checked; Newer = $newer; NotShipped = $notShipped }
}

function Assert-AssemblyReferenceVersions {
    <# Test-AssemblyReferenceVersions, throwing a message that names every assembly referenced at a newer version than shipped. #>
    param([Parameter(Mandatory = $true)] [string[]] $AssemblyPaths, [Parameter(Mandatory = $true)] [string] $JellyfinDir, [string] $What = 'The plugin')
    $result = Test-AssemblyReferenceVersions -AssemblyPaths $AssemblyPaths -JellyfinDir $JellyfinDir
    if (@($result.Newer).Count -gt 0) {
        $list = @($result.Newer | ForEach-Object { "$($_.Assembly) references $($_.Reference) $($_.Referenced), but the server ships $($_.Shipped)" }) -join '; '
        throw "$What references assemblies newer than the Jellyfin in $($result.JellyfinDir) ships: $list. " +
            'Such a plugin passes the DLL allowlist but can stop the whole server at startup: ' +
            'reference exactly the version the server ships (Directory.Packages.props).'
    }

    return $result
}

function Get-JellyfinServerVersion {
    <#
    The version of the Jellyfin in a program folder (only read): the assembly version of its jellyfin.dll (10.11.5.0 for
    10.11.5), read from the file's metadata without loading it, or else jellyfin.exe's file version. $null when neither
    can be read.
    #>
    param([Parameter(Mandatory = $true)] [string] $JellyfinDir)
    $JellyfinDir = Resolve-FullPath $JellyfinDir
    $dll = Join-Path $JellyfinDir 'jellyfin.dll'
    if (Test-Path -LiteralPath $dll -PathType Leaf) {
        try {
            $identity = [System.Reflection.AssemblyName]::GetAssemblyName($dll)
            if ($identity.Name -ieq 'jellyfin') { return $identity.Version }
        }
        catch {
            Write-Verbose "$dll is not a .NET assembly: $_"
        }
    }

    $exe = Join-Path $JellyfinDir 'jellyfin.exe'
    if (Test-Path -LiteralPath $exe -PathType Leaf) {
        $text = [string](Get-Item -LiteralPath $exe).VersionInfo.FileVersion
        $parsed = $null
        if ($text -and [version]::TryParse($text.Trim(), [ref]$parsed)) { return $parsed }
    }

    return $null
}

function Test-JellyfinTargetMatch {
    <#
    True when a Jellyfin version is the one a JellyfinTarget builds for: '10.11' is 10.11.x, '12' is 12.x (the rule of
    the kit's ABI guard, kit/README.md). Every part the target names must match.
    #>
    param([Parameter(Mandatory = $true)] [string] $JellyfinTarget, [Parameter(Mandatory = $true)] [version] $Version)
    if ($JellyfinTarget -notmatch '^\d+(\.\d+)?$') { throw "JellyfinTarget '$JellyfinTarget' is not a major or major.minor version." }
    $parts = $JellyfinTarget.Split('.')
    if ($Version.Major -ne [int]$parts[0]) { return $false }
    if ($parts.Count -gt 1 -and $Version.Minor -ne [int]$parts[1]) { return $false }
    return $true
}

function Assert-JellyfinTarget {
    <#
    Deploy-Plugin.ps1: the Jellyfin program folder the reference-version check compares with (-JellyfinDir) must hold
    the Jellyfin the build is for (-JellyfinTarget). Otherwise every reference of a 12 build would be compared with a
    10.11 server's assemblies (or the other way round) and refused for the wrong reason. Returns the server version.
    #>
    param([Parameter(Mandatory = $true)] [string] $JellyfinDir, [Parameter(Mandatory = $true)] [string] $JellyfinTarget)
    $JellyfinDir = Resolve-FullPath $JellyfinDir
    $version = Get-JellyfinServerVersion -JellyfinDir $JellyfinDir
    if ($null -eq $version) {
        throw "Can't tell which Jellyfin $JellyfinDir holds (no readable jellyfin.dll or jellyfin.exe). " +
            "Pass -JellyfinDir with the program folder of a Jellyfin $JellyfinTarget server."
    }

    if (-not (Test-JellyfinTargetMatch -JellyfinTarget $JellyfinTarget -Version $version)) {
        # Targets name the major version from 12 on, major.minor before (Directory.Build.props).
        $serverTarget = if ($version.Major -ge 12) { [string]$version.Major } else { "$($version.Major).$($version.Minor)" }
        throw "-JellyfinTarget $JellyfinTarget builds for Jellyfin $JellyfinTarget.x, but $JellyfinDir holds Jellyfin $version " +
            '(-JellyfinDir defaults to the installed server). Pass -JellyfinDir with the program folder of a Jellyfin ' +
            "$JellyfinTarget.x server, whose assemblies the reference-version check compares with; for the Jellyfin in $JellyfinDir " +
            "the target is -JellyfinTarget $serverTarget."
    }

    return $version
}

# -------------------------------------------------------------------------------------------- build.yaml

function Read-BuildYaml {
    <#
    Reads the top-level fields of a jprm build.yaml, like kit/testsupport/PluginIdentity/BuildYaml.cs:
    plain, quoted, folded (>) and literal (|) scalars, and lists of scalars. Returns an ordered hashtable.
    #>
    param([Parameter(Mandatory = $true)] [string] $Path)
    return ConvertFrom-BuildYaml -Text ([System.IO.File]::ReadAllText((Resolve-FullPath $Path))) -Source $Path
}

function ConvertFrom-BuildYaml {
    <#
    Read-BuildYaml on text: a build.yaml that isn't a file of the checkout, such as a release tag's (read with git
    show). Source names it in the errors.
    #>
    param([Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $Text, [Parameter(Mandatory = $true)] [string] $Source)
    $Path = $Source
    $lines = ($Text -replace "`r`n", "`n").Split("`n")
    $result = [ordered]@{}
    $i = 0
    while ($i -lt $lines.Length) {
        $line = $lines[$i]
        if ($line.Trim().Length -eq 0 -or $line.TrimStart().StartsWith('#') -or $line.StartsWith('---') -or $line.StartsWith('...')) { $i++; continue }
        if ([char]::IsWhiteSpace($line[0]) -or $line[0] -eq '-') { throw "${Path}: line $($i + 1): expected a top-level 'key: value'." }
        $colon = $line.IndexOf(':')
        if ($colon -le 0) { throw "${Path}: line $($i + 1): expected 'key: value'." }
        $key = $line.Substring(0, $colon).Trim()
        $rest = (Remove-YamlComment $line.Substring($colon + 1)).Trim()
        if ($result.Contains($key)) { throw "${Path}: duplicate key '$key'." }
        $i++
        if ($rest.Length -gt 0 -and ($rest[0] -eq '>' -or $rest[0] -eq '|')) {
            $block = New-Object System.Collections.Generic.List[string]
            $indent = -1
            while ($i -lt $lines.Length) {
                $blockLine = $lines[$i]
                if ($blockLine.Trim().Length -eq 0) { $block.Add(''); $i++; continue }
                $lineIndent = $blockLine.Length - $blockLine.TrimStart().Length
                if ($lineIndent -eq 0) { break }
                if ($indent -lt 0) { $indent = $lineIndent }
                $block.Add($blockLine.Substring([Math]::Min($indent, $lineIndent)))
                $i++
            }

            while ($block.Count -gt 0 -and $block[$block.Count - 1].Length -eq 0) { $block.RemoveAt($block.Count - 1) }
            if ($rest[0] -eq '>') {
                $builder = New-Object System.Text.StringBuilder
                $blanks = 0
                foreach ($blockLine in $block) {
                    if ($blockLine.Length -eq 0) { $blanks++; continue }
                    if ($blanks -gt 0) { [void]$builder.Append("`n" * $blanks) } elseif ($builder.Length -gt 0) { [void]$builder.Append(' ') }
                    [void]$builder.Append($blockLine)
                    $blanks = 0
                }

                $result[$key] = $builder.ToString()
            }
            else {
                $result[$key] = ($block -join "`n")
            }
        }
        elseif ($rest.Length -gt 0) {
            $result[$key] = ConvertFrom-YamlScalar $rest
        }
        else {
            $items = New-Object System.Collections.Generic.List[string]
            while ($i -lt $lines.Length) {
                $itemLine = $lines[$i]
                if ($itemLine.Trim().Length -eq 0 -or $itemLine.TrimStart().StartsWith('#')) { $i++; continue }
                $trimmed = $itemLine.TrimStart()
                if (-not $trimmed.StartsWith('-')) { break }
                $items.Add((ConvertFrom-YamlScalar (Remove-YamlComment $trimmed.Substring(1)).Trim()))
                $i++
            }

            if ($items.Count -eq 0) { throw "${Path}: '$key' has neither a value nor list items." }
            $result[$key] = [string[]]$items.ToArray()
        }
    }

    return $result
}

function Remove-YamlComment {
    param([string] $Value)
    $quote = [char]0
    for ($n = 0; $n -lt $Value.Length; $n++) {
        $c = $Value[$n]
        if ($quote -ne [char]0) { if ($c -eq $quote) { $quote = [char]0 } }
        elseif ($c -eq '"' -or $c -eq "'") { $quote = $c }
        elseif ($c -eq '#' -and ($n -eq 0 -or [char]::IsWhiteSpace($Value[$n - 1]))) { return $Value.Substring(0, $n) }
    }

    return $Value
}

function ConvertFrom-YamlScalar {
    param([string] $Value)
    if ($Value.Length -ge 2 -and $Value[0] -eq '"' -and $Value[$Value.Length - 1] -eq '"') {
        return $Value.Substring(1, $Value.Length - 2).Replace('\"', '"').Replace('\\', '\')
    }

    if ($Value.Length -ge 2 -and $Value[0] -eq "'" -and $Value[$Value.Length - 1] -eq "'") {
        return $Value.Substring(1, $Value.Length - 2).Replace("''", "'")
    }

    return $Value
}

function Get-PluginFolders {
    <# Names of plugins/<Name> folders that hold a build.yaml, in this repository or the checkout RepoRoot. #>
    param([string] $RepoRoot = $script:RepoRoot)
    $plugins = Join-Path $RepoRoot 'plugins'
    if (-not (Test-Path -LiteralPath $plugins -PathType Container)) { return @() }
    return @(Get-ChildItem -LiteralPath $plugins -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'build.yaml') } |
        Sort-Object Name | Select-Object -ExpandProperty Name)
}

function Get-PluginBuildInfo {
    <#
    What plugins/<Plugin>/build.yaml says, with the project to build: in this repository, or in the checkout
    RepoRoot (Deploy-Plugin.ps1 -SourceRoot, a release tag's temporary worktree). With YamlText the file's text is
    given instead (a release tag's build.yaml read with git show, YamlSource naming it); Project is still RepoRoot's.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $Plugin,
        [string] $RepoRoot = $script:RepoRoot,
        [AllowEmptyString()] [string] $YamlText,
        [string] $YamlSource
    )
    # The folder's own spelling (-Plugin bookmarks is plugins\Bookmarks): it names the build's folders, the
    # backup and, through git, the release tag's files, where case matters.
    if (-not $PSBoundParameters.ContainsKey('YamlText')) {
        $named = @(Get-PluginFolders -RepoRoot $RepoRoot | Where-Object { $_ -ieq $Plugin })
        if ($named.Count -eq 1) { $Plugin = $named[0] }
    }

    $folder = Join-Path (Join-Path $RepoRoot 'plugins') $Plugin
    $yamlPath = Join-Path $folder 'build.yaml'
    if ($PSBoundParameters.ContainsKey('YamlText')) {
        if (-not $YamlSource) { $YamlSource = $yamlPath }
        $yamlPath = $YamlSource
        $yaml = ConvertFrom-BuildYaml -Text $YamlText -Source $YamlSource
    }
    else {
        if (-not (Test-Path -LiteralPath $yamlPath)) {
            throw "Unknown plugin '$Plugin': $yamlPath not found. Known: $((Get-PluginFolders -RepoRoot $RepoRoot) -join ', ')."
        }

        $yaml = Read-BuildYaml $yamlPath
    }

    foreach ($field in 'name', 'guid', 'version', 'targetAbi', 'framework', 'overview', 'description', 'category', 'owner', 'artifacts', 'changelog') {
        if (-not $yaml.Contains($field) -or -not $yaml[$field]) { throw "$yamlPath has no '$field'." }
    }

    $assemblies = @($yaml['artifacts'] | Where-Object { $_ -like '*.dll' })
    if ($assemblies.Count -eq 0) { throw "$yamlPath lists no DLL in 'artifacts'." }
    $mainAssembly = [System.IO.Path]::GetFileNameWithoutExtension($assemblies[0])
    return [pscustomobject]@{
        Folder     = $Plugin
        Path       = $folder
        Yaml       = $yaml
        Name       = [string]$yaml['name']
        Guid       = [string]$yaml['guid']
        Version    = [string]$yaml['version']
        Assemblies = [string[]]$assemblies
        Project    = Join-Path (Join-Path $folder $mainAssembly) ($mainAssembly + '.csproj')
    }
}

# ------------------------------------------------------------------------------------ git and release tags
#
# A real-server install builds the plugin's release tag, never the working tree: main carries unreleased work under
# the released version number between releases (Bookmarks 1.2's tasks while build.yaml still says the last release's
# version), so building the checkout would put unreleased code on the real server under a released version. Release tags
# are <plugin folder in lower case>/v<major>.<minor>.<patch> (bookmarks/v1.1.1), on a commit whose build.yaml says
# <major>.<minor>.<patch>.0. Install-RealServer.ps1 checks the tag out into a temporary detached worktree next to the
# repository, Deploy-Plugin.ps1 -RealServer builds and installs only such a checkout, and the worktree is removed
# afterwards. The worktree is locked with its run's own reason (New-ReleaseLockReason), which marks
# it as that run's for Deploy-Plugin.ps1 and for its removal, and the checkout must hold nothing ignored and hide nothing
# from git status; the real server also needs the checkout's own scripts committed (Get-RepoScriptsProblem).

function Test-SamePath {
    <#
    Whether two paths name the same file or folder: the same full path (ignoring case), or, when both exist, the same final
    path as Windows resolves it (8.3 names, junctions, subst). git reports the final spelling of what it was given.
    #>
    param([Parameter(Mandatory = $true)] [string] $Path, [Parameter(Mandatory = $true)] [string] $Other)
    $a = ConvertFrom-DevicePath (Resolve-FullPath $Path)
    $b = ConvertFrom-DevicePath (Resolve-FullPath $Other)
    if ($a -ieq $b) { return $true }
    $finalA = $null
    $finalB = $null
    try {
        $finalA = Get-FinalPath $a
        $finalB = Get-FinalPath $b
    }
    catch {
        return $false
    }

    if (-not $finalA -or -not $finalB) { return $false }
    return [bool]((ConvertFrom-DevicePath $finalA) -ieq (ConvertFrom-DevicePath $finalB))
}

function Get-GitPath {
    <# git.exe from PATH (a .cmd shim only when there is no .exe). The release installs need it. #>
    $commands = @(Get-Command git -CommandType Application -ErrorAction SilentlyContinue)
    $exe = @($commands | Where-Object { $_.Path -like '*.exe' } | Select-Object -First 1)
    if ($exe.Count -gt 0) { return $exe[0].Path }
    if ($commands.Count -gt 0) { return $commands[0].Path }
    throw "git isn't on PATH. A real-server install builds the plugin's release tag, which needs git (Git for Windows)."
}

function ConvertTo-CommandLineArgument {
    <# One argument of a Windows command line, quoted as CommandLineToArgvW reads it back (backslashes before a quote doubled). #>
    param([Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $Value)
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($c in $Value.ToCharArray()) {
        if ($c -eq [char]'\') { $backslashes++; continue }
        if ($c -eq [char]'"') {
            [void]$builder.Append([char]'\', 2 * $backslashes + 1)
        }
        elseif ($backslashes -gt 0) {
            [void]$builder.Append([char]'\', $backslashes)
        }

        [void]$builder.Append($c)
        $backslashes = 0
    }

    if ($backslashes -gt 0) { [void]$builder.Append([char]'\', 2 * $backslashes) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-Git {
    <#
    Runs git -C Repo with Arguments in a process of its own and returns ExitCode, Output (stdout's lines) and Error (stderr,
    trimmed). Not through the PowerShell pipeline: Windows PowerShell 5.1 turns a native command's redirected stderr (git's
    "Preparing worktree" line, progress) into error records, which $ErrorActionPreference = 'Stop' throws. Optional locks
    are off (GIT_OPTIONAL_LOCKS=0: git status doesn't write the index) and so are prompts.
    #>
    param([Parameter(Mandatory = $true)] [string] $Repo, [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]] $Arguments)
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = Get-GitPath
    $start.Arguments = (@(@('-C', $Repo) + @($Arguments)) | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $start.EnvironmentVariables['GIT_OPTIONAL_LOCKS'] = '0'
    $start.EnvironmentVariables['GIT_TERMINAL_PROMPT'] = '0'
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $errorRead = $process.StandardError.ReadToEndAsync()
        $text = $process.StandardOutput.ReadToEnd()
        $process.WaitForExit()
        $errorText = [string]$errorRead.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
    }
    finally {
        $process.Dispose()
    }

    $lines = @($text -split "`r?`n")
    if ($lines.Count -gt 0 -and $lines[$lines.Count - 1] -eq '') { $lines = @($lines | Select-Object -First ($lines.Count - 1)) }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = [string[]]$lines; Error = $errorText.Trim() }
}

function Assert-Git {
    <# Invoke-Git that throws, with git's own message, unless git succeeded; returns stdout's lines. What says what it was for. #>
    param(
        [Parameter(Mandatory = $true)] [string] $Repo,
        [Parameter(Mandatory = $true)] [string[]] $Arguments,
        [Parameter(Mandatory = $true)] [string] $What
    )
    $run = Invoke-Git -Repo $Repo -Arguments $Arguments
    if ($run.ExitCode -ne 0) {
        $detail = 'no message'
        if ($run.Error) { $detail = $run.Error -replace '\s*\r?\n\s*', ' ' }
        throw "$What failed (git $($Arguments -join ' ') in $Repo, exit code $($run.ExitCode): $detail)."
    }

    return $run.Output
}

function Get-PluginReleaseTagPrefix {
    <# The start of a plugin's release tags: its folder name in lower case and /v, so bookmarks/v1.1.1 is Bookmarks 1.1.1. #>
    param([Parameter(Mandatory = $true)] [string] $Plugin)
    return $Plugin.ToLowerInvariant() + '/v'
}

function ConvertTo-PluginReleaseTag {
    <#
    A tag name read as a release tag of Plugin, or $null when it isn't one: exactly <prefix><major>.<minor>.<patch> in ASCII
    digits (not bookmarks/v1.2, bookmarks/v1.2.0-rc1 or bookmarks/v1.2.0.0). Name, Version (a [version], so versions compare
    as numbers) and Text (x.y.z with the numbers as numbers).
    #>
    param([Parameter(Mandatory = $true)] [string] $Plugin, [Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $Name)
    $prefix = Get-PluginReleaseTagPrefix $Plugin
    $match = [regex]::Match($Name, '^' + [regex]::Escape($prefix) + '([0-9]{1,9})\.([0-9]{1,9})\.([0-9]{1,9})$')
    if (-not $match.Success) { return $null }
    $text = '{0}.{1}.{2}' -f @([int]$match.Groups[1].Value, [int]$match.Groups[2].Value, [int]$match.Groups[3].Value)
    return [pscustomobject]@{ Name = $Name; Version = [version]$text; Text = $text }
}

function Get-PluginReleaseTags {
    <#
    A plugin's release tags in the repository at RepoRoot (ConvertTo-PluginReleaseTag), newest first by version compared as
    numbers: bookmarks/v1.10.0 before bookmarks/v1.9.0, which string order would put first. Only reads (git tag --list).
    #>
    param([Parameter(Mandatory = $true)] [string] $Plugin, [Parameter(Mandatory = $true)] [string] $RepoRoot)
    $prefix = Get-PluginReleaseTagPrefix $Plugin
    $names = @(Assert-Git -Repo $RepoRoot -Arguments @('tag', '--list', ($prefix + '*')) -What "Listing the $($prefix)* tags")
    $tags = @($names | ForEach-Object { ConvertTo-PluginReleaseTag -Plugin $Plugin -Name ([string]$_).Trim() } | Where-Object { $null -ne $_ })
    return @($tags | Sort-Object -Property @{ Expression = { $_.Version }; Descending = $true }, @{ Expression = { $_.Name }; Descending = $false })
}

function Get-ReleaseWorktreePath {
    <#
    Where Install-RealServer.ps1 checks a release tag out: next to the repository, <parent>\jfp-rel-<plugin in lower
    case>-<x.y.z> (C:\src\jfp-rel-bookmarks-1.1.1). Short on purpose: git can't write this repository's
    longest paths under a long folder, such as a scratch folder deep in %TEMP%.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $Plugin,
        [Parameter(Mandatory = $true)] [string] $Version,
        [Parameter(Mandatory = $true)] [string] $RepoRoot
    )
    $root = ConvertFrom-DevicePath (Resolve-FullPath $RepoRoot)
    $parent = Split-Path -Parent $root
    if (-not $parent) { $parent = $root }
    return Join-Path $parent ('jfp-rel-' + $Plugin.ToLowerInvariant() + '-' + $Version)
}

function Resolve-PluginRelease {
    <#
    The release of Plugin that a real-server install builds, read from the repository at RepoRoot without changing
    anything (git tag, rev-parse, for-each-ref, show): the tag of Version (x.y.z), else the newest release tag
    (Get-PluginReleaseTags). Throws the reason when
    - there is no such tag, or two tags name the same version (bookmarks/v1.2.0 and bookmarks/v01.2.0);
    - the tag doesn't point at a commit, or at one that no branch contains (refs/heads, refs/remotes): a release is tagged on
      the history of the branch it was released from, so the tag of a commit nothing else keeps isn't one;
    - that commit has no plugins folder (bookmarks/v1.0.0 predates this repository's layout), no folder plugins/<Plugin> in
      any case or no build.yaml in it, or its version isn't the tag's as x.y.z.0 (bookmarks/v1.1.1 needs 1.1.1.0).
    Plugin is matched to the tag's folder case-insensitively (-Plugin bookmarks is plugins/Bookmarks).
    Returns Plugin (the folder's name as the tag spells it), Tag, Version (x.y.z), PluginVersion (x.y.z.0), Commit (the
    full id), Branches (those that contain it),
    Newest (no tag names a newer version), Tags (every release tag, newest first), Info (Get-PluginBuildInfo of the tag's
    build.yaml, its Project inside the worktree) and WorktreePath (Get-ReleaseWorktreePath; Get-ReleaseWorktreeProblem says
    whether it is free).
    #>
    param([Parameter(Mandatory = $true)] [string] $Plugin, [Parameter(Mandatory = $true)] [string] $RepoRoot, [string] $Version)
    $RepoRoot = ConvertFrom-DevicePath (Resolve-FullPath $RepoRoot)
    $prefix = Get-PluginReleaseTagPrefix $Plugin
    $tags = @(Get-PluginReleaseTags -Plugin $Plugin -RepoRoot $RepoRoot)
    $known = 'none'
    if ($tags.Count -gt 0) { $known = @($tags | ForEach-Object { $_.Name }) -join ', ' }

    if ($Version) {
        if ($Version -notmatch '^[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}$') {
            throw "-Version $Version is not <major>.<minor>.<patch>, such as 1.1.1 (for the tag $($prefix)1.1.1)."
        }

        $wanted = [version]$Version
        $matching = @($tags | Where-Object { $_.Version -eq $wanted })
        if ($matching.Count -eq 0) { throw "There is no release tag $prefix$Version in $RepoRoot (the release tags of ${Plugin}: $known)." }
    }
    else {
        if ($tags.Count -eq 0) {
            $plugins = @(Get-PluginFolders -RepoRoot $RepoRoot)
            $pluginList = 'none'
            if ($plugins.Count -gt 0) { $pluginList = $plugins -join ', ' }
            throw ("There is no release tag of $Plugin ($prefix<major>.<minor>.<patch>) in $RepoRoot, and a real-server install builds a release tag, " +
                "never the working tree. Tag the release first, or rehearse the working tree on a stopped sandbox with -FromWorkingTree. Plugins in this checkout: $pluginList.")
        }

        $matching = @($tags | Where-Object { $_.Version -eq $tags[0].Version })
    }

    if ($matching.Count -gt 1) {
        throw "The tags $(@($matching | ForEach-Object { $_.Name }) -join ' and ') both name version $($matching[0].Text); delete the wrong one first."
    }

    $tag = $matching[0]
    $peeled = Invoke-Git -Repo $RepoRoot -Arguments @('rev-parse', '--verify', '--quiet', ('refs/tags/' + $tag.Name + '^{commit}'))
    if ($peeled.ExitCode -ne 0 -or @($peeled.Output).Count -eq 0) { throw "$($tag.Name) doesn't point at a commit." }
    $commit = ([string]@($peeled.Output)[0]).Trim()

    $branches = @(Assert-Git -Repo $RepoRoot -Arguments @('for-each-ref', '--contains', $commit, '--format=%(refname:short)', 'refs/heads', 'refs/remotes') -What "Looking for the branches that contain $($tag.Name)" |
        ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
    if ($branches.Count -eq 0) {
        throw ("$($tag.Name) points at commit $commit, which no branch of $RepoRoot contains (git branch --all --contains $commit lists none). " +
            'A release is tagged on the history of the branch it was released from, so this tag is not a release.')
    }

    # The plugin's folder as the tag's commit spells it, whatever case -Plugin came in (git paths are case-sensitive;
    # -Plugin bookmarks was once refused as predating the layout).
    $listing = Invoke-Git -Repo $RepoRoot -Arguments @('ls-tree', '--name-only', $commit, 'plugins/')
    $folders = @()
    if ($listing.ExitCode -eq 0) {
        $folders = @($listing.Output | ForEach-Object { [string]$_ } | Where-Object { $_.StartsWith('plugins/') } | ForEach-Object { $_.Substring(8) })
    }

    if ($folders.Count -eq 0) {
        throw "$($tag.Name) (commit $commit) has no plugins folder, so it predates this repository's layout (Bookmarks 1.0.0 was released from its own repository) and this script can't build it."
    }

    $named = @($folders | Where-Object { $_ -ceq $Plugin })
    if ($named.Count -eq 0) { $named = @($folders | Where-Object { $_ -ieq $Plugin }) }
    if ($named.Count -eq 0) {
        throw "$($tag.Name) (commit $commit) has no plugins/$Plugin folder (its plugins folder holds: $($folders -join ', '))."
    }

    if ($named.Count -gt 1) {
        throw "$($tag.Name) (commit $commit) has more than one folder for ${Plugin}: $(@($named | ForEach-Object { "plugins/$_" }) -join ' and '). Give -Plugin as one of them is spelled."
    }

    $Plugin = $named[0]
    $yamlName = "plugins/$Plugin/build.yaml"
    $show = Invoke-Git -Repo $RepoRoot -Arguments @('show', ($commit + ':' + $yamlName))
    if ($show.ExitCode -ne 0) {
        throw "$($tag.Name) (commit $commit) has no ${yamlName}, so this script can't build it."
    }

    $worktree = Get-ReleaseWorktreePath -Plugin $Plugin -Version $tag.Text -RepoRoot $RepoRoot
    $info = Get-PluginBuildInfo -Plugin $Plugin -RepoRoot $worktree -YamlText (@($show.Output) -join "`n") -YamlSource "$($tag.Name):$yamlName"
    $pluginVersion = $tag.Text + '.0'
    if ($info.Version -ne $pluginVersion) {
        throw "$($tag.Name)'s $yamlName says version $($info.Version), not $pluginVersion, so the tag isn't the release it names: a release tag goes on the commit that sets its version."
    }

    return [pscustomobject]@{
        Plugin        = $Plugin
        Tag           = $tag.Name
        Version       = $tag.Text
        PluginVersion = $pluginVersion
        Commit        = $commit
        Branches      = [string[]]$branches
        Newest        = [bool]($tags[0].Version -eq $tag.Version)
        Tags          = @($tags)
        Info          = $info
        WorktreePath  = $worktree
    }
}

function Get-GitWorktrees {
    <#
    The worktrees git has registered for the repository at RepoRoot (git worktree list --porcelain): Path (in Windows
    spelling), Head, Branch, Detached, Locked, LockReason (the text of git's "locked <reason>", '' without one) and Prunable
    (its folder is gone).
    #>
    param([Parameter(Mandatory = $true)] [string] $RepoRoot)
    $lines = @(Assert-Git -Repo $RepoRoot -Arguments @('worktree', 'list', '--porcelain') -What 'Listing the worktrees')
    $result = New-Object System.Collections.Generic.List[object]
    $current = $null
    foreach ($entry in @($lines) + @('')) {
        $line = [string]$entry
        if ($line -eq '' -or $line.StartsWith('worktree ')) {
            if ($null -ne $current) { $result.Add([pscustomobject]$current) }
            $current = $null
            if ($line -eq '') { continue }
            $current = [ordered]@{
                Path = ConvertFrom-DevicePath (Resolve-FullPath $line.Substring(9)); Head = ''; Branch = ''; Detached = $false; Locked = $false; LockReason = ''; Prunable = $false
            }
            continue
        }

        if ($null -eq $current) { continue }
        if ($line.StartsWith('HEAD ')) { $current.Head = $line.Substring(5) }
        elseif ($line.StartsWith('branch ')) { $current.Branch = $line.Substring(7) }
        elseif ($line -eq 'detached') { $current.Detached = $true }
        elseif ($line -eq 'locked') { $current.Locked = $true }
        elseif ($line.StartsWith('locked ')) { $current.Locked = $true; $current.LockReason = $line.Substring(7) }
        elseif ($line -eq 'prunable' -or $line.StartsWith('prunable ')) { $current.Prunable = $true }
    }

    return $result.ToArray()
}

function New-ReleaseLockReason {
    <#
    The lock reason of the release worktree one run of Install-RealServer.ps1 makes: a new id for the run, its
    PID and its start. A worktree is the run's only while git has it locked with exactly this text: Deploy-Plugin.ps1
    -RealServer installs nothing else, and the run removes nothing else. Plain ASCII without quotes or backslashes, which
    git worktree list --porcelain would quote.
    #>
    $started = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ', [System.Globalization.CultureInfo]::InvariantCulture)
    return "Install-RealServer.ps1 run $([guid]::NewGuid().ToString('N')) (PID $PID, started $started)"
}

function Get-WorktreeLockText {
    <# How a registered worktree is locked, for the messages: "unlocked", "locked: <reason>" or "locked without a reason". #>
    param([Parameter(Mandatory = $true)] [AllowEmptyCollection()] [object[]] $Registered)
    $locked = @($Registered | Where-Object { $_.Locked })
    if ($locked.Count -eq 0) { return 'unlocked' }
    if ($locked[0].LockReason) { return 'locked: ' + $locked[0].LockReason }
    return 'locked without a reason'
}

function Get-ReleaseWorktreeProblem {
    <#
    Why Install-RealServer.ps1 can't check a release out at Path, or $null. It never deletes or reuses what it didn't
    create, so the path must be free: nothing there, and no worktree of the repository at RepoRoot registered there (one
    whose folder is gone stays registered until git worktree prune; a locked one, another run's while that runs, until it
    is unlocked). Nor may it be under C:\ProgramData\Jellyfin.
    #>
    param([Parameter(Mandatory = $true)] [string] $Path, [Parameter(Mandatory = $true)] [string] $RepoRoot)
    $full = ConvertFrom-DevicePath (Resolve-FullPath $Path)
    if (Test-ProtectedPath $full) { return "The temporary worktree's folder $full is under C:\ProgramData\Jellyfin, the real server's folder." }
    $registered = @(Get-GitWorktrees -RepoRoot $RepoRoot | Where-Object { Test-SamePath $_.Path $full })
    if ($registered.Count -gt 0) {
        if (@($registered | Where-Object { $_.Locked }).Count -gt 0) {
            # Another run's while it runs (New-ReleaseWorktree locks them), or one a run that was stopped left.
            $lock = Get-WorktreeLockText -Registered $registered
            $unlock = "git -C `"$RepoRoot`" worktree unlock `"$full`""
            if (-not (Test-Path -LiteralPath $full)) {
                return "The temporary worktree's folder $full is already taken: git has a worktree registered there whose folder is gone ($lock). If no install is running, clear it first: $unlock, then git -C `"$RepoRoot`" worktree prune."
            }

            return ("The temporary worktree's folder $full is already taken: git has a worktree registered there ($lock), another install's while it runs. If none is running, remove it first: " +
                "$unlock, git -C `"$RepoRoot`" worktree remove --force `"$full`", then git -C `"$RepoRoot`" worktree prune.")
        }

        if (-not (Test-Path -LiteralPath $full)) {
            return "The temporary worktree's folder $full is already taken: git has a worktree registered there whose folder is gone. Clear it first: git -C `"$RepoRoot`" worktree prune."
        }

        return ("The temporary worktree's folder $full is already taken: git has a worktree registered there. If an earlier run left it, remove it first: " +
            "git -C `"$RepoRoot`" worktree remove --force `"$full`", then git -C `"$RepoRoot`" worktree prune.")
    }

    if (Test-Path -LiteralPath $full) {
        return "The temporary worktree's folder $full is already taken: something is there. Move it away (or delete it, if an earlier run left it); this script never deletes what it didn't create."
    }

    return $null
}

function New-ReleaseWorktree {
    <#
    Checks Commit out at Path as a detached worktree of the repository at RepoRoot, locked with LockReason in the same step
    (git worktree add --lock --reason <LockReason> --detach), and checks its HEAD and its lock. The lock makes it this run's:
    Deploy-Plugin.ps1 -RealServer installs only a worktree locked with its run's reason, and
    Remove-ReleaseWorktree removes only such a one. Throws git's message when that fails; a worktree git made all the same
    with this lock is Remove-ReleaseWorktree's to clear, and anything else at Path is left alone.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $RepoRoot,
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $Commit,
        [Parameter(Mandatory = $true)] [string] $LockReason
    )
    $null = Assert-Git -Repo $RepoRoot -Arguments @('worktree', 'add', '--lock', '--reason', $LockReason, '--detach', $Path, $Commit) -What "Checking $Commit out into $Path"
    $head = ([string]@(Assert-Git -Repo $Path -Arguments @('rev-parse', 'HEAD') -What "Reading the HEAD of $Path")[0]).Trim()
    if ($head -ne $Commit) { throw "The worktree $Path is at $head, not at $Commit." }
    $mine = @(Get-GitWorktrees -RepoRoot $RepoRoot | Where-Object { (Test-SamePath $_.Path $Path) -and $_.Locked -and $_.LockReason -ceq $LockReason })
    if ($mine.Count -ne 1) { throw "The worktree $Path isn't locked as this run's ($LockReason)." }
}

function Remove-ReleaseWorktree {
    <#
    Removes the worktree at Path that this run of Install-RealServer.ps1 made, and nothing else: only a
    worktree registered there and locked with exactly LockReason (New-ReleaseWorktree's) is unlocked, removed (git worktree
    remove --force, up to Attempts tries a second apart, while something still holds a file in it) and pruned (git worktree
    prune). Another run's worktree there, an unlocked one, or a folder no worktree is registered for is left as it is. Never
    throws: returns Status (Removed; None, nothing there; NotOurs, what is there isn't this run's and was left; Left, this
    run's but not all of it could be removed), Removed (nothing of this run's is left), Problem and Path.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $RepoRoot,
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $LockReason,
        [int] $Attempts = 3
    )
    $full = ConvertFrom-DevicePath (Resolve-FullPath $Path)
    try {
        $registered = @(Get-GitWorktrees -RepoRoot $RepoRoot | Where-Object { Test-SamePath $_.Path $full })
    }
    catch {
        return [pscustomobject]@{ Status = 'Left'; Removed = $false; Problem = "git couldn't list the worktrees ($($_.Exception.Message))"; Path = $full }
    }

    if ($registered.Count -eq 0) {
        if (Test-Path -LiteralPath $full) {
            return [pscustomobject]@{ Status = 'NotOurs'; Removed = $true; Problem = 'something is there that no worktree is registered for, so not this run''s'; Path = $full }
        }

        return [pscustomobject]@{ Status = 'None'; Removed = $true; Problem = $null; Path = $full }
    }

    if (@($registered | Where-Object { $_.Locked -and $_.LockReason -ceq $LockReason }).Count -eq 0) {
        return [pscustomobject]@{ Status = 'NotOurs'; Removed = $true; Problem = "the worktree registered there isn't this run's ($(Get-WorktreeLockText -Registered $registered))"; Path = $full }
    }

    $messages = New-Object System.Collections.Generic.List[string]
    $stillRegistered = $true
    try {
        $unlock = Invoke-Git -Repo $RepoRoot -Arguments @('worktree', 'unlock', $full)
        if ($unlock.ExitCode -ne 0 -and $unlock.Error) { $messages.Add($unlock.Error) }
        for ($attempt = 1; $attempt -le [Math]::Max(1, $Attempts); $attempt++) {
            if (@(Get-GitWorktrees -RepoRoot $RepoRoot | Where-Object { Test-SamePath $_.Path $full }).Count -eq 0) { break }
            $run = Invoke-Git -Repo $RepoRoot -Arguments @('worktree', 'remove', '--force', $full)
            if ($run.ExitCode -eq 0) { break }
            if ($run.Error) { $messages.Add($run.Error) }
            if ($attempt -lt $Attempts) { Start-Sleep -Seconds 1 }
        }

        $prune = Invoke-Git -Repo $RepoRoot -Arguments @('worktree', 'prune')
        if ($prune.ExitCode -ne 0 -and $prune.Error) { $messages.Add($prune.Error) }
        $stillRegistered = @(Get-GitWorktrees -RepoRoot $RepoRoot | Where-Object { Test-SamePath $_.Path $full }).Count -gt 0
    }
    catch {
        $messages.Add($_.Exception.Message)
    }

    $stillThere = Test-Path -LiteralPath $full
    $problem = $null
    if ($stillRegistered -or $stillThere) {
        $left = @()
        if ($stillThere) { $left += 'its folder is still there' }
        if ($stillRegistered) { $left += 'git still has it registered' }
        $problem = $left -join ' and '
        if ($messages.Count -gt 0) { $problem += ' (' + ((@($messages | Select-Object -Unique) -join '; ') -replace '\s*\r?\n\s*', ' ') + ')' }
    }

    $removed = (-not $stillRegistered -and -not $stillThere)
    $status = 'Left'
    if ($removed) { $status = 'Removed' }
    return [pscustomobject]@{ Status = $status; Removed = $removed; Problem = $problem; Path = $full }
}

function Assert-PluginReleaseCheckout {
    <#
    Deploy-Plugin.ps1 -RealServer: the checkout at Path is installed as a release only when it is one, exactly as it was
    tagged, and the one this run made:
    - the top of a worktree of the repository at RepoRoot;
    - nothing in it changed, added or deleted, and nothing ignored in it either, before anything is built there (git status
      --porcelain --ignored=matching --untracked-files=all empty): a build reads ignored files too, such as a *.csproj.user,
      which MSBuild imports into its project;
    - no file git is told not to look at (git ls-files -v: lower case is assume-unchanged, S skip-worktree), whose changes
      git status doesn't show;
    - its HEAD the commit of the plugin's release tag for the version its plugins/<Plugin>/build.yaml says (x.y.z.0 is the
      tag <plugin>/v<x.y.z>, checked as Resolve-PluginRelease checks it);
    - registered as a worktree locked with exactly LockReason, the reason Install-RealServer.ps1 gave the worktree it made of
      the tag for this run (New-ReleaseLockReason, New-ReleaseWorktree).
    Returns the release; throws why not.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $Plugin,
        [Parameter(Mandatory = $true)] [string] $RepoRoot,
        [Parameter(Mandatory = $true)] [string] $LockReason
    )
    $full = ConvertFrom-DevicePath (Resolve-FullPath $Path)
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw "$full is not a folder." }
    $top = Invoke-Git -Repo $full -Arguments @('rev-parse', '--show-toplevel')
    if ($top.ExitCode -ne 0 -or @($top.Output).Count -eq 0 -or -not (Test-SamePath ([string]@($top.Output)[0]).Trim() $full)) {
        throw "$full is not the top of a git checkout."
    }

    $common = ConvertFrom-DevicePath (Resolve-FullPath ([string]@(Assert-Git -Repo $full -Arguments @('rev-parse', '--path-format=absolute', '--git-common-dir') -What "Reading the repository of $full")[0]).Trim())
    $repoCommon = ConvertFrom-DevicePath (Resolve-FullPath ([string]@(Assert-Git -Repo $RepoRoot -Arguments @('rev-parse', '--path-format=absolute', '--git-common-dir') -What "Reading the repository of $RepoRoot")[0]).Trim())
    if (-not (Test-SamePath $common $repoCommon)) { throw "$full is a checkout of another repository ($common), not of the one at $RepoRoot ($repoCommon)." }

    $status = @(Assert-Git -Repo $full -Arguments @('status', '--porcelain', '--ignored=matching', '--untracked-files=all') -What "Reading the status of $full" | Where-Object { $_ })
    $changes = @($status | Where-Object { -not $_.StartsWith('!! ') })
    if ($changes.Count -gt 0) {
        throw "$full has $($changes.Count) change(s) ($(@($changes | Select-Object -First 5) -join '; ')), and a release is installed exactly as it was tagged."
    }

    $ignored = @($status | Where-Object { $_.StartsWith('!! ') })
    if ($ignored.Count -gt 0) {
        throw ("$full has $($ignored.Count) ignored file(s) or folder(s) ($(@($ignored | Select-Object -First 5) -join '; ')), and a build reads those too (MSBuild imports " +
            'an ignored *.csproj.user into its project): a release is built from a checkout of its tag that nothing was added to.')
    }

    $hidden = @(Assert-Git -Repo $full -Arguments @('ls-files', '-v') -What "Listing the files of $full" | Where-Object { $_ -cmatch '^([a-z]|S) ' })
    if ($hidden.Count -gt 0) {
        throw ("$full has $($hidden.Count) file(s) git is told not to look at ($(@($hidden | Select-Object -First 5) -join '; '); in git ls-files -v lower case is assume-unchanged " +
            'and S skip-worktree), so git status can''t vouch for them, and a release is installed exactly as it was tagged.')
    }

    $head = ([string]@(Assert-Git -Repo $full -Arguments @('rev-parse', 'HEAD') -What "Reading the HEAD of $full")[0]).Trim()
    $info = Get-PluginBuildInfo -Plugin $Plugin -RepoRoot $full
    $match = [regex]::Match($info.Version, '^([0-9]{1,9})\.([0-9]{1,9})\.([0-9]{1,9})\.0$')
    if (-not $match.Success) {
        throw "$full's plugins/$($info.Folder)/build.yaml says version $($info.Version), which no release tag names (a release's version is <major>.<minor>.<patch>.0)."
    }

    $version = '{0}.{1}.{2}' -f @([int]$match.Groups[1].Value, [int]$match.Groups[2].Value, [int]$match.Groups[3].Value)
    $release = Resolve-PluginRelease -Plugin $info.Folder -RepoRoot $RepoRoot -Version $version
    if ($release.Commit -ne $head) { throw "$full is at commit $head, but $($release.Tag) is commit $($release.Commit): it isn't that release's checkout." }

    $registered = @(Get-GitWorktrees -RepoRoot $RepoRoot | Where-Object { Test-SamePath $_.Path $full })
    if (@($registered | Where-Object { $_.Locked -and $_.LockReason -ceq $LockReason }).Count -eq 0) {
        $what = 'git lists no worktree there'
        if ($registered.Count -gt 0) { $what = 'git has it ' + (Get-WorktreeLockText -Registered $registered) }
        throw ("$full is $($release.Tag) as tagged, but not the worktree Install-RealServer.ps1 made of it for this run ($what; this run's is locked with the run's own reason): " +
            '-RealServer installs only that one, so run Install-RealServer.ps1.')
    }

    return $release
}

function Read-GitBlob {
    <#
    The bytes of the file Path (with '/') at Commit in the repository at Repo, or $null when that commit has no such file
    (or git can't read it). Read from git cat-file's output stream as bytes, never through PowerShell's pipeline, which
    reads a native command's output as text and would corrupt an image. Only reads.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $Repo,
        [Parameter(Mandatory = $true)] [string] $Commit,
        [Parameter(Mandatory = $true)] [string] $Path
    )
    $spec = $Commit + ':' + $Path
    $type = Invoke-Git -Repo $Repo -Arguments @('cat-file', '-t', $spec)
    if ($type.ExitCode -ne 0 -or @($type.Output).Count -eq 0 -or ([string]@($type.Output)[0]).Trim() -ne 'blob') { return $null }

    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = Get-GitPath
    $start.Arguments = (@('-C', $Repo, 'cat-file', 'blob', $spec) | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['GIT_OPTIONAL_LOCKS'] = '0'
    $start.EnvironmentVariables['GIT_TERMINAL_PROMPT'] = '0'
    $process = [System.Diagnostics.Process]::Start($start)
    $buffer = New-Object System.IO.MemoryStream
    try {
        $errorRead = $process.StandardError.ReadToEndAsync()
        $process.StandardOutput.BaseStream.CopyTo($buffer)
        $process.WaitForExit()
        $null = $errorRead.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { return $null }
        return , $buffer.ToArray()
    }
    finally {
        $buffer.Dispose()
        $process.Dispose()
    }
}

function Get-PluginImageName {
    <# The plugin's image: plugins/<Plugin>/image.png, installed as image.png next to its DLLs and named in meta.json's imagePath. #>
    return 'image.png'
}

function Resolve-PluginImage {
    <#
    The image a deploy installs with Plugin (Get-PluginImageName in plugins/<Plugin>), or $null when there is none:
    - with Commit (a release tag's commit, CommitLabel naming it): the file of that commit, read from git;
    - with SourceFolder (a checkout): the file in its plugins/<Plugin> on disk, the working tree's;
    - with FallbackToHead, when the above has none: the file committed at the HEAD of the repository at RepoRoot (the
      checkout the scripts run from), read from git, never from its working tree. Release tags made before the plugins had
      images have none, and reinstalling such a release gets the image all the same.
    Returns Name, From (where it came from, for the messages and the backup's records), Source (Tag, Head or WorkingTree),
    Commit (the commit it was read from; $null for a working tree's file), Bytes (byte[]), Sha256 and Size. Only reads.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $Plugin,
        [Parameter(Mandatory = $true)] [string] $RepoRoot,
        [string] $Commit,
        [string] $CommitLabel,
        [string] $SourceFolder,
        [switch] $FallbackToHead
    )
    $name = Get-PluginImageName
    $relative = "plugins/$Plugin/$name"
    $bytes = $null
    $from = $null
    $source = $null
    $readCommit = $null
    $missing = $null
    if ($Commit) {
        $label = $CommitLabel
        if (-not $label) { $label = "commit $Commit" }
        $bytes = Read-GitBlob -Repo $RepoRoot -Commit $Commit -Path $relative
        if ($null -ne $bytes) { $from = "$label (commit $Commit)"; $source = 'Tag'; $readCommit = $Commit }
        $missing = "$label has no $relative"
    }
    elseif ($SourceFolder) {
        $path = Join-Path (Join-Path (Join-Path $SourceFolder 'plugins') $Plugin) $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $bytes = [System.IO.File]::ReadAllBytes((Resolve-FullPath $path))
            $from = "the working tree of $SourceFolder ($relative)"
            $source = 'WorkingTree'
        }
        $missing = "the working tree of $SourceFolder has no $relative"
    }

    if ($null -eq $bytes -and $FallbackToHead) {
        $head = Invoke-Git -Repo $RepoRoot -Arguments @('rev-parse', '--verify', '--quiet', 'HEAD^{commit}')
        if ($head.ExitCode -eq 0 -and @($head.Output).Count -gt 0) {
            $headCommit = ([string]@($head.Output)[0]).Trim()
            $bytes = Read-GitBlob -Repo $RepoRoot -Commit $headCommit -Path $relative
            if ($null -ne $bytes) {
                $from = "the committed HEAD of $RepoRoot (commit $headCommit), as $missing"
                $source = 'Head'
                $readCommit = $headCommit
            }
        }
    }

    if ($null -eq $bytes) { return $null }
    $sha = New-Object System.Security.Cryptography.SHA256Managed
    try { $hash = (-join @($sha.ComputeHash([byte[]]$bytes) | ForEach-Object { $_.ToString('x2') })) }
    finally { $sha.Dispose() }
    return [pscustomobject]@{
        Name   = $name
        From   = $from
        Source = $source
        Commit = $readCommit
        Bytes  = [byte[]]$bytes
        Sha256 = $hash
        Size   = [long]$bytes.Length
    }
}

function Get-RepoScriptsProblem {
    <#
    Why the PowerShell scripts of the checkout at RepoRoot (scripts/*.ps1, *.psm1 and *.psd1, in subfolders too) aren't
    exactly its HEAD's, or $null. A real-server install builds a release tag, but runs this checkout's
    Install-RealServer.ps1, Deploy-Plugin.ps1 and JellyfinSandbox.psm1 as they are on disk, so they must be the committed
    ones: none changed (staged or not), added or deleted (git status --porcelain --untracked-files=all), none that git is
    told not to look at (git ls-files -v: assume-unchanged or skip-worktree, whose changes git status doesn't show), and
    each of Running (the files the install runs) tracked. The Node probes and webchecks (.mjs, .json) never run in an
    install and aren't looked at. Only reads.
    #>
    param([Parameter(Mandatory = $true)] [string] $RepoRoot, [string[]] $Running = @())
    $root = ConvertFrom-DevicePath (Resolve-FullPath $RepoRoot)
    $specs = @('scripts/*.ps1', 'scripts/*.psm1', 'scripts/*.psd1')
    $status = Invoke-Git -Repo $root -Arguments (@('status', '--porcelain', '--untracked-files=all', '--') + $specs)
    if ($status.ExitCode -ne 0) {
        return "git status can't read the scripts of $root ($($status.Error -replace '\s*\r?\n\s*', ' ')), so this install can't tell that they are the committed ones."
    }

    $problems = New-Object System.Collections.Generic.List[string]
    $changes = @($status.Output | Where-Object { $_ })
    if ($changes.Count -gt 0) { $problems.Add("git status lists $($changes.Count) change(s) to them ($(@($changes | Select-Object -First 5) -join '; '))") }
    $hidden = @((Invoke-Git -Repo $root -Arguments (@('ls-files', '-v', '--') + $specs)).Output | Where-Object { $_ -cmatch '^([a-z]|S) ' })
    if ($hidden.Count -gt 0) {
        $problems.Add("git is told not to look at $($hidden.Count) of them ($(@($hidden | Select-Object -First 5) -join '; '); lower case is assume-unchanged, S skip-worktree)")
    }

    $untracked = New-Object System.Collections.Generic.List[string]
    foreach ($file in $Running) {
        $full = ConvertFrom-DevicePath (Resolve-FullPath $file)
        if (-not (Test-PathUnder $full $root)) { $untracked.Add($full); continue }
        $relative = $full.Substring($root.Length).TrimStart('\').Replace('\', '/')
        $listed = Invoke-Git -Repo $root -Arguments @('ls-files', '--', $relative)
        if ($listed.ExitCode -ne 0 -or @($listed.Output | Where-Object { $_ }).Count -eq 0) { $untracked.Add($relative) }
    }

    if ($untracked.Count -gt 0) { $problems.Add("$($untracked.Count) of the files it runs aren't committed at all ($($untracked -join ', '))") }
    if ($problems.Count -eq 0) { return $null }
    return ("The scripts this install runs aren't the committed ones of ${root}: Install-RealServer.ps1, Deploy-Plugin.ps1 and JellyfinSandbox.psm1 run as they are " +
        "in its scripts folder, whichever tag is built, and $($problems -join '; '). Commit those changes or put them aside first, or run the install from a checkout " +
        "whose scripts are committed, such as a new worktree: git -C `"$root`" worktree add --detach <folder> main, then <folder>\scripts\Install-RealServer.ps1.")
}

function Test-RealServerTarget {
    <#
    Whether a data folder is the real server's, for Install-RealServer.ps1: C:\ProgramData\Jellyfin\Server or anything
    else under C:\ProgramData\Jellyfin in every spelling Test-ProtectedPath knows (8.3 names, '..', \\?\ and \\.\ prefixes, a
    junction, symbolic link, subst or mapped drive on the way, a PowerShell drive), and every share or device path and drive
    letter Windows doesn't know, which it can't tell apart from it. Only a release tag's build, run by committed scripts,
    goes onto the real server; the working tree is for rehearsals. A script passes DataDir resolved with its own drives
    first (ConvertTo-FileSystemPath -SessionState $ExecutionContext.SessionState).
    #>
    param([Parameter(Mandatory = $true)] [string] $DataDir)
    return [bool](Test-ProtectedPath $DataDir)
}

# ------------------------------------------------------------------------------------------------ HTTP

function Assert-SandboxUrl {
    <# Only loopback URLs, and never the real server's port. #>
    param([Parameter(Mandatory = $true)] [string] $Url)
    $uri = [Uri]$Url
    if ($uri.Host -notin @('127.0.0.1', 'localhost', '[::1]', '::1')) { throw "Refusing to call ${Url}: sandbox requests go to loopback only." }
    if ($uri.Port -eq $script:ProductionPort) { throw "Refusing to call ${Url}: port $($script:ProductionPort) is the real server." }
}

function Get-AuthorizationHeader {
    param([string] $Token, [string] $DeviceId = 'jellyfin-plugins-scripts')
    $value = 'MediaBrowser Client="jellyfin-plugins-scripts", Device="sandbox-scripts", DeviceId="' + $DeviceId + '", Version="1.0.0"'
    if ($Token) { $value += ', Token="' + $Token + '"' }
    return $value
}

function Invoke-SandboxApi {
    <# Calls a sandbox Jellyfin API and returns the parsed JSON; arrays come out as items (wrap calls in @()). #>
    param(
        [Parameter(Mandatory = $true)] [string] $BaseUrl,
        [Parameter(Mandatory = $true)] [string] $Path,
        [ValidateSet('GET', 'POST', 'DELETE')] [string] $Method = 'GET',
        $Body,
        [string] $Token,
        [string] $DeviceId = 'jellyfin-plugins-scripts',
        [int] $TimeoutSec = 60
    )
    $url = $BaseUrl.TrimEnd('/') + '/' + $Path.TrimStart('/')
    Assert-SandboxUrl $url
    $parameters = @{
        Uri         = $url
        Method      = $Method
        Headers     = @{ Authorization = (Get-AuthorizationHeader -Token $Token -DeviceId $DeviceId) }
        TimeoutSec  = $TimeoutSec
        ErrorAction = 'Stop'
    }
    if ($PSBoundParameters.ContainsKey('Body')) {
        $json = if ($Body -is [string]) { $Body } else { ConvertTo-Json -InputObject $Body -Depth 20 -Compress }
        $parameters.Body = [System.Text.Encoding]::UTF8.GetBytes($json)
        $parameters.ContentType = 'application/json; charset=utf-8'
    }

    try {
        # Windows PowerShell 5.1 emits a JSON array as one object; ForEach-Object unrolls it into items.
        Invoke-RestMethod @parameters | ForEach-Object { $_ }
    }
    catch {
        $status = ''
        if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) { $status = ' (HTTP ' + [int]$_.Exception.Response.StatusCode + ')' }
        throw "$Method $url failed$status`: $($_.Exception.Message)"
    }
}

function Read-SharedText {
    <# Reads a file another process is writing (the server log). #>
    param([Parameter(Mandatory = $true)] [string] $Path)
    $stream = New-Object System.IO.FileStream((Resolve-FullPath $Path), [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read,
        ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
    try {
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
        return $reader.ReadToEnd()
    }
    finally {
        $stream.Dispose()
    }
}

function Get-StartupCompleteCount {
    <# How many "Startup complete" lines the server logs in LogDir hold. #>
    param([Parameter(Mandatory = $true)] [string] $LogDir)
    if (-not (Test-Path -LiteralPath $LogDir)) { return 0 }
    $count = 0
    foreach ($file in @(Get-ChildItem -LiteralPath $LogDir -Filter 'log_*.log' -File)) {
        $count += ([regex]::Matches((Read-SharedText $file.FullName), 'Startup complete')).Count
    }

    return $count
}

function Get-StartupCompleteCounts {
    <# How many "Startup complete" lines each server log in LogDir holds, by file name. #>
    param([Parameter(Mandatory = $true)] [string] $LogDir)
    $counts = @{}
    if (-not (Test-Path -LiteralPath $LogDir)) { return $counts }
    foreach ($file in @(Get-ChildItem -LiteralPath $LogDir -Filter 'log_*.log' -File)) {
        $counts[$file.Name] = ([regex]::Matches((Read-SharedText $file.FullName), 'Startup complete')).Count
    }

    return $counts
}

function Test-NewStartupComplete {
    <#
    Whether a server log in LogDir holds more "Startup complete" lines than it did in Before (Get-StartupCompleteCounts;
    a log that wasn't there counts from 0). Each file is compared with itself, so an old log that Jellyfin deletes at the
    start (it keeps three, retainedFileCountLimit, and a new day's log makes it drop the oldest) can't hide the new line,
    as it did in a total count (the JE 12.12 re-check: the je sandbox's first start in nine days timed out, its server up).
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $LogDir,
        [Parameter(Mandatory = $true)] [hashtable] $Before
    )
    $now = Get-StartupCompleteCounts -LogDir $LogDir
    foreach ($name in $now.Keys) {
        $was = 0
        if ($Before.ContainsKey($name)) { $was = [int]$Before[$name] }
        if ([int]$now[$name] -gt $was) { return $true }
    }

    return $false
}

function Wait-SandboxReady {
    <#
    Waits until the real server (not Jellyfin 10.11's temporary setup server, which answers /System/Info/Public
    during migrations) is up: /System/Ping answers and, with LogDir, one more "Startup complete" line has been logged:
    in one of the logs compared with StartupCountsBefore (Get-StartupCompleteCounts, file by file, so a log Jellyfin
    deletes at the start doesn't hide it), else than StartupCountBefore in all of them. Returns /System/Info/Public.
    Throws if the process exits first.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $BaseUrl,
        [int] $ProcessId = 0,
        [int] $TimeoutSec = 240,
        [string] $LogDir,
        [int] $StartupCountBefore = 0,
        [hashtable] $StartupCountsBefore
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ($ProcessId -and -not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
            throw "The sandbox server (PID $ProcessId) exited during startup."
        }

        $up = $false
        try {
            Invoke-SandboxApi -BaseUrl $BaseUrl -Path 'System/Ping' -TimeoutSec 5 | Out-Null
            $up = $true
        }
        catch {
            $up = $false
        }

        $started = -not $LogDir
        if ($up -and -not $started) {
            if ($null -ne $StartupCountsBefore) { $started = Test-NewStartupComplete -LogDir $LogDir -Before $StartupCountsBefore }
            else { $started = (Get-StartupCompleteCount -LogDir $LogDir) -gt $StartupCountBefore }
        }

        if ($up -and $started) {
            return Invoke-SandboxApi -BaseUrl $BaseUrl -Path 'System/Info/Public' -TimeoutSec 10
        }

        Start-Sleep -Milliseconds 1000
    }

    throw "The sandbox server at $BaseUrl did not finish starting within $TimeoutSec s."
}

# ------------------------------------------------------------------------------------------ Bookmarks' clip paths

function Get-BookmarksStoreClipPaths {
    <#
    The clip paths a parsed bookmarks.json names, whatever its schema (1.0's, 1.1's or a newer one): every ClipRoots
    entry, every string property of a Clip object whose name ends in Path (FilePath), and the Path of every
    PendingFileDeletes entry. Bookmarks writes clips into, and deletes files from, these.
    A media file's path (Identity.Path) is not a clip path and is left out. One object (Field, Path) per path.
    #>
    param([AllowNull()] $Document)
    $found = New-Object System.Collections.Generic.List[object]
    $pending = New-Object System.Collections.Generic.Queue[object]
    if ($null -ne $Document) { $pending.Enqueue($Document) }
    while ($pending.Count -gt 0) {
        $node = $pending.Dequeue()
        if ($node -is [System.Array]) {
            foreach ($item in $node) {
                if ($item -is [System.Array] -or $item -is [System.Management.Automation.PSCustomObject]) { $pending.Enqueue($item) }
            }

            continue
        }

        if ($node -isnot [System.Management.Automation.PSCustomObject]) { continue }
        foreach ($property in $node.PSObject.Properties) {
            $value = $property.Value
            if ($null -eq $value) { continue }
            if ($property.Name -eq 'ClipRoots') {
                foreach ($root in @($value)) {
                    if ($root -is [string]) { $found.Add([pscustomobject]@{ Field = 'ClipRoots'; Path = $root }) }
                }
            }
            elseif ($property.Name -eq 'PendingFileDeletes') {
                foreach ($entry in @($value)) {
                    if ($entry -is [System.Management.Automation.PSCustomObject] -and $entry.PSObject.Properties['Path'] -and $entry.Path -is [string]) {
                        $found.Add([pscustomobject]@{ Field = 'PendingFileDeletes.Path'; Path = $entry.Path })
                    }
                }
            }
            elseif ($property.Name -eq 'Clip' -and $value -is [System.Management.Automation.PSCustomObject]) {
                foreach ($clipProperty in $value.PSObject.Properties) {
                    if ($clipProperty.Name -match 'Path$' -and $clipProperty.Value -is [string]) {
                        $found.Add([pscustomobject]@{ Field = 'Clip.' + $clipProperty.Name; Path = $clipProperty.Value })
                    }
                }
            }

            if ($value -is [System.Array] -or $value -is [System.Management.Automation.PSCustomObject]) { $pending.Enqueue($value) }
        }
    }

    return $found.ToArray()
}

function Get-JsonTextDrivePaths {
    <#
    Every string of JSON text, whole or cut short, that is a drive path (C:\x) or a device path (\\?\C:\x), for a file
    ConvertFrom-Json refuses (cut short, or keys that differ only in case, which Bookmarks' reader still takes).
    #>
    param([Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $Text)
    $paths = New-Object System.Collections.Generic.List[string]
    foreach ($match in [regex]::Matches($Text, '"((?:[^"\\]|\\.)*)"')) {
        try { $value = [regex]::Unescape($match.Groups[1].Value) } catch { continue }
        if ($value -match '^([A-Za-z]:[\\/]|[\\/]{2}[?.][\\/])') { $paths.Add($value) }
    }

    return $paths.ToArray()
}

function Get-SandboxClipPathProblem {
    <#
    Why a sandbox's Bookmarks may not use this clip path, or $null: not a path, not on a local drive, under
    C:\ProgramData\Jellyfin in any spelling (Test-ProtectedPath), or outside every folder of Inside. A relative path
    resolves against WorkingDir, the server's working directory, as Bookmarks resolves it.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $WorkingDir,
        [Parameter(Mandatory = $true)] [string[]] $Inside
    )
    $value = $Path.Trim()
    try {
        if (-not [System.IO.Path]::IsPathRooted($value)) { $value = [System.IO.Path]::Combine($WorkingDir, $value) }
        $full = Resolve-FullPath $value
    }
    catch {
        return "can't be read as a path"
    }

    if (-not (Test-LocalDrivePath $full)) { return 'is not on a local drive' }
    $full = ConvertFrom-DevicePath $full
    if (Test-ProtectedPath $full) { return 'is under C:\ProgramData\Jellyfin, the real server''s folder' }
    foreach ($folder in $Inside) {
        if (Test-PathUnder $full $folder) { return $null }
    }

    return 'is outside the sandbox'
}

function Test-SandboxClipPaths {
    <#
    The clip paths a sandbox's Bookmarks would use, checked by dev-server.ps1 before the sandbox starts.
    Bookmarks writes clips into, and deletes files from, the folders its data names, and it
    deletes stale .partial files in every recorded clip folder at startup, unattended. A store copied from elsewhere
    (a real server's bookmarks.json used as a --fixture, a unit-test fixture) would point that at another server's
    folders.

    Reads every bookmarks.json* in <DataDir>\data\bookmarks (the store, its .bak, the .v1-* copies, a .tmp) and
    ClipOutputPath in <DataDir>\plugins\configurations\Jellyfin.Plugin.Bookmarks.xml. Each clip path (ClipRoots, a
    clip's FilePath, PendingFileDeletes, ClipOutputPath) must be on a local drive, outside C:\ProgramData\Jellyfin and
    inside Root or DataDir; a relative one resolves against Root, the server's working directory. In a file
    ConvertFrom-Json refuses, no drive path may be under C:\ProgramData\Jellyfin. Returns one line per problem (file,
    field, path, why); none means the sandbox may start.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $Root,
        [Parameter(Mandatory = $true)] [string] $DataDir
    )
    $rootFull = Resolve-FullPath $Root
    $inside = @($rootFull, (Resolve-FullPath $DataDir))
    $problems = New-Object System.Collections.Generic.List[string]
    $seen = @{}
    $verdicts = @{}
    $candidates = New-Object System.Collections.Generic.List[object]

    $storeFolder = Join-Path (Join-Path $DataDir 'data') 'bookmarks'
    if (Test-Path -LiteralPath $storeFolder -PathType Container) {
        foreach ($file in @(Get-ChildItem -LiteralPath $storeFolder -File -Force | Where-Object { $_.Name -like 'bookmarks.json*' } | Sort-Object Name)) {
            $text = Read-SharedText $file.FullName
            if (-not $text.Trim()) { continue }
            $document = $null
            $parsed = $true
            try { $document = $text | ConvertFrom-Json } catch { $parsed = $false }
            if ($parsed) {
                foreach ($entry in @(Get-BookmarksStoreClipPaths $document)) {
                    $candidates.Add([pscustomobject]@{ File = $file.Name; Field = $entry.Field; Path = $entry.Path; ProtectedOnly = $false })
                }
            }
            else {
                foreach ($path in @(Get-JsonTextDrivePaths $text)) {
                    $candidates.Add([pscustomobject]@{ File = $file.Name + ' (not JSON this check reads)'; Field = 'a string'; Path = $path; ProtectedOnly = $true })
                }
            }
        }
    }

    $settings = Join-Path $DataDir 'plugins\configurations\Jellyfin.Plugin.Bookmarks.xml'
    if (Test-Path -LiteralPath $settings -PathType Leaf) {
        $text = Read-SharedText $settings
        $values = @()
        try {
            $xml = [xml]$text
            $values = @($xml.SelectNodes('//*[local-name()="ClipOutputPath"]') | ForEach-Object { $_.InnerText })
        }
        catch {
            $values = @([regex]::Matches($text, '<ClipOutputPath>([^<]*)</ClipOutputPath>') | ForEach-Object { [System.Net.WebUtility]::HtmlDecode($_.Groups[1].Value) })
        }

        foreach ($value in $values) {
            $candidates.Add([pscustomobject]@{ File = 'Jellyfin.Plugin.Bookmarks.xml'; Field = 'ClipOutputPath'; Path = [string]$value; ProtectedOnly = $false })
        }
    }

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate.Path)) { continue }
        $key = $candidate.File + '|' + $candidate.Field + '|' + $candidate.Path.ToUpperInvariant()
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true

        # The same path in the store, its .bak and its copies is looked at once.
        $pathKey = $candidate.Path.Trim().ToUpperInvariant()
        if (-not $verdicts.ContainsKey($pathKey)) {
            $verdicts[$pathKey] = [string](Get-SandboxClipPathProblem -Path $candidate.Path -WorkingDir $rootFull -Inside $inside)
        }

        $why = $verdicts[$pathKey]
        if (-not $why) { continue }
        if ($candidate.ProtectedOnly -and $why -notlike 'is under C:\ProgramData\Jellyfin*') { continue }
        $problems.Add("$($candidate.File): $($candidate.Field) $($candidate.Path) $why")
    }

    return $problems.ToArray()
}

function New-SandboxPassword {
    <# A random 24-character password for sandbox-only users. #>
    $bytes = New-Object byte[] 18
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return ([Convert]::ToBase64String($bytes)).Replace('+', 'A').Replace('/', 'b').Replace('=', '')
}

Export-ModuleMember -Function *
