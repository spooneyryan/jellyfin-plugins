<#
.SYNOPSIS
Generates the sandbox test media with the installed ffmpeg.

.DESCRIPTION
Creates short testsrc2 videos with a sine tone, and sine-tone FLAC tracks for the Music library, under .sandbox\media
(C: only, never the server's media drive). Existing files are kept, so the script can be re-run; -Force regenerates the standard set.

Standard set (Initialize-Sandbox.ps1 creates one library per top folder):
  Movies\        duplicate title+year (also in Movies 2), a remake pair with different [tmdbid-] tags, a remake pair
                 with the same tag, a folder-named unidentified item, a two-version movie (- 1080p / - 720p),
                 a [tmdbid-] folder, an edition pair (same [tmdbid-], 60 s and 90 s), "Test Pattern (2020)",
                 "Blade Runner (1982)", the accented half of a diacritics pair, a longer movie for bookmarks and
                 "Chaptered Movie (2022)" with embedded chapters (titled ones, a generic "Chapter 4" and an untitled
                 one; for the name dialog's suggestion)
  Movies 2\      the other halves: duplicate, loose (mixed-folder) "Test Pattern.mp4" and "Blade Runner 2049.mkv",
                 the plain half of the diacritics pair
  Shows\         a two-season series tagged [tvdbid-]
  Home Videos\   two plain videos
  Music\         two artist folders with one album each, two sine-tone FLAC tracks per album with artist, album
                 artist, album, title, track and year tags (User Ratings' lifecycle checks: rate an
                 album, delete its artist folder, restore it); Initialize-Sandbox.ps1 makes it a library in 'default'
  LM Duplicates\ Library Maintenance's own cases, each with an NFO (title, year, TMDb and IMDb
                 ids): one half of a Confirmed Title + Year pair, and an edition pair in the separator form
                 ("LM Edition Pair - Extended (2003)"); a poster.jpg for the first of each pair
  LM Duplicates 2\ the Confirmed pair's other half (rated R in its NFO, for a parental limit's check)
  Player Tracks\ "Multi Track (2021)": two audio tracks (English 440 Hz, French 660 Hz) and two SRT subtitle tracks
                 (English, French), for the player's track sheets; not in
                 media-set.json, and its library only with Initialize-Sandbox.ps1 -PlayerTracks

Synthetic scale seed (one shared definition for every plan's timing work):
  -SyntheticMovies <n>    Synthetic\<bucket>\<Title> (<Year>)\ folders from a generated title list, about 5 %
                          planted duplicates and variants (same title and year, remake years, case, accents,
                          trailing article)
  -SyntheticEpisodes <n>  SyntheticTV\: one TV library of up to 200 shows, 20 episodes per season
  -SyntheticMode HardLink one 2 s clip hard-linked into every folder (real size, runtime and streams, no extra disk;
                          a new link source every 1000 links, below the NTFS limit of 1023)
  -SyntheticMode Stub     zero-byte .mkv files, when media info isn't needed

.EXAMPLE
./scripts/New-SandboxMedia.ps1

.EXAMPLE
./scripts/New-SandboxMedia.ps1 -SyntheticMovies 10000 -SyntheticEpisodes 50000
#>
[CmdletBinding()]
param(
    [string] $MediaRoot,

    [string] $FfmpegPath = 'C:\ProgramData\Jellyfin\Server\ffmpeg.exe',

    [switch] $Force,

    [ValidateRange(0, 100000)]
    [int] $SyntheticMovies = 0,

    [ValidateRange(0, 200000)]
    [int] $SyntheticEpisodes = 0,

    [ValidateSet('HardLink', 'Stub')]
    [string] $SyntheticMode = 'HardLink',

    [int] $SyntheticSeed = 20260925
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'JellyfinSandbox.psm1') -Force

if (-not $MediaRoot) { $MediaRoot = Join-Path (Get-SandboxRoot) 'media' }
$MediaRoot = Assert-SafePath -Path $MediaRoot -What 'media folder'
if (-not $MediaRoot.StartsWith('C:\', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write media to ${MediaRoot}: sandbox media stays on C:."
}

if (-not (Test-Path -LiteralPath $FfmpegPath)) { throw "ffmpeg not found at $FfmpegPath; pass -FfmpegPath." }
$sources = Join-Path $MediaRoot '_sources'
New-Item -ItemType Directory -Force -Path $MediaRoot, $sources | Out-Null
$started = Get-Date

function Get-SourceClip {
    <# Encodes (once) a testsrc2 + sine clip and returns its path. #>
    param([int] $Width, [int] $Height, [int] $Seconds, [string] $Extension = 'mkv', [int] $Tone = 440)
    $path = Join-Path $sources ('testsrc2-{0}x{1}-{2}s-{3}hz.{4}' -f $Width, $Height, $Seconds, $Tone, $Extension)
    if ((Test-Path -LiteralPath $path) -and (-not $script:ForceSources -or $script:EncodedThisRun.Contains($path))) { return $path }

    $partial = $path + '.partial.' + $Extension
    $ffmpegArgs = @(
        '-hide_banner', '-loglevel', 'error', '-nostdin', '-y',
        '-f', 'lavfi', '-i', ('testsrc2=size={0}x{1}:rate=25:duration={2}' -f $Width, $Height, $Seconds),
        '-f', 'lavfi', '-i', ('sine=frequency={0}:sample_rate=48000:duration={1}' -f $Tone, $Seconds),
        '-map', '0:v', '-map', '1:a',
        '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '38', '-pix_fmt', 'yuv420p', '-g', '50',
        '-c:a', 'aac', '-b:a', '64k', '-shortest'
    )
    if ($Extension -eq 'mp4') { $ffmpegArgs += @('-movflags', '+faststart') }
    $ffmpegArgs += $partial

    & $FfmpegPath @ffmpegArgs
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $partial)) { throw "ffmpeg failed to create $path." }
    Move-Item -LiteralPath $partial -Destination $path -Force
    [void]$script:EncodedThisRun.Add($path)
    return $path
}

$script:ForceSources = [bool]$Force
$script:EncodedThisRun = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

function Copy-ClipWithChapters {
    <#
    Stream-copies a source clip into $Target with the given chapters (an array of @(startSeconds, title or $null))
    embedded from an ffmpeg FFMETADATA file; the last chapter ends at the clip's
    length. Jellyfin names an untitled chapter itself ("Chapter N" in the server's UI culture).
    #>
    param([string] $Source, [string] $Target, [object[]] $Chapters, [int] $Seconds)
    $folder = Split-Path -Parent $Target
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $lines = @(';FFMETADATA1', "title=$([IO.Path]::GetFileNameWithoutExtension($Target))")
    for ($i = 0; $i -lt $Chapters.Count; $i++) {
        $start = [int]$Chapters[$i][0] * 1000
        $end = if ($i + 1 -lt $Chapters.Count) { [int]$Chapters[$i + 1][0] * 1000 } else { $Seconds * 1000 }
        $lines += '[CHAPTER]', 'TIMEBASE=1/1000', "START=$start", "END=$end"
        if ($null -ne $Chapters[$i][1]) { $lines += "title=$($Chapters[$i][1])" }
    }

    $meta = Join-Path $folder 'chapters.ffmeta'
    [IO.File]::WriteAllText($meta, ($lines -join "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))
    $partial = $Target + '.partial.mkv'
    try {
        & $FfmpegPath @('-hide_banner', '-loglevel', 'error', '-nostdin', '-y', '-i', $Source, '-i', $meta, '-map', '0', '-map_metadata', '1', '-map_chapters', '1', '-c', 'copy', '-f', 'matroska', $partial)
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $partial)) { throw "ffmpeg failed to write the chapters of $Target." }
        Move-Item -LiteralPath $partial -Destination $Target -Force
    }
    finally {
        Remove-Item -LiteralPath $meta -ErrorAction SilentlyContinue
    }
}

# ------------------------------------------------------------------------------------------ standard set

$eAcute = [string][char]0x00E9
$amelieAccented = 'Am' + $eAcute + 'lie (2001)'

$movie60 = @{ Width = 640; Height = 360; Seconds = 60 }
$standard = @(
    # Duplicate title and year across two libraries.
    @{ Path = 'Movies\Big Buck Test (2008)\Big Buck Test (2008).mkv'; Clip = $movie60 }
    @{ Path = 'Movies 2\Big Buck Test (2008)\Big Buck Test (2008).mkv'; Clip = $movie60 }
    # Remake pair: same title, different years and different ids.
    @{ Path = 'Movies\Remake Story (1990) [tmdbid-900101]\Remake Story (1990) [tmdbid-900101].mkv'; Clip = $movie60 }
    @{ Path = 'Movies\Remake Story (2015) [tmdbid-900102]\Remake Story (2015) [tmdbid-900102].mkv'; Clip = $movie60 }
    # Remake pair tagged with the same id.
    @{ Path = 'Movies\Same Id Remake (1999) [tmdbid-900201]\Same Id Remake (1999) [tmdbid-900201].mkv'; Clip = $movie60 }
    @{ Path = 'Movies\Same Id Remake (2005) [tmdbid-900201]\Same Id Remake (2005) [tmdbid-900201].mkv'; Clip = $movie60 }
    # Folder-named unidentified item.
    @{ Path = 'Movies\unsorted footage\clip0001.mkv'; Clip = $movie60 }
    # Two-version movie (local alternate versions).
    @{ Path = 'Movies\Versions Movie (2019)\Versions Movie (2019) - 1080p.mkv'; Clip = @{ Width = 1920; Height = 1080; Seconds = 30 } }
    @{ Path = 'Movies\Versions Movie (2019)\Versions Movie (2019) - 720p.mkv'; Clip = @{ Width = 1280; Height = 720; Seconds = 30 } }
    # [tmdbid-] folder.
    @{ Path = 'Movies\Tagged Movie (2012) [tmdbid-900001]\Tagged Movie (2012) [tmdbid-900001].mkv'; Clip = $movie60 }
    # Edition pair: same id, 60 s and 90 s (RelinkPolicy runtime check).
    @{ Path = 'Movies\Edition Movie (2001) [tmdbid-900301]\Edition Movie (2001) [tmdbid-900301].mkv'; Clip = $movie60 }
    @{ Path = 'Movies\Edition Movie Extended (2001) [tmdbid-900301]\Edition Movie Extended (2001) [tmdbid-900301].mkv'; Clip = @{ Width = 640; Height = 360; Seconds = 90 } }
    # Folder vs mixed-folder pairs.
    @{ Path = 'Movies\Test Pattern (2020)\Test Pattern (2020).mkv'; Clip = $movie60 }
    @{ Path = 'Movies 2\Test Pattern.mp4'; Clip = @{ Width = 640; Height = 360; Seconds = 60; Extension = 'mp4' } }
    @{ Path = 'Movies\Blade Runner (1982)\Blade Runner (1982).mkv'; Clip = $movie60 }
    @{ Path = 'Movies 2\Blade Runner 2049.mkv'; Clip = $movie60 }
    # Diacritics pair.
    @{ Path = ('Movies\' + $amelieAccented + '\' + $amelieAccented + '.mkv'); Clip = $movie60 }
    @{ Path = 'Movies 2\Amelie (2001)\Amelie (2001).mkv'; Clip = $movie60 }
    # A longer movie for bookmark and clip tests.
    @{ Path = 'Movies\Long Test Movie (2021)\Long Test Movie (2021).mkv'; Clip = @{ Width = 640; Height = 360; Seconds = 180; Tone = 660 } }
    # A movie with embedded chapters for the name dialog's suggestion: real titles,
    # a ripper's generic "Chapter 4" and an untitled fifth chapter Jellyfin names itself.
    @{ Path = 'Movies\Chaptered Movie (2022)\Chaptered Movie (2022).mkv'; Clip = $movie60; Chapters = @(@(0, 'Opening'), @(12, 'Bank job'), @(24, 'Chase'), @(36, 'Chapter 4'), @(48, $null)) }
    # Two-season series with a provider-id folder tag.
    @{ Path = 'Shows\Test Series (2020) [tvdbid-900401]\Season 01\Test Series (2020) S01E01.mkv'; Clip = @{ Width = 640; Height = 360; Seconds = 30; Tone = 550 } }
    @{ Path = 'Shows\Test Series (2020) [tvdbid-900401]\Season 01\Test Series (2020) S01E02.mkv'; Clip = @{ Width = 640; Height = 360; Seconds = 30; Tone = 550 } }
    @{ Path = 'Shows\Test Series (2020) [tvdbid-900401]\Season 02\Test Series (2020) S02E01.mkv'; Clip = @{ Width = 640; Height = 360; Seconds = 30; Tone = 550 } }
    @{ Path = 'Shows\Test Series (2020) [tvdbid-900401]\Season 02\Test Series (2020) S02E02.mkv'; Clip = @{ Width = 640; Height = 360; Seconds = 30; Tone = 550 } }
    # Library Maintenance's own cases, in their own two libraries so the shared ones stay
    # as Bookmarks' and User Ratings' fixtures expect: a Title + Year pair across two libraries that NFOs give the same
    # TMDb and IMDb ids (Confirmed; the copy in LM Duplicates 2 is rated R, for a parental limit's check), and an edition
    # pair in the separator form the edition rule cuts ("LM Edition Pair - Extended"), the same TMDb id. One poster per pair (a
    # testsrc2 frame); the other copy shows the page's initials.
    @{ Path = 'LM Duplicates\LM Confirmed Pair (2010)\LM Confirmed Pair (2010).mkv'; Clip = @{ Width = 1280; Height = 720; Seconds = 30 }; Poster = $true; Nfo = @{ Title = 'LM Confirmed Pair'; Year = 2010; Tmdb = '900501'; Imdb = 'tt9005010' } }
    @{ Path = 'LM Duplicates 2\LM Confirmed Pair (2010)\LM Confirmed Pair (2010).mkv'; Clip = $movie60; Nfo = @{ Title = 'LM Confirmed Pair'; Year = 2010; Tmdb = '900501'; Imdb = 'tt9005010'; Rating = 'R' } }
    @{ Path = 'LM Duplicates\LM Edition Pair (2003)\LM Edition Pair (2003).mkv'; Clip = $movie60; Poster = $true; Nfo = @{ Title = 'LM Edition Pair'; Year = 2003; Tmdb = '900601' } }
    @{ Path = 'LM Duplicates\LM Edition Pair - Extended (2003)\LM Edition Pair - Extended (2003).mkv'; Clip = @{ Width = 640; Height = 360; Seconds = 90 }; Nfo = @{ Title = 'LM Edition Pair - Extended'; Year = 2003; Tmdb = '900601' } }
    # Home videos.
    @{ Path = 'Home Videos\Birthday 2023\birthday cake.mkv'; Clip = @{ Width = 640; Height = 360; Seconds = 20; Tone = 330 } }
    @{ Path = 'Home Videos\Holiday\beach.mkv'; Clip = @{ Width = 640; Height = 360; Seconds = 20; Tone = 330 } }
)

function Write-MovieNfo {
    <# A Kodi movie NFO next to the video (Jellyfin's local NFO reader runs with the fetchers off): the title, the year,
       the TMDb and IMDb ids, and an MPAA rating when given. #>
    param([string] $Target, [hashtable] $Nfo)
    $escape = { param($text) [Security.SecurityElement]::Escape([string]$text) }
    $lines = @('<?xml version="1.0" encoding="utf-8" standalone="yes"?>', '<movie>', "  <title>$(& $escape $Nfo.Title)</title>", "  <year>$($Nfo.Year)</year>")
    if ($Nfo.ContainsKey('Tmdb')) { $lines += "  <uniqueid type=`"tmdb`" default=`"true`">$(& $escape $Nfo.Tmdb)</uniqueid>" }
    if ($Nfo.ContainsKey('Imdb')) { $lines += "  <uniqueid type=`"imdb`">$(& $escape $Nfo.Imdb)</uniqueid>" }
    if ($Nfo.ContainsKey('Rating')) { $lines += "  <mpaa>$(& $escape $Nfo.Rating)</mpaa>" }
    $lines += '</movie>'
    [IO.File]::WriteAllText($Target, ($lines -join "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))
}

function New-Poster {
    <# A 200x300 poster.jpg (one testsrc2 frame), which Jellyfin's local image provider reads with the fetchers off. #>
    param([string] $Target)
    $partial = $Target + '.partial.jpg'
    & $FfmpegPath @('-hide_banner', '-loglevel', 'error', '-nostdin', '-y', '-f', 'lavfi', '-i', 'testsrc2=size=200x300:rate=1:duration=1', '-frames:v', '1', '-update', '1', '-q:v', '4', $partial)
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $partial)) { throw "ffmpeg failed to create $Target." }
    Move-Item -LiteralPath $partial -Destination $Target -Force
}

$created = 0
foreach ($entry in $standard) {
    $target = Join-Path $MediaRoot $entry.Path
    $folder = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $target) -or $Force) {
        $clip = $entry.Clip
        $extension = if ($clip.ContainsKey('Extension')) { $clip.Extension } else { 'mkv' }
        $tone = if ($clip.ContainsKey('Tone')) { $clip.Tone } else { 440 }
        $source = Get-SourceClip -Width $clip.Width -Height $clip.Height -Seconds $clip.Seconds -Extension $extension -Tone $tone
        New-Item -ItemType Directory -Force -Path $folder | Out-Null
        if ($entry.ContainsKey('Chapters')) {
            Copy-ClipWithChapters -Source $source -Target $target -Chapters $entry.Chapters -Seconds $clip.Seconds
        }
        else {
            Copy-Item -LiteralPath $source -Destination $target -Force
        }

        $created++
    }

    # An NFO or a poster the set gained after the video was made is added on the next run.
    if ($entry.ContainsKey('Nfo')) {
        $nfo = Join-Path $folder ([IO.Path]::GetFileNameWithoutExtension($target) + '.nfo')
        if (-not (Test-Path -LiteralPath $nfo) -or $Force) { Write-MovieNfo -Target $nfo -Nfo $entry.Nfo }
    }

    if ($entry.ContainsKey('Poster')) {
        $poster = Join-Path $folder 'poster.jpg'
        if (-not (Test-Path -LiteralPath $poster) -or $Force) { New-Poster -Target $poster }
    }
}

# Music (User Ratings' lifecycle checks): sine-tone FLAC tracks with tags, in Artist\Album (Year)\NN - Title.flac folders, as a
# music library expects them. Synthetic names and tones only.
$music = @(
    @{ Path = 'Music\Test Artist A\Test Album A (2020)\01 - First Tone.flac'; Artist = 'Test Artist A'; Album = 'Test Album A'; Title = 'First Tone'; Track = 1; Year = 2020; Tone = 262 }
    @{ Path = 'Music\Test Artist A\Test Album A (2020)\02 - Second Tone.flac'; Artist = 'Test Artist A'; Album = 'Test Album A'; Title = 'Second Tone'; Track = 2; Year = 2020; Tone = 330 }
    @{ Path = 'Music\Test Artist B\Test Album B (2021)\01 - Low Tone.flac'; Artist = 'Test Artist B'; Album = 'Test Album B'; Title = 'Low Tone'; Track = 1; Year = 2021; Tone = 196 }
    @{ Path = 'Music\Test Artist B\Test Album B (2021)\02 - High Tone.flac'; Artist = 'Test Artist B'; Album = 'Test Album B'; Title = 'High Tone'; Track = 2; Year = 2021; Tone = 392 }
)

foreach ($track in $music) {
    $target = Join-Path $MediaRoot $track.Path
    if ((Test-Path -LiteralPath $target) -and -not $Force) { continue }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    $partial = $target + '.partial.flac'
    & $FfmpegPath @(
        '-hide_banner', '-loglevel', 'error', '-nostdin', '-y',
        '-f', 'lavfi', '-i', ('sine=frequency={0}:sample_rate=44100:duration=12' -f $track.Tone),
        '-c:a', 'flac', '-ac', '2',
        '-metadata', "artist=$($track.Artist)", '-metadata', "album_artist=$($track.Artist)", '-metadata', "album=$($track.Album)",
        '-metadata', "title=$($track.Title)", '-metadata', "track=$($track.Track)", '-metadata', "date=$($track.Year)",
        '-f', 'flac', $partial
    )
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $partial)) { throw "ffmpeg failed to create $target." }
    Move-Item -LiteralPath $partial -Destination $target -Force
    $created++
}

foreach ($library in 'Movies', 'Movies 2', 'Shows', 'Home Videos', 'Music', 'LM Duplicates', 'LM Duplicates 2') {
    New-Item -ItemType Directory -Force -Path (Join-Path $MediaRoot $library) | Out-Null
}

$setFile = Join-Path $MediaRoot 'media-set.json'
Write-JsonFile -Path $setFile -Value ([ordered]@{
    Version   = 1
    Generated = (Get-Date).ToUniversalTime().ToString('o')
    Files     = [string[]]@(@($standard) + @($music) | ForEach-Object { $_.Path.Replace('\', '/') })
})
Write-Host ("Standard set: {0} files ({1} created) in {2}" -f ($standard.Count + $music.Count), $created, $MediaRoot)

# ------------------------------------------------------------------------------------------ player tracks

# The player's audio and subtitle sheets only show (and only need a choice) with more than one
# track. One movie with two of each, muxed by ffmpeg from two of the clips and two SRT files, the languages and titles
# set per stream. Its own folder and library (Initialize-Sandbox.ps1 -PlayerTracks), so the standard libraries' counts
# stay as every plugin's checks expect.
$multiTrack = Join-Path $MediaRoot 'Player Tracks\Multi Track (2021)\Multi Track (2021).mkv'
if (-not (Test-Path -LiteralPath $multiTrack) -or $Force) {
    $english = Get-SourceClip -Width 640 -Height 360 -Seconds 30 -Tone 440
    $french = Get-SourceClip -Width 640 -Height 360 -Seconds 30 -Tone 660
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $subtitles = @()
    foreach ($language in @(@('eng', 'English line'), @('fre', 'Ligne en francais'))) {
        $srt = Join-Path $sources ('multi-track-{0}.srt' -f $language[0])
        [IO.File]::WriteAllText($srt, "1`n00:00:00,000 --> 00:00:30,000`n$($language[1])`n", $utf8)
        $subtitles += $srt
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $multiTrack) | Out-Null
    $partial = $multiTrack + '.partial.mkv'
    & $FfmpegPath @('-hide_banner', '-loglevel', 'error', '-nostdin', '-y',
        '-i', $english, '-i', $french, '-i', $subtitles[0], '-i', $subtitles[1],
        '-map', '0:v', '-map', '0:a', '-map', '1:a', '-map', '2:s', '-map', '3:s',
        '-c:v', 'copy', '-c:a', 'copy', '-c:s', 'srt',
        '-metadata:s:a:0', 'language=eng', '-metadata:s:a:0', 'title=English',
        '-metadata:s:a:1', 'language=fre', '-metadata:s:a:1', 'title=French',
        '-metadata:s:s:0', 'language=eng', '-metadata:s:s:0', 'title=English',
        '-metadata:s:s:1', 'language=fre', '-metadata:s:s:1', 'title=French',
        '-f', 'matroska', $partial)
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $partial)) { throw "ffmpeg failed to create $multiTrack." }
    Move-Item -LiteralPath $partial -Destination $multiTrack -Force
    Write-Host "Player tracks: $multiTrack"
}

# ------------------------------------------------------------------------------------------ synthetic seed

if ($SyntheticMovies -gt 0 -or $SyntheticEpisodes -gt 0) {
    if ($SyntheticMode -eq 'HardLink' -and -not ('JfSandbox.NativeMethods' -as [type])) {
        Add-Type -Namespace JfSandbox -Name NativeMethods -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
'@
    }

    $random = New-Object System.Random($SyntheticSeed)
    $adjectives = @('Silent', 'Crimson', 'Hidden', 'Broken', 'Golden', 'Last', 'Frozen', 'Distant', 'Wild', 'Quiet',
        'Burning', 'Endless', 'Lost', 'Midnight', 'Electric', 'Hollow', 'Iron', 'Paper', 'Velvet', 'Winter',
        'Northern', 'Secret', 'Shattered', 'Bright', 'Dark', 'Final', 'First', 'Little', 'Lonely', 'Neon')
    $nouns = @('River', 'Harbor', 'Garden', 'Signal', 'Empire', 'Mirror', 'Station', 'Orchard', 'Horizon', 'Canyon',
        'Lighthouse', 'Machine', 'Kingdom', 'Letter', 'Island', 'Circus', 'Voyage', 'Frontier', 'Promise', 'Shadow',
        'Engine', 'Forest', 'Tower', 'Echo', 'Winter', 'Summer', 'Harvest', 'Comet', 'Anthem', 'Bridge')

    function New-SyntheticTitle {
        switch ($random.Next(4)) {
            0 { return 'The ' + $adjectives[$random.Next($adjectives.Count)] + ' ' + $nouns[$random.Next($nouns.Count)] }
            1 { return $nouns[$random.Next($nouns.Count)] + ' of the ' + $nouns[$random.Next($nouns.Count)] }
            2 { return $adjectives[$random.Next($adjectives.Count)] + ' ' + $nouns[$random.Next($nouns.Count)] }
            default { return $adjectives[$random.Next($adjectives.Count)] + ' ' + $nouns[$random.Next($nouns.Count)] + ' ' + (2 + $random.Next(4)) }
        }
    }

    $linkState = @{ Source = $null; Count = 0; Generation = 0 }
    $baseClip = $null
    if ($SyntheticMode -eq 'HardLink') { $baseClip = Get-SourceClip -Width 320 -Height 180 -Seconds 2 -Tone 440 }

    function New-SyntheticFile {
        param([string] $Path)
        if (Test-Path -LiteralPath $Path) { return $false }
        [System.IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
        if ($SyntheticMode -eq 'Stub') {
            [System.IO.File]::WriteAllBytes($Path, [byte[]]@())
            return $true
        }

        if (-not $linkState.Source -or $linkState.Count -ge 1000) {
            $linkState.Generation++
            $linkState.Source = Join-Path $sources ('synthetic-link-source-{0:D4}.mkv' -f $linkState.Generation)
            if (-not (Test-Path -LiteralPath $linkState.Source)) { Copy-Item -LiteralPath $baseClip -Destination $linkState.Source }
            $linkState.Count = 0
        }

        if (-not [JfSandbox.NativeMethods]::CreateHardLink($Path, $linkState.Source, [IntPtr]::Zero)) {
            $code = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
            if ($code -eq 1142) {
                # ERROR_TOO_MANY_LINKS: this source is full (earlier runs linked it too); move on to the next one.
                $linkState.Count = 1000
                return (New-SyntheticFile -Path $Path)
            }

            throw "CreateHardLink failed for $Path (Win32 error $code)."
        }

        $linkState.Count++
        return $true
    }

    function Get-SafeName([string] $Name) {
        return ($Name -replace '[<>:"/\\|?*]', '').Trim()
    }

    if ($SyntheticMovies -gt 0) {
        $movieRoot = Join-Path $MediaRoot 'Synthetic'
        $movies = New-Object System.Collections.Generic.List[object]
        $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        $planted = 0
        $made = 0
        for ($i = 0; $i -lt $SyntheticMovies; $i++) {
            if ($i % 20 -eq 19 -and $movies.Count -gt 0) {
                # About 5 %: a planted duplicate or variant of an earlier movie.
                $original = $movies[$random.Next($movies.Count)]
                $title = $original.Title
                $year = $original.Year
                switch ($planted % 5) {
                    0 { }                                                              # same title and year
                    1 { $year = $original.Year + 5 + $random.Next(25) }                # remake
                    2 { $title = $original.Title.ToUpperInvariant() }                  # case
                    3 { $title = $original.Title -replace 'e', $eAcute }               # accents
                    default {
                        if ($original.Title.StartsWith('The ')) { $title = $original.Title.Substring(4) + ', The' } else { $title = 'The ' + $original.Title }
                    }
                }

                $planted++
            }
            else {
                do {
                    $title = New-SyntheticTitle
                    $year = 1950 + $random.Next(76)
                } while (-not $seen.Add("$title|$year"))
            }

            $movies.Add([pscustomobject]@{ Title = $title; Year = $year })
            $name = Get-SafeName ('{0} ({1})' -f $title, $year)
            # Planted items get a folder of their own, so an exact duplicate never shares its original's path.
            $bucket = if ($i % 20 -eq 19) { 'variants\{0:D6}' -f $i } else { '{0:D3}' -f [int][Math]::Floor($i / 100) }
            $file = Join-Path $movieRoot (Join-Path $bucket (Join-Path $name ($name + '.mkv')))
            if (New-SyntheticFile -Path $file) { $made++ }
        }

        Write-JsonFile -Path (Join-Path $movieRoot 'seed.json') -Value ([ordered]@{ Movies = $SyntheticMovies; Planted = $planted; Mode = $SyntheticMode; Seed = $SyntheticSeed })
        Write-Host ("Synthetic movies: {0} ({1} created, {2} planted duplicates or variants) in {3}" -f $SyntheticMovies, $made, $planted, $movieRoot)
    }

    if ($SyntheticEpisodes -gt 0) {
        $tvRoot = Join-Path $MediaRoot 'SyntheticTV'
        $showCount = [Math]::Min(200, $SyntheticEpisodes)
        $shows = New-Object System.Collections.Generic.List[string]
        $seenShows = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        while ($shows.Count -lt $showCount) {
            $name = Get-SafeName ('{0} ({1})' -f (New-SyntheticTitle), (1980 + $random.Next(46)))
            if ($seenShows.Add($name)) { $shows.Add($name) }
        }

        $made = 0
        for ($i = 0; $i -lt $SyntheticEpisodes; $i++) {
            $show = $shows[$i % $showCount]
            $index = [int][Math]::Floor($i / $showCount)        # episode index within the show
            $season = [int][Math]::Floor($index / 20) + 1
            $episode = ($index % 20) + 1
            $file = Join-Path $tvRoot (Join-Path $show (Join-Path ('Season {0:D2}' -f $season) ('{0} S{1:D2}E{2:D2}.mkv' -f $show, $season, $episode)))
            if (New-SyntheticFile -Path $file) { $made++ }
        }

        Write-JsonFile -Path (Join-Path $tvRoot 'seed.json') -Value ([ordered]@{ Episodes = $SyntheticEpisodes; Shows = $showCount; Mode = $SyntheticMode; Seed = $SyntheticSeed })
        Write-Host ("Synthetic episodes: {0} across {1} shows ({2} created) in {3}" -f $SyntheticEpisodes, $showCount, $made, $tvRoot)
    }
}

Write-Host ("Done in {0:N1} s." -f ((Get-Date) - $started).TotalSeconds)
