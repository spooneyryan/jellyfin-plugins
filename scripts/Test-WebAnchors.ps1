<#
.SYNOPSIS
Checks that the jellyfin-web anchors our plugins and the kit rely on still exist in an installed web client bundle.

.DESCRIPTION
Reads the anchor lists (kit/web/anchors.json and every
plugins/<Name>/<Project>/Web/anchors.json), takes the classes, ids and attribute names out of each anchor's selectors
(the primary first, then the fallbacks), and looks for each name as a whole word in the web client's .js, .css and
.html files. It only reads: the bundle, the anchor lists, and (with -Sandbox) the sandbox's sandbox.json. Nothing is
written except the -Out file, which may not be under C:\ProgramData\Jellyfin.

An anchor is "ok" when every name of its primary selector occurs, "fallback" when only a fallback's names all occur,
and "missing" otherwise. Tag names (video, div) are not looked for. The check is a heuristic: a name that occurs
somewhere doesn't prove the selector matches, but a name that occurs nowhere means jellyfin-web renamed or dropped it.
Run it after every Jellyfin, jellyfin-web, Jellyfin Enhanced or Plugin Pages update, and before installing a plugin on
the real server.

It also checks Plugin Pages' three bundle anchors, the exact text
Plugin Pages 2.4.x's File Transformation patches look for to add its user route: 8372:"home-html" in runtime.bundle.js,
path:"queue" exactly twice in main.jellyfin.bundle.js (the stable and the experimental routes) and "./home.html":[ in
main.jellyfin.bundle.js. They are build-specific (8372 is a webpack chunk id), and when one moves Plugin Pages' route,
and with it our pages in its host (PageHostMode Auto or PluginPagesOnly), silently disappears. A changed one is a
warning, since page hosting defaults to FallbackOnly; with -RequirePluginPages or -Strict it fails the check.

Exit codes: 0 when every required anchor is ok or found by a fallback (with -Strict: every anchor ok, and Plugin Pages'
anchors as expected; with -RequirePluginPages: Plugin Pages' anchors as expected), 1 otherwise, 2 when the bundle or
an anchor list can't be read.

.EXAMPLE
./scripts/Test-WebAnchors.ps1                      # the installed web client, C:\ProgramData\Jellyfin\Server\jellyfin-web
./scripts/Test-WebAnchors.ps1 -Sandbox default     # a sandbox's copy of it
./scripts/Test-WebAnchors.ps1 -RequirePluginPages  # before relying on Plugin Pages' host (PageHostMode Auto or PluginPagesOnly)
./scripts/Test-WebAnchors.ps1 -WebDir D:\jellyfin-web-10.11.6 -Plugin Bookmarks -Strict -Out anchors.json
#>
[CmdletBinding(DefaultParameterSetName = 'Installed')]
param(
    [Parameter(ParameterSetName = 'Installed')]
    [string] $JellyfinDir = 'C:\ProgramData\Jellyfin\Server',

    [Parameter(ParameterSetName = 'WebDir', Mandatory = $true)]
    [string] $WebDir,

    [Parameter(ParameterSetName = 'Sandbox', Mandatory = $true)]
    [string] $Sandbox,

    [string[]] $Plugin,

    [switch] $Strict,

    [switch] $RequirePluginPages,

    [string] $Out
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'JellyfinSandbox.psm1') -Force

function Get-SelectorNames {
    <# The classes, ids and attribute names of a CSS selector, in order, without duplicates. #>
    param([Parameter(Mandatory = $true)] [string] $Selector)
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($match in [regex]::Matches($Selector, '[.#](?<name>-?[A-Za-z_][\w-]*)|\[\s*(?<name>[A-Za-z_][\w-]*)')) {
        $name = $match.Groups['name'].Value
        if (-not $names.Contains($name)) { $names.Add($name) }
    }

    return , $names.ToArray()
}

function Find-Name {
    <# The files (at most three) in which Name occurs as a whole word: not next to a letter, digit, '_' or '-'. #>
    param([Parameter(Mandatory = $true)] [string] $Name, [Parameter(Mandatory = $true)] $Bundle)
    $regex = New-Object System.Text.RegularExpressions.Regex(('(?<![\w-])' + [regex]::Escape($Name) + '(?![\w-])'), [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    $files = New-Object System.Collections.Generic.List[string]
    $start = 0
    while ($files.Count -lt 3 -and $start -lt $Bundle.Text.Length) {
        $match = $regex.Match($Bundle.Text, $start)
        if (-not $match.Success) { break }
        $index = [Array]::BinarySearch($Bundle.Starts, $match.Index)
        if ($index -lt 0) { $index = (-bnot $index) - 1 }
        $files.Add($Bundle.Names[$index])
        # Go on after this file, so the three files listed are different ones.
        $start = $Bundle.Text.Length
        if ($index + 1 -lt $Bundle.Starts.Length) { $start = $Bundle.Starts[$index + 1] }
    }

    return , $files.ToArray()
}

# --- The bundle.
switch ($PSCmdlet.ParameterSetName) {
    'Sandbox' {
        $box = Get-Sandbox -Name $Sandbox
        $WebDir = $box.WebDir
    }
    'Installed' {
        $WebDir = Join-Path $JellyfinDir 'jellyfin-web'
    }
}

$WebDir = Resolve-FullPath $WebDir
if (-not (Test-Path -LiteralPath (Join-Path $WebDir 'index.html'))) {
    Write-Error "No jellyfin-web bundle in $WebDir (index.html not found)." -ErrorAction Continue
    exit 2
}

if ($Out) {
    $Out = Assert-SafePath -Path $Out -What 'result file'
}

$files = @(Get-ChildItem -LiteralPath $WebDir -Recurse -File |
    Where-Object { $_.Extension -in '.js', '.css', '.html' } |
    Sort-Object FullName)
$builder = New-Object System.Text.StringBuilder
$starts = New-Object System.Collections.Generic.List[int]
$names = New-Object System.Collections.Generic.List[string]
foreach ($file in $files) {
    $starts.Add($builder.Length)
    $names.Add($file.FullName.Substring($WebDir.Length).TrimStart('\', '/').Replace('\', '/'))
    [void]$builder.Append([System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)).Append("`n")
}

$bundle = [pscustomobject]@{ Text = $builder.ToString(); Starts = $starts.ToArray(); Names = $names.ToArray() }
$builder = $null
$webVersion = $null
$versionMatch = [regex]::Match($bundle.Text, 'version: "\)\.concat\("(?<v>\d+\.\d+\.\d+(?:\.\d+)?)"')
if ($versionMatch.Success) { $webVersion = $versionMatch.Groups['v'].Value }

# --- The anchor lists: the kit's, then each plugin's.
$lists = New-Object System.Collections.Generic.List[object]
$lists.Add([pscustomobject]@{ Owner = 'kit'; Path = Join-Path (Get-RepoRoot) 'kit\web\anchors.json' })
$selected = Get-PluginFolders
if ($Plugin) {
    foreach ($name in $Plugin) {
        if ($selected -notcontains $name) { throw "Unknown plugin '$name'. Known: $($selected -join ', ')." }
    }

    $selected = $Plugin
}

foreach ($name in $selected) {
    $project = Split-Path -Parent (Get-PluginBuildInfo -Plugin $name).Project
    $path = Join-Path $project 'Web\anchors.json'
    if (Test-Path -LiteralPath $path) { $lists.Add([pscustomobject]@{ Owner = $name; Path = $path }) }
}

$results = New-Object System.Collections.Generic.List[object]
foreach ($list in $lists) {
    try {
        $anchors = @((Read-JsonFile $list.Path).anchors)
    }
    catch {
        Write-Error "Can't read $($list.Path): $($_.Exception.Message)" -ErrorAction Continue
        exit 2
    }

    foreach ($anchor in $anchors) {
        $selectors = New-Object System.Collections.Generic.List[object]
        $status = 'missing'
        $index = 0
        foreach ($css in @($anchor.css)) {
            $found = [ordered]@{}
            $all = $true
            foreach ($name in (Get-SelectorNames -Selector ([string]$css))) {
                $where = Find-Name -Name $name -Bundle $bundle
                $found[$name] = $where
                if ($where.Count -eq 0) { $all = $false }
            }

            if ($found.Count -eq 0) { $all = $false }
            if ($all -and $status -eq 'missing') { $status = if ($index -eq 0) { 'ok' } else { 'fallback' } }
            $selectors.Add([pscustomobject]@{ css = [string]$css; found = $all; names = [pscustomobject]$found })
            $index++
        }

        $results.Add([pscustomobject]@{
            plugin    = $list.Owner
            name      = [string]$anchor.name
            required  = [bool]$anchor.required
            views     = @($anchor.views)
            status    = $status
            selectors = $selectors.ToArray()
        })
    }
}

$failed = @($results | Where-Object { ($_.status -eq 'missing' -and $_.required) -or ($Strict -and $_.status -ne 'ok') })
$warnings = @($results | Where-Object { $_.status -ne 'ok' -and $failed -notcontains $_ } |
    ForEach-Object { "$($_.plugin) anchor $($_.name): $($_.status)" })

# --- Plugin Pages' three bundle anchors: the text Plugin Pages 2.4.x's
# File Transformation patches (Helpers/TransformationPatches.cs) look for, counted in the file each patch rewrites.
$pluginPagesAnchors = @(
    [pscustomobject]@{ file = 'runtime.bundle.js'; text = '8372:"home-html"'; count = 1; exact = $false
        patch = 'RuntimeBundle: its chunk names go in front of it' },
    [pscustomobject]@{ file = 'main.jellyfin.bundle.js'; text = 'path:"queue"'; count = 2; exact = $true
        patch = 'MainBundlePluginSettingsRoute: its user route goes next to each, in the stable and the experimental layout' },
    [pscustomobject]@{ file = 'main.jellyfin.bundle.js'; text = '"./home.html":['; count = 1; exact = $false
        patch = 'MainBundleRouteIds: its controllers go in front of it' }
)
$bundleFiles = @{}
$pluginPages = New-Object System.Collections.Generic.List[object]
foreach ($anchor in $pluginPagesAnchors) {
    if (-not $bundleFiles.ContainsKey($anchor.file)) {
        $path = Join-Path $WebDir $anchor.file
        $bundleFiles[$anchor.file] = if (Test-Path -LiteralPath $path -PathType Leaf) { [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) } else { $null }
    }

    $text = $bundleFiles[$anchor.file]
    $found = if ($null -eq $text) { 0 } else { [regex]::Matches($text, [regex]::Escape($anchor.text)).Count }
    $status = if ($found -eq 0) { 'missing' } elseif (($anchor.exact -and $found -eq $anchor.count) -or (-not $anchor.exact -and $found -ge $anchor.count)) { 'ok' } else { 'changed' }
    $pluginPages.Add([pscustomobject]@{
        file     = $anchor.file
        text     = $anchor.text
        expected = $(if ($anchor.exact) { "exactly $($anchor.count)" } else { "at least $($anchor.count)" })
        found    = $found
        status   = $status
        fileRead = ($null -ne $text)
        patch    = $anchor.patch
    })
}

$pluginPagesPass = @($pluginPages | Where-Object { $_.status -ne 'ok' }).Count -eq 0
$pluginPagesProblems = @($pluginPages | Where-Object { $_.status -ne 'ok' } | ForEach-Object {
    "Plugin Pages bundle anchor $($_.text) in $($_.file): $($_.status), found $($_.found), expected $($_.expected)" + $(if ($_.fileRead) { '' } else { ' (no such file)' }) })
$failedText = @($failed | ForEach-Object { "$($_.plugin) anchor $($_.name): $($_.status)" + $(if ($_.required) { ' (required)' } else { '' }) })
if ($Strict -or $RequirePluginPages) {
    $failedText += $pluginPagesProblems
}
else {
    $warnings += $pluginPagesProblems
}

$result = [pscustomobject]@{
    pass               = ($failedText.Count -eq 0)
    webDir             = $WebDir
    webVersion         = $webVersion
    files              = $files.Count
    strict             = [bool]$Strict
    requirePluginPages = [bool]$RequirePluginPages
    failed             = $failedText
    warnings           = $warnings
    anchors            = $results.ToArray()
    pluginPages        = [pscustomobject]@{ pass = $pluginPagesPass; anchors = $pluginPages.ToArray() }
}

$json = ($result | ConvertTo-Json -Depth 8) -replace "`r`n", "`n"
if ($Out) { Write-Utf8NoBom -Path $Out -Text ($json + "`n") }
Write-Output $json
if ($result.pass) { exit 0 }
exit 1
