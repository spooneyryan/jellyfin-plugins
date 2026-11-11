using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;

namespace Jellyfin.Plugin.Kit.TestSupport.Web;

/// <summary>
/// Node.js checks of a plugin's built web scripts.
/// Each method returns <c>null</c> when Node isn't on the PATH, and the test then skips:
/// <code>
/// var run = await KitScriptChecks.CheckSyntaxAsync(script.ClientScriptText, module: false);
/// Assert.SkipWhen(run is null, "Node.js is not on the PATH.");
/// Assert.True(run.ExitCode == 0, run.Output);
/// </code>
/// </summary>
public static class KitScriptChecks
{
    /// <summary>
    /// Gets the kit's JavaScript test folder, <c>kit/web/tests</c>.
    /// </summary>
    public static string KitWebTestsFolder => Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "kit", "web", "tests");

    /// <summary>
    /// Runs <c>node --check</c> on a script: a classic script (<c>.js</c>) or an ES module (<c>.mjs</c>, the AdminScript).
    /// </summary>
    /// <param name="script">The script's text.</param>
    /// <param name="module">Whether it is an ES module.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The run, or <c>null</c> without Node.</returns>
    public static async Task<NodeRun?> CheckSyntaxAsync(string script, bool module, CancellationToken cancellationToken = default)
    {
        var node = NodeRunner.FindNode();
        if (node is null)
        {
            return null;
        }

        using var temp = new TempDirectory();
        var file = temp.CreateFile(module ? "script.mjs" : "script.js", script);
        return await NodeRunner.RunAsync(node, ["--check", file], cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the kit's generic ClientScript tests (<c>kit/web/tests/client-script.test.mjs</c>) on a plugin's script: it
    /// runs in a fake page, registers <c>window.__jfkit[prefix]</c> once, injects its styles once, starts within 25 ms of
    /// <c>window.ApiClient</c> and sends no request before the signed-in user exists.
    /// </summary>
    /// <param name="clientScript">The ClientScript's text.</param>
    /// <param name="prefix">The plugin's DOM prefix.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The run, or <c>null</c> without Node.</returns>
    public static Task<NodeRun?> RunClientScriptTestsAsync(string clientScript, string prefix, CancellationToken cancellationToken = default)
        => RunTestFileAsync(Path.Combine(KitWebTestsFolder, "client-script.test.mjs"), clientScript, prefix, cancellationToken);

    /// <summary>
    /// Runs the kit's AdminScript tests (<c>kit/web/tests/admin-script.test.mjs</c>) on a plugin's dashboard module: it
    /// exports only <c>mount</c>, adds nothing to the page on import and mounts into a view.
    /// </summary>
    /// <param name="adminScript">The AdminScript module's text.</param>
    /// <param name="prefix">The plugin's DOM prefix.</param>
    /// <param name="expectedText">Optional: the text the view must hold after <c>mount(view, { name: 'fixture' })</c>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The run, or <c>null</c> without Node.</returns>
    public static async Task<NodeRun?> RunAdminScriptTestsAsync(string adminScript, string prefix, string? expectedText = null, CancellationToken cancellationToken = default)
    {
        var node = NodeRunner.FindNode();
        if (node is null)
        {
            return null;
        }

        using var temp = new TempDirectory();
        var file = temp.CreateFile("admin-script.mjs", adminScript);
        var environment = new Dictionary<string, string> { ["KIT_ADMIN_SCRIPT"] = file, ["KIT_PREFIX"] = prefix };
        if (expectedText is not null)
        {
            environment["KIT_ADMIN_EXPECT"] = expectedText;
        }

        return await NodeRunner.RunAsync(node, ["--test", Path.Combine(KitWebTestsFolder, "admin-script.test.mjs")], environment, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a <c>node:test</c> file with the script in <c>KIT_CLIENT_SCRIPT</c> and the prefix in <c>KIT_PREFIX</c>, for
    /// a plugin's own script tests (for example <c>plugins/Bookmarks/tests/.../Web/bookmarks.test.mjs</c>, which uses the
    /// kit's <c>fake-dom.mjs</c>).
    /// </summary>
    /// <param name="testFile">The test file.</param>
    /// <param name="clientScript">The ClientScript's text.</param>
    /// <param name="prefix">The plugin's DOM prefix.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The run, or <c>null</c> without Node.</returns>
    public static async Task<NodeRun?> RunTestFileAsync(string testFile, string clientScript, string prefix, CancellationToken cancellationToken = default)
    {
        var node = NodeRunner.FindNode();
        if (node is null)
        {
            return null;
        }

        using var temp = new TempDirectory();
        var file = temp.CreateFile("client-script.js", clientScript);
        var environment = new Dictionary<string, string> { ["KIT_CLIENT_SCRIPT"] = file, ["KIT_PREFIX"] = prefix };
        return await NodeRunner.RunAsync(node, ["--test", testFile], environment, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
