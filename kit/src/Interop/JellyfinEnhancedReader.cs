using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// Reads what Jellyfin Enhanced (JE), Custom Tabs and Plugin Pages hold on this server, read-only and tolerant: JE's
/// settings XML, each user's own JE shortcuts, Custom Tabs' XML, Plugin Pages' list in memory, and JE's data files for
/// the importers (<c>reviews.json</c>, a user's <c>bookmark.json</c>). Tested with JE 12.8, 12.11 and 12.12 (the same
/// files, element names and defaults); a later version that changes them fails soft: an unknown
/// file shape is reported, never thrown, and a missing setting reads JE's default.
/// </summary>
/// <remarks>
/// <para>Every file is opened read-only with <c>FileShare.ReadWrite | FileShare.Delete</c>, so JE can write or replace it
/// meanwhile, and read up to <see cref="JellyfinEnhancedFiles.MaxBytes"/>; a file being written is "not valid JSON" for
/// that read. The kit never writes, moves or deletes one of these files, and never changes JE's settings.</para>
/// <para><see cref="Read"/> is memoised for <see cref="SnapshotLifetime"/>, so the requests one settings page makes at
/// once (Capabilities, Diagnostics) share one read; a change in JE's settings shows at the next request after that
/// (re-read per request).</para>
/// </remarks>
internal sealed class JellyfinEnhancedReader
{
    /// <summary>How long one snapshot serves the requests that follow each other.</summary>
    public static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMilliseconds(500);

    private static readonly XmlReaderSettings _xmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        MaxCharactersInDocument = JellyfinEnhancedFiles.MaxBytes
    };

    private readonly IPluginManager? _pluginManager;
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private JellyfinEnhancedSnapshot? _last;
    private DateTimeOffset _lastAt;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinEnhancedReader"/> class.
    /// </summary>
    /// <param name="applicationPaths">The server's paths (<c>PluginConfigurationsPath</c>).</param>
    /// <param name="pluginManager">The server's plugin manager, or <c>null</c> when there is none (tests): JE then counts
    /// as installed and running when its settings file is there.</param>
    /// <param name="services">The server's services, for Plugin Pages' DI manager.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="clock">The clock the memo follows; the system clock when <c>null</c>.</param>
    public JellyfinEnhancedReader(IApplicationPaths applicationPaths, IPluginManager? pluginManager, IServiceProvider services, ILogger logger, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        ConfigurationsFolder = applicationPaths.PluginConfigurationsPath;
        _pluginManager = pluginManager;
        _services = services;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Gets the server's plugin configurations folder.</summary>
    public string ConfigurationsFolder { get; }

    /// <summary>Gets JE's settings file, <c>Jellyfin.Plugin.JellyfinEnhanced.xml</c>.</summary>
    public string SettingsFile => Path.Combine(ConfigurationsFolder, JellyfinEnhancedFiles.SettingsFile);

    /// <summary>Gets JE's data folder, <c>Jellyfin.Plugin.JellyfinEnhanced</c>: <c>reviews.json</c> and the user folders.</summary>
    public string DataFolder => Path.Combine(ConfigurationsFolder, JellyfinEnhancedFiles.ConfigurationName);

    /// <summary>Gets Custom Tabs' settings file.</summary>
    public string CustomTabsFile => Path.Combine(ConfigurationsFolder, JellyfinEnhancedFiles.CustomTabsConfigurationName + ".xml");

    /// <summary>
    /// Gets or sets how Plugin Pages' list is read: <see cref="Web.Pages.PluginPagesBridge.TryListPages"/> over the loaded
    /// assemblies by default; a test points it at its own fake assembly.
    /// </summary>
    public Func<IServiceProvider, PluginPagesState> PluginPagesLister { get; set; } = static services => Web.Pages.PluginPagesBridge.TryListPages(services);

    /// <summary>
    /// Reads everything the shortcut policy and the switch-off checklist look at: JE's state and settings, every user's
    /// own shortcuts, Custom Tabs' tabs and Plugin Pages' list. The same snapshot is returned for
    /// <see cref="SnapshotLifetime"/>.
    /// </summary>
    /// <returns>The snapshot.</returns>
    public JellyfinEnhancedSnapshot Read()
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (_last is not null && now - _lastAt < SnapshotLifetime && now >= _lastAt)
            {
                return _last;
            }
        }

        var users = ReadUserShortcuts(out var usersProblem);
        var snapshot = new JellyfinEnhancedSnapshot(ReadPluginState(), ReadSettings(), users, ReadCustomTabs(), ReadPluginPages()) { UsersProblem = usersProblem };
        lock (_gate)
        {
            _last = snapshot;
            _lastAt = now;
        }

        return snapshot;
    }

    /// <summary>Forgets the memoised snapshot, so the next <see cref="Read"/> reads again.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _last = null;
        }
    }

    /// <summary>
    /// Reads whether JE is installed and loaded, from the server's plugin manager.
    /// </summary>
    /// <returns>The state.</returns>
    public JePluginState ReadPluginState() => ReadPluginState(JellyfinEnhancedFiles.PluginId, SettingsFile);

    /// <summary>
    /// Reads JE's settings XML: the flags the kit uses (<see cref="JellyfinEnhancedDefaults.Flags"/>) and the shortcut list,
    /// in file order, duplicates included (JE's own reader keeps the last of a name, <see cref="JeShortcuts"/>).
    /// </summary>
    /// <returns>The settings; <see cref="JeSettings.Missing"/> without a file.</returns>
    public JeSettings ReadSettings()
    {
        if (!TryLoadXml(SettingsFile, out var root, out var problem))
        {
            return problem is null ? JeSettings.Missing : new JeSettings(true, new Dictionary<string, bool>(StringComparer.Ordinal), [], problem);
        }

        var flags = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var name in JellyfinEnhancedDefaults.Flags.Keys)
        {
            // The last element of a name, as XmlSerializer leaves the last value; text that isn't a boolean reads the default.
            var element = root!.Elements(name).LastOrDefault();
            if (element is not null && bool.TryParse(element.Value.Trim(), out var value))
            {
                flags[name] = value;
            }
        }

        // Whether each presence-only setting is set (the last element of its name), never its value.
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in JellyfinEnhancedDefaults.PresenceOnly)
        {
            if (root!.Elements(name).LastOrDefault() is { } element && !string.IsNullOrWhiteSpace(element.Value))
            {
                set.Add(name);
            }
        }

        var shortcuts = new List<JeShortcut>();
        foreach (var element in root!.Elements("Shortcuts").Elements("Shortcut"))
        {
            var name = element.Element("Name")?.Value.Trim() ?? string.Empty;
            var key = element.Element("Key");
            shortcuts.Add(new JeShortcut(name, key?.Value, element.Element("Label")?.Value, element.Element("Category")?.Value));
        }

        return new JeSettings(true, flags, shortcuts, null) { Set = set };
    }

    /// <summary>
    /// Reads every user's own JE shortcuts (<c>&lt;userIdN&gt;/shortcuts.json</c>), the users whose folder has one, once
    /// each, in folder-name order.
    /// </summary>
    /// <returns>The files; a file that can't be read is listed with its problem.</returns>
    public IReadOnlyList<JeUserShortcuts> ReadUserShortcuts() => ReadUserShortcuts(out _);

    /// <summary>
    /// Reads every user's own JE shortcuts, saying when the folders couldn't be listed at all.
    /// </summary>
    /// <param name="problem">Why JE's data folder couldn't be listed (the list is then empty and says nothing), or <c>null</c>.</param>
    /// <returns>The files; a file that can't be read is listed with its problem.</returns>
    public IReadOnlyList<JeUserShortcuts> ReadUserShortcuts(out string? problem)
    {
        var users = new List<JeUserShortcuts>();
        foreach (var (userId, file) in UserFiles(JellyfinEnhancedFiles.ShortcutsFile, out problem))
        {
            if (!TryReadFile(file, out var bytes, out var fileProblem, _logger))
            {
                users.Add(new JeUserShortcuts(userId, [], fileProblem));
                continue;
            }

            users.Add(JeDataParser.TryParseUserShortcuts(bytes, out var shortcuts, out fileProblem)
                ? new JeUserShortcuts(userId, shortcuts, null)
                : new JeUserShortcuts(userId, [], fileProblem));
        }

        return users;
    }

    /// <summary>
    /// Reads Custom Tabs' settings: whether it is installed and running, and its tabs.
    /// </summary>
    /// <returns>The state.</returns>
    public CustomTabsState ReadCustomTabs()
    {
        var plugin = ReadPluginState(JellyfinEnhancedFiles.CustomTabsPluginId, CustomTabsFile);
        if (!TryLoadXml(CustomTabsFile, out var root, out var problem))
        {
            return new CustomTabsState(plugin.Installed, plugin.Active, [], problem);
        }

        var tabs = root!.Elements("Tabs").Elements("TabConfig")
            .Select(tab => new CustomTab(tab.Element("Title")?.Value ?? string.Empty, tab.Element("ContentHtml")?.Value ?? string.Empty))
            .ToList();
        return new CustomTabsState(plugin.Installed, plugin.Active, tabs, null);
    }

    /// <summary>
    /// Reads the pages Plugin Pages lists right now, from its manager in memory (never its <c>config.json</c>, which it
    /// reads only when it starts).
    /// </summary>
    /// <returns>The state.</returns>
    public PluginPagesState ReadPluginPages() => PluginPagesLister(_services);

    /// <summary>
    /// Reads JE's server-wide <c>reviews.json</c>, for User Ratings' importer.
    /// </summary>
    /// <returns>The file; <see cref="JeDataFormat.Missing"/> without one.</returns>
    public JeReviewsFile ReadReviews()
    {
        var file = Path.Combine(DataFolder, JellyfinEnhancedFiles.ReviewsFile);
        if (!File.Exists(file))
        {
            return new JeReviewsFile(JeDataFormat.Missing, [], [], null);
        }

        return TryReadFile(file, out var bytes, out var problem, _logger)
            ? JeDataParser.ParseReviews(bytes)
            : new JeReviewsFile(JeDataFormat.Unreadable, [], [], problem);
    }

    /// <summary>
    /// Reads one user's JE <c>bookmark.json</c>, for Bookmarks' importer: the one in the lower-case N folder,
    /// which JE reads, else the first older spelling's that has one (folder-name order).
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The file; <see cref="JeDataFormat.Missing"/> without one.</returns>
    public JeBookmarkFile ReadBookmarks(Guid userId)
    {
        var file = FileOf(userId, JellyfinEnhancedFiles.BookmarksFile, () => UserFolders(out _).Where(f => f.UserId == userId).Select(f => f.Folder));
        if (file is null)
        {
            return new JeBookmarkFile(userId, JeDataFormat.Missing, [], [], null);
        }

        return TryReadFile(file, out var bytes, out var problem, _logger)
            ? JeDataParser.ParseBookmarks(bytes, userId)
            : new JeBookmarkFile(userId, JeDataFormat.Unreadable, [], [], problem);
    }

    /// <summary>
    /// Lists the users whose JE folder holds a <c>bookmark.json</c>.
    /// </summary>
    /// <returns>The user ids, each once (a user with two folder spellings too), in folder-name order.</returns>
    public IReadOnlyList<Guid> ListUsersWithBookmarks() => ListUsersWithBookmarks(out _);

    /// <summary>
    /// Lists the users whose JE folder holds a <c>bookmark.json</c>, saying when the folders couldn't be listed at all.
    /// </summary>
    /// <param name="problem">Why JE's data folder couldn't be listed (the list is then empty and says nothing), or <c>null</c>.</param>
    /// <returns>The user ids, each once (a user with two folder spellings too), in folder-name order.</returns>
    public IReadOnlyList<Guid> ListUsersWithBookmarks(out string? problem)
        => UserFiles(JellyfinEnhancedFiles.BookmarksFile, out problem).Select(f => f.UserId).ToList();

    /// <summary>
    /// Reads a file read-only, sharing it with a writer (<c>FileShare.ReadWrite | FileShare.Delete</c>), up to
    /// <see cref="JellyfinEnhancedFiles.MaxBytes"/>.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="bytes">Its bytes.</param>
    /// <param name="problem">Why it couldn't be read: the file's name and a plain reason, never its folder or the system's
    /// message (which can hold the full path). The details go to <paramref name="logger"/>.</param>
    /// <param name="logger">Where the system's message goes; none by default.</param>
    /// <returns>Whether it was read.</returns>
    public static bool TryReadFile(string path, out ReadOnlyMemory<byte> bytes, out string? problem, ILogger? logger = null)
    {
        bytes = ReadOnlyMemory<byte>.Empty;
        problem = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            if (stream.Length > JellyfinEnhancedFiles.MaxBytes)
            {
                problem = $"{Path.GetFileName(path)} is {stream.Length:N0} bytes, over the {JellyfinEnhancedFiles.MaxBytes / (1024 * 1024)} MB the kit reads.";
                return false;
            }

            var buffer = new byte[stream.Length];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            bytes = new ReadOnlyMemory<byte>(buffer, 0, read);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            problem = $"{Path.GetFileName(path)} couldn't be read: {PlainReason(ex)}";
            logger?.LogWarning(ex, "Jellyfin Enhanced's file {File} couldn't be read", path);
            return false;
        }
    }

    /// <summary>
    /// Gets a plain reason for a file system error, without the system's message (which can hold the full path).
    /// </summary>
    /// <param name="ex">The error.</param>
    /// <returns>The reason.</returns>
    internal static string PlainReason(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "access to it was denied (the server log has the details).",
        FileNotFoundException or DirectoryNotFoundException => "it isn't there any more (the server log has the details).",
        NotSupportedException => "its path isn't supported (the server log has the details).",
        _ => "it's in use or couldn't be opened (the server log has the details)."
    };

    private JePluginState ReadPluginState(Guid pluginId, string settingsFile)
    {
        if (_pluginManager is null)
        {
            var present = File.Exists(settingsFile);
            return present ? new JePluginState(true, true, null, "Unknown") : JePluginState.NotInstalled;
        }

        try
        {
            // The loaded version first, else the newest folder in any state but deleted (a pending update, a disabled one).
            var candidates = _pluginManager.Plugins
                .Where(p => p.Id == pluginId && p.Manifest.Status != PluginStatus.Deleted)
                .OrderByDescending(p => p.Instance is not null)
                .ThenByDescending(p => p.Version)
                .ToList();
            if (candidates.Count == 0)
            {
                return JePluginState.NotInstalled;
            }

            var plugin = candidates[0];
            var active = plugin.Instance is not null && plugin.Manifest.Status == PluginStatus.Active;
            return new JePluginState(true, active, plugin.Version?.ToString(), plugin.Manifest.Status.ToString());
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or NullReferenceException)
        {
            _logger.LogWarning(ex, "The plugin manager couldn't list plugin {PluginId}; its settings file decides", pluginId);
            var present = File.Exists(settingsFile);
            return present ? new JePluginState(true, true, null, "Unknown") : JePluginState.NotInstalled;
        }
    }

    // One file per user, whatever the folder spellings: JE 12.8+ renames other spellings to the
    // lower-case N form at its start, but leaves one when the N folder is already there, and reading both would import a
    // user's bookmarks twice. In the order of each user's first folder name.
    private List<(Guid UserId, string File)> UserFiles(string fileName, out string? problem)
    {
        var files = new List<(Guid UserId, string File)>();
        foreach (var user in UserFolders(out problem).GroupBy(f => f.UserId))
        {
            if (FileOf(user.Key, fileName, () => user.Select(f => f.Folder)) is { } file)
            {
                files.Add((user.Key, file));
            }
        }

        return files;
    }

    // The file JE reads, in the lower-case N folder; else the first older spelling's that has one (folder-name order).
    private string? FileOf(Guid userId, string fileName, Func<IEnumerable<string>> folders)
    {
        var own = Path.Combine(DataFolder, userId.ToString("N", CultureInfo.InvariantCulture), fileName);
        return File.Exists(own) ? own : folders().Select(folder => Path.Combine(folder, fileName)).FirstOrDefault(File.Exists);
    }

    private IEnumerable<(Guid UserId, string Folder)> UserFolders(out string? problem)
    {
        // A missing data folder is "no user has JE data" (JE creates it at its first write); a folder that is there but
        // can't be listed says nothing about the users, so the caller is told.
        problem = null;
        IEnumerable<string> folders;
        try
        {
            if (!Directory.Exists(DataFolder))
            {
                return [];
            }

            folders = Directory.EnumerateDirectories(DataFolder).Take(JellyfinEnhancedFiles.MaxUserFolders).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Jellyfin Enhanced's data folder {Folder} couldn't be listed", DataFolder);
            problem = $"Jellyfin Enhanced's data folder couldn't be listed: {PlainReason(ex)}";
            return [];
        }

        var users = new List<(Guid UserId, string Folder)>();
        foreach (var folder in folders.Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(folder);
            if (name.Length is 32 or 36 && Guid.TryParseExact(name, name.Length == 32 ? "N" : "D", out var id) && id != Guid.Empty)
            {
                users.Add((id, folder));
            }
        }

        return users;
    }

    private bool TryLoadXml(string path, out XElement? root, out string? problem)
    {
        root = null;
        problem = null;
        if (!File.Exists(path))
        {
            return false;
        }

        if (!TryReadFile(path, out var bytes, out problem, _logger))
        {
            return false;
        }

        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, _xmlSettings);
            root = XDocument.Load(reader, LoadOptions.None).Root;
            if (root is null)
            {
                problem = $"{Path.GetFileName(path)} has no root element.";
                return false;
            }

            return true;
        }
        catch (XmlException ex)
        {
            // Where, never the system's message; the detail goes to the server log.
            problem = string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(path)} isn't well-formed XML at line {ex.LineNumber}, position {ex.LinePosition} (the file may be being written; try again).");
            _logger.LogWarning(ex, "Jellyfin Enhanced's settings file {File} isn't well-formed XML", path);
            return false;
        }
    }
}
