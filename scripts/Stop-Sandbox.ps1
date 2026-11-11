<#
.SYNOPSIS
Stops a sandbox server started by dev-server.ps1, and any headless browser webcheck.mjs left behind for it.

.DESCRIPTION
Reads the PID from .sandbox\<Name>\server.pid and stops only that process, after checking that it is
jellyfin.exe and that its --datadir is the sandbox's data folder. It first asks the server to shut down
through the API (POST /System/Shutdown with the sandbox admin token, only on the sandbox's own loopback port
and only when that port belongs to the recorded PID), and forcibly stops the process only if it is still
running after -TimeoutSec. Whether the process has gone is polled (Wait-ProcessExit, for up to -ExitTimeoutSec
more before it reports a failure), never read once: right after a graceful shutdown the server was sometimes still
listed for a moment. A process started at another time under the same PID counts as gone.

Browser processes are stopped only when their command line contains this sandbox's browser profile folder
(.sandbox\<Name>\browser). Nothing is ever stopped by process name.

.EXAMPLE
./scripts/Stop-Sandbox.ps1 -Name default
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Name,

    [int] $TimeoutSec = 45,

    [int] $ExitTimeoutSec = 30
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'JellyfinSandbox.psm1') -Force

$sandbox = Get-Sandbox -Name $Name
$process = Get-SandboxServerProcess -Sandbox $sandbox

if (-not $process) {
    Write-Host "Sandbox '$Name' is not running."
}
else {
    $processId = [int]$process.ProcessId
    # When this server started: a process started at another time under the same PID is not it.
    $startTime = $process.CreationDate
    $stopped = $false

    # Graceful shutdown through the API when we can prove the port is this process's.
    $state = $sandbox.State
    if ($state -and $state.PSObject.Properties['Port'] -and (Test-Path -LiteralPath $sandbox.CredentialsFile)) {
        try {
            $port = [int]$state.Port
            if ((Get-ListeningProcessIds -Port $port) -contains $processId) {
                $credentials = Read-JsonFile $sandbox.CredentialsFile
                $token = $credentials.users.admin.token
                if ($token) {
                    Invoke-SandboxApi -BaseUrl (Get-SandboxBaseUrl -Sandbox $sandbox) -Path 'System/Shutdown' -Method POST -Token $token -TimeoutSec 10 | Out-Null
                    $stopped = Wait-ProcessExit -ProcessId $processId -StartTime $startTime -TimeoutSec $TimeoutSec
                }
            }
        }
        catch {
            Write-Warning "Graceful shutdown failed: $($_.Exception.Message)"
        }
    }

    if (-not $stopped -and (Test-ProcessRunning -ProcessId $processId -StartTime $startTime)) {
        # Check the command line again right before stopping it. Gone meanwhile is fine.
        $again = Get-CimInstance Win32_Process -Filter "ProcessId = $processId" -ErrorAction SilentlyContinue
        if ($again -and (Test-SandboxServerCommandLine -Process $again -Sandbox $sandbox)) {
            Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
        }
        elseif ($again -and (Test-ProcessRunning -ProcessId $processId -StartTime $startTime)) {
            throw "PID $processId is no longer this sandbox's server; not stopping it."
        }
    }

    # Polled, not read once ("still running" right after a graceful shutdown in 2 of about 14 stops,
    # the process gone a moment later).
    if (-not (Wait-ProcessExit -ProcessId $processId -StartTime $startTime -TimeoutSec $ExitTimeoutSec)) {
        throw "Sandbox '$Name' (PID $processId) is still running $ExitTimeoutSec s after it was asked to stop."
    }
    Write-Host "Stopped sandbox '$Name' (PID $processId)."
}

if (Test-Path -LiteralPath $sandbox.PidFile) { Remove-Item -LiteralPath $sandbox.PidFile -Force }
if ($sandbox.State -and $sandbox.State.PSObject.Properties['Pid']) {
    $sandbox.State.Pid = $null
    Write-JsonFile -Path $sandbox.StateFile -Value $sandbox.State
}

# Headless browsers of this sandbox that webcheck.mjs could not close (for example after a crash).
$browserMarker = (Resolve-FullPath $sandbox.BrowserDir) + '\'
$strays = @(Get-CimInstance Win32_Process -Filter "Name = 'chrome.exe' or Name = 'msedge.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($browserMarker, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 })
foreach ($stray in $strays) {
    if (Stop-VerifiedProcess -ProcessId $stray.ProcessId -Marker $browserMarker) { Write-Host "Stopped stray browser process $($stray.ProcessId)." }
}
