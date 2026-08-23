using System.Text.Json;
using Deckwraith.Core.Context;
using Deckwraith.Core.Naming;
using Deckwraith.Core.State;

namespace Deckwraith.Core.Tests;

public sealed class ContextTests
{
    [Fact]
    public void ElisionReplacesACompletePairWithoutChangingArchiveProvenance()
    {
        var context = ContextWith(
            turn: 5,
            ContextItem.ToolInteraction(
                "item-1",
                "operation-1",
                "Get-DwThing",
                OperationStatus.Completed,
                completedAtTurn: 4,
                JsonSerializer.SerializeToElement(new { value = 1 }),
                JsonSerializer.SerializeToElement(new { value = 2 }),
                archiveFirstSequence: 10,
                archiveLastSequence: 11));

        var result = ToolElision.Apply(context, effectiveRetentionTurns: 0, DateTimeOffset.UnixEpoch);

        var marker = Assert.Single(result.Context.Items);
        Assert.Equal(ContextItemKind.ToolElision, marker.Kind);
        Assert.Null(marker.Input);
        Assert.Null(marker.Output);
        Assert.Equal(10, marker.ArchiveFirstSequence);
        Assert.Equal(11, marker.ArchiveLastSequence);
        Assert.Equal(["operation-1"], result.ElidedOperationIds);
    }

    [Fact]
    public void ElisionWaitsForSubsequentTurnsAndRetainsUnknownOutcomes()
    {
        var retained = ContextItem.ToolInteraction(
            "item-1",
            "operation-1",
            "Get-DwThing",
            OperationStatus.Completed,
            completedAtTurn: 4,
            JsonSerializer.SerializeToElement(new { }),
            JsonSerializer.SerializeToElement(new { }),
            10,
            11);
        var unknown = retained with
        {
            ItemId = "item-2",
            OperationId = "operation-2",
            Status = OperationStatus.OutcomeUnknown,
        };

        var result = ToolElision.Apply(
            ContextWith(turn: 6, retained, unknown),
            effectiveRetentionTurns: 2,
            DateTimeOffset.UnixEpoch);

        Assert.All(result.Context.Items, item => Assert.Equal(ContextItemKind.ToolInteraction, item.Kind));
        Assert.Empty(result.ElidedOperationIds);
    }

    [Fact]
    public void ManifestIsStableAcrossDictionaryAndToolEnumerationOrder()
    {
        var firstIdentity = IdentityDocument.CreateSparse(
            CanonicalName.Parse("wraith1"), DateTimeOffset.UnixEpoch) with
        {
            Calibration = new Dictionary<string, string>
            {
                ["register"] = "technical",
                ["opsec"] = "careful",
            },
        };
        var secondIdentity = firstIdentity with
        {
            Calibration = new Dictionary<string, string>
            {
                ["opsec"] = "careful",
                ["register"] = "technical",
            },
        };
        var context = ContextWith(turn: 0);
        ContextToolDescriptor[] firstTools =
        [
            new("Write-DwThing", "sha256:b"),
            new("Get-DwThing", "sha256:a"),
        ];

        var first = ContextManifestBuilder.Build(
            firstIdentity, context, "Finish it", "fake", "test", firstTools);
        var second = ContextManifestBuilder.Build(
            secondIdentity, context, "Finish it", "fake", "test", firstTools.Reverse());

        Assert.Equal(first.IdentityHash, second.IdentityHash);
        Assert.Equal(first.ToolCatalogHash, second.ToolCatalogHash);
        Assert.Equal(first.ManifestHash, second.ManifestHash);
    }

    private static CurrentContextDocument ContextWith(int turn, params ContextItem[] items) =>
        new(
            CurrentContextDocument.CurrentSchemaVersion,
            "wraith1",
            0,
            turn,
            0,
            "sha256:identity",
            0,
            8,
            items,
            DateTimeOffset.UnixEpoch);
}
