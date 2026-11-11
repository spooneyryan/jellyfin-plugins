using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using Xunit;
using static Jellyfin.Plugin.SmartSearch.Tests.AutoCollections.AutoCollectionTestData;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>
/// The definition rules, on save and before every run: the same checks catch a query edited after the definition was
/// saved, a deleted user and a collection that became an auto-collection.
/// </summary>
public sealed class AutoCollectionChecksTests
{
    private const string PerUser = "{\"Field\":\"Played\",\"Op\":\"Is\",\"Value\":false}";
    private const string AdminOnly = "{\"Field\":\"Path\",\"Op\":\"Contains\",\"Value\":\"film\"}";

    private readonly Jellyfin.Database.Implementations.Entities.User _admin = Admin();
    private readonly Jellyfin.Database.Implementations.Entities.User _manager = PlannerWorld.NewUser("manager");
    private readonly Jellyfin.Database.Implementations.Entities.User _viewer = PlannerWorld.NewUser("viewer");

    private AutoCollectionCheck Run(AutoCollectionDefinition definition, params Storage.SavedQueryRecord[] queries)
        => Checks.CheckForRun(definition, Context(queries, [_admin, _manager, _viewer]));

    [Fact]
    public void AValidReference_Runs_WithItsQueryAndHash()
    {
        var query = SavedQuery(_manager.Id, Q());

        var check = Run(Definition(_manager.Id, query.Id), query);

        Assert.True(check.Ok, string.Join("; ", check.Messages));
        Assert.Equal(2020, check.Query!.Where.Children![0].Value!.Value.GetInt32());
        Assert.StartsWith("sha256:", check.QueryHash, StringComparison.Ordinal);
        Assert.Same(_manager, check.Creator);
        Assert.False(check.UsesPerUserFields);
    }

    [Fact]
    public void ASnapshot_RunsItsCopy()
    {
        Assert.True(Run(Snapshot(_manager.Id, Q())).Ok);
        Assert.Equal(AutoCollectionReasons.SourceMissing, Run(Snapshot(_manager.Id, Q()) with { Source = new AutoCollectionSource { Mode = SmartSearchAutoCollectionSourceMode.Snapshot } }).Reason);
    }

    [Fact]
    public void ADeletedCreator_IsCreatorMissing_BeforeAnythingElse()
    {
        var gone = Guid.NewGuid();

        var check = Run(Definition(gone, Guid.NewGuid()));

        Assert.Equal(SmartSearchAutoCollectionStatus.CreatorMissing, check.Status);
        Assert.Equal(AutoCollectionReasons.CreatorDeleted, check.Reason);
    }

    [Fact]
    public void AMissingQuery_IsQueryMissing_AndAQueryGivenToSomeoneElse_IsQueryInvalid()
    {
        var query = SavedQuery(_viewer.Id, Q());

        Assert.Equal(SmartSearchAutoCollectionStatus.QueryMissing, Run(Definition(_manager.Id, Guid.NewGuid()), query).Status);
        var foreign = Run(Definition(_manager.Id, query.Id), query);
        Assert.Equal(SmartSearchAutoCollectionStatus.QueryInvalid, foreign.Status);
        Assert.Equal(AutoCollectionReasons.SourceOwnerChanged, foreign.Reason);
    }

    [Theory]
    [InlineData("{\"Version\":9,\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Where\":{\"Children\":[]}}", AutoCollectionReasons.QueryUnsupported)]
    [InlineData("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Where\":{\"Children\":[{\"Field\":\"Nope\",\"Op\":\"Is\",\"Value\":1}]}}", AutoCollectionReasons.QueryUnsupported)]
    [InlineData("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Where\":{\"Children\":[{\"Field\":\"Year\",\"Op\":\"Ge\",\"Value\":1}]}}", AutoCollectionReasons.InvalidQuery)]
    [InlineData("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Where\":{\"Children\":[{\"Field\":\"MemberRating\",\"Op\":\"Ge\",\"Value\":3}]}}", AutoCollectionReasons.FieldUnavailable)]
    [InlineData("{\"Scope\":{\"ItemKinds\":[\"Movie\",\"BoxSet\"]},\"Where\":{\"Children\":[]}}", AutoCollectionReasons.BoxSetKind)]
    [InlineData("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Where\":{\"Children\":[]},\"Limit\":5001}", AutoCollectionReasons.LimitAboveCap)]
    public void AQueryEditedAfterTheSave_IsCaughtAtTheNextRun(string edited, string reason)
    {
        var query = SavedQuery(_manager.Id, Q());
        var definition = Definition(_manager.Id, query.Id);
        Assert.True(Run(definition, query).Ok);

        var check = Run(definition, query with { Query = System.Text.Json.JsonDocument.Parse(edited).RootElement.Clone() });

        Assert.Equal(SmartSearchAutoCollectionStatus.QueryInvalid, check.Status);
        Assert.Equal(reason, check.Reason);
        Assert.NotEmpty(check.Messages);
    }

    [Fact]
    public void AnAdminOnlyField_RunsOnlyForAnAdministratorCreator()
    {
        var mine = SavedQuery(_admin.Id, Q(AdminOnly));
        var theirs = SavedQuery(_manager.Id, Q(AdminOnly));

        Assert.True(Run(Definition(_admin.Id, mine.Id), mine).Ok);
        var check = Run(Definition(_manager.Id, theirs.Id), theirs);
        Assert.Equal(AutoCollectionReasons.AdminOnlyField, check.Reason);
    }

    [Fact]
    public void AnInCollectionRule_NamingAnAutoCollection_IsACycle_OnEveryRun()
    {
        var box = Guid.NewGuid();
        var query = SavedQuery(_manager.Id, Q("{\"Field\":\"InCollection\",\"Op\":\"AnyOf\",\"Values\":[\"" + box + "\"]}"));
        var definition = Definition(_manager.Id, query.Id);

        // A normal collection at save time...
        Assert.True(Checks.CheckForRun(definition, Context([query], [_manager])).Ok);

        // ...that later became an auto-collection (re-adopted).
        var check = Checks.CheckForRun(definition, Context([query], [_manager], new HashSet<Guid> { box }));
        Assert.Equal(AutoCollectionReasons.CycleWithAutoCollection, check.Reason);
        Assert.Equal([box], Checks.CollectionsNamed(Read(query)));
    }

    [Fact]
    public void PerUserRules_NeedAnEvaluationUserWhoExists()
    {
        var query = SavedQuery(_manager.Id, Q(PerUser));
        var definition = Definition(_manager.Id, query.Id);

        Assert.Equal(AutoCollectionReasons.EvaluationUserRequired, Run(definition, query).Reason);
        Assert.Equal(SmartSearchAutoCollectionStatus.EvaluationUserMissing, Run(definition, query).Status);
        Assert.Equal(AutoCollectionReasons.EvaluationUserDeleted, Run(definition with { EvaluationUserId = Guid.NewGuid() }, query).Reason);
        var ok = Run(definition with { EvaluationUserId = _viewer.Id }, query);
        Assert.True(ok.Ok);
        Assert.Same(_viewer, ok.EvaluationUser);
        Assert.True(ok.UsesPerUserFields);
    }

    [Fact]
    public void ALeftoverEvaluationUser_IsClearedByARun_AndRefusedOnASave()
    {
        var query = SavedQuery(_manager.Id, Q());
        var definition = Definition(_manager.Id, query.Id) with { EvaluationUserId = _manager.Id };

        var run = Run(definition, query);
        Assert.True(run.Ok);
        Assert.True(run.ClearEvaluationUser);
        Assert.Null(run.EvaluationUser);

        var save = Checks.CheckForSave(definition, Context([query], [_manager]), _manager, false);
        Assert.Equal(AutoCollectionReasons.EvaluationUserNotNeeded, save.Reason);
    }

    [Fact]
    public void OnASave_OnlyAnAdministratorPicksSomeoneElsesWatchState()
    {
        var query = SavedQuery(_manager.Id, Q(PerUser));
        var context = Context([query], [_admin, _manager, _viewer]);
        var forViewer = Definition(_manager.Id, query.Id) with { EvaluationUserId = _viewer.Id };

        Assert.Equal(AutoCollectionReasons.EvaluationUserNotAllowed, Checks.CheckForSave(forViewer, context, _manager, false).Reason);
        Assert.True(Checks.CheckForSave(forViewer with { EvaluationUserId = _manager.Id }, context, _manager, false).Ok);
        Assert.True(Checks.CheckForSave(forViewer, context, _admin, true).Ok);

        // An edit may keep the evaluation user an administrator chose.
        Assert.True(Checks.CheckForSave(forViewer, context, _manager, false, keptEvaluationUser: _viewer.Id).Ok);
    }

    [Fact]
    public void ANewerDefinition_FailsClosed_EvenWithAValidQuery()
    {
        var query = SavedQuery(_manager.Id, Q());
        var definition = Definition(_manager.Id, query.Id);
        var unknownMode = definition with { ManualEdits = new AutoCollectionManualEdits { Mode = new StoredEnum<SmartSearchManualEditsMode>(SmartSearchManualEditsMode.Unknown, "AddOnly") } };

        var check = Run(unknownMode, query);

        Assert.Equal(SmartSearchAutoCollectionStatus.NeedsNewerVersion, check.Status);
        Assert.Equal(AutoCollectionReasons.NewerDefinition, check.Reason);
        Assert.Null(check.Query);
    }

    private static Jellyfin.Plugin.SmartSearch.Api.Models.SmartSearchQueryDefinition Read(Storage.SavedQueryRecord record)
        => Jellyfin.Plugin.SmartSearch.Query.QueryJson.Read(System.Text.Encoding.UTF8.GetBytes(record.Query.GetRawText())).Definition!;
}
