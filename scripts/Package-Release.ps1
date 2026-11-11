<#
.SYNOPSIS
Packages plugins into the zips Jellyfin installs and keeps the plugin repository's catalog, manifest.json.

.DESCRIPTION
For each plugin (every plugins/<Plugin> with a build.yaml, or the ones -Plugin or -Tag name):

- builds it in Release from scratch (the project's obj\Release and bin\Release are removed first, so no stale
  incremental output is reused; dotnet publish, deterministic, with the checkout's path mapped to /_/ and without git or
  source-link data, so the build doesn't depend on where the checkout is or on its commit) into <OutDir>\build\<Plugin>;
- checks the build like Deploy-Plugin.ps1: no DLL other than build.yaml's artifacts, the main DLL's file version is
  build.yaml's version, and, when -JellyfinDir holds a Jellyfin 10.11 (only read), no reference newer than it ships;
- writes <OutDir>\<plugin in lower case>_<x.y.z.0>.zip, flat: the artifacts' DLLs and PDBs, image.png and meta.json.
  The entries are stored (not compressed), in name order, with a fixed date, so the same build gives the same bytes on
  any machine and in either PowerShell. Jellyfin extracts the zip into plugins\<name>_<version> and keeps meta.json,
  whose assemblies list is the allowlist it loads; meta.json carries no timestamp (Jellyfin takes the catalog's);
- computes the zip's MD5, which Jellyfin checks before it installs, and writes <zip>.md5 and <zip>.notes.md (the
  version's section of build.yaml's changelog, the GitHub release's notes).

Then, by mode:

- default: updates manifest.json at the repository root, the catalog Jellyfin reads: each package's fields from
  build.yaml, and the version's entry (changelog, targetAbi, sourceUrl, checksum, timestamp) added or replaced; other
  versions' entries are kept. A rerun that gives the same checksum keeps the entry's timestamp.
- -Verify: changes nothing; fails unless manifest.json already has each version with this zip's checksum and sourceUrl.
  The release workflow runs it on a pushed tag before it attaches the zip to the GitHub release.
- -BaseUrl: a catalog for a local test, <OutDir>\manifest.json, whose sourceUrl and imageUrl point under BaseUrl (the
  images are copied to <OutDir>\images\<Plugin>\image.png); manifest.json at the root is not touched.

A plugin's release tag is <plugin in lower case>/v<x.y.z> (bookmarks/v1.2.2), and its zip is the GitHub release's
asset: https://github.com/<Repository>/releases/download/<tag, its / as %2F>/<zip>. build.yaml's version must be
x.y.z.0 and its changelog must have an x.y.z section. The plugin's sources (plugins/<Plugin>, kit/ and the root build
files) must be committed unless -AllowDirty. The script warns when `dotnet --version` isn't RELEASE_SDK of
.github/workflows/ci.yml: the workflow builds with that SDK, and a different compiler can give a different DLL.

.PARAMETER Plugin
Folder names under plugins/ (any case). Default: every plugin.

.PARAMETER Tag
A release tag (bookmarks/v1.2.2): packages that plugin, whose build.yaml must say that version.

.PARAMETER OutDir
Where the zips go; artifacts\release by default (ignored by git). Its build\ folder is replaced on each run.

.EXAMPLE
./scripts/Package-Release.ps1 -Plugin Bookmarks
# artifacts\release\bookmarks_1.2.2.0.zip, and manifest.json updated

.EXAMPLE
./scripts/Package-Release.ps1 -Tag bookmarks/v1.2.2 -Verify

.EXAMPLE
./scripts/Package-Release.ps1 -BaseUrl http://127.0.0.1:8115 -OutDir artifacts\release-test
#>
[CmdletBinding()]
param(
    [string[]] $Plugin,

    [string] $Tag,

    [string] $OutDir,

    [switch] $Verify,

    [string] $BaseUrl,

    [string] $Repository = 'spooneyryan/jellyfin-plugins',

    [string] $JellyfinDir = 'C:\ProgramData\Jellyfin\Server',

    [switch] $AllowDirty
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
Import-Module (Join-Path $PSScriptRoot 'JellyfinSandbox.psm1') -Force

$repo = Get-RepoRoot
# powershell -File passes "A,B" as one string.
$Plugin = @($Plugin | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($Verify -and $BaseUrl) { throw '-Verify checks manifest.json and -BaseUrl writes a test catalog. Use one of them.' }
if ($Tag -and $Plugin) { throw '-Tag names the plugin already; leave out -Plugin.' }
if ($BaseUrl) {
    $uri = $null
    if (-not [Uri]::TryCreate($BaseUrl, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -notin @('http', 'https')) { throw "-BaseUrl $BaseUrl is not an http(s) URL." }
    $BaseUrl = $BaseUrl.TrimEnd('/')
}

if (-not $OutDir) { $OutDir = Join-Path $repo 'artifacts\release' }
$OutDir = Resolve-FullPath $OutDir
if (Test-ProtectedPath $OutDir) { throw "Refusing to write into $OutDir." }
$manifestPath = Join-Path $repo 'manifest.json'
$utf8 = New-Object System.Text.UTF8Encoding($false)

# ------------------------------------------------------------------------------------------ helpers

if (-not ('JellyfinPlugins.StoredZip' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;

namespace JellyfinPlugins
{
    // A zip of stored (uncompressed) entries, byte for byte the same for the same files: no compression library, no
    // extra fields, one fixed date. ZipFile.ExtractToDirectory (Jellyfin's installer) reads it like any zip.
    public static class StoredZip
    {
        private static readonly uint[] Table = MakeTable();

        private static uint[] MakeTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        public static uint Crc32(byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        public static void Write(string path, string[] names, byte[][] contents)
        {
            // 2000-01-01 00:00:00 in DOS date and time.
            const ushort time = 0;
            const ushort date = (ushort)(((2000 - 1980) << 9) | (1 << 5) | 1);
            var offsets = new long[names.Length];
            var crcs = new uint[names.Length];
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(stream))
            {
                for (int i = 0; i < names.Length; i++)
                {
                    byte[] name = Encoding.ASCII.GetBytes(names[i]);
                    crcs[i] = Crc32(contents[i]);
                    offsets[i] = stream.Position;
                    w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)0);
                    w.Write(time); w.Write(date); w.Write(crcs[i]);
                    w.Write((uint)contents[i].Length); w.Write((uint)contents[i].Length);
                    w.Write((ushort)name.Length); w.Write((ushort)0);
                    w.Write(name); w.Write(contents[i]);
                }

                long directory = stream.Position;
                for (int i = 0; i < names.Length; i++)
                {
                    byte[] name = Encoding.ASCII.GetBytes(names[i]);
                    w.Write(0x02014b50u); w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)0);
                    w.Write(time); w.Write(date); w.Write(crcs[i]);
                    w.Write((uint)contents[i].Length); w.Write((uint)contents[i].Length);
                    w.Write((ushort)name.Length); w.Write((ushort)0); w.Write((ushort)0);
                    w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write((uint)offsets[i]);
                    w.Write(name);
                }

                long end = stream.Position;
                w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0);
                w.Write((ushort)names.Length); w.Write((ushort)names.Length);
                w.Write((uint)(end - directory)); w.Write((uint)directory); w.Write((ushort)0);
            }
        }
    }
}
'@
}

function ConvertTo-JsonText {
    <# Stable JSON: two-space indents, keys in the order given, LF line ends; the same value always gives the same text. #>
    param($Value, [string] $Indent = '')
    $inner = $Indent + '  '
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [bool]) { if ($Value) { return 'true' } else { return 'false' } }
    if ($Value -is [string]) {
        $builder = New-Object System.Text.StringBuilder
        [void]$builder.Append('"')
        foreach ($c in $Value.ToCharArray()) {
            switch ([int]$c) {
                0x22 { [void]$builder.Append('\"') }
                0x5C { [void]$builder.Append('\\') }
                0x0A { [void]$builder.Append('\n') }
                0x0D { [void]$builder.Append('\r') }
                0x09 { [void]$builder.Append('\t') }
                default { if ([int]$c -lt 0x20) { [void]$builder.Append(('\u{0:x4}' -f [int]$c)) } else { [void]$builder.Append($c) } }
            }
        }
        [void]$builder.Append('"')
        return $builder.ToString()
    }
    if ($Value -is [System.Collections.IDictionary]) {
        if ($Value.Count -eq 0) { return '{}' }
        $parts = @(foreach ($key in $Value.Keys) { $inner + (ConvertTo-JsonText ([string]$key)) + ': ' + (ConvertTo-JsonText $Value[$key] $inner) })
        return "{`n" + ($parts -join ",`n") + "`n$Indent}"
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        $items = @($Value)
        if ($items.Count -eq 0) { return '[]' }
        $parts = @(foreach ($item in $items) { $inner + (ConvertTo-JsonText $item $inner) })
        return "[`n" + ($parts -join ",`n") + "`n$Indent]"
    }
    if ($Value -is [int] -or $Value -is [long]) { return [string]$Value }
    throw "Can't write a $($Value.GetType().FullName) as JSON."
}

function ConvertTo-Ordered {
    <# A ConvertFrom-Json object as ordered dictionaries and arrays, keeping the key order. #>
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $result = [ordered]@{}
        foreach ($property in $Value.PSObject.Properties) { $result[$property.Name] = ConvertTo-Ordered $property.Value }
        return $result
    }
    if ($Value -is [System.Array]) { return , @($Value | ForEach-Object { ConvertTo-Ordered $_ }) }
    return $Value
}

function Get-ChangelogSection {
    <# The lines of build.yaml's changelog under the header line "x.y.z", up to the next header. #>
    param([string] $Changelog, [string] $Version, [string] $Source)
    $lines = ($Changelog -replace "`r`n", "`n").Split("`n")
    $start = [array]::IndexOf($lines, $Version)
    if ($start -lt 0) { throw "$Source's changelog has no '$Version' section." }
    $section = New-Object System.Collections.Generic.List[string]
    for ($i = $start + 1; $i -lt $lines.Length; $i++) {
        if ($lines[$i] -match '^\d+\.\d+\.\d+$') { break }
        $section.Add($lines[$i].TrimEnd())
    }
    while ($section.Count -gt 0 -and $section[$section.Count - 1] -eq '') { $section.RemoveAt($section.Count - 1) }
    if ($section.Count -eq 0) { throw "$Source's changelog section '$Version' is empty." }
    return ($section -join "`n")
}

function Get-Md5 {
    param([string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm MD5).Hash.ToLowerInvariant()
}

# ------------------------------------------------------------------------------------------ what to package

$known = @(Get-PluginFolders -RepoRoot $repo)
$selected = @()
if ($Tag) {
    $match = @($known | Where-Object { $null -ne (ConvertTo-PluginReleaseTag -Plugin $_ -Name $Tag) })
    if ($match.Count -ne 1) { throw "$Tag is not a release tag of a plugin here (<plugin in lower case>/v<x.y.z>; plugins: $($known -join ', '))." }
    $selected = $match
}
elseif ($Plugin) {
    foreach ($name in $Plugin) {
        $match = @($known | Where-Object { $_ -ieq $name })
        if ($match.Count -ne 1) { throw "Unknown plugin '$name'. Known: $($known -join ', ')." }
        $selected += $match[0]
    }
}
else {
    $selected = $known
}
if ($selected.Count -eq 0) { throw "No plugins/*/build.yaml in $repo." }

# The sources must be what is committed: a release is built from a commit.
$inputs = @('kit', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'nuget.config') + @($selected | ForEach-Object { "plugins/$_" })
$status = Invoke-Git -Repo $repo -Arguments (@('status', '--porcelain', '--') + $inputs)
if ($status.ExitCode -ne 0) { throw "git status failed: $($status.Error)" }
$dirty = @($status.Output | Where-Object { $_ })
if ($dirty.Count -gt 0) {
    $message = "Uncommitted changes in the plugin's sources: $($dirty -join '; ')."
    if (-not $AllowDirty) { throw "$message Commit them first (or pass -AllowDirty for a test package)." }
    Write-Warning $message
}

$sdk = @(& dotnet --version)
if ($LASTEXITCODE -ne 0) { throw 'dotnet --version failed; is the .NET SDK installed?' }
$sdk = ([string]$sdk[0]).Trim()
$workflow = Join-Path $repo '.github\workflows\ci.yml'
if (Test-Path -LiteralPath $workflow) {
    $pinned = [regex]::Match([IO.File]::ReadAllText($workflow), "(?m)^\s*RELEASE_SDK:\s*'?([0-9][0-9A-Za-z.\-]*)'?\s*$")
    if ($pinned.Success -and $pinned.Groups[1].Value -ne $sdk) {
        Write-Warning ("This build uses .NET SDK $sdk, the release workflow $($pinned.Groups[1].Value) (RELEASE_SDK). " +
            'Its build can differ byte for byte, and then the workflow refuses to attach its zip (the checksum in manifest.json is this one).')
    }
}

$checkReferences = Test-Path -LiteralPath (Join-Path $JellyfinDir 'jellyfin.dll') -PathType Leaf
if ($checkReferences) { $serverVersion = Assert-JellyfinTarget -JellyfinDir $JellyfinDir -JellyfinTarget '10.11' }
else { Write-Warning "No Jellyfin in ${JellyfinDir}: the reference-version check is skipped (the build's package pins still apply)." }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$buildRoot = Join-Path $OutDir 'build'
if (Test-Path -LiteralPath $buildRoot) { Remove-SandboxItem -Path $buildRoot -Root $OutDir }

# ------------------------------------------------------------------------------------------ build and zip

$results = New-Object System.Collections.Generic.List[object]
foreach ($folder in $selected) {
    $info = Get-PluginBuildInfo -Plugin $folder -RepoRoot $repo
    $yaml = $info.Yaml
    if ($info.Version -notmatch '^(\d+\.\d+\.\d+)\.0$') { throw "plugins/$folder/build.yaml: version $($info.Version) is not x.y.z.0." }
    $short = $Matches[1]
    $releaseTag = (Get-PluginReleaseTagPrefix $folder) + $short
    if ($Tag -and $Tag -ne $releaseTag) { throw "$Tag is not the version plugins/$folder/build.yaml says ($($info.Version), tag $releaseTag)." }
    $changelog = Get-ChangelogSection -Changelog ([string]$yaml['changelog']) -Version $short -Source "plugins/$folder/build.yaml"

    Write-Host "== $($info.Name) $($info.Version)"
    $output = Join-Path $buildRoot $folder
    # A clean build: an incremental one can keep a stale DLL (a checkout can give sources older timestamps than obj\),
    # and then this MD5 would not be the one the release workflow's fresh checkout produces.
    $projectDir = Split-Path -Parent $info.Project
    foreach ($stale in @((Join-Path $projectDir 'obj\Release'), (Join-Path $projectDir 'bin\Release'))) {
        if (Test-Path -LiteralPath $stale) { Remove-SandboxItem -Path $stale -Root $projectDir }
    }
    $publish = @($info.Project, '-c', 'Release', '-o', $output, '--nologo', '-v', 'quiet', '-p:JellyfinTarget=10.11',
        '-p:ContinuousIntegrationBuild=true', '-p:Deterministic=true', '-p:EnableSourceLink=false',
        '-p:EnableSourceControlManagerQueries=false', '-p:IncludeSourceRevisionInInformationalVersion=false',
        ('-p:PathMap=' + $repo.TrimEnd([char]92) + '\=/_/'))
    & dotnet publish @publish | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "The Release build of $folder failed." }

    $allowed = $info.Assemblies
    $unexpected = @(Get-ChildItem -LiteralPath $output -Recurse -File -Filter '*.dll' | Where-Object { $allowed -notcontains $_.Name } |
        ForEach-Object { $_.FullName.Substring($output.Length + 1) })
    if ($unexpected.Count -gt 0) { throw "The $folder build has DLLs that are not in build.yaml's artifacts: $($unexpected -join ', ')." }

    $entries = [ordered]@{}
    foreach ($assembly in $allowed) {
        $path = Join-Path $output $assembly
        if (-not (Test-Path -LiteralPath $path)) { throw "build.yaml lists $assembly, but the build did not produce it." }
        $entries[$assembly] = [IO.File]::ReadAllBytes($path)
        $pdb = [IO.Path]::ChangeExtension($assembly, '.pdb')
        if (Test-Path -LiteralPath (Join-Path $output $pdb)) { $entries[$pdb] = [IO.File]::ReadAllBytes((Join-Path $output $pdb)) }
    }

    $fileVersion = (Get-Item -LiteralPath (Join-Path $output $allowed[0])).VersionInfo.FileVersion
    if ($fileVersion -ne $info.Version) { throw "$($allowed[0]) has file version $fileVersion, but build.yaml says $($info.Version)." }

    if ($checkReferences) {
        $references = Assert-AssemblyReferenceVersions -AssemblyPaths @($allowed | ForEach-Object { Join-Path $output $_ }) -JellyfinDir $JellyfinDir -What $folder
        Write-Host "Reference check passed: $(@($references.Checked).Count) references, none newer than Jellyfin $serverVersion ships."
    }

    $imageName = Get-PluginImageName
    $imagePath = Join-Path $info.Path $imageName
    if (-not (Test-Path -LiteralPath $imagePath -PathType Leaf)) { throw "plugins/$folder/$imageName is missing; every released plugin has an image." }
    $entries[$imageName] = [IO.File]::ReadAllBytes($imagePath)

    $meta = [ordered]@{
        category    = [string]$yaml['category']
        changelog   = $changelog
        description = ([string]$yaml['description']).TrimEnd()
        guid        = $info.Guid
        name        = $info.Name
        overview    = ([string]$yaml['overview']).TrimEnd()
        owner       = [string]$yaml['owner']
        targetAbi   = [string]$yaml['targetAbi']
        version     = $info.Version
        status      = 'Active'
        autoUpdate  = $true
        imagePath   = $imageName
        assemblies  = [string[]]$allowed
    }
    $entries['meta.json'] = $utf8.GetBytes((ConvertTo-JsonText $meta) + "`n")

    $names = [string[]]@($entries.Keys)
    [Array]::Sort($names, [StringComparer]::Ordinal)
    $zipName = '{0}_{1}.zip' -f $folder.ToLowerInvariant(), $info.Version
    $zipPath = Join-Path $OutDir $zipName
    [JellyfinPlugins.StoredZip]::Write($zipPath, $names, [byte[][]]@($names | ForEach-Object { , $entries[$_] }))
    $md5 = Get-Md5 $zipPath
    [IO.File]::WriteAllText("$zipPath.md5", "$md5  $zipName`n", $utf8)
    [IO.File]::WriteAllText("$zipPath.notes.md", "$($info.Name) $short for Jellyfin $([string]$yaml['targetAbi'] -replace '\.0$', '').`n`n$changelog`n", $utf8)

    $encodedTag = [Uri]::EscapeDataString($releaseTag)
    $results.Add([pscustomobject]@{
            Plugin    = $folder
            Name      = $info.Name
            Guid      = $info.Guid
            Version   = $info.Version
            Tag       = $releaseTag
            Zip       = $zipPath
            ZipName   = $zipName
            Md5       = $md5
            Bytes     = (Get-Item -LiteralPath $zipPath).Length
            Entries   = $names
            Changelog = $changelog
            Yaml      = $yaml
            SourceUrl = "https://github.com/$Repository/releases/download/$encodedTag/$zipName"
            ImageUrl  = "https://raw.githubusercontent.com/$Repository/main/plugins/$folder/$imageName"
        })
    Write-Host "$zipName  MD5 $md5  ($($names -join ', '))"
}

# ------------------------------------------------------------------------------------------ the catalog

function New-PackageEntry([object] $result, [string] $imageUrl) {
    $yaml = $result.Yaml
    return [ordered]@{
        guid        = $result.Guid
        name        = $result.Name
        description = ([string]$yaml['description']).TrimEnd()
        overview    = ([string]$yaml['overview']).TrimEnd()
        owner       = [string]$yaml['owner']
        category    = [string]$yaml['category']
        imageUrl    = $imageUrl
        versions    = @()
    }
}

function New-VersionEntry([object] $result, [string] $sourceUrl, [string] $timestamp) {
    return [ordered]@{
        version   = $result.Version
        changelog = $result.Changelog
        targetAbi = [string]$result.Yaml['targetAbi']
        sourceUrl = $sourceUrl
        checksum  = $result.Md5
        timestamp = $timestamp
    }
}

$now = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)

if ($BaseUrl) {
    $catalog = @()
    foreach ($result in $results) {
        $imageCopy = Join-Path $OutDir "images\$($result.Plugin)\image.png"
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $imageCopy) | Out-Null
        Copy-Item -LiteralPath (Join-Path $repo "plugins\$($result.Plugin)\image.png") -Destination $imageCopy -Force
        $package = New-PackageEntry $result "$BaseUrl/images/$($result.Plugin)/image.png"
        $package['versions'] = @((New-VersionEntry $result "$BaseUrl/$($result.ZipName)" $now))
        $catalog += $package
    }
    $target = Join-Path $OutDir 'manifest.json'
    [IO.File]::WriteAllText($target, (ConvertTo-JsonText @($catalog)) + "`n", $utf8)
    Write-Host "Test catalog: $target (URLs under $BaseUrl)"
}
else {
    $catalog = @()
    if (Test-Path -LiteralPath $manifestPath) {
        # One object, the array (ConvertTo-Ordered returns arrays whole); @() of it is the packages.
        $parsed = ConvertTo-Ordered (([IO.File]::ReadAllText($manifestPath, $utf8)) | ConvertFrom-Json)
        $catalog = @($parsed)
    }

    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($result in $results) {
        $package = @($catalog | Where-Object { $_['guid'] -ieq $result.Guid }) | Select-Object -First 1
        $existing = $null
        if ($package) { $existing = @($package['versions'] | Where-Object { $_['version'] -eq $result.Version }) | Select-Object -First 1 }

        if ($Verify) {
            if (-not $existing) { $problems.Add("manifest.json has no $($result.Name) $($result.Version)."); continue }
            if ($existing['checksum'] -ne $result.Md5) { $problems.Add("$($result.ZipName): MD5 $($result.Md5), manifest.json says $($existing['checksum']).") }
            if ($existing['sourceUrl'] -ne $result.SourceUrl) { $problems.Add("$($result.Name) $($result.Version): sourceUrl $($existing['sourceUrl']), expected $($result.SourceUrl).") }
            continue
        }

        $fresh = New-PackageEntry $result $result.ImageUrl
        $timestamp = $now
        if ($existing -and $existing['checksum'] -eq $result.Md5 -and $existing['timestamp']) { $timestamp = [string]$existing['timestamp'] }
        $versions = @()
        if ($package) { $versions = @($package['versions'] | Where-Object { $_['version'] -ne $result.Version }) }
        $versions += New-VersionEntry $result $result.SourceUrl $timestamp
        $fresh['versions'] = @($versions | Sort-Object -Descending { [version]$_['version'] })
        $catalog = @(@($catalog | Where-Object { $_['guid'] -ine $result.Guid }) + $fresh)
    }

    if ($Verify) {
        if ($problems.Count -gt 0) { throw ("The zips don't match manifest.json:`n  " + ($problems -join "`n  ")) }
        Write-Host 'manifest.json lists every zip with its checksum and sourceUrl.'
    }
    else {
        $catalog = @($catalog | Sort-Object { [string]$_['name'] })
        [IO.File]::WriteAllText($manifestPath, (ConvertTo-JsonText @($catalog)) + "`n", $utf8)
        Write-Host "Updated $manifestPath"
    }
}

$summary = @($results | ForEach-Object {
        [ordered]@{ plugin = $_.Plugin; name = $_.Name; version = $_.Version; tag = $_.Tag; zip = $_.ZipName; md5 = $_.Md5; bytes = [long]$_.Bytes; sourceUrl = $_.SourceUrl; entries = [string[]]$_.Entries }
    })
[IO.File]::WriteAllText((Join-Path $OutDir 'release.json'), (ConvertTo-JsonText @($summary)) + "`n", $utf8)
$results | Format-Table Name, Version, Tag, ZipName, Md5, Bytes -AutoSize | Out-String -Width 200 | Write-Host
