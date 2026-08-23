using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;

namespace Deckwraith.Core.Context;

public sealed record ContextToolDescriptor(string Name, string SchemaHash);

public sealed record ContextManifest(
    int SchemaVersion,
    string Agent,
    string Provider,
    string Model,
    string IdentityHash,
    string CurrentContextHash,
    string ObjectiveHash,
    string ToolCatalogHash,
    int ContextRevision,
    int ContextItemCount,
    string ManifestHash)
{
    public const int CurrentSchemaVersion = 1;
}

public static class ContextManifestBuilder
{
    public static ContextManifest Build(
        IdentityDocument identity,
        CurrentContextDocument context,
        string objective,
        string provider,
        string model,
        IEnumerable<ContextToolDescriptor> tools)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objective);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var identityHash = CanonicalJson.Hash(identity);
        var contextHash = CanonicalJson.Hash(context);
        var objectiveHash = CanonicalJson.Hash(objective);
        var orderedTools = tools.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
        var toolCatalogHash = CanonicalJson.Hash(orderedTools);
        var unsigned = new
        {
            SchemaVersion = ContextManifest.CurrentSchemaVersion,
            Agent = identity.Name,
            Provider = provider,
            Model = model,
            IdentityHash = identityHash,
            CurrentContextHash = contextHash,
            ObjectiveHash = objectiveHash,
            ToolCatalogHash = toolCatalogHash,
            ContextRevision = context.Revision,
            ContextItemCount = context.Items.Count,
        };
        return new ContextManifest(
            unsigned.SchemaVersion,
            unsigned.Agent,
            unsigned.Provider,
            unsigned.Model,
            unsigned.IdentityHash,
            unsigned.CurrentContextHash,
            unsigned.ObjectiveHash,
            unsigned.ToolCatalogHash,
            unsigned.ContextRevision,
            unsigned.ContextItemCount,
            CanonicalJson.Hash(unsigned));
    }
}
