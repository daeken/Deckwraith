namespace Deckwraith.Core.Context;

public sealed record ToolElisionResult(
    CurrentContextDocument Context,
    IReadOnlyList<string> ElidedOperationIds);

public static class ToolElision
{
    public static ToolElisionResult Apply(
        CurrentContextDocument context,
        int effectiveRetentionTurns,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(effectiveRetentionTurns);
        var elided = new List<string>();
        var items = context.Items.Select(item =>
        {
            if (!IsEligible(item, context.Turn, effectiveRetentionTurns))
            {
                return item;
            }

            elided.Add(item.OperationId!);
            return item with
            {
                Kind = ContextItemKind.ToolElision,
                Text = "Full tool input and output remain retrievable from the archive.",
                Input = null,
                Output = null,
            };
        }).ToArray();

        if (elided.Count == 0 && context.ToolElisionTurns == effectiveRetentionTurns)
        {
            return new ToolElisionResult(context, []);
        }

        return new ToolElisionResult(
            context with
            {
                Revision = checked(context.Revision + 1),
                ToolElisionTurns = effectiveRetentionTurns,
                Items = items,
                UpdatedAt = now,
            },
            elided);
    }

    private static bool IsEligible(ContextItem item, int currentTurn, int retentionTurns) =>
        item.Kind is ContextItemKind.ToolInteraction &&
        item.OperationId is not null &&
        item.CompletedAtTurn is { } completedAtTurn &&
        item.Status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled &&
        currentTurn - completedAtTurn > retentionTurns;
}
