using Deckwraith.Application.Inference;
using Deckwraith.Credentials;
using Deckwraith.Providers.Abstractions;
using Deckwraith.Providers.Anthropic;
using Deckwraith.Providers.Google;
using Deckwraith.Providers.OpenAI;
using Deckwraith.Providers.OpenAICompatible;

namespace Deckwraith.Hosting;

public sealed record DeckwraithHostOptions(
    Uri AnthropicBaseUri,
    Uri GoogleBaseUri,
    Uri OpenAICompatibleBaseUri,
    Uri OpenAiSubscriptionBaseUri,
    Uri XaiBaseUri,
    Uri ZaiBaseUri,
    int EventCapacity = 2048)
{
    public IProviderCredentialStore CredentialStore { get; init; } =
        new PlatformProviderCredentialStore();

    public static DeckwraithHostOptions CreateDefault() => new(
        ReadUriEnvironment("DECKWRAITH_ANTHROPIC_BASE_URL", "https://api.anthropic.com/"),
        ReadUriEnvironment(
            "DECKWRAITH_GOOGLE_BASE_URL",
            "https://generativelanguage.googleapis.com/"),
        ReadUriEnvironment("DECKWRAITH_OPENAI_BASE_URL", "https://api.openai.com/"),
        ReadUriEnvironment(
            "DECKWRAITH_OPENAI_SUBSCRIPTION_BASE_URL",
            "https://chatgpt.com/"),
        ReadUriEnvironment("DECKWRAITH_XAI_BASE_URL", "https://api.x.ai/"),
        ReadUriEnvironment("DECKWRAITH_ZAI_BASE_URL", "https://api.z.ai/api/v1/"));

    public ModelProviderRegistry CreateProviderRegistry(
        IEnumerable<IModelProvider>? additionalProviders = null)
    {
        var openAiSubscriptionCredentials = new OpenAiSubscriptionCredentialManager(CredentialStore);
        var providers = new List<IModelProvider>
        {
            new OpenAiSubscriptionProvider(
                openAiSubscriptionCredentials,
                new OpenAiSubscriptionProviderOptions(OpenAiSubscriptionBaseUri)),
            new AnthropicProvider(new AnthropicProviderOptions(AnthropicBaseUri)),
            new GoogleGeminiProvider(new GoogleGeminiProviderOptions(GoogleBaseUri)),
            new OpenAICompatibleProvider(new OpenAICompatibleProviderOptions(OpenAICompatibleBaseUri)),
            new OpenAICompatibleProvider(new OpenAICompatibleProviderOptions(
                OpenAICompatibleBaseUri,
                ProviderId: "openai-api")),
            new OpenAICompatibleProvider(new OpenAICompatibleProviderOptions(
                XaiBaseUri,
                ApiKeyEnvironment: "XAI_API_KEY",
                ProviderId: "xai-api")),
            new OpenAICompatibleProvider(new OpenAICompatibleProviderOptions(
                ZaiBaseUri,
                ApiKeyEnvironment: "ZAI_API_KEY",
                ResponsesPath: "responses",
                ProviderId: "zai-api")),
        };
        if (additionalProviders is not null)
        {
            providers.AddRange(additionalProviders);
        }

        return new ModelProviderRegistry(providers);
    }

    private static Uri ReadUriEnvironment(string name, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(name);
        return new Uri(string.IsNullOrWhiteSpace(configured) ? fallback : configured);
    }
}
