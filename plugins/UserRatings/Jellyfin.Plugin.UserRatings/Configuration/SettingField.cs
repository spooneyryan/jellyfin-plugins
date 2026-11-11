using System.Collections.Generic;

namespace Jellyfin.Plugin.UserRatings.Configuration;

/// <summary>
/// One field of the Settings tab (<see cref="RatingsSettingsFields.Fields"/>).
/// </summary>
/// <param name="Name">The setting's name, as <see cref="PluginConfiguration"/> and the API have it.</param>
/// <param name="Label">The label the report uses.</param>
/// <param name="RestartNeeded">Whether a change takes effect only after the server restarts.</param>
/// <param name="Min">A number's least value.</param>
/// <param name="Max">A number's greatest value.</param>
/// <param name="Names">A name setting's values, as a save writes them.</param>
internal sealed record SettingField(string Name, string Label, bool RestartNeeded = false, int Min = 0, int Max = 0, IReadOnlyList<string>? Names = null);
