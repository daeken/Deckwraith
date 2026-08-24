using Deckwraith.Core.Naming;
using Deckwraith.Core.State;

namespace Deckwraith.Application.Files;

public sealed record ProjectCommitPreparation(
    string ProjectPath,
    string RepositoryPath,
    CanonicalName Wraith,
    CanonicalName Haunt,
    string Subject,
    string? Body,
    string AuthorName,
    string AuthorEmail,
    IReadOnlyList<string> TargetPaths,
    IReadOnlyList<string> RepositoryRelativePaths);

public sealed record ProjectCommitReceipt(
    string RepositoryPath,
    string CommitId,
    string Subject,
    string AuthorName,
    string AuthorEmail,
    IReadOnlyList<string> Paths,
    string? Warning = null);

public sealed class ProjectCommitException : Exception
{
    public ProjectCommitException(string message)
        : base(message)
    {
    }

    public ProjectCommitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface IProjectCommitter
{
    Task<ProjectCommitPreparation> PrepareAsync(
        HauntProjectPolicy policy,
        CanonicalName wraith,
        CanonicalName haunt,
        string subject,
        string? body,
        IReadOnlyList<string> targetPaths,
        CancellationToken cancellationToken);

    Task<ProjectCommitReceipt?> CommitAsync(
        ProjectCommitPreparation preparation,
        IReadOnlyList<FileEditReceipt> files,
        CancellationToken cancellationToken);
}
