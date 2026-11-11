<#
.SYNOPSIS
Installs every plugin from a local copy of the plugin repository into a fresh sandbox, the way a Jellyfin admin would.

.DESCRIPTION
The release rehearsal of the catalog, end to end, on this machine only:

1. Package-Release.ps1 -BaseUrl http://127.0.0.1:<ServePort> -OutDir <OutDir>: the zips and a test catalog whose
   sourceUrl and imageUrl point at the local server (manifest.json at the repository root is not touched).
2. serve-release.mjs serves <OutDir> on 127.0.0.1:<ServePort> (its log: <OutDir>\serve.log).
3. dev-server.ps1 -Name <Name> -Port <Port> -Plugins None -Community None -Fresh -Background: a new, empty sandbox.
4. The startup wizard (user admin, a generated password kept in .sandbox\<Name>\credentials.json, never printed), then
   the test catalog as the server's only plugin repository (POST /Repositories).
5. Each package must be in GET /Packages with its version; POST /Packages/Installed/<name> installs it from the catalog
   (download, MD5 check, extraction into plugins\<name>_<version>, meta.json), as Dashboard -> Plugins -> Catalog does.
6. POST /System/Restart (Jellyfin restarts in its own process), then each plugin must be in GET /Plugins with its
   version, status Active and an image, GET /Plugins/<id>/<version>/Image must answer the plugin's image.png, and its
   folder must hold exactly the zip's files with meta.json's assemblies, imagePath and autoUpdate.
7. The sandbox and the server are stopped (also after a failure).

One JSON result is printed and saved as <OutDir>\catalog-test.json; the exit code is 0 only when every check passed.

.EXAMPLE
./scripts/Test-ReleaseCatalog.ps1 -Port 8114 -ServePort 8115
#>
[CmdletBinding()]
param(
    [string] $Name = 'catalog',

    [Parameter(Mandatory = $true)]
    [int] $Port,

    [Parameter(Mandatory = $true)]
    [int] $ServePort,

    [string[]] $Plugin,

    [string] $OutDir,

    [switch] $AllowDirty,

    [string] $JellyfinDir = 'C:\ProgramData\Jellyfin\Server'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'JellyfinSandbox.psm1') -Force

$repo = Get-RepoRoot
if (-not $OutDir) { $OutDir = Join-Path $repo 'artifacts\release-test' }
$OutDir = Resolve-FullPath $OutDir
if ($Port -eq $ServePort) { throw '-Port and -ServePort must differ.' }
if (@(Get-ListeningProcessIds -Port $ServePort).Count -gt 0) { throw "Port $ServePort is in use." }
$serveUrl = "http://127.0.0.1:$ServePort"
$baseUrl = "http://127.0.0.1:$Port"
$sandbox = Get-Sandbox -Name $Name
$checks = New-Object System.Collections.Generic.List[object]
# Jellyfin's API writes GUIDs without dashes.
function ConvertTo-GuidKey($value) { return ([string]$value).Replace('-', '').ToLowerInvariant() }
function Add-Check([string] $what, [bool] $ok, [string] $detail = '') {
    $checks.Add([pscustomobject]@{ check = $what; ok = $ok; detail = $detail })
    $mark = if ($ok) { 'ok  ' } else { 'FAIL' }
    Write-Host "$mark $what $detail"
}

$server = $null
$started = $false
try {
    # --- 1. Package
    $package = @{ BaseUrl = $serveUrl; OutDir = $OutDir; JellyfinDir = $JellyfinDir; AllowDirty = $AllowDirty }
    if ($Plugin) { $package.Plugin = $Plugin }
    & (Join-Path $PSScriptRoot 'Package-Release.ps1') @package
    $catalog = @(Get-Content -LiteralPath (Join-Path $OutDir 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json | ForEach-Object { $_ })
    $packaged = @(Get-Content -LiteralPath (Join-Path $OutDir 'release.json') -Raw -Encoding UTF8 | ConvertFrom-Json | ForEach-Object { $_ })

    # --- 2. Serve
    $node = (Get-Command node -CommandType Application).Path
    $arguments = (@((Join-Path $PSScriptRoot 'serve-release.mjs'), '--root', $OutDir, '--port', [string]$ServePort) | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join ' '
    $server = Start-ProcessWithLogs -FilePath $node -Arguments $arguments -WorkingDirectory $repo -StdOut (Join-Path $OutDir 'serve.log') -StdErr (Join-Path $OutDir 'serve.err.log')
    $deadline = (Get-Date).AddSeconds(20)
    while ($true) {
        try { Invoke-RestMethod -Uri "$serveUrl/manifest.json" -TimeoutSec 5 | Out-Null; break }
        catch { if ((Get-Date) -gt $deadline) { throw "serve-release.mjs did not answer on $serveUrl." }; Start-Sleep -Milliseconds 300 }
    }

    # --- 3. A fresh, empty sandbox
    & (Join-Path $PSScriptRoot 'dev-server.ps1') -Name $Name -Port $Port -Plugins None -Community None -Fresh -Background -JellyfinDir $JellyfinDir
    $started = $true
    $sandbox = Get-Sandbox -Name $Name
    $serverPid = [int](Get-Content -LiteralPath $sandbox.PidFile -Raw).Trim()

    # --- 4. Wizard, admin, the test repository
    $password = New-SandboxPassword
    Write-JsonFile -Path $sandbox.CredentialsFile -Value ([ordered]@{
            note = 'Sandbox-only test accounts (gitignored). Never reuse these passwords anywhere else.'; sandbox = $Name; url = $baseUrl
            users = [ordered]@{ admin = [ordered]@{ id = ''; password = $password; token = '' } }
        })
    Invoke-SandboxApi -BaseUrl $baseUrl -Path 'Startup/Configuration' -Method POST -Body @{ ServerName = "jf-sandbox-$Name"; UICulture = 'en-US'; MetadataCountryCode = 'US'; PreferredMetadataLanguage = 'en' } | Out-Null
    Invoke-SandboxApi -BaseUrl $baseUrl -Path 'Startup/User' | Out-Null
    Invoke-SandboxApi -BaseUrl $baseUrl -Path 'Startup/User' -Method POST -Body @{ Name = 'admin'; Password = $password } | Out-Null
    Invoke-SandboxApi -BaseUrl $baseUrl -Path 'Startup/RemoteAccess' -Method POST -Body @{ EnableRemoteAccess = $false; EnableAutomaticPortMapping = $false } | Out-Null
    Invoke-SandboxApi -BaseUrl $baseUrl -Path 'Startup/Complete' -Method POST | Out-Null
    $auth = Invoke-SandboxApi -BaseUrl $baseUrl -Path 'Users/AuthenticateByName' -Method POST -DeviceId "jf-sandbox-$Name-admin" -Body @{ Username = 'admin'; Pw = $password }
    $token = [string]$auth.AccessToken
    Write-JsonFile -Path $sandbox.CredentialsFile -Value ([ordered]@{
            note = 'Sandbox-only test accounts (gitignored). Never reuse these passwords anywhere else.'; sandbox = $Name; url = $baseUrl
            users = [ordered]@{ admin = [ordered]@{ id = [string]$auth.User.Id; password = $password; token = $token } }
        })
    $api = @{ BaseUrl = $baseUrl; Token = $token; DeviceId = "jf-sandbox-$Name-admin" }

    $repositoryUrl = "$serveUrl/manifest.json"
    Invoke-SandboxApi @api -Path 'Repositories' -Method POST -Body (ConvertTo-Json -InputObject @(@{ Name = 'Release test'; Url = $repositoryUrl; Enabled = $true }) -Compress) | Out-Null
    $repositories = @(Invoke-SandboxApi @api -Path 'Repositories')
    Add-Check 'repository added' ($repositories.Count -eq 1 -and $repositories[0].Url -eq $repositoryUrl) $repositoryUrl

    # --- 5. Install every package from the catalog
    $packages = @(Invoke-SandboxApi @api -Path 'Packages')
    foreach ($entry in $catalog) {
        $version = [string]$entry.versions[0].version
        $listed = @($packages | Where-Object { (ConvertTo-GuidKey $_.guid) -eq (ConvertTo-GuidKey $entry.guid) })
        $offered = $listed.Count -eq 1 -and @($listed[0].versions | Where-Object { $_.version -eq $version }).Count -eq 1
        Add-Check "$($entry.name): in the catalog" $offered $version
        $query = 'assemblyGuid=' + [Uri]::EscapeDataString([string]$entry.guid) + '&version=' + [Uri]::EscapeDataString($version) + '&repositoryUrl=' + [Uri]::EscapeDataString($repositoryUrl)
        Invoke-SandboxApi @api -Path ('Packages/Installed/' + [Uri]::EscapeDataString([string]$entry.name) + '?' + $query) -Method POST | Out-Null
    }

    $deadline = (Get-Date).AddSeconds(180)
    do {
        Start-Sleep -Seconds 2
        $plugins = @(Invoke-SandboxApi @api -Path 'Plugins')
        $missing = @($catalog | Where-Object { $g = ConvertTo-GuidKey $_.guid; @($plugins | Where-Object { (ConvertTo-GuidKey $_.Id) -eq $g }).Count -eq 0 })
    } while ($missing.Count -gt 0 -and (Get-Date) -lt $deadline)
    Add-Check 'every package installed before the restart' ($missing.Count -eq 0) (@($missing | ForEach-Object { $_.name }) -join ', ')

    # --- 6. Restart, then check what loaded
    $before = Get-StartupCompleteCounts -LogDir $sandbox.LogDir
    Invoke-SandboxApi @api -Path 'System/Restart' -Method POST | Out-Null
    Wait-SandboxReady -BaseUrl $baseUrl -ProcessId $serverPid -LogDir $sandbox.LogDir -StartupCountsBefore $before | Out-Null
    $plugins = @(Invoke-SandboxApi @api -Path 'Plugins')
    foreach ($entry in $catalog) {
        $version = [string]$entry.versions[0].version
        $loaded = @($plugins | Where-Object { (ConvertTo-GuidKey $_.Id) -eq (ConvertTo-GuidKey $entry.guid) })
        $state = if ($loaded.Count -eq 1) { "$($loaded[0].Version) $($loaded[0].Status) HasImage=$($loaded[0].HasImage)" } else { 'not listed' }
        Add-Check "$($entry.name): Active after the restart" ($loaded.Count -eq 1 -and $loaded[0].Version -eq $version -and $loaded[0].Status -eq 'Active' -and $loaded[0].HasImage) $state

        $info = $packaged | Where-Object { $_.version -eq $version -and $_.name -eq $entry.name }
        $expectedImage = [IO.File]::ReadAllBytes((Join-Path $repo "plugins\$($info.plugin)\image.png"))
        $image = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/Plugins/$($entry.guid)/$version/Image" -Headers @{ Authorization = (Get-AuthorizationHeader -Token $token -DeviceId $api.DeviceId) } -TimeoutSec 30
        $bytes = $image.RawContentStream.ToArray()
        $same = ($bytes.Length -eq $expectedImage.Length) -and ([Convert]::ToBase64String($bytes) -eq [Convert]::ToBase64String($expectedImage))
        Add-Check "$($entry.name): its image is served" ($image.StatusCode -eq 200 -and $same) "$($image.Headers['Content-Type']), $($bytes.Length) bytes"

        $folder = Join-Path $sandbox.PluginsDir "$($entry.name)_$version"
        $files = @(Get-ChildItem -LiteralPath $folder -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name } | Sort-Object)
        $meta = Read-JsonFile (Join-Path $folder 'meta.json')
        $sameFiles = (($files -join '|') -eq ((@($info.entries) | Sort-Object) -join '|'))
        Add-Check "$($entry.name): folder and meta.json" ($sameFiles -and $meta.imagePath -eq 'image.png' -and $meta.autoUpdate -eq $true -and @($meta.assemblies).Count -ge 1 -and $meta.version -eq $version) ("$($entry.name)_$version`: " + ($files -join ', '))
    }
}
catch {
    Add-Check 'run' $false $_.Exception.Message
}
finally {
    if ($started) {
        try { & (Join-Path $PSScriptRoot 'Stop-Sandbox.ps1') -Name $Name | Out-Null; Add-Check 'sandbox stopped' $true }
        catch { Add-Check 'sandbox stopped' $false $_.Exception.Message }
    }
    if ($server) {
        if (-not $server.HasExited) { Stop-VerifiedProcess -ProcessId $server.Id -Marker 'serve-release.mjs' | Out-Null }
        $server.Dispose()
    }
}

$failed = @($checks | Where-Object { -not $_.ok })
$result = [ordered]@{ ok = ($failed.Count -eq 0); sandbox = $Name; port = $Port; repository = "$serveUrl/manifest.json"; checks = $checks }
Write-JsonFile -Path (Join-Path $OutDir 'catalog-test.json') -Value $result
$result | ConvertTo-Json -Depth 5
if ($failed.Count -gt 0) { exit 1 }
