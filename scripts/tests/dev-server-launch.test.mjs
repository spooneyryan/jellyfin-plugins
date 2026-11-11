// node --test scripts/tests/dev-server-launch.test.mjs (run by tests/Repo.Tests ScriptsTests). A driver
// trap: dev-server.ps1 -Background started jellyfin.exe with Start-Process -RedirectStandardOutput, which lets the server
// inherit every inheritable handle of the PowerShell process, so a caller that started the script with piped output
// (Node's spawnSync, as the probes do) waited on its pipe until the server exited. JellyfinSandbox.psm1's
// Start-ProcessWithLogs hands the program only its two log files and NUL. Each case starts a 15 s child (cmd.exe and
// ping to 127.0.0.1, nothing else) from a PowerShell that spawnSync runs with pipes, then looks at whether spawnSync came
// back while the child still ran; the child is stopped afterwards by its id.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const windows = process.platform === 'win32';
const module = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'JellyfinSandbox.psm1');
const quote = (s) => "'" + s.replace(/'/g, "''") + "'";

function running(pid) {
    const r = spawnSync('tasklist.exe', ['/FI', `PID eq ${pid}`, '/FO', 'CSV', '/NH'], { encoding: 'utf8', windowsHide: true });
    return (r.stdout || '').includes(`"${pid}"`);
}

function stop(pid) {
    if (pid && running(pid)) {
        spawnSync('taskkill.exe', ['/PID', String(pid), '/T', '/F'], { windowsHide: true });
    }
}

// Runs a PowerShell script that starts the child one way and prints its id, with piped output as a driver would.
function launch(dir, how) {
    const out = path.join(dir, `${how}.out`);
    const err = path.join(dir, `${how}.err`);
    const child = "'/d /c \"echo env=%JFP_LAUNCH_TEST%& echo to-stderr 1>&2& ping -n 16 127.0.0.1 >nul\"'";
    const start = how === 'logs'
        ? `$p = Start-ProcessWithLogs -FilePath $cmd -Arguments ${child} -WorkingDirectory ${quote(dir)} -StdOut ${quote(out)} -StdErr ${quote(err)}`
        : `$p = Start-Process -FilePath $cmd -ArgumentList ${child} -WorkingDirectory ${quote(dir)} -WindowStyle Hidden -PassThru -RedirectStandardOutput ${quote(out)} -RedirectStandardError ${quote(err)}`;
    const script = path.join(dir, `${how}.ps1`);
    fs.writeFileSync(script, [
        "$ErrorActionPreference = 'Stop'",
        `Import-Module ${quote(module)} -Force`,
        "$cmd = Join-Path $env:SystemRoot 'System32\\cmd.exe'",
        "$p = Invoke-WithChildEnvironment -Variables @([pscustomobject]@{ Name = 'JFP_LAUNCH_TEST'; Value = 'from-the-script' }) -Action {",
        `    ${start}`,
        '    $p',
        '}',
        "Write-Output ('pid=' + $p.Id)",
        ''
    ].join('\r\n'));
    const t = Date.now();
    const r = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', script], { stdio: ['pipe', 'pipe', 'pipe'], encoding: 'utf8', timeout: 60000, windowsHide: true });
    const seconds = (Date.now() - t) / 1000;
    const pid = Number((/pid=(\d+)/.exec(r.stdout || '') || [])[1]) || null;
    return { r, seconds, pid, alive: pid ? running(pid) : false, out, err };
}

test('Start-ProcessWithLogs: the caller gets its pipes back at once; the child has its log files, NUL and the environment', { skip: !windows }, async (t) => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'jfp-launch-'));
    let pid = null;
    try {
        const run = launch(dir, 'logs');
        pid = run.pid;
        t.diagnostic(`spawnSync came back after ${run.seconds} s, the child ${run.alive ? 'still running' : 'gone'}`);
        assert.equal(run.r.status, 0, (run.r.stderr || '') + (run.r.error ? run.r.error.message : ''));
        assert.ok(pid, run.r.stdout);
        assert.ok(run.alive, `spawnSync came back after the child had exited (${run.seconds} s): it waited on an inherited pipe`);
        assert.ok(run.seconds < 15, `${run.seconds} s`);
        // The child's own output went to its files (it writes them before its ping), with the script's variable.
        for (let i = 0; i < 50 && !(fs.existsSync(run.err) && fs.readFileSync(run.err, 'utf8').includes('to-stderr')); i++) {
            await new Promise((resolve) => setTimeout(resolve, 100));
        }
        assert.match(fs.readFileSync(run.out, 'utf8'), /env=from-the-script/);
        assert.match(fs.readFileSync(run.err, 'utf8'), /to-stderr/);
        // Readable while the child holds them (dev-server's log checks read them as the server runs).
        assert.ok(running(pid), 'the child still runs');
    } finally {
        stop(pid);
        fs.rmSync(dir, { recursive: true, force: true });
    }
});

test('the trap itself: Start-Process -RedirectStandardOutput keeps a piped caller waiting until the child exits', { skip: !windows }, (t) => {
    // The negative control, so this file tells the two apart on the machine it runs on.
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'jfp-launch-'));
    let pid = null;
    try {
        const run = launch(dir, 'start-process');
        pid = run.pid;
        t.diagnostic(`spawnSync came back after ${run.seconds} s, the child ${run.alive ? 'still running' : 'gone'}`);
        assert.equal(run.r.status, 0, run.r.stderr || '');
        assert.ok(pid, run.r.stdout);
        assert.equal(run.alive, false, `spawnSync came back after ${run.seconds} s with the child still running`);
        assert.ok(run.seconds >= 14, `${run.seconds} s`);
    } finally {
        stop(pid);
        fs.rmSync(dir, { recursive: true, force: true });
    }
});

test("dev-server.ps1 -Background starts jellyfin.exe with Start-ProcessWithLogs, not Start-Process's redirection", () => {
    const devServer = fs.readFileSync(path.join(path.dirname(module), 'dev-server.ps1'), 'utf8');
    assert.match(devServer, /Start-ProcessWithLogs -FilePath \$jellyfinExe -Arguments \$arguments -WorkingDirectory \$sandbox\.Root `\r?\n\s+-StdOut \$sandbox\.StdOutLog -StdErr \$sandbox\.StdErrLog/);
    assert.doesNotMatch(devServer, /-RedirectStandard(Output|Error)/);
    const psm1 = fs.readFileSync(module, 'utf8');
    assert.match(psm1, /ExtendedStartupInfoPresent \| CreateNoWindow, IntPtr\.Zero, workingDirectory/);
    assert.match(psm1, /ProcThreadAttributeHandleList = new IntPtr\(0x00020002\)/);
});
