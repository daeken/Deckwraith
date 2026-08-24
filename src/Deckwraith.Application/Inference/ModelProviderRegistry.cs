using Deckwraith.Providers.Abstractions;

namespace Deckwraith.Application.Inference;

public interface IModelProviderRegistry
{
    IReadOnlyList<IModelProvider> Providers { get; }

    IModelProvider GetProvider(string providerId);
}

public sealed class ModelProviderRegistry : IModelProviderRegistry
{
    private readonly Dictionary<string, IModelProvider> _providers;

    public ModelProviderRegistry(IEnumerable<IModelProvider> providers)
    {
        var materialized = providers.ToDictionary(
            provider => provider.ProviderId,
            StringComparer.OrdinalIgnoreCase);
        if (materialized.Count == 0)
        {
            throw new ArgumentException("At least one model provider is required.", nameof(providers));
        }

        _providers = materialized;
    }

    public IModelProvider GetProvider(string providerId) =>
        _providers.TryGetValue(providerId, out var provider)
            ? provider
            : throw new KeyNotFoundException($"Model provider '{providerId}' is not registered.");

    public IReadOnlyList<IModelProvider> Providers =>
        _providers.Values.OrderBy(provider => provider.ProviderId, StringComparer.Ordinal).ToArray();
}
