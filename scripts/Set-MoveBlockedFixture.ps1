<#
.SYNOPSIS
Sets up, shows and removes what librarymaintenance-move-blocked.json needs in the move sandbox's media: two junctions,
and a file another process holds open.

.DESCRIPTION
-Action Setup
  Records the last-write times of the links' parent folder (Movies), makes two empty target folders under
  .sandbox\move-blocked, and the junctions
    .sandbox\move-media\Movies\Link Folder
    .sandbox\move-media\Movies\Plain Loose (2005).trickplay
  to them (mklink /J). Then it starts a hidden child PowerShell that opens
    .sandbox\move-media\Movies\Tagged Loose (2003) [tmdbid-910003].mkv
  for reading (FileShare.Read: another reader is fine, an exclusive open is not, as with a player), holds it for
  -HoldSeconds (120 by default, at most 600), closes it and exits by itself. Setup returns once the child has the file
  open; run the webcheck straight after, as its "Check again" step waits for the file to be closed.

-Action Remove
  Stops the holder if it is still running (only the recorded PID with its recorded start time), removes each junction
  with rmdir without /s (the link only: what is in its target is never touched), then each target folder if it is empty
  (one with anything in it stays, and is named), and puts the parent folder's last-write time back, to the 100 ns.
  Making and removing a link changes that time, and the move server's watcher refreshes the library 60 s later. .NET's
  own setter meets a sharing violation from Jellyfin's watcher, so this takes CreateFileW with FILE_WRITE_ATTRIBUTES,
  every share mode and FILE_FLAG_BACKUP_SEMANTICS, then SetFileTime. A path that is not the junction Setup made (a
  real folder, or a link to elsewhere) is left alone and named.

-Action Status
  What is there now: each link and where it leads, the holder, and the parent's last-write time against the recorded one.

Every link, target, the held file and the state folder must be inside this checkout's .sandbox folder, on a local drive,
also after the junctions and links on the way are followed. A share or device path (\\server\share, \\?\..., \\.\...)
is refused before anything is read or made, and so is anything under C:\ProgramData\Jellyfin. Nothing outside
.sandbox is ever changed, and no network path is opened.

.EXAMPLE
./scripts/Set-MoveBlockedFixture.ps1 -Action Setup
node scripts/webcheck.mjs --sandbox move --steps scripts/webchecks/librarymaintenance-move-blocked.json --out result.json --fail-on-exception
./scripts/Set-MoveBlockedFixture.ps1 -Action Remove
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('Setup', 'Remove', 'Status')]
    [string] $Action,

    [ValidateRange(5, 600)]
    [int] $HoldSeconds = 120,

    # The move sandbox's media folder; it must be inside .sandbox.
    [string] $MediaRoot
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'JellyfinSandbox.psm1') -Force

$sandboxRoot = Resolve-FullPath (Get-SandboxRoot)
if (-not (Test-Path -LiteralPath $sandboxRoot -PathType Container)) { throw "There is no $sandboxRoot." }
$sandboxFinal = ConvertFrom-DevicePath ([string](Get-FinalPath $sandboxRoot))
if (-not $sandboxFinal -or $sandboxFinal -notmatch '^[A-Za-z]:\\') { throw "Refusing to continue: $sandboxRoot does not lead to a folder on a local drive." }

function Assert-InSandboxFolder {
    <# The full path, when it is strictly inside .sandbox on a local drive, also as Windows resolves it; it throws otherwise. #>
    param([Parameter(Mandatory = $true)] [string] $Path, [Parameter(Mandatory = $true)] [string] $What)
    if ($Path -match '^[\\/]{2}') { throw "Refusing to continue: the $What ($Path) is a share or device path, not a folder inside $sandboxRoot." }
    $full = Resolve-FullPath $Path
    if ($full -match '^[\\/]{2}' -or -not (Test-LocalDrivePath $full)) { throw "Refusing to continue: the $What ($full) is not on a local drive." }
    if ($full -ieq $sandboxRoot -or -not (Test-PathUnder $full $sandboxRoot)) { throw "Refusing to continue: the $What ($full) is not inside $sandboxRoot." }
    if (Test-ProtectedPath $full) { throw "Refusing to continue: the $What ($full) is under C:\ProgramData\Jellyfin." }
    if ($full -match '["%]') { throw "Refusing to continue: the $What ($full) has a character cmd would read (a quote or %)." }

    # Where Windows really puts it: the deepest existing folder on the way, junctions and links followed.
    $existing = $full
    while ($existing -and -not (Test-Path -LiteralPath $existing)) {
        $parent = Split-Path -Parent $existing
        if (-not $parent -or $parent -eq $existing) { $existing = $null; break }
        $existing = $parent
    }

    if (-not $existing) { throw "Refusing to continue: nothing of the $What ($full) exists." }
    $final = Get-FinalPath $existing
    if (-not $final) { throw "Refusing to continue: where the $What ($full) leads can't be read." }
    $final = ConvertFrom-DevicePath ([string]$final)
    if ($final -match '^[\\/]{2}' -or $final -notmatch '^[A-Za-z]:\\') { throw "Refusing to continue: the $What ($full) leads to $final, not a local drive." }
    if (-not (Test-PathUnder $final $sandboxFinal)) { throw "Refusing to continue: the $What ($full) leads to $final, outside $sandboxFinal." }
    return $full
}

function Get-FolderLastWrite {
    param([Parameter(Mandatory = $true)] [string] $Path)
    return [System.IO.Directory]::GetLastWriteTimeUtc($Path).ToFileTimeUtc()
}

function Set-FolderLastWrite {
    <# Puts a folder's last-write time back (a FILETIME, UTC), with every share mode, past Jellyfin's watcher. #>
    param([Parameter(Mandatory = $true)] [string] $Path, [Parameter(Mandatory = $true)] [long] $FileTime)
    if (-not ('MoveBlockedFixture.FolderTime' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MoveBlockedFixture
{
    public static class FolderTime
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileTime(SafeFileHandle handle, IntPtr creation, IntPtr lastAccess, ref long lastWrite);

        public static void SetLastWrite(string path, long fileTime)
        {
            // FILE_WRITE_ATTRIBUTES only, share read/write/delete, OPEN_EXISTING,
            // FILE_FLAG_BACKUP_SEMANTICS (a folder) | FILE_FLAG_OPEN_REPARSE_POINT (never through a link).
            using (SafeFileHandle handle = CreateFileW(path, 0x100, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero))
            {
                if (handle.IsInvalid) { throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFileW " + path); }
                if (!SetFileTime(handle, IntPtr.Zero, IntPtr.Zero, ref fileTime)) { throw new Win32Exception(Marshal.GetLastWin32Error(), "SetFileTime " + path); }
            }
        }
    }
}
'@
    }

    [MoveBlockedFixture.FolderTime]::SetLastWrite($Path, $FileTime)
}

function Test-IsLink {
    param([Parameter(Mandatory = $true)] [string] $Path)
    $item = Get-Item -LiteralPath $Path -Force
    return [bool]($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)
}

function Get-Holder {
    <# The recorded holder process, if it is still the one Setup started (same PID, same start time). #>
    param($Held)
    if (-not $Held -or -not $Held.ProcessId) { return $null }
    $process = Get-Process -Id ([int]$Held.ProcessId) -ErrorAction SilentlyContinue
    if (-not $process) { return $null }
    try { $started = $process.StartTime.ToUniversalTime().Ticks } catch { return $null }
    if ($started -ne [long]::Parse([string]$Held.StartTicks)) { return $null }
    return $process
}

if ($MediaRoot) { $media = Assert-InSandboxFolder -Path $MediaRoot -What 'media folder' }
else { $media = Assert-InSandboxFolder -Path (Join-Path $sandboxRoot 'move-media') -What 'media folder' }
$movies = Assert-InSandboxFolder -Path (Join-Path $media 'Movies') -What 'Movies folder'
$stateDir = Assert-InSandboxFolder -Path (Join-Path $sandboxRoot 'move-blocked') -What 'state folder'
$stateFile = Join-Path $stateDir 'state.json'
$readyFile = Join-Path $stateDir 'held.ready'
$closedFile = Join-Path $stateDir 'held.closed'
$plan = @(
    [ordered]@{ Link = Join-Path $movies 'Link Folder'; Target = Join-Path $stateDir 'link-folder' }
    [ordered]@{ Link = Join-Path $movies 'Plain Loose (2005).trickplay'; Target = Join-Path $stateDir 'plain-loose-trickplay' }
)
$heldFile = Join-Path $movies 'Tagged Loose (2003) [tmdbid-910003].mkv'

function Invoke-Remove {
    param($State)
    $problems = New-Object System.Collections.Generic.List[string]

    $holder = Get-Holder $State.Held
    if ($holder) {
        Stop-Process -Id $holder.Id -Force
        $holder.WaitForExit(10000) | Out-Null
        Write-Host "Stopped the holder (PID $($holder.Id)); the file is closed."
    }
    elseif ($State.Held) {
        Write-Host 'The holder had already closed the file and exited.'
    }

    foreach ($entry in @($State.Links)) {
        $link = Assert-InSandboxFolder -Path ([string]$entry.Link) -What 'link'
        $target = Assert-InSandboxFolder -Path ([string]$entry.Target) -What 'link target'
        if (Test-Path -LiteralPath $link) {
            if (-not (Test-IsLink $link)) {
                $problems.Add("$link is a real folder, not the junction Setup made: left alone.")
            }
            else {
                $leadsTo = ConvertFrom-DevicePath ([string](Get-FinalPath $link))
                $targetFinal = $null
                if (Test-Path -LiteralPath $target) { $targetFinal = ConvertFrom-DevicePath ([string](Get-FinalPath $target)) }
                if (-not $targetFinal -or $leadsTo -ine $targetFinal) {
                    $problems.Add("$link leads to $leadsTo, not to ${target}: left alone.")
                }
                else {
                    # rmdir without /s removes the junction only, never what is in its target.
                    & cmd.exe /d /v:off /c rmdir "$link"
                    if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $link)) { $problems.Add("rmdir could not remove the junction $link.") }
                    else { Write-Host "Removed the junction $link (the link only)." }
                }
            }
        }

        if (Test-Path -LiteralPath $target) {
            if (Test-IsLink $target) { $problems.Add("$target is a link itself: left alone.") }
            elseif (@([System.IO.Directory]::GetFileSystemEntries($target)).Count -gt 0) { $problems.Add("$target is not empty: left as it is.") }
            else { [System.IO.Directory]::Delete($target, $false) }
        }
    }

    foreach ($parent in @($State.Parents)) {
        $path = Assert-InSandboxFolder -Path ([string]$parent.Path) -What 'parent folder'
        $recorded = [long]::Parse([string]$parent.LastWrite)
        $before = Get-FolderLastWrite $path
        if ($before -ne $recorded) { Set-FolderLastWrite -Path $path -FileTime $recorded }
        $after = Get-FolderLastWrite $path
        if ($after -ne $recorded) { $problems.Add("$path's last-write time is $([DateTime]::FromFileTimeUtc($after).ToString('o')), not the recorded $([DateTime]::FromFileTimeUtc($recorded).ToString('o')).") }
        else { Write-Host "$path's last-write time is the recorded one again ($([DateTime]::FromFileTimeUtc($recorded).ToString('o')))." }
    }

    if ($problems.Count -gt 0) {
        foreach ($problem in $problems) { Write-Warning $problem }
        throw "Remove left $($problems.Count) thing(s) as they are; the state is kept in $stateFile."
    }

    foreach ($file in $readyFile, $closedFile, $stateFile) {
        if (Test-Path -LiteralPath $file) { [System.IO.File]::Delete($file) }
    }

    if (Test-Path -LiteralPath $stateDir) {
        if (@([System.IO.Directory]::GetFileSystemEntries($stateDir)).Count -eq 0) { [System.IO.Directory]::Delete($stateDir, $false) }
        else { Write-Warning "$stateDir has other files in it: left as it is." }
    }

    Write-Host 'The blocked fixture is removed.'
}

switch ($Action) {
    'Status' {
        if (Test-Path -LiteralPath $stateFile) { $state = Read-JsonFile $stateFile } else { $state = $null }
        if (-not $state) { Write-Host "No fixture recorded ($stateFile is not there)." }
        foreach ($entry in $plan) {
            $link = $entry.Link
            if (-not (Test-Path -LiteralPath $link)) { Write-Host "$link`: not there"; continue }
            if (Test-IsLink $link) { Write-Host "$link`: a link to $(ConvertFrom-DevicePath ([string](Get-FinalPath $link)))" }
            else { Write-Host "$link`: a real folder" }
        }

        if ($state) {
            if (Get-Holder $state.Held) { Write-Host "The holder (PID $($state.Held.ProcessId)) has $heldFile open." }
            elseif (Test-Path -LiteralPath $closedFile) { Write-Host "The holder closed the file at $([System.IO.File]::ReadAllText($closedFile)) and exited." }
            else { Write-Host 'The holder is not running.' }
            foreach ($parent in @($state.Parents)) {
                $now = Get-FolderLastWrite ([string]$parent.Path)
                $recorded = [long]::Parse([string]$parent.LastWrite)
                $same = if ($now -eq $recorded) { 'the recorded one' } else { "recorded $([DateTime]::FromFileTimeUtc($recorded).ToString('o'))" }
                Write-Host "$($parent.Path): last written $([DateTime]::FromFileTimeUtc($now).ToString('o')) ($same)"
            }
        }
    }

    'Remove' {
        if (-not (Test-Path -LiteralPath $stateFile)) {
            foreach ($entry in $plan) {
                if (Test-Path -LiteralPath $entry.Link) { Write-Warning "$($entry.Link) is there, but no fixture is recorded: left alone." }
            }

            Write-Host "No fixture recorded ($stateFile is not there); nothing removed."
            return
        }

        Invoke-Remove -State (Read-JsonFile $stateFile)
    }

    'Setup' {
        if (Test-Path -LiteralPath $stateFile) { throw "A fixture is already recorded in $stateFile; run -Action Remove first." }
        if (-not (Test-Path -LiteralPath $movies -PathType Container)) { throw "There is no $movies; is the move sandbox's media there?" }
        if (Test-IsLink $movies) { throw "Refusing to continue: $movies is a link." }
        if (-not (Test-Path -LiteralPath $heldFile -PathType Leaf)) { throw "There is no $heldFile." }
        $null = Assert-InSandboxFolder -Path $heldFile -What 'held file'
        foreach ($entry in $plan) {
            if (Test-Path -LiteralPath $entry.Link) { throw "$($entry.Link) is already there; it is not this script's to replace." }
            if (Test-Path -LiteralPath $entry.Target) { throw "$($entry.Target) is already there." }
            $null = Assert-InSandboxFolder -Path $entry.Link -What 'link'
            $null = Assert-InSandboxFolder -Path $entry.Target -What 'link target'
        }

        if (-not (Test-Path -LiteralPath $stateDir)) { New-Item -ItemType Directory -Path $stateDir | Out-Null }
        $state = [ordered]@{
            Created = [DateTime]::UtcNow.ToString('o')
            Parents = @([ordered]@{ Path = $movies; LastWrite = [string](Get-FolderLastWrite $movies) })
            Links   = @()
            Held    = $null
        }
        Write-JsonFile -Path $stateFile -Value $state

        try {
            foreach ($entry in $plan) {
                $target = Assert-InSandboxFolder -Path $entry.Target -What 'link target'
                New-Item -ItemType Directory -Path $target | Out-Null
                $state.Links = @($state.Links) + @([ordered]@{ Link = $entry.Link; Target = $target })
                Write-JsonFile -Path $stateFile -Value $state
                $link = Assert-InSandboxFolder -Path $entry.Link -What 'link'
                & cmd.exe /d /v:off /c mklink /J "$link" "$target" | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "mklink /J could not make $link." }
                $leadsTo = ConvertFrom-DevicePath ([string](Get-FinalPath $link))
                if (-not (Test-IsLink $link) -or $leadsTo -ine (ConvertFrom-DevicePath ([string](Get-FinalPath $target)))) { throw "$link is not a junction to $target." }
                Write-Host "Made the junction $link -> $target"
            }

            # The holder: a child PowerShell with the file open for reading, which closes it and exits by itself.
            $quote = { param($text) "'" + ($text -replace "'", "''") + "'" }
            $child = @"
`$ErrorActionPreference = 'Stop'
`$stream = [System.IO.File]::Open($(& $quote $heldFile), [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
try {
    [System.IO.File]::WriteAllText($(& $quote $readyFile), [DateTime]::UtcNow.ToString('o'))
    Start-Sleep -Seconds $HoldSeconds
}
finally {
    `$stream.Dispose()
    [System.IO.File]::WriteAllText($(& $quote $closedFile), [DateTime]::UtcNow.ToString('o'))
}
"@
            $encoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($child))
            $hostPath = (Get-Process -Id $PID).Path
            $holder = Start-Process -FilePath $hostPath -ArgumentList '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded -WindowStyle Hidden -PassThru
            $state.Held = [ordered]@{ File = $heldFile; Seconds = $HoldSeconds; ProcessId = $holder.Id; StartTicks = [string]$holder.StartTime.ToUniversalTime().Ticks }
            Write-JsonFile -Path $stateFile -Value $state

            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            while (-not (Test-Path -LiteralPath $readyFile)) {
                if ($holder.HasExited) { throw "The holder exited (code $($holder.ExitCode)) before it had the file open." }
                if ([DateTime]::UtcNow -gt $deadline) { throw 'The holder did not open the file within 30 s.' }
                Start-Sleep -Milliseconds 100
            }

            Write-Host "The holder (PID $($holder.Id)) has $heldFile open for $HoldSeconds s, until about $([DateTime]::UtcNow.AddSeconds($HoldSeconds).ToString('HH:mm:ss')) UTC."
            Write-Host 'Run the blocked webcheck now; afterwards, -Action Remove.'
        }
        catch {
            Write-Warning "Setup failed: $($_.Exception.Message); undoing it."
            Invoke-Remove -State (Read-JsonFile $stateFile)
            throw
        }
    }
}
