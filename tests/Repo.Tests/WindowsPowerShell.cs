using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Xunit;

namespace Jellyfin.Plugins.Repo.Tests;

/// <summary>
/// Runs a script in Windows PowerShell 5.1, the shell the repository scripts target (scripts/README.md). Only the
/// powershell.exe started here is ever stopped.
/// </summary>
internal static class WindowsPowerShell
{
    public static readonly string Scripts = Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "scripts");

    /// <summary>Runs script text after importing scripts/JellyfinSandbox.psm1, with $ErrorActionPreference = 'Stop'.</summary>
    public static async Task<PowerShellRun> RunWithModuleAsync(string script, TimeSpan? timeout = null)
    {
        using var work = new TempDirectory();
        var file = Path.Combine(work.Path, "script.ps1");
        var text = "$ErrorActionPreference = 'Stop'\n"
            + $"Import-Module '{Path.Combine(Scripts, "JellyfinSandbox.psm1").Replace("'", "''", StringComparison.Ordinal)}' -Force\n"
            + script;
        await File.WriteAllTextAsync(file, text, TestContext.Current.CancellationToken);
        return await RunFileAsync(file, [], timeout);
    }

    /// <summary>Runs a script file with arguments (-File); <paramref name="executable"/> another PowerShell (pwsh.exe) instead.</summary>
    public static async Task<PowerShellRun> RunFileAsync(string file, string[] arguments, TimeSpan? timeout = null, string? executable = null)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The scripts run on Windows only.");
        var token = TestContext.Current.CancellationToken;
        var powershell = executable ?? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file })
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(120));
        try
        {
            var errors = process.StandardError.ReadToEndAsync(limit.Token);
            var output = await process.StandardOutput.ReadToEndAsync(limit.Token);
            var errorText = await errors;
            await process.WaitForExitAsync(limit.Token);
            return new PowerShellRun(process.ExitCode, output, errorText);
        }
        catch (OperationCanceledException)
        {
            // Only the powershell.exe this test started.
            process.Kill(entireProcessTree: true);
            throw;
        }
    }
}

/// <summary>The result of a Windows PowerShell run.</summary>
internal sealed record PowerShellRun(int ExitCode, string StandardOutput, string StandardError)
{
    public string Output => (StandardOutput + Environment.NewLine + StandardError).Trim();

    public string[] Lines => StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
