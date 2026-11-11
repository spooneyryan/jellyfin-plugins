using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Users;

namespace Jellyfin.Plugin.AccessRoles.Policy;

/// <summary>
/// Every field of a <see cref="UserPolicy"/> as text, so two reads can be compared field by field (the
/// re-read before each write compares every field but the managed ones). The fields are the type's
/// public read-write properties, found by reflection, so a field a later Jellyfin adds is compared too.
/// </summary>
internal static class PolicyFields
{
    /// <summary>The field that says whether every library is granted.</summary>
    public const string EnableAllFolders = nameof(UserPolicy.EnableAllFolders);

    /// <summary>The field with the granted libraries.</summary>
    public const string EnabledFolders = nameof(UserPolicy.EnabledFolders);

    /// <summary>
    /// The lyric field: <c>GetUserDto</c> never fills it (<c>v10.11.5:Jellyfin.Server.Implementations/Users/UserManager.cs:344-389</c>),
    /// so the DTO's value means nothing and the writer takes the user's permission instead (seen on 10.11.5).
    /// </summary>
    public const string EnableLyricManagement = nameof(UserPolicy.EnableLyricManagement);

    /// <summary>
    /// The failed sign-in count: sign-ins change it on the cached user and save only the user row, so the cache and the
    /// database can differ in it for a moment without anyone's edit (<c>UserManager.cs:497-506</c>, 855-871).
    /// </summary>
    public const string InvalidLoginAttemptCount = nameof(UserPolicy.InvalidLoginAttemptCount);

    private static readonly PropertyInfo[] _properties = [.. typeof(UserPolicy)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
        .OrderBy(p => p.Name, StringComparer.Ordinal)];

    /// <summary>Gets the only fields Access Roles owns and writes: <c>EnableAllFolders</c> and <c>EnabledFolders</c>.</summary>
    public static IReadOnlySet<string> Managed { get; } = new HashSet<string>(StringComparer.Ordinal) { EnableAllFolders, EnabledFolders };

    /// <summary>Gets the names of every field, in ordinal order.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. _properties.Select(p => p.Name)];

    /// <summary>
    /// Writes every field of a policy as text: values in the invariant culture, ids in Jellyfin's <c>N</c> form, lists in
    /// their order, access schedules by their day and hours (not their row ids, which the core may renumber).
    /// </summary>
    /// <param name="policy">The policy.</param>
    /// <returns>The fields by name.</returns>
    public static IReadOnlyDictionary<string, string> Dump(UserPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in _properties)
        {
            fields[property.Name] = Text(property.GetValue(policy));
        }

        return fields;
    }

    /// <summary>
    /// Lists the fields whose values differ between two dumps, leaving some out.
    /// </summary>
    /// <param name="before">One dump.</param>
    /// <param name="after">The other.</param>
    /// <param name="except">Fields not to compare.</param>
    /// <returns>The differing fields, in ordinal order.</returns>
    public static IReadOnlyList<string> Differences(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after, IEnumerable<string> except)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var skip = (except ?? []).ToHashSet(StringComparer.Ordinal);
        return [.. before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Where(name => !skip.Contains(name))
            .Where(name => !before.TryGetValue(name, out var a) || !after.TryGetValue(name, out var b) || !string.Equals(a, b, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];
    }

    private static string Text(object? value) => value switch
    {
        null => "null",
        string text => JsonSerializer.Serialize(text),
        bool flag => flag ? "true" : "false",
        Guid id => id.ToString("N", CultureInfo.InvariantCulture),
        Enum member => member.ToString(),
        AccessSchedule schedule => string.Format(CultureInfo.InvariantCulture, "{0} {1}-{2}", schedule.DayOfWeek, schedule.StartHour, schedule.EndHour),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable list => "[" + string.Join(",", list.Cast<object?>().Select(Text)) + "]",
        _ => JsonSerializer.Serialize(value, value.GetType())
    };
}
