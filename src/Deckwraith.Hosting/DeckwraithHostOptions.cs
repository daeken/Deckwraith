using Deckwraith.Application.Inference;
using Deckwraith.Providers.Abstractions;
using Deckwraith.Providers.Anthropic;
using Deckwraith.Providers.Google;
using Deckwraith.Providers.OpenAI;
using Deckwraith.Providers.OpenAICompatible;

namespace Deckwraith.Hosting;

public sealed record DeckwraithHostOptions(
    string CodexExecutablePath,
    string ProviderWorkingDirectory,
    Uri AnthropicBaseUri,
    Uri GoogleBaseUri,
    Uri OpenAICompatibleBaseUri,
    int EventCapacity = 2048)
{
    public static DeckwraithHostOptions CreateDefault() => new(
        ResolveCodexExecutable(),
        Path.GetTempPath(),
        ReadUriEnvironment("DECKWRAITH_ANTHROPIC_BASE_URL", "https://api.anthropic.com/"),
        ReadUriEnvironment(
            "DECKWRAITH_GOOGLE_BASE_URL",
            "https://generativelanguage.googleapis.com/"),
        ReadUriEnvironment("DECKWRAITH_OPENAI_BASE_URL", "https://api.openai.com/"));

    public ModelProviderRegistry CreateProviderRegistry(
        IEnumerable<IModelProvider>? additionalProviders = null)
    {
        var providers = new List<IModelProvider>
        {
            new CodexAppServerProvider(new CodexAppServerProviderOptions(
                CodexExecutablePath,
                ProviderWorkingDirectory)),
            new AnthropicProvider(new AnthropicProviderOptions(AnthropicBaseUri)),
            new GoogleGeminiProvider(new GoogleGeminiProviderOptions(GoogleBaseUri)),
            new OpenAICompatibleProvider(new OpenAICompatibleProviderOptions(OpenAICompatibleBaseUri)),
        };
        if (additionalProviders is not null)
        {
            providers.AddRange(additionalProviders);
        }

        return new ModelProviderRegistry(providers);
    }

    private static string ResolveCodexExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("DECKWRAITH_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        const string desktopPath = "/Applications/ChatGPT.app/Contents/Resources/codex";
        return File.Exists(desktopPath) ? desktopPath : "codex";
    }

    private static Uri ReadUriEnvironment(string name, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(name);
        return new Uri(string.IsNullOrWhiteSpace(configured) ? fallback : configured);
    }
}
