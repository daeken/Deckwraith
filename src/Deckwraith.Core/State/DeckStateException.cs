namespace Deckwraith.Core.State;

public sealed class DeckStateException : Exception
{
    public DeckStateException(string message)
        : base(message)
    {
    }

    public DeckStateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
