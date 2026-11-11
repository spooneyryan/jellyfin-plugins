using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Xunit;

namespace Jellyfin.Plugins.Repo.Tests;

/// <summary>
/// The Access Roles README's recovery line ("Recovery after the plugin folder was deleted with the mirror on"), run as
/// it is there but for the server's address, the key and the data file, in Windows
/// PowerShell 5.1 (and PowerShell 7 when it is installed) against a loopback stand-in for Jellyfin's two user routes: each
/// policy posted back as UTF-8 with its charset, non-ASCII tags and all, and nothing of it changed but the flag. The stand-in
/// answers <c>GET /Users/{id}</c> as Jellyfin does (<c>application/json; charset=utf-8</c>) and records each post; it
/// listens on 127.0.0.1 only, on a port the system picks, and no Jellyfin server is involved.
/// </summary>
public sealed class AccessRolesRecoveryLineTests
{
    private const string Key = "recovery-line-test-key";

    private static readonly string Readme = Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "plugins", "AccessRoles", "README.md");

    // The data file's PreferenceMirror keys, as the store writes them (Guid "D"), and a role name that isn't ASCII either.
    private static readonly Guid Kid = Guid.Parse("4b606ec3-8c35-4458-9a17-562d8125cb0c");
    private static readonly Guid Guest = Guid.Parse("5b34ba94-c225-40db-a22b-a91f9a675edc");

    [Fact]
    public void TheLine_ReadsTheDataFileAsUtf8_AndPostsUtf8Bytes_NamingTheCharset()
    {
        var (first, second) = Lines();
        Assert.Matches(@"^\$server = '[^']*'; \$key = '<API key>'; \$data = '[^']*'$", first);
        Assert.Contains("(Get-Content $data -Raw -Encoding UTF8 | ConvertFrom-Json).PreferenceMirror.PSObject.Properties.Name", second, StringComparison.Ordinal);
        Assert.Contains("-ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($p | ConvertTo-Json -Depth 10)))", second, StringComparison.Ordinal);
        Assert.DoesNotContain("-ContentType 'application/json' ", second, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task TheLine_PutsBackOnlyTheFlag_KeepingNonAsciiTags(string shell)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The line is for Windows.");
        var executable = shell == "pwsh" ? FindPwsh() : null;
        if (shell == "pwsh" && executable is null)
        {
            Assert.Skip("PowerShell 7 (pwsh.exe) isn't installed.");
        }

        var policies = new Dictionary<Guid, string>
        {
            [Kid] = Policy(blocked: ["Séries", "Ünïcode", "日本語"], allowed: ["Café"]),
            [Guest] = Policy(blocked: ["日本語", "🎬 Ciné"], allowed: [])
        };
        using var work = new TempDirectory();
        var data = work.Combine("accessroles.json");
        await File.WriteAllTextAsync(
            data,
            "{\"Version\":1,\"Roles\":[{\"Name\":\"Séries für Kinder 日本語\"}],\"Assignments\":[],\"PreferenceMirror\":{"
                + $"\"{Kid:D}\":{{\"WrittenUtc\":\"2026-11-01T00:00:00Z\"}},\"{Guest:D}\":{{\"WrittenUtc\":\"2026-11-01T00:00:00Z\"}}}}}}",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);

        await using var server = new StandIn(policies);
        var (first, second) = Lines();
        var script = work.Combine("recovery-line.ps1");
        await File.WriteAllTextAsync(
            script,
            "$ErrorActionPreference = 'Stop'\n"
                + first.Replace("'http://localhost:8096'", $"'http://127.0.0.1:{server.Port}'", StringComparison.Ordinal)
                    .Replace("'<API key>'", $"'{Key}'", StringComparison.Ordinal)
                    .Replace("'C:\\ProgramData\\Jellyfin\\Server\\data\\accessroles\\accessroles.json'", $"'{data.Replace("'", "''", StringComparison.Ordinal)}'", StringComparison.Ordinal)
                + "\n" + second + "\n",
            new UTF8Encoding(true),
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("8096", await File.ReadAllTextAsync(script, TestContext.Current.CancellationToken), StringComparison.Ordinal);

        var run = await WindowsPowerShell.RunFileAsync(script, [], TimeSpan.FromSeconds(60), executable);

        Assert.True(run.ExitCode == 0, run.Output);
        var posts = server.Requests.Where(r => r.Method == "POST").ToList();
        Assert.Equal(new[] { Kid, Guest }.Order(), posts.Select(r => r.User).Order());
        Assert.Equal(2, server.Requests.Count(r => r.Method == "GET"));
        foreach (var post in posts)
        {
            Assert.Equal("/Users/" + post.User.ToString("D") + "/Policy", post.Path, ignoreCase: true);
            Assert.Equal($"MediaBrowser Token=\"{Key}\"", post.Header("Authorization"));
            Assert.Equal("application/json; charset=utf-8", post.Header("Content-Type"));
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(post.Body);
            var expected = JsonNode.Parse(policies[post.User])!.AsObject();
            expected["EnableUserPreferenceAccess"] = true;
            var sent = JsonNode.Parse(text)!.AsObject();
            Assert.Equal(expected.Select(p => p.Key).Order(StringComparer.Ordinal), sent.Select(p => p.Key).Order(StringComparer.Ordinal));
            foreach (var (name, value) in expected)
            {
                Assert.True(JsonNode.DeepEquals(value, sent[name]), $"{post.User:N} {name}: sent {sent[name]?.ToJsonString() ?? "null"}, expected {value?.ToJsonString() ?? "null"}");
            }
        }
    }

    private static (string First, string Second) Lines()
    {
        var readme = File.ReadAllText(Readme).ReplaceLineEndings("\n");
        var section = readme[readme.IndexOf("**Recovery after the plugin folder was deleted with the mirror on.**", StringComparison.Ordinal)..];
        var block = section[(section.IndexOf("```powershell\n", StringComparison.Ordinal) + "```powershell\n".Length)..];
        var lines = block[..block.IndexOf("\n```", StringComparison.Ordinal)].Split('\n');
        Assert.Equal(2, lines.Length);
        return (lines[0], lines[1]);
    }

    private static string? FindPwsh()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (File.Exists(installed))
        {
            return installed;
        }

        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim('"'), "pwsh.exe"))
            .FirstOrDefault(File.Exists);
    }

    // A Jellyfin 10.11.5 UserPolicy as GET /Users/{id} returns it (every field, a schedule, a library), but for the tags.
    private static string Policy(string[] blocked, string[] allowed)
    {
        var policy = new JsonObject
        {
            ["IsAdministrator"] = false,
            ["IsHidden"] = true,
            ["EnableCollectionManagement"] = false,
            ["EnableSubtitleManagement"] = false,
            ["EnableLyricManagement"] = false,
            ["IsDisabled"] = false,
            ["MaxParentalRating"] = 13,
            ["MaxParentalSubRating"] = null,
            ["BlockedTags"] = new JsonArray(blocked.Select(t => (JsonNode?)t).ToArray()),
            ["AllowedTags"] = new JsonArray(allowed.Select(t => (JsonNode?)t).ToArray()),
            ["EnableUserPreferenceAccess"] = false,
            ["AccessSchedules"] = new JsonArray(new JsonObject { ["Id"] = 3, ["UserId"] = "4b606ec38c3544589a17562d8125cb0c", ["DayOfWeek"] = "Sunday", ["StartHour"] = 8.5, ["EndHour"] = 20 }),
            ["BlockUnratedItems"] = new JsonArray("Movie"),
            ["EnableRemoteControlOfOtherUsers"] = false,
            ["EnableSharedDeviceControl"] = true,
            ["EnableRemoteAccess"] = true,
            ["EnableLiveTvManagement"] = false,
            ["EnableLiveTvAccess"] = true,
            ["EnableMediaPlayback"] = true,
            ["EnableAudioPlaybackTranscoding"] = true,
            ["EnableVideoPlaybackTranscoding"] = true,
            ["EnablePlaybackRemuxing"] = true,
            ["ForceRemoteSourceTranscoding"] = false,
            ["EnableContentDeletion"] = false,
            ["EnableContentDeletionFromFolders"] = new JsonArray(),
            ["EnableContentDownloading"] = true,
            ["EnableSyncTranscoding"] = true,
            ["EnableMediaConversion"] = true,
            ["EnabledDevices"] = new JsonArray(),
            ["EnableAllDevices"] = true,
            ["EnabledChannels"] = new JsonArray(),
            ["EnableAllChannels"] = true,
            ["EnabledFolders"] = new JsonArray("f137a2dd21bbc1b99aa5c0f6bf02a805"),
            ["EnableAllFolders"] = false,
            ["InvalidLoginAttemptCount"] = 0,
            ["LoginAttemptsBeforeLockout"] = -1,
            ["MaxActiveSessions"] = 0,
            ["EnablePublicSharing"] = true,
            ["BlockedMediaFolders"] = new JsonArray(),
            ["BlockedChannels"] = new JsonArray(),
            ["RemoteClientBitrateLimit"] = 0,
            ["AuthenticationProviderId"] = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider",
            ["PasswordResetProviderId"] = "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider",
            ["SyncPlayAccess"] = "CreateAndJoinGroups"
        };
        return policy.ToJsonString();
    }

    private sealed record Request(string Method, string Path, Guid User, IReadOnlyDictionary<string, string> Headers, byte[] Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    // A one-connection-at-a-time HTTP/1.1 stand-in on 127.0.0.1: every response closes its connection.
    private sealed class StandIn : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly IReadOnlyDictionary<Guid, string> _policies;
        private readonly List<Request> _requests = [];
        private readonly Task _loop;

        public StandIn(IReadOnlyDictionary<Guid, string> policies)
        {
            _policies = policies;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(LoopAsync);
        }

        public int Port { get; }

        public IReadOnlyList<Request> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            _stop.Dispose();
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                await ServeAsync(stream, _stop.Token);
            }
        }

        private async Task ServeAsync(NetworkStream stream, CancellationToken token)
        {
            var head = new List<byte>();
            var one = new byte[1];
            while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
            {
                if (await stream.ReadAsync(one, token) == 0)
                {
                    return;
                }

                head.Add(one[0]);
            }

            var lines = Encoding.ASCII.GetString([.. head]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var start = lines[0].Split(' ');
            var headers = lines.Skip(1)
                .Select(l => (Name: l[..l.IndexOf(':', StringComparison.Ordinal)], Value: l[(l.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim()))
                .ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase);
            if (headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            {
                await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), token);
            }

            var body = new byte[headers.TryGetValue("Content-Length", out var length) ? int.Parse(length, System.Globalization.CultureInfo.InvariantCulture) : 0];
            await stream.ReadExactlyAsync(body, token);

            var match = Regex.Match(start[1], @"^/Users/([0-9a-fA-F-]{32,36})(/Policy)?$");
            var user = match.Success && Guid.TryParse(match.Groups[1].Value, out var id) ? id : Guid.Empty;
            lock (_requests)
            {
                _requests.Add(new Request(start[0], start[1], user, headers, body));
            }

            var (status, json) = (start[0], match.Success && _policies.ContainsKey(user), match.Groups[2].Success) switch
            {
                ("GET", true, false) => ("200 OK", $"{{\"Name\":\"user\",\"Id\":\"{user:N}\",\"HasPassword\":true,\"Policy\":{_policies[user]}}}"),
                ("POST", true, true) => ("204 No Content", null),
                _ => ("404 Not Found", null)
            };
            var content = json is null ? [] : Encoding.UTF8.GetBytes(json);
            var response = $"HTTP/1.1 {status}\r\n"
                + (json is null ? string.Empty : "Content-Type: application/json; charset=utf-8\r\n")
                + $"Content-Length: {content.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token);
            await stream.WriteAsync(content, token);
        }
    }
}
