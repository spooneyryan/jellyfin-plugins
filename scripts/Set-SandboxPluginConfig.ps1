<#
.SYNOPSIS
Reads or changes a plugin's settings in a running sandbox, through the plugin configuration API.

.DESCRIPTION
Reads GET /Plugins/<guid>/Configuration of the plugin named by plugins/<Plugin>/build.yaml, changes the given
settings, posts the whole configuration back and prints the settings it changed (as read back). Without -Set it
prints the current configuration. Only the sandbox's own loopback URL is called (never port 8096), with the sandbox
admin token from .sandbox/<Name>/credentials.json, which is never printed.

Settings that take effect at startup (for example PageHostMode and RegisterPluginPages, which decide the Plugin Pages
registration) need a restart afterwards: ./scripts/Stop-Sandbox.ps1 -Name <Name>, then
./scripts/dev-server.ps1 -Name <Name> -Background -NoBuild.

.EXAMPLE
./scripts/Set-SandboxPluginConfig.ps1 -Name bare -Plugin Bookmarks -Set @{ EnablePageDemo = $true }

.EXAMPLE
./scripts/Set-SandboxPluginConfig.ps1 -Name default -Plugin Bookmarks -Set @{ PageHostMode = 'Auto'; EnableDemoPageB = $true }

.EXAMPLE
./scripts/Set-SandboxPluginConfig.ps1 -Name default -Plugin Bookmarks
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Name,

    [Parameter(Mandatory = $true)]
    [string] $Plugin,

    [hashtable] $Set
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'JellyfinSandbox.psm1') -Force

$sandbox = Get-Sandbox -Name $Name
if (-not (Get-SandboxServerProcess -Sandbox $sandbox)) { throw "Sandbox '$Name' is not running; start it with ./scripts/dev-server.ps1 -Name $Name -Background." }
if (-not (Test-Path -LiteralPath $sandbox.CredentialsFile)) { throw "Sandbox '$Name' has no credentials.json; run ./scripts/Initialize-Sandbox.ps1 -Name $Name." }

$info = Get-PluginBuildInfo -Plugin $Plugin
$baseUrl = Get-SandboxBaseUrl -Sandbox $sandbox
$token = (Read-JsonFile $sandbox.CredentialsFile).users.admin.token
if (-not $token) { throw "credentials.json of sandbox '$Name' has no admin token; run ./scripts/Initialize-Sandbox.ps1 -Name $Name." }

$path = 'Plugins/' + $info.Guid + '/Configuration'
$configuration = Invoke-SandboxApi -BaseUrl $baseUrl -Path $path -Token $token
if (-not $Set -or $Set.Count -eq 0) {
    $configuration | ConvertTo-Json -Depth 10
    return
}

foreach ($key in $Set.Keys) {
    if (-not $configuration.PSObject.Properties[$key]) {
        throw "$($info.Name) has no setting '$key'. Settings: $(($configuration.PSObject.Properties | ForEach-Object { $_.Name }) -join ', ')."
    }

    $configuration.$key = $Set[$key]
}

Invoke-SandboxApi -BaseUrl $baseUrl -Path $path -Method POST -Body $configuration -Token $token | Out-Null
$after = Invoke-SandboxApi -BaseUrl $baseUrl -Path $path -Token $token
$changed = [ordered]@{}
foreach ($key in $Set.Keys) { $changed[$key] = $after.$key }
Write-Host "$($info.Name) in sandbox '$Name': $((($changed.Keys | ForEach-Object { $_ + '=' + $changed[$_] })) -join ', ')"
