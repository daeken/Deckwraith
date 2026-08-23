using Deckwraith.Application.State;
using Deckwraith.Persistence.Archives;
using Deckwraith.Persistence.Artifacts;
using Deckwraith.Persistence.Git;
using Deckwraith.Persistence.State;

namespace Deckwraith.Persistence;

public static class DeckwraithPersistence
{
    public static StateSpine CreateStateSpine(string rootPath) => new(
        new JsonDeckStateStore(rootPath),
        new JsonlAgentArchive(rootPath),
        new ContentAddressedArtifactStore(rootPath),
        new GitCheckpointStore(rootPath));
}
