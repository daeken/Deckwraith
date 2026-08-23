using System.Text.Json;
using Deckwraith.Core.Naming;
using Deckwraith.Core.State;

namespace Deckwraith.Core.Tests;

public sealed class IdentityDocumentTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void SparseIdentityIncludesPersonalityAndRegisterCalibration()
    {
        var identity = IdentityDocument.CreateSparse(
            CanonicalName.Parse("wraith1"), DateTimeOffset.UnixEpoch);

        Assert.Equal(IdentityDocument.CurrentSchemaVersion, identity.SchemaVersion);
        Assert.Equal(string.Empty, identity.Personality);
        Assert.Equal(string.Empty, identity.Calibration["register"]);
    }

    [Fact]
    public void VersionOneIdentityReceivesBackwardCompatibleDefaults()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "name": "wraith1",
              "pronouns": [],
              "selfDescription": "",
              "knownTendencies": [],
              "openQuestions": [],
              "updatedAt": "2026-08-23T20:15:00Z"
            }
            """;

        var identity = JsonSerializer.Deserialize<IdentityDocument>(
            json, WebJson);

        Assert.NotNull(identity);
        Assert.Equal(string.Empty, identity.Personality);
        Assert.Equal(string.Empty, identity.Calibration["register"]);
    }
}
