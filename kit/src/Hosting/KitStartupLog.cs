using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// Logging for code in <c>RegisterServices</c>, before the server has a service provider.
/// </summary>
/// <remarks>
/// Jellyfin 10.11.5 registers <c>ILoggerFactory</c> only after the plugins' <c>RegisterServices</c> ran
/// (<c>Jellyfin.Server/Program.cs</c>: <c>ConfigureServices(appHost.Init)</c> precedes <c>UseSerilog()</c>), and
/// <c>IApplicationHost</c> exposes no logger. Every Jellyfin log line goes through Serilog's static <c>Log.Logger</c>
/// (Program.cs: <c>new SerilogLoggerFactory()</c>), so this writes there by reflection, with the kit's logger category as
/// <c>SourceContext</c>: the line looks like any other Jellyfin log line. Plugins don't reference Serilog, so it is found
/// in the default load context by name. Best effort: when Serilog isn't there (unit tests) nothing is written.
/// </remarks>
internal static class KitStartupLog
{
    /// <summary>
    /// Writes an Error line.
    /// </summary>
    /// <param name="category">The logger category, for example <c>Jellyfin.Plugin.Bookmarks.Kit.AbiGuard</c>.</param>
    /// <param name="message">The message; braces are escaped, so it is never read as a message template.</param>
    /// <returns><c>true</c> when the line was handed to Serilog.</returns>
    public static bool Error(string category, string message)
    {
        try
        {
            var serilog = AssemblyLoadContext.Default.Assemblies
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, "Serilog", StringComparison.Ordinal));
            var loggerInterface = serilog?.GetType("Serilog.ILogger");
            var logger = serilog?.GetType("Serilog.Log")?.GetProperty("Logger", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var forContext = loggerInterface?.GetMethod("ForContext", [typeof(string), typeof(object), typeof(bool)]);
            var error = loggerInterface?.GetMethod("Error", [typeof(string)]);
            if (logger is null || forContext is null || error is null)
            {
                return false;
            }

            var contextual = forContext.Invoke(logger, ["SourceContext", category, false]);
            error.Invoke(contextual, [message.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal)]);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
