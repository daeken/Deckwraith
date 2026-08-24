using Deckwraith.Application.State;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;
using Deckwraith.Persistence.Archives;
using Deckwraith.Persistence.Artifacts;
using Deckwraith.Persistence.Git;
using Deckwraith.Persistence.State;

namespace Deckwraith.IntegrationTests;

public sealed class IdentityEditingEndToEndTests
{
    [Fact]
    public async Task IdentityPersonalityAndCalibrationAreEditedAsOneCheckpoint()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(), $"deckwraith-identity-edit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        try
        {
            var state = new JsonDeckStateStore(rootPath);
            using var spine = new StateSpine(
                state,
                new JsonlAgentArchive(rootPath),
                new ContentAddressedArtifactStore(rootPath),
                new GitCheckpointStore(rootPath));
            await spine.InitializeAsync(CancellationToken.None);
            var created = await spine.CreateWraithAsync("lumen", CancellationToken.None);
            await spine.CreateHauntAsync("deckwraith", CancellationToken.None);

            var updated = await spine.UpdateIdentityAsync(
                "lumen",
                created.Value with
                {
                    Personality = "curious, rigorous, and a little feral",
                    Calibration = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["register"] = "terse and playful",
                        ["opsec"] = "never disclose credentials",
                    },
                    Pronouns = ["it", "she"],
                },
                CancellationToken.None);

            Assert.Equal("curious, rigorous, and a little feral", updated.Value.Personality);
            Assert.Equal("never disclose credentials", updated.Value.Calibration["opsec"]);
            Assert.Equal(["lumen"], (await spine.ListWraithsAsync()).Select(item => item.Name));
            Assert.Equal(["deckwraith"], (await spine.ListHauntsAsync()).Select(item => item.Name));
            var archive = await spine.ReadArchiveAsync("lumen", CancellationToken.None);
            Assert.Equal("identity.updated", archive[^1].Kind);
            Assert.Equal(
                CanonicalJson.Hash(updated.Value),
                CanonicalJson.Hash(await spine.ReadIdentityAsync("lumen", CancellationToken.None)));
            Assert.Equal(
                string.Empty,
                await StateSpineEndToEndTests.RunGitForTestsAsync(
                    rootPath, ["status", "--porcelain"], CancellationToken.None));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
