using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Hosting;

/// <summary>
/// KitDuplicateGuard: which folders count as another loaded copy of the plugin, which copy stands down, and what
/// KitAbiGuard.Register registers for each verdict. The folders are laid out as Jellyfin's installer does, with
/// synthetic DLLs with and without the guard.
/// </summary>
public sealed class KitDuplicateGuardTests : IDisposable
{
    private const string Dll = "Jellyfin.Plugin.KitTest.dll";
    private static readonly Version Server = JellyfinLine.Server;
    private readonly PluginCopies _copies = new();

    public void Dispose() => _copies.Dispose();

    [Fact]
    public void NoOtherFolder_NoSibling()
    {
        var own = Own("Kit Test_2.0.0.0", "2.0.0.0");
        _copies.AddBuild("Other Plugin_1.0.0.0", "Jellyfin.Plugin.Other.dll", Guid.NewGuid(), new Version(1, 0, 0, 0));
        Directory.CreateDirectory(_copies.Folder("configurations"));

        var verdict = Inspect(own);

        Assert.False(verdict.HasSiblings);
        Assert.False(verdict.StandsDown);
        Assert.Empty(verdict.ToRemove);
        Assert.Equal("2.0.0.0", verdict.Own!.Version.ToString());
    }

    [Fact]
    public void AFolderOfAnotherPluginWithTheSameDllName_IsNoSibling()
    {
        var own = Own("Kit Test_2.0.0.0", "2.0.0.0");
        _copies.AddBuild("Someone Else_1.0.0.0", Dll, Guid.NewGuid(), new Version(1, 0, 0, 0));

        Assert.False(Inspect(own).HasSiblings);
    }

    [Fact]
    public void LowerCopyWithoutTheGuard_ThisCopyStandsDown_AndRemovesIt()
    {
        var own = Own("Kit Test And More_2.5.2.0", "2.5.2.0");
        _copies.AddBuild("Kit Test_2.5.0.0", Dll, TestKit.PluginId, new Version(2, 5, 0, 0));

        var verdict = Inspect(own);

        Assert.True(verdict.StandsDown);
        Assert.Equal("Kit Test_2.5.0.0", Assert.Single(verdict.ToRemove).FolderName);
        Assert.Equal(
            "Kit Test 2.5.2.0 in the plugin folder 'Kit Test And More_2.5.2.0' is inactive for this start: an older copy of it, 'Kit Test_2.5.0.0' (2.5.0.0), "
            + "was loaded too and runs instead. This copy turns the older one off once the server has started; the log says whether that worked. "
            + "Then restart Jellyfin to finish the update.",
            verdict.Message);
    }

    [Fact]
    public void LowerCopyWithTheGuard_ThisCopyRuns_AndRemovesIt()
    {
        var own = Own("Kit Test And More_2.5.3.0", "2.5.3.0");
        _copies.AddBuild("Kit Test_2.5.2.0", Dll, TestKit.PluginId, new Version(2, 5, 2, 0), hasGuard: true);

        var verdict = Inspect(own);

        Assert.False(verdict.StandsDown);
        Assert.Equal("Kit Test_2.5.2.0", Assert.Single(verdict.ToRemove).FolderName);
        Assert.Contains("found an older copy of itself, 'Kit Test_2.5.2.0' (2.5.2.0), which stands down for this start", verdict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralLowerCopies_OneWithoutTheGuard_StandsDownAndRemovesAll()
    {
        var own = Own("C_3.0.0.0", "3.0.0.0");
        _copies.AddBuild("A_1.0.0.0", Dll, TestKit.PluginId, new Version(1, 0, 0, 0), hasGuard: true);
        _copies.AddBuild("B_2.0.0.0", Dll, TestKit.PluginId, new Version(2, 0, 0, 0));

        var verdict = Inspect(own);

        Assert.True(verdict.StandsDown);
        Assert.Equal(["B_2.0.0.0", "A_1.0.0.0"], verdict.ToRemove.Select(c => c.FolderName));
        Assert.Contains("older copies of it, 'B_2.0.0.0' (2.0.0.0) and 'A_1.0.0.0' (1.0.0.0), were loaded too and run instead. This copy turns the older ones off", verdict.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HigherCopy_ThisCopyStandsDown_AndRemovesNothing(bool higherHasGuard)
    {
        var own = Own("Kit Test_2.5.0.0", "2.5.0.0");
        _copies.AddBuild("Kit Test And More_2.5.2.0", Dll, TestKit.PluginId, new Version(2, 5, 2, 0), higherHasGuard);

        var verdict = Inspect(own);

        Assert.True(verdict.StandsDown);
        Assert.Empty(verdict.ToRemove);
        Assert.Equal(
            "Kit Test 2.5.0.0 in the plugin folder 'Kit Test_2.5.0.0' is inactive for this start: a newer copy of it, 2.5.2.0 in 'Kit Test And More_2.5.2.0', "
            + "was loaded too. The newer copy turns this one off. Restart Jellyfin to finish the update.",
            verdict.Message);
    }

    [Fact]
    public void UnreadableDll_CountsAsWithoutTheGuard()
    {
        var own = Own("Kit Test And More_2.5.2.0", "2.5.2.0");
        var folder = _copies.Folder("Kit Test_2.5.0.0");
        PluginCopies.WriteManifest(folder, TestKit.PluginId, "Kit Test", "2.5.0.0");
        File.WriteAllText(Path.Combine(folder, Dll), "not an assembly");

        var verdict = Inspect(own);

        Assert.False(KitDuplicateGuard.HasGuard(Path.Combine(folder, Dll)));
        Assert.True(verdict.StandsDown);
        Assert.Single(verdict.ToRemove);
    }

    [Fact]
    public void HasGuard_ReadsTheTypeFromMetadata()
    {
        var with = PluginCopies.WriteAssembly(Path.Combine(_copies.Folder("x"), "with.dll"), "With", new Version(1, 0), hasGuard: true);
        var without = PluginCopies.WriteAssembly(Path.Combine(_copies.Folder("x"), "without.dll"), "Without", new Version(1, 0), hasGuard: false);

        Assert.True(KitDuplicateGuard.HasGuard(with));
        Assert.False(KitDuplicateGuard.HasGuard(without));
        Assert.True(KitDuplicateGuard.HasGuard(typeof(KitDuplicateGuard).Assembly.Location));
        Assert.False(KitDuplicateGuard.HasGuard(typeof(Assert).Assembly.Location));
        Assert.False(KitDuplicateGuard.HasGuard(Path.Combine(_copies.Folder("x"), "missing.dll")));
    }

    [Theory]
    [InlineData("Active", true)]
    [InlineData("Restart", true)]
    [InlineData("0", true)]
    [InlineData("Disabled", false)]
    [InlineData("NotSupported", false)]
    [InlineData("Malfunctioned", false)]
    [InlineData("Deleted", false)]
    [InlineData("Superseded", false)]
    [InlineData("Superceded", false)]
    public void SiblingStatus_DecidesWhetherJellyfinLoadsIt_AndEveryLowerCopyButADeletedOneIsCleanedUp(string status, bool loads)
    {
        // A superseded lower copy stays superseded while this (enabled, higher) copy exists, on both lines. One that isn't
        // loaded is still removed: the catalog refresh would set it Active again.
        var own = Own("Kit Test And More_2.5.2.0", "2.5.2.0");
        var folder = _copies.AddBuild("Kit Test_2.5.0.0", Dll, TestKit.PluginId, new Version(2, 5, 0, 0));
        var text = File.ReadAllText(Path.Combine(folder, "meta.json"));
        File.WriteAllText(Path.Combine(folder, "meta.json"), text.Replace("\"status\":\"Active\"", status == "0" ? "\"status\":0" : $"\"status\":\"{status}\"", StringComparison.Ordinal));

        var verdict = Inspect(own);

        Assert.Equal(loads, verdict.HasSiblings);
        Assert.Equal(loads, verdict.StandsDown);
        if (status == "Deleted")
        {
            Assert.Empty(verdict.ToRemove);
        }
        else
        {
            Assert.Equal(loads, Assert.Single(verdict.ToRemove).IsLoaded);
        }

        if (!loads)
        {
            Assert.Empty(verdict.Message);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void SupersededHigherCopy_LoadsOnJellyfin12Only(bool jellyfin12, bool loads)
    {
        // Jellyfin 12 makes a superseded copy active again when no higher copy replaces it; 10.11 keeps it superseded
        // while any other enabled version exists.
        var own = Own("Kit Test_2.5.0.0", "2.5.0.0");
        var folder = _copies.AddBuild("Kit Test And More_2.5.2.0", Dll, TestKit.PluginId, new Version(2, 5, 2, 0));
        File.WriteAllText(Path.Combine(folder, "meta.json"), File.ReadAllText(Path.Combine(folder, "meta.json")).Replace("\"Active\"", "\"Superseded\"", StringComparison.Ordinal));

        var verdict = KitDuplicateGuard.Inspect("Kit Test", own, TestKit.PluginId, Server, ownRunsOnServer: true, jellyfin12Rules: jellyfin12);

        Assert.Equal(loads, verdict.HasSiblings);
        Assert.Equal(loads, verdict.StandsDown);
    }

    [Fact]
    public void FolderWithoutMetaJson_IsASibling_VersionedByItsAssembly()
    {
        var own = Own("Kit Test And More_2.5.2.0", "2.5.2.0");
        PluginCopies.WriteAssembly(Path.Combine(_copies.Folder("KitTest"), Dll), "Jellyfin.Plugin.KitTest", new Version(2, 4, 0, 0), hasGuard: false);

        var verdict = Inspect(own);

        var sibling = Assert.Single(verdict.ToRemove);
        Assert.False(sibling.HasManifest);
        Assert.Equal(new Version(2, 4, 0, 0), sibling.Version);
        Assert.True(verdict.StandsDown);
    }

    [Fact]
    public void WhatTheServerLoaded_DecidesOverTheFolders()
    {
        // A folder Jellyfin deleted in part at this start (meta.json gone, the DLL locked) reads as an Active copy, but
        // Jellyfin dropped it: no stand-down, and it is still cleaned up.
        var own = Own("Kit Test_2.5.2.0", "2.5.2.0");
        var leftover = _copies.Folder("Kit Test_2.5.1.0");
        PluginCopies.WriteAssembly(Path.Combine(leftover, Dll), "Jellyfin.Plugin.KitTest", new Version(2, 5, 1, 0), hasGuard: false);

        var guessed = Inspect(own);
        var told = KitDuplicateGuard.Inspect("Kit Test", own, TestKit.PluginId, Server, ownRunsOnServer: true, jellyfin12Rules: JellyfinLine.Is12, loadedByServer: _ => false);

        Assert.True(guessed.StandsDown);
        Assert.False(told.HasSiblings);
        Assert.False(told.StandsDown);
        Assert.Empty(told.Message);
        Assert.False(Assert.Single(told.ToRemove).IsLoaded);
    }

    [Fact]
    public void Register_AsksJellyfinsPluginManagerWhatItLoaded()
    {
        var own = Own("Kit Test And More_2.5.2.0", "2.5.2.0");
        var lower = _copies.AddBuild("Kit Test_2.5.0.0", Dll, TestKit.PluginId, new Version(2, 5, 0, 0));
        var manager = new Mock<IPluginManager>();
        var loaded = new LocalPlugin(Path.GetDirectoryName(own)!, true, new PluginManifest { Id = TestKit.PluginId, Name = "Kit Test And More", Version = "2.5.2.0" }) { DllFiles = [own] };
        var notLoaded = new LocalPlugin(lower, true, new PluginManifest { Id = TestKit.PluginId, Name = "Kit Test", Version = "2.5.0.0", Status = PluginStatus.Superseded });
        manager.SetupGet(m => m.Plugins).Returns([loaded, notLoaded]);
        var services = new ServiceCollection();
        services.AddSingleton(manager.Object);
        var warnings = new List<string>();

        Assert.True(KitAbiGuard.RegisterAt(services, Host(), TestKit.Options, (_, _) => { }, (_, m) => warnings.Add(m), own));

        Assert.Empty(warnings);
        Assert.False(Assert.IsType<KitAbiStatus>(Assert.Single(services, d => d.ServiceType == typeof(KitAbiStatus)).ImplementationInstance).IsDuplicate);
        Assert.Single(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void UnreadableMetaJson_IsReadAsMissing()
    {
        var own = Own("Kit Test And More_2.5.2.0", "2.5.2.0");
        var folder = _copies.AddBuild("Kit Test_2.5.0.0", Dll, TestKit.PluginId, new Version(2, 5, 0, 0), hasGuard: true);
        File.WriteAllText(Path.Combine(folder, "meta.json"), "{ not json");

        var sibling = Assert.Single(Inspect(own).ToRemove);

        Assert.False(sibling.HasManifest);
        Assert.Equal(new Version(2, 5, 0, 0), sibling.Version);
    }

    [Fact]
    public void ManifestListingOtherAssemblies_IsNoSibling()
    {
        var own = Own("Kit Test And More_2.5.2.0", "2.5.2.0");
        var folder = _copies.AddBuild("Kit Test_2.5.0.0", Dll, TestKit.PluginId, new Version(2, 5, 0, 0));
        File.WriteAllText(Path.Combine(folder, "meta.json"), File.ReadAllText(Path.Combine(folder, "meta.json")).Replace("\"assemblies\":[]", "\"assemblies\":[\"Other.dll\"]", StringComparison.Ordinal));
        PluginCopies.WriteAssembly(Path.Combine(folder, "Other.dll"), "Other", new Version(1, 0), hasGuard: false);

        Assert.False(Inspect(own).HasSiblings);
    }

    [Fact]
    public void TargetAbiAboveTheServer_IsNoSibling()
    {
        var own = Own("Kit Test And More_2.5.2.0", "2.5.2.0");
        var folder = _copies.Folder("Kit Test_2.5.0.0");
        PluginCopies.WriteAssembly(Path.Combine(folder, Dll), "Jellyfin.Plugin.KitTest", new Version(2, 5, 0, 0), hasGuard: false);
        PluginCopies.WriteManifest(folder, TestKit.PluginId, "Kit Test", "2.5.0.0", targetAbi: "99.0.0.0");

        Assert.False(Inspect(own).HasSiblings);
    }

    [Fact]
    public void SameVersion_TheFolderNameDecides()
    {
        // Every copy orders the copies the same way, so exactly one of them is the highest.
        var b = Own("B_2.5.2.0", "2.5.2.0");
        _copies.AddBuild("A_2.5.2.0", Dll, TestKit.PluginId, new Version(2, 5, 2, 0), hasGuard: true);

        var verdict = Inspect(b);

        Assert.False(verdict.StandsDown);
        Assert.Equal("A_2.5.2.0", Assert.Single(verdict.ToRemove).FolderName);
    }

    [Fact]
    public void HigherCopyForAnotherJellyfinLine_IsIgnored_AndNotRemoved()
    {
        var own = Own("Kit Test_2.5.0.0", "2.5.0.0");
        var folder = _copies.Folder("Kit Test And More_2.5.2.0");
        PluginCopies.WriteAssembly(Path.Combine(folder, Dll), "Jellyfin.Plugin.KitTest", new Version(2, 5, 2, 0), hasGuard: true, jellyfinTarget: JellyfinLine.OtherTarget);
        PluginCopies.WriteManifest(folder, TestKit.PluginId, "Kit Test And More", "2.5.2.0");

        var verdict = Inspect(own);

        Assert.True(verdict.HasSiblings);
        Assert.False(verdict.StandsDown);
        Assert.Empty(verdict.ToRemove);
    }

    [Fact]
    public void ThisCopyInertOnTheServer_StandsDownWithoutRemovingAnything()
    {
        var own = Own("Kit Test And More_2.5.2.0", "2.5.2.0");
        _copies.AddBuild("Kit Test_2.5.0.0", Dll, TestKit.PluginId, new Version(2, 5, 0, 0));

        var verdict = KitDuplicateGuard.Inspect("Kit Test", own, TestKit.PluginId, Server, ownRunsOnServer: false, jellyfin12Rules: JellyfinLine.Is12);

        Assert.True(verdict.StandsDown);
        Assert.Empty(verdict.ToRemove);
        Assert.Empty(verdict.Message);
    }

    [Fact]
    public void Register_WithALowerCopyWithoutTheGuard_RegistersTheStatusAndTheRemoval_AndWarnsOnce()
    {
        var own = Own("Kit Test And More_2.5.2.0", "2.5.2.0");
        _copies.AddBuild("Kit Test_2.5.0.0", Dll, TestKit.PluginId, new Version(2, 5, 0, 0));
        var services = new ServiceCollection();
        var errors = new List<string>();
        var warnings = new List<(string Category, string Message)>();

        var active = KitAbiGuard.RegisterAt(services, Host(), TestKit.Options, (_, m) => errors.Add(m), (c, m) => warnings.Add((c, m)), own);

        Assert.False(active);
        Assert.Empty(errors);
        var (category, message) = Assert.Single(warnings);
        Assert.Equal("Jellyfin.Plugin.KitTest.Kit.DuplicateGuard", category);
        Assert.Contains("'Kit Test_2.5.0.0' (2.5.0.0)", message, StringComparison.Ordinal);
        Assert.Contains("'Kit Test And More_2.5.2.0'", message, StringComparison.Ordinal);
        Assert.Equal(2, services.Count);
        var status = Assert.IsType<KitAbiStatus>(Assert.Single(services, d => d.ServiceType == typeof(KitAbiStatus)).ImplementationInstance);
        Assert.False(status.IsActive);
        Assert.True(status.IsDuplicate);
        Assert.Equal(message, status.Message);
        Assert.Single(services, d => d.ServiceType == typeof(IHostedService));
        Assert.False(KitDuplicateGuard.IsStandingDown);
    }

    [Fact]
    public void Register_WithALowerCopyWithTheGuard_IsActive_AndAddsTheRemoval()
    {
        var own = Own("Kit Test And More_2.5.3.0", "2.5.3.0");
        _copies.AddBuild("Kit Test_2.5.2.0", Dll, TestKit.PluginId, new Version(2, 5, 2, 0), hasGuard: true);
        var services = new ServiceCollection();
        var warnings = new List<string>();

        Assert.True(KitAbiGuard.RegisterAt(services, Host(), TestKit.Options, (_, _) => { }, (_, m) => warnings.Add(m), own));

        Assert.Empty(warnings);
        Assert.True(Assert.IsType<KitAbiStatus>(Assert.Single(services, d => d.ServiceType == typeof(KitAbiStatus)).ImplementationInstance).IsActive);
        Assert.Single(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void Register_WithAHigherCopy_RegistersOnlyTheStatus()
    {
        var own = Own("Kit Test_2.5.0.0", "2.5.0.0");
        _copies.AddBuild("Kit Test And More_2.5.2.0", Dll, TestKit.PluginId, new Version(2, 5, 2, 0), hasGuard: true);
        var services = new ServiceCollection();

        Assert.False(KitAbiGuard.RegisterAt(services, Host(), TestKit.Options, (_, _) => { }, (_, _) => { }, own));

        var status = Assert.IsType<KitAbiStatus>(Assert.Single(services).ImplementationInstance);
        Assert.True(status.IsDuplicate);
    }

    [Fact]
    public void Register_OnTheOtherLine_WithASibling_IsDuplicateWithTheAbiMessage()
    {
        var own = Own("Kit Test And More_2.5.2.0", "2.5.2.0");
        _copies.AddBuild("Kit Test_2.5.0.0", Dll, TestKit.PluginId, new Version(2, 5, 0, 0));
        var services = new ServiceCollection();
        var errors = new List<string>();
        var warnings = new List<string>();

        Assert.False(KitAbiGuard.RegisterAt(services, Host(JellyfinLine.OtherLineVersion), TestKit.Options, (_, m) => errors.Add(m), (_, m) => warnings.Add(m), own));

        var status = Assert.IsType<KitAbiStatus>(Assert.Single(services).ImplementationInstance);
        Assert.True(status.IsDuplicate);
        Assert.Equal(Assert.Single(errors), status.Message);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Register_WhenItsOwnFolderIsGone_RunsAsUsual()
    {
        var services = new ServiceCollection();
        var warnings = new List<string>();
        var missing = Path.Combine(_copies.PluginsPath, "gone", "Kit Test_1.0.0.0", Dll);
        Directory.CreateDirectory(Path.GetDirectoryName(missing)!);
        PluginCopies.WriteAssembly(missing, "Jellyfin.Plugin.KitTest", new Version(1, 0, 0, 0), hasGuard: true);
        Directory.Delete(Path.Combine(_copies.PluginsPath, "gone"), recursive: true);

        Assert.True(KitAbiGuard.RegisterAt(services, Host(), TestKit.Options, (_, _) => { }, (_, m) => warnings.Add(m), missing));

        Assert.Single(services);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ThrowIfStandingDown_DoesNothingForACopyThatRuns()
    {
        KitDuplicateGuard.ThrowIfStandingDown("Kit Test");
        Assert.False(KitDuplicateGuard.IsStandingDown);
    }

    private string Own(string folderName, string version)
    {
        var folder = _copies.Folder(folderName);
        PluginCopies.WriteAssembly(Path.Combine(folder, Dll), "Jellyfin.Plugin.KitTest", Version.Parse(version), hasGuard: true);
        PluginCopies.WriteManifest(folder, TestKit.PluginId, folderName.Split('_')[0], version);
        return Path.Combine(folder, Dll);
    }

    private static KitDuplicateVerdict Inspect(string own)
        => KitDuplicateGuard.Inspect("Kit Test", own, TestKit.PluginId, Server, ownRunsOnServer: true, jellyfin12Rules: JellyfinLine.Is12);

    private static IServerApplicationHost Host(string? version = null)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(Version.Parse(version ?? JellyfinLine.ServerVersion));
        return host.Object;
    }
}
