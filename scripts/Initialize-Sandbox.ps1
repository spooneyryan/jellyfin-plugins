<#
.SYNOPSIS
Seeds a sandbox: setup wizard, users, libraries and a first scan.

.DESCRIPTION
Starts the sandbox with dev-server.ps1 -Background if it isn't running (using the settings recorded in
.sandbox\<Name>\sandbox.json, or the standard sandbox table), generates the test media if it is missing, then:
- completes the startup wizard through /Startup/* (admin user, remote access off);
- clears the plugin repositories, so the sandbox never downloads or updates plugins;
- creates the users admin, viewer (no download permission) and viewer2 with generated passwords;
- saves ids, passwords and access tokens in .sandbox\<Name>\credentials.json (gitignored; never printed);
- creates the libraries Movies, Movies 2, Shows and Home Videos over .sandbox\media with metadata and image
  fetchers, trickplay and chapter images off ('default' adds Music, for User Ratings' lifecycle checks; the 'scale'
  sandbox adds Synthetic Movies and Synthetic Shows; every other one adds Library Maintenance's LM Duplicates and
  LM Duplicates 2);
- starts a library scan and waits for it to finish;
- asserts that the scheduled tasks that would reach the internet on their own (Jellyfin's "Update Plugins", and
  Jellyfin Enhanced's "Refresh CDN Assets" and "Send Anonymous Usage Report" when the sandbox has it) have no triggers
  and haven't run since the server started, and that Jellyfin Enhanced's CDN cache got no file since then
  (dev-server.ps1 writes their empty trigger lists; JellyfinSandbox.psm1's Test-SandboxTasksOff decides);
- checks that GET /api-docs/openapi.json answers 200 (scripts/api-docs-probe.mjs).
Re-running it is safe: existing users and libraries are kept, tokens are refreshed.

For 'je' it also asserts the pinned settings fixture (kit\testdata\je-sandbox): it reads Jellyfin Enhanced's,
Plugin Pages' and Custom Tabs' settings back through the running sandbox and fails if any value differs from
expected.json, and checks that Jellyfin Enhanced, Custom Tabs and JavaScript Injector are Active. The fixture itself
is written before the first start by dev-server.ps1, which calls this script with -PreStart.

.PARAMETER PreStart
Only write the je settings fixture into the data folder. The server must never have started on it.

.PARAMETER AssertJeFixture
Also assert the je settings fixture for a sandbox with another name that was given the fixture with -PreStart
(sandboxes of other names made for one-off checks). 'je' is always checked.

.PARAMETER SkipApiDocs
Skip the /api-docs/openapi.json check (it needs Node.js and takes about 10-12 s after a start).

.PARAMETER PlayerTracks
Also create the library Player Tracks over .sandbox\media\Player Tracks (New-SandboxMedia.ps1's movie with two audio
and two subtitle tracks, for the player's track sheets). Only when asked: one
more movie library would change the counts the other plugins' checks expect of the standard libraries. Re-running with
it on a seeded sandbox adds just that library and scans.

.EXAMPLE
./scripts/Initialize-Sandbox.ps1 -Name default
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Name,

    [switch] $PreStart,

    [switch] $AssertJeFixture,

    [string] $DataDir,

    [switch] $NoStart,

    [switch] $SkipScan,

    [switch] $SkipApiDocs,

    [switch] $PlayerTracks,

    [int] $ScanTimeoutSec = 900
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'JellyfinSandbox.psm1') -Force

$repo = Get-RepoRoot
$sandbox = Get-Sandbox -Name $Name
$fixtureRoot = Join-Path $repo 'kit\testdata\je-sandbox'

# ------------------------------------------------------------------------------------------ je fixture

function Write-JeFixture {
    param([Parameter(Mandatory = $true)] [string] $TargetDataDir)
    $TargetDataDir = Assert-SafePath -Path $TargetDataDir -What 'data folder'
    if (Test-Path -LiteralPath (Join-Path $TargetDataDir 'data\jellyfin.db')) {
        throw "The '$Name' sandbox has already started, so the je fixture can no longer be applied before its first start. Recreate it: ./scripts/dev-server.ps1 -Name $Name -Fresh"
    }

    $running = @(Get-JellyfinProcessesForDataDir -DataDir $TargetDataDir)
    if ($running.Count -gt 0) { throw "A jellyfin.exe is using $TargetDataDir; the fixture is written before the first start only." }

    $source = Join-Path $fixtureRoot 'plugins\configurations'
    $target = Join-Path $TargetDataDir 'plugins\configurations'
    foreach ($file in @(Get-ChildItem -LiteralPath $source -Recurse -File)) {
        $relative = $file.FullName.Substring($source.Length + 1)
        $destination = Join-Path $target $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        Write-Host "je fixture: $relative"
    }
}

if ($PreStart) {
    if (-not $DataDir) { $DataDir = $sandbox.DataDir }
    Write-JeFixture -TargetDataDir $DataDir
    return
}

function Assert-JeFixture {
    param([Parameter(Mandatory = $true)] [string] $BaseUrl, [Parameter(Mandatory = $true)] [string] $Token, [string[]] $Community = @())
    $expected = Read-JsonFile (Join-Path $fixtureRoot 'expected.json')
    $problems = New-Object System.Collections.Generic.List[string]

    $plugins = @(Invoke-SandboxApi -BaseUrl $BaseUrl -Path 'Plugins' -Token $Token)
    $required = @($expected.jellyfinEnhanced.pluginId, $expected.customTabs.pluginId)
    # JavaScript Injector: production runs it (another index.html middleware), so the standard je sandbox has it too.
    if ($Community -contains 'JavaScriptInjector') { $required += $expected.javaScriptInjector.pluginId }
    foreach ($id in $required) {
        $plugin = $plugins | Where-Object { ([guid]$_.Id) -eq ([guid]$id) } | Select-Object -First 1
        if (-not $plugin) { $problems.Add("plugin $id is not installed") }
        elseif ($plugin.Status -ne 'Active') { $problems.Add("plugin $($plugin.Name) is $($plugin.Status), not Active") }
    }

    # Jellyfin Enhanced settings and shortcuts.
    $je = Invoke-SandboxApi -BaseUrl $BaseUrl -Path ("Plugins/{0}/Configuration" -f $expected.jellyfinEnhanced.pluginId) -Token $Token
    foreach ($property in $expected.jellyfinEnhanced.settings.PSObject.Properties) {
        if (-not $je.PSObject.Properties[$property.Name]) { $problems.Add("JE setting $($property.Name) is missing"); continue }
        $actual = $je.($property.Name)
        if ($actual -ne $property.Value) { $problems.Add("JE setting $($property.Name) is $actual, expected $($property.Value)") }
    }

    $shortcuts = @()
    if ($je.PSObject.Properties['Shortcuts']) { $shortcuts = @($je.Shortcuts) }
    foreach ($property in $expected.jellyfinEnhanced.shortcuts.PSObject.Properties) {
        $entries = @($shortcuts | Where-Object { $_.Name -eq $property.Name })
        if ($entries.Count -eq 0) { $problems.Add("JE shortcut $($property.Name) is missing"); continue }
        foreach ($entry in $entries) {
            if ($entry.Key -cne $property.Value) { $problems.Add("JE shortcut $($property.Name) is '$($entry.Key)', expected '$($property.Value)'") }
        }
    }

    # Plugin Pages: the anonymous list every client reads.
    $pages = Invoke-SandboxApi -BaseUrl $BaseUrl -Path 'PluginPages/User' -Token $Token
    $items = @()
    if ($pages -and $pages.PSObject.Properties['Items']) { $items = @($pages.Items) }
    foreach ($page in $expected.pluginPages.pages) {
        $found = $items | Where-Object { $_.Id -eq $page.Id } | Select-Object -First 1
        if (-not $found) { $problems.Add("Plugin Pages does not list $($page.Id)"); continue }
        foreach ($field in 'Url', 'DisplayText') {
            if ($found.$field -ne $page.$field) { $problems.Add("Plugin Pages $($page.Id) $field is '$($found.$field)', expected '$($page.$field)'") }
        }
    }

    # Custom Tabs.
    $tabs = Invoke-SandboxApi -BaseUrl $BaseUrl -Path ("Plugins/{0}/Configuration" -f $expected.customTabs.pluginId) -Token $Token
    $actualTabs = @()
    if ($tabs -and $tabs.PSObject.Properties['Tabs']) { $actualTabs = @($tabs.Tabs) }
    foreach ($tab in $expected.customTabs.tabs) {
        $found = $actualTabs | Where-Object { $_.Title -eq $tab.Title } | Select-Object -First 1
        if (-not $found) { $problems.Add("Custom Tabs has no '$($tab.Title)' tab"); continue }
        if ($found.ContentHtml -ne $tab.ContentHtml) { $problems.Add("Custom Tabs '$($tab.Title)' ContentHtml is '$($found.ContentHtml)'") }
    }

    if ($problems.Count -gt 0) {
        throw "The je sandbox does not hold the pinned fixture settings (kit\testdata\je-sandbox):`n - $($problems -join "`n - ")"
    }

    $settingCount = @($expected.jellyfinEnhanced.settings.PSObject.Properties).Count
    $shortcutCount = @($expected.jellyfinEnhanced.shortcuts.PSObject.Properties).Count
    $active = if ($Community -contains 'JavaScriptInjector') { 'Jellyfin Enhanced, Custom Tabs and JavaScript Injector' } else { 'Jellyfin Enhanced and Custom Tabs' }
    Write-Host "je fixture check passed: $active Active; $settingCount JE settings, $shortcutCount shortcuts (BookmarkCurrentTime = B), the Plugin Pages entry and the Custom Tab match."
}

# ------------------------------------------------------------------------------------------ download tasks

function Assert-SandboxTasksOff {
    <#
    The scheduled tasks that would reach the internet on their own (JellyfinSandbox.psm1's task
    table: Jellyfin's "Update Plugins", and with Jellyfin Enhanced its "Refresh CDN Assets" and "Send Anonymous Usage
    Report") are listed with no triggers, are idle and haven't run since the server started, and Jellyfin Enhanced's CDN
    cache got no file since then. The module's Test-SandboxTasksOff decides; this reads the API and fails on any
    problem. dev-server.ps1 writes the empty trigger lists before every start.
    #>
    param([Parameter(Mandatory = $true)] [string] $BaseUrl, [Parameter(Mandatory = $true)] [string] $Token, [string[]] $Community = @(),
        [Parameter(Mandatory = $true)] $Sandbox)
    $startedUtc = $null
    if ($Sandbox.State -and $Sandbox.State.PSObject.Properties['StartedUtc']) { $startedUtc = $Sandbox.State.StartedUtc }
    if (-not $startedUtc) { throw "sandbox.json of '$($Sandbox.Name)' has no StartedUtc; start the sandbox with dev-server.ps1." }

    $tasks = @(Invoke-SandboxApi -BaseUrl $BaseUrl -Path 'ScheduledTasks' -Token $Token)
    $cdnCache = Join-Path $Sandbox.PluginsDir 'configurations\Jellyfin.Plugin.JellyfinEnhanced\cdn-cache'
    $result = Test-SandboxTasksOff -Tasks $tasks -StartedUtc $startedUtc -Community $Community -CdnCacheDir $cdnCache
    if (@($result.Problems).Count -gt 0) {
        throw "Sandbox '$($Sandbox.Name)' could reach the internet on its own:`n - $($result.Problems -join "`n - ")"
    }

    Write-Host "Download tasks check passed: $($result.Report -join '; ')."
}

# ------------------------------------------------------------------------------------------ start

$mediaRoot = Join-Path (Get-SandboxRoot) 'media'
# A media folder from before the Music set gets it added; the existing files are kept.
if (-not (Test-Path -LiteralPath (Join-Path $mediaRoot 'media-set.json')) -or -not (Test-Path -LiteralPath (Join-Path $mediaRoot 'Music\Test Artist A'))) {
    & (Join-Path $PSScriptRoot 'New-SandboxMedia.ps1')
}

if ($Name -eq 'scale' -and -not (Test-Path -LiteralPath (Join-Path $mediaRoot 'Synthetic\seed.json'))) {
    throw "The scale sandbox needs the synthetic seed first: ./scripts/New-SandboxMedia.ps1 -SyntheticMovies 10000 -SyntheticEpisodes 50000"
}

$process = Get-SandboxServerProcess -Sandbox $sandbox
if (-not $process) {
    if ($NoStart) { throw "Sandbox '$Name' is not running; start it with ./scripts/dev-server.ps1 -Name $Name -Background." }
    $startArgs = @{ Name = $Name; Background = $true }
    $state = $sandbox.State
    if ($state) {
        $startArgs.Port = [int]$state.Port
        $startArgs.BaseUrl = [string]$state.BaseUrl
        $startArgs.Plugins = if (@($state.Plugins).Count -gt 0) { [string[]]@($state.Plugins) } else { @('None') }
        $startArgs.Community = if (@($state.Community).Count -gt 0) { [string[]]@($state.Community) } else { @('None') }
        foreach ($key in 'DataDir', 'CacheDir', 'LogDir', 'WebDir') { if ($state.$key) { $startArgs[$key] = [string]$state.$key } }
    }
    elseif (-not (Get-StandardSandbox -Name $Name)) {
        throw "Sandbox '$Name' has never been started; run ./scripts/dev-server.ps1 -Name $Name -Port <port> -Background first."
    }

    & (Join-Path $PSScriptRoot 'dev-server.ps1') @startArgs
    $sandbox = Get-Sandbox -Name $Name
}

$baseUrl = Get-SandboxBaseUrl -Sandbox $sandbox
$info = Invoke-SandboxApi -BaseUrl $baseUrl -Path 'System/Info/Public'

# ------------------------------------------------------------------------------------------ credentials

$credentials = [ordered]@{ Sandbox = $Name; Url = $baseUrl; Users = [ordered]@{} }
if (Test-Path -LiteralPath $sandbox.CredentialsFile) {
    $saved = Read-JsonFile $sandbox.CredentialsFile
    if ($saved.PSObject.Properties['users']) {
        foreach ($property in $saved.users.PSObject.Properties) {
            $credentials.Users[$property.Name] = [ordered]@{
                id       = [string]$property.Value.id
                password = [string]$property.Value.password
                token    = [string]$property.Value.token
            }
        }
    }
}

function Save-Credentials {
    $document = [ordered]@{
        note    = 'Sandbox-only test accounts (gitignored). Never reuse these passwords anywhere else.'
        sandbox = $Name
        url     = $baseUrl
        users   = $credentials.Users
    }
    Write-JsonFile -Path $sandbox.CredentialsFile -Value $document
}

function Get-UserEntry([string] $UserName) {
    if (-not $credentials.Users.Contains($UserName)) {
        $credentials.Users[$UserName] = [ordered]@{ id = ''; password = ''; token = '' }
    }

    return $credentials.Users[$UserName]
}

function Connect-SandboxUser([string] $UserName) {
    $entry = Get-UserEntry $UserName
    $result = Invoke-SandboxApi -BaseUrl $baseUrl -Path 'Users/AuthenticateByName' -Method POST -DeviceId "jf-sandbox-$Name-$UserName" `
        -Body @{ Username = $UserName; Pw = $entry.password }
    $entry.id = [string]$result.User.Id
    $entry.token = [string]$result.AccessToken
    return $entry
}

# ------------------------------------------------------------------------------------------ wizard

$admin = Get-UserEntry 'admin'
if (-not $info.StartupWizardCompleted) {
    if (-not $admin.password) { $admin.password = New-SandboxPassword }
    Save-Credentials
    Invoke-SandboxApi -BaseUrl $baseUrl -Path 'Startup/Configuration' -Method POST -Body @{
        ServerName = "jf-sandbox-$Name"; UICulture = 'en-US'; MetadataCountryCode = 'US'; PreferredMetadataLanguage = 'en'
    } | Out-Null
    Invoke-SandboxApi -BaseUrl $baseUrl -Path 'Startup/User' | Out-Null
    Invoke-SandboxApi -BaseUrl $baseUrl -Path 'Startup/User' -Method POST -Body @{ Name = 'admin'; Password = $admin.password } | Out-Null
    Invoke-SandboxApi -BaseUrl $baseUrl -Path 'Startup/RemoteAccess' -Method POST -Body @{ EnableRemoteAccess = $false; EnableAutomaticPortMapping = $false } | Out-Null
    Invoke-SandboxApi -BaseUrl $baseUrl -Path 'Startup/Complete' -Method POST | Out-Null
    Write-Host 'Completed the startup wizard (user admin).'
}
elseif (-not $admin.password) {
    throw "Sandbox '$Name' has completed its wizard, but $($sandbox.CredentialsFile) has no admin password. Recreate it with dev-server.ps1 -Fresh."
}

$admin = Connect-SandboxUser 'admin'
$token = $admin.token
Save-Credentials

# No plugin repositories: the sandbox never downloads or updates plugins.
Invoke-SandboxApi -BaseUrl $baseUrl -Path 'Repositories' -Method POST -Token $token -Body '[]' | Out-Null

# ------------------------------------------------------------------------------------------ users

$users = @(Invoke-SandboxApi -BaseUrl $baseUrl -Path 'Users' -Token $token)
foreach ($userName in 'viewer', 'viewer2') {
    $entry = Get-UserEntry $userName
    $existing = $users | Where-Object { $_.Name -eq $userName } | Select-Object -First 1
    if (-not $existing) {
        if (-not $entry.password) { $entry.password = New-SandboxPassword }
        Save-Credentials
        $existing = Invoke-SandboxApi -BaseUrl $baseUrl -Path 'Users/New' -Method POST -Token $token -Body @{ Name = $userName; Password = $entry.password }
        Write-Host "Created user $userName."
    }
    elseif (-not $entry.password) {
        $entry.password = New-SandboxPassword
        Save-Credentials
        Invoke-SandboxApi -BaseUrl $baseUrl -Path ("Users/Password?userId={0}" -f $existing.Id) -Method POST -Token $token -Body @{ NewPw = $entry.password } | Out-Null
        Write-Host "Reset the password of $userName (it was not in credentials.json)."
    }

    $user = Invoke-SandboxApi -BaseUrl $baseUrl -Path ("Users/{0}" -f $existing.Id) -Token $token
    $policy = $user.Policy
    $wantDownload = ($userName -ne 'viewer')
    if ($policy.IsAdministrator -or $policy.EnableContentDownloading -ne $wantDownload) {
        $policy.IsAdministrator = $false
        $policy.EnableContentDownloading = $wantDownload
        Invoke-SandboxApi -BaseUrl $baseUrl -Path ("Users/{0}/Policy" -f $existing.Id) -Method POST -Token $token -Body $policy | Out-Null
        Write-Host "Set the policy of ${userName}: downloads $(if ($wantDownload) { 'allowed' } else { 'not allowed' })."
    }

    Connect-SandboxUser $userName | Out-Null
    Save-Credentials
}

# ------------------------------------------------------------------------------------------ libraries

function New-LibraryOptions([string[]] $Types) {
    $typeOptions = @($Types | ForEach-Object {
        @{ Type = $_; MetadataFetchers = @(); MetadataFetcherOrder = @(); ImageFetchers = @(); ImageFetcherOrder = @() }
    })
    return @{
        LibraryOptions = @{
            Enabled                              = $true
            EnableRealtimeMonitor                = $false
            EnableInternetProviders              = $false
            SaveLocalMetadata                    = $false
            EnableChapterImageExtraction         = $false
            ExtractChapterImagesDuringLibraryScan = $false
            EnableTrickplayImageExtraction       = $false
            ExtractTrickplayImagesDuringLibraryScan = $false
            EnableEmbeddedTitles                 = $false
            AutomaticRefreshIntervalDays         = 0
            MetadataSavers                       = @()
            DisabledSubtitleFetchers             = @()
            TypeOptions                          = $typeOptions
        }
    }
}

$libraries = @(
    @{ Name = 'Movies'; Folder = 'Movies'; Type = 'movies'; ItemTypes = @('Movie') }
    @{ Name = 'Movies 2'; Folder = 'Movies 2'; Type = 'movies'; ItemTypes = @('Movie') }
    @{ Name = 'Shows'; Folder = 'Shows'; Type = 'tvshows'; ItemTypes = @('Series', 'Season', 'Episode') }
    @{ Name = 'Home Videos'; Folder = 'Home Videos'; Type = 'homevideos'; ItemTypes = @('Video') }
)
# User Ratings' lifecycle checks: a small music library, in 'default' only.
if ($Name -eq 'default') {
    $libraries += @{ Name = 'Music'; Folder = 'Music'; Type = 'music'; ItemTypes = @('MusicArtist', 'MusicAlbum', 'Audio') }
}
# Library Maintenance's own two: new movie libraries change no other plugin's fixture,
# and 'scale' keeps its numbers (the groups' timings).
if ($Name -ne 'scale') {
    $libraries += @{ Name = 'LM Duplicates'; Folder = 'LM Duplicates'; Type = 'movies'; ItemTypes = @('Movie') }
    $libraries += @{ Name = 'LM Duplicates 2'; Folder = 'LM Duplicates 2'; Type = 'movies'; ItemTypes = @('Movie') }
}
if ($Name -eq 'scale') {
    $libraries += @{ Name = 'Synthetic Movies'; Folder = 'Synthetic'; Type = 'movies'; ItemTypes = @('Movie') }
    $libraries += @{ Name = 'Synthetic Shows'; Folder = 'SyntheticTV'; Type = 'tvshows'; ItemTypes = @('Series', 'Season', 'Episode') }
}
# The player's track sheets' movie, only with -PlayerTracks.
if ($PlayerTracks) {
    $libraries += @{ Name = 'Player Tracks'; Folder = 'Player Tracks'; Type = 'movies'; ItemTypes = @('Movie') }
}

$existingLibraries = @(Invoke-SandboxApi -BaseUrl $baseUrl -Path 'Library/VirtualFolders' -Token $token | ForEach-Object { $_.Name })
foreach ($library in $libraries) {
    if ($existingLibraries -contains $library.Name) { continue }
    $path = Join-Path $mediaRoot $library.Folder
    if (-not (Test-Path -LiteralPath $path)) { throw "Media folder $path is missing; run ./scripts/New-SandboxMedia.ps1." }
    $query = 'Library/VirtualFolders?name={0}&collectionType={1}&paths={2}&refreshLibrary=false' -f `
        [Uri]::EscapeDataString($library.Name), $library.Type, [Uri]::EscapeDataString($path)
    Invoke-SandboxApi -BaseUrl $baseUrl -Path $query -Method POST -Token $token -Body (New-LibraryOptions $library.ItemTypes) | Out-Null
    Write-Host "Created library $($library.Name) over $path."
}

# A Music library an earlier one-off check made over a folder of its own (in some 'default' sandboxes) gets the standard
# Music folder as a further location, which User Ratings' lifecycle probe works on; its own folder stays.
$music = @(Invoke-SandboxApi -BaseUrl $baseUrl -Path 'Library/VirtualFolders' -Token $token) | Where-Object { $_.Name -eq 'Music' } | Select-Object -First 1
$musicPath = Join-Path $mediaRoot 'Music'
if ($Name -eq 'default' -and $music -and -not (@($music.Locations) | Where-Object { $_.TrimEnd('\') -ieq $musicPath })) {
    Invoke-SandboxApi -BaseUrl $baseUrl -Path 'Library/VirtualFolders/Paths?refreshLibrary=false' -Method POST -Token $token -Body @{ Name = 'Music'; PathInfo = @{ Path = $musicPath } } | Out-Null
    Write-Host "Added $musicPath to the library Music."
}

# ------------------------------------------------------------------------------------------ scan

if (-not $SkipScan) {
    $scanStart = (Get-Date).ToUniversalTime()
    Invoke-SandboxApi -BaseUrl $baseUrl -Path 'Library/Refresh' -Method POST -Token $token | Out-Null
    Write-Host 'Library scan started...'
    $deadline = (Get-Date).AddSeconds($ScanTimeoutSec)
    $done = $false
    Start-Sleep -Seconds 2
    while ((Get-Date) -lt $deadline) {
        $task = @(Invoke-SandboxApi -BaseUrl $baseUrl -Path 'ScheduledTasks?isHidden=false' -Token $token) |
            Where-Object { $_.Key -eq 'RefreshLibrary' } | Select-Object -First 1
        if ($task -and $task.State -eq 'Idle' -and $task.LastExecutionResult) {
            $end = ConvertTo-UtcTime $task.LastExecutionResult.EndTimeUtc
            if ($end -ge $scanStart.AddSeconds(-1)) {
                Write-Host "Library scan finished: $($task.LastExecutionResult.Status)."
                $done = $true
                break
            }
        }

        Start-Sleep -Seconds 2
    }

    if (-not $done) { throw "The library scan did not finish within $ScanTimeoutSec s." }
}

$counts = Invoke-SandboxApi -BaseUrl $baseUrl -Path 'Items/Counts' -Token $token
Write-Host ("Items: {0} movies, {1} series, {2} episodes." -f $counts.MovieCount, $counts.SeriesCount, $counts.EpisodeCount)

$community = @()
if ($sandbox.State -and $sandbox.State.PSObject.Properties['Community']) { $community = [string[]]@($sandbox.State.Community | Where-Object { $_ }) }

if ($Name -eq 'je' -or $AssertJeFixture) { Assert-JeFixture -BaseUrl $baseUrl -Token $token -Community $community }

Assert-SandboxTasksOff -BaseUrl $baseUrl -Token $token -Community $community -Sandbox $sandbox

# One schema-id collision among the loaded plugins makes the whole OpenAPI document 500.
if (-not $SkipApiDocs) {
    $node = Get-Command node -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $node) { throw 'Node.js is not on the PATH; install Node 22 or pass -SkipApiDocs.' }
    & $node.Source (Join-Path $PSScriptRoot 'api-docs-probe.mjs') --sandbox $Name --summary
    if ($LASTEXITCODE -ne 0) { throw "GET /api-docs/openapi.json failed in sandbox '$Name' (above)." }
}

Save-Credentials
Write-Host "Sandbox '$Name' is ready at $baseUrl. Users: $(($credentials.Users.Keys) -join ', ') (passwords and tokens in $($sandbox.CredentialsFile))."
