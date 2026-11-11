using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Enforcement;

/// <summary>
/// The settings sections' server side: every <c>UserConfiguration</c> field in exactly
/// one section, the mergers' per-section copies and reports, the usersettings document's id forms and the key rules.
/// </summary>
public sealed class SettingsMergersTests
{
    [Fact]
    public void EveryUserConfigurationField_IsInExactlyOneSection_AndTheMergerKnowsIt()
    {
        // A field a later Jellyfin adds fails here until it is given a section (or reviewed as none).
        var properties = typeof(UserConfiguration).GetProperties().Where(p => p.CanWrite).Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        var mapped = SettingsSections.ConfigurationFields.Values.SelectMany(f => f).ToList();

        Assert.Equal(properties, mapped.Order(StringComparer.Ordinal));
        Assert.Equal(mapped.Count, mapped.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(properties, UserConfigurationMerger.KnownFields.Order(StringComparer.Ordinal));
        Assert.Equal(16, properties.Count);
    }

    [Fact]
    public void TheSections_ServerEnforcedAndHiddenOnly_SplitTheNineSettingsSections()
    {
        Assert.Equal(
            [AccessRolesLock.Profile, AccessRolesLock.DisplayAccount, AccessRolesLock.Home, AccessRolesLock.Playback, AccessRolesLock.Subtitles, AccessRolesLock.QuickConnect, AccessRolesLock.EnhancedPanel],
            SettingsSections.ServerEnforced);
        Assert.Equal([AccessRolesLock.DisplayDevice, AccessRolesLock.Controls], SettingsSections.HiddenOnly.Order());
        Assert.Equal(AccessPresets.SettingsSections.Order(), SettingsSections.ServerEnforced.Concat(SettingsSections.HiddenOnly).Order());
        Assert.Equal("Settings.Home", SettingsSections.RuleName(AccessRolesLock.Home));
    }

    [Theory]
    [InlineData("homesection0", AccessRolesLock.Home)]
    [InlineData("HOMESECTION0", AccessRolesLock.Home)]
    [InlineData("homesection12", AccessRolesLock.Home)]
    [InlineData("HomeSectionX", AccessRolesLock.Home)]
    [InlineData("TvHome", AccessRolesLock.Home)]
    [InlineData("Landing-abc", AccessRolesLock.Home)]
    [InlineData("SKIPBACKLENGTH", AccessRolesLock.Playback)]
    [InlineData("skipForwardLength", AccessRolesLock.Playback)]
    [InlineData("DashboardTheme", AccessRolesLock.DisplayAccount)]
    [InlineData("useEpisodeImagesInNextUpAndResume", AccessRolesLock.DisplayAccount)]
    [InlineData("enableNextVideoInfoOverlay", null)]
    [InlineData("enableVideoRemainingTime", null)]
    [InlineData("chromecastVersion", null)]
    [InlineData("landing", null)]
    public void DisplayPreferenceKeys_BelongToTheirSection_InAnyLetterCase(string key, AccessRolesLock? section)
        => Assert.Equal(section, SettingsSections.SectionOfKey(key));

    [Fact]
    public void TheConfigurationMerge_CopiesALockedSectionsFields_AndReportsOnlyTheChangedOnes()
    {
        var posted = FilterHarness.Stored();
        posted.SubtitleLanguagePreference = "eng";
        posted.AudioLanguagePreference = "eng";

        var reverted = UserConfigurationMerger.Merge(posted, FilterHarness.Stored(), new HashSet<AccessRolesLock> { AccessRolesLock.Subtitles });

        Assert.Equal("deu", posted.SubtitleLanguagePreference);
        Assert.Equal("eng", posted.AudioLanguagePreference);
        Assert.Equal([nameof(UserConfiguration.SubtitleLanguagePreference)], Assert.Single(reverted).Value);
        Assert.Empty(UserConfigurationMerger.Merge(FilterHarness.Stored(), FilterHarness.Stored(), new HashSet<AccessRolesLock>(AccessPresets.AllLocks)));
        Assert.False(UserConfigurationMerger.Applies(new HashSet<AccessRolesLock> { AccessRolesLock.QuickConnect, AccessRolesLock.Controls }));
    }

    [Theory]
    [InlineData(nameof(UserConfiguration.AudioLanguagePreference), AccessRolesLock.Playback)]
    [InlineData(nameof(UserConfiguration.CastReceiverId), AccessRolesLock.Playback)]
    [InlineData(nameof(UserConfiguration.SubtitleLanguagePreference), AccessRolesLock.Subtitles)]
    public void TheConfigurationMerge_TakesATextNeverSetAndOneSavedEmpty_AsTheSame(string field, AccessRolesLock section)
    {
        // A freshly seeded user's AudioLanguagePreference is null and jellyfin-web's Playback form
        // posts "" for it, which was answered X-AccessRoles-Kept: Playback although nothing changed.
        var property = typeof(UserConfiguration).GetProperty(field)!;
        var stored = FilterHarness.Stored();
        property.SetValue(stored, null);
        var posted = FilterHarness.Stored();
        property.SetValue(posted, string.Empty);
        var locked = new HashSet<AccessRolesLock> { section };

        var same = UserConfigurationMerger.Merge(posted, stored, locked);
        var back = FilterHarness.Stored();
        property.SetValue(back, null);
        var emptyStored = FilterHarness.Stored();
        property.SetValue(emptyStored, string.Empty);
        var sameTheOtherWay = UserConfigurationMerger.Merge(back, emptyStored, locked);
        var changed = FilterHarness.Stored();
        property.SetValue(changed, "fre");
        var reverted = UserConfigurationMerger.Merge(changed, stored, locked);

        Assert.DoesNotContain(field, same.Values.SelectMany(v => v));
        Assert.Null(property.GetValue(posted));
        Assert.DoesNotContain(field, sameTheOtherWay.Values.SelectMany(v => v));

        // The negative control: a real change is still reverted and reported.
        Assert.Contains(field, reverted[section]);
        Assert.Null(property.GetValue(changed));
    }

    [Theory]
    [InlineData("usersettings")]
    [InlineData("3ce5b65de116d73165d1efc4a30ec35c")]
    [InlineData("3ce5b65d-e116-d731-65d1-efc4a30ec35c")]
    [InlineData("{3CE5B65D-E116-D731-65D1-EFC4A30EC35C}")]
    [InlineData("(3ce5b65d-e116-d731-65d1-efc4a30ec35c)")]
    public void TheUserSettingsDocument_IsTheSameUnderEveryIdForm(string id)
    {
        Assert.Equal(Guid.Parse("3ce5b65d-e116-d731-65d1-efc4a30ec35c"), DisplayPreferencesMerger.UserSettingsDocumentId);
        Assert.True(DisplayPreferencesMerger.IsUserSettings(id, "emby"));
        Assert.True(DisplayPreferencesMerger.IsUserSettings(id, "EMBY"));
        Assert.False(DisplayPreferencesMerger.IsUserSettings(id, "emby2"));
        Assert.False(DisplayPreferencesMerger.IsUserSettings("UserSettings", "emby"));
    }

    [Fact]
    public void TheDisplayMerge_WritesTheStoredKeysBack_WhenThePostLeftThemOut_AndReportsThem()
    {
        var stored = new DisplayPreferences(Guid.NewGuid(), DisplayPreferencesMerger.UserSettingsDocumentId, "emby") { SkipBackwardLength = 5000, SkipForwardLength = 15000 };
        stored.HomeSections.Add(new HomeSection { Order = 0, Type = HomeSectionType.NextUp });
        var posted = new DisplayPreferencesDto { CustomPrefs = new Dictionary<string, string?> { ["chromecastVersion"] = "stable" } };

        var reverted = DisplayPreferencesMerger.Merge(posted, stored, new Dictionary<string, string?>(), new HashSet<AccessRolesLock> { AccessRolesLock.Home, AccessRolesLock.Playback });

        Assert.Equal(("nextup", "5000", "15000", "stable"), (posted.CustomPrefs["homesection0"], posted.CustomPrefs["skipBackLength"], posted.CustomPrefs["skipForwardLength"], posted.CustomPrefs["chromecastVersion"]));
        Assert.True(posted.CustomPrefs.ContainsKey("tvhome"));
        Assert.Equal([AccessRolesLock.Home, AccessRolesLock.Playback], reverted.Keys.Order());

        // Posting exactly the stored values reports nothing; an unset column posted as "" is no change either.
        var again = new DisplayPreferencesDto { CustomPrefs = new Dictionary<string, string?>(posted.CustomPrefs) { ["tvhome"] = string.Empty } };
        Assert.Empty(DisplayPreferencesMerger.Merge(again, stored, new Dictionary<string, string?>(), new HashSet<AccessRolesLock> { AccessRolesLock.Home, AccessRolesLock.Playback }));
    }

    [Fact]
    public void TheDisplayMerge_OfAPostWithoutCustomPrefs_GivesItTheStoredKeys()
    {
        var stored = new DisplayPreferences(Guid.NewGuid(), DisplayPreferencesMerger.UserSettingsDocumentId, "emby") { DashboardTheme = "dark" };
        var posted = new DisplayPreferencesDto { CustomPrefs = null! };

        DisplayPreferencesMerger.Merge(posted, stored, new Dictionary<string, string?> { ["useEpisodeImagesInNextUpAndResume"] = "true" }, new HashSet<AccessRolesLock> { AccessRolesLock.DisplayAccount });

        Assert.Equal(("dark", "true"), (posted.CustomPrefs["dashboardTheme"], posted.CustomPrefs["useEpisodeImagesInNextUpAndResume"]));
    }

    [Fact]
    public void AnUnchangedDisplaySave_ReportsNothing_ThoughTheWebPostsItsDefaultsForKeysNeverStored()
    {
        // jellyfin-web 10.11.5's Display page posts useEpisodeImagesInNextUpAndResume "false"
        // (the disabled checkbox) and its default theme on every save, stored or not; a user who never saved them has
        // neither stored (DashboardTheme null, no custom key). That is no change, so the guard says nothing.
        var display = new HashSet<AccessRolesLock> { AccessRolesLock.DisplayAccount, AccessRolesLock.Playback };
        var stored = new DisplayPreferences(Guid.NewGuid(), DisplayPreferencesMerger.UserSettingsDocumentId, "emby") { SkipBackwardLength = 10000, SkipForwardLength = 30000 };
        var unchanged = new DisplayPreferencesDto { CustomPrefs = new Dictionary<string, string?> { ["useEpisodeImagesInNextUpAndResume"] = "false", ["dashboardTheme"] = "dark", ["chromecastVersion"] = "stable" } };

        Assert.Empty(DisplayPreferencesMerger.Merge(unchanged, stored, new Dictionary<string, string?>(), display));
        Assert.False(unchanged.CustomPrefs.ContainsKey("useEpisodeImagesInNextUpAndResume"), "nothing stored, nothing written");

        // A stored empty theme (the core stores "" for a post without one) is the default too; the skip lengths left out are
        // the core's defaults, the same as the stored ones.
        stored.DashboardTheme = string.Empty;
        Assert.Empty(DisplayPreferencesMerger.Merge(new DisplayPreferencesDto { CustomPrefs = new Dictionary<string, string?> { ["dashboardTheme"] = "dark" } }, stored, new Dictionary<string, string?>(), display));

        // A real change still reports: another value than the stored one, or than the default when nothing is stored.
        Assert.Equal(["useEpisodeImagesInNextUpAndResume"], DisplayPreferencesMerger.Merge(new DisplayPreferencesDto { CustomPrefs = new Dictionary<string, string?> { ["useEpisodeImagesInNextUpAndResume"] = "true", ["dashboardTheme"] = "dark" } }, stored, new Dictionary<string, string?>(), display)[AccessRolesLock.DisplayAccount]);
        Assert.Equal(["dashboardTheme"], DisplayPreferencesMerger.Merge(new DisplayPreferencesDto { CustomPrefs = new Dictionary<string, string?> { ["dashboardTheme"] = "light" } }, stored, new Dictionary<string, string?>(), display)[AccessRolesLock.DisplayAccount]);
        Assert.Equal(["useEpisodeImagesInNextUpAndResume"], DisplayPreferencesMerger.Merge(new DisplayPreferencesDto { CustomPrefs = new Dictionary<string, string?> { ["useEpisodeImagesInNextUpAndResume"] = "false" } }, stored, new Dictionary<string, string?> { ["useEpisodeImagesInNextUpAndResume"] = "true" }, display)[AccessRolesLock.DisplayAccount]);
        Assert.Equal(["skipForwardLength"], DisplayPreferencesMerger.Merge(new DisplayPreferencesDto { CustomPrefs = new Dictionary<string, string?> { ["skipForwardLength"] = "45000" } }, stored, new Dictionary<string, string?>(), display)[AccessRolesLock.Playback]);
    }

    [Fact]
    public void TheWebClientsDefaultTheme_IsItsConfigsDefaultOne_ElseDark()
    {
        var web = Directory.CreateTempSubdirectory("accessroles-web-");
        try
        {
            Assert.Equal("dark", WebClientDefaults.DefaultTheme(web.FullName));
            Assert.Equal("dark", WebClientDefaults.DefaultTheme(null));
            File.WriteAllText(Path.Combine(web.FullName, "config.json"), "{\"themes\":[{\"name\":\"Dark\",\"id\":\"dark\"},{\"name\":\"Light\",\"id\":\"light\",\"default\":true}]}");
            Assert.Equal("light", WebClientDefaults.DefaultTheme(web.FullName));

            // A server whose web client defaults to Light: an unchanged save posts "light" for a theme never stored.
            var defaults = new WebClientDefaults(web.FullName).Defaults;
            var stored = new DisplayPreferences(Guid.NewGuid(), DisplayPreferencesMerger.UserSettingsDocumentId, "emby");
            var display = new HashSet<AccessRolesLock> { AccessRolesLock.DisplayAccount };
            Assert.Empty(DisplayPreferencesMerger.Merge(new DisplayPreferencesDto { CustomPrefs = new Dictionary<string, string?> { ["dashboardTheme"] = "light" } }, stored, new Dictionary<string, string?>(), display, defaults));
            Assert.Single(DisplayPreferencesMerger.Merge(new DisplayPreferencesDto { CustomPrefs = new Dictionary<string, string?> { ["dashboardTheme"] = "dark" } }, stored, new Dictionary<string, string?>(), display, defaults));

            File.WriteAllText(Path.Combine(web.FullName, "config.json"), "{ not json");
            Assert.Equal("dark", WebClientDefaults.DefaultTheme(web.FullName));
        }
        finally
        {
            web.Delete(recursive: true);
        }
    }
}
