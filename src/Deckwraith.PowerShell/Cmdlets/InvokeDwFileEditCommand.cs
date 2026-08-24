using System.Globalization;
using System.Management.Automation;
using System.Text.Json;
using Deckwraith.Application.Files;
using Deckwraith.Core.Naming;
using Deckwraith.Core.State;
using Deckwraith.PowerShell.Serialization;

namespace Deckwraith.PowerShell.Cmdlets;

[Cmdlet(VerbsLifecycle.Invoke, "DwFileEdit")]
[OutputType(typeof(AtomicFileEditResult))]
public sealed class InvokeDwFileEditCommand : DwCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrEmpty]
    public object[] Operation { get; set; } = [];

    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? RootPath { get; set; }

    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? CommitSubject { get; set; }

    [Parameter]
    public string? CommitBody { get; set; }

    protected override void ProcessRecord()
    {
        try
        {
            var session = RuntimeSession;
            var operations = Operation.Select(ParseOperation).ToArray();
            var rootWasSpecified = MyInvocation.BoundParameters.ContainsKey(nameof(RootPath));
            var root = rootWasSpecified
                ? RootPath
                : SessionState.Path.CurrentFileSystemLocation.Path;
            HauntProjectPolicy? project = null;
            CanonicalName? resolvedHaunt = null;
            if (session.DeckState is not null && session.Invocation.Haunt is { } haunt)
            {
                resolvedHaunt = session.DeckState.ResolveHauntAsync(
                    CanonicalName.Parse(haunt), CancellationToken.None).GetAwaiter().GetResult();
                project = session.DeckState.ReadHauntAsync(
                    resolvedHaunt.Value, CancellationToken.None).GetAwaiter().GetResult().Project;
                if (!rootWasSpecified && project is not null)
                {
                    root = project.ProjectPath;
                }
            }

            var batch = new AtomicFileEditBatch(
                operations, root, CommitSubject, CommitBody);
            ProjectCommitPreparation? preparation = null;
            if (project?.AutoCommitEnabled is true)
            {
                if (session.ProjectCommitter is null)
                {
                    throw new ProjectCommitException(
                        "This hosted runspace cannot create project commits.");
                }

                if (string.IsNullOrWhiteSpace(CommitSubject))
                {
                    throw new ProjectCommitException(
                        "This haunt requires an edit-authored CommitSubject for automatic commits.");
                }

                preparation = session.ProjectCommitter.PrepareAsync(
                    project,
                    CanonicalName.Parse(session.Invocation.Wraith),
                    resolvedHaunt ?? throw new ProjectCommitException(
                        "Automatic project commits require a current haunt."),
                    CommitSubject,
                    CommitBody,
                    AtomicFileEditor.ResolvePaths(batch),
                    CancellationToken.None).GetAwaiter().GetResult();
            }

            var result = AtomicFileEditor.ApplyAsync(
                batch,
                CancellationToken.None).GetAwaiter().GetResult();
            if (preparation is not null)
            {
                var commit = session.ProjectCommitter!.CommitAsync(
                    preparation, result.Files, CancellationToken.None).GetAwaiter().GetResult();
                result = result with { Commit = commit };
            }

            WriteObject(result);
        }
        catch (Exception exception) when (exception is not PipelineStoppedException)
        {
            ThrowTerminatingError(new ErrorRecord(
                exception,
                "Deckwraith.AtomicFileEditFailed",
                ErrorCategory.InvalidData,
                Operation));
        }
    }

    private static AtomicFileEdit ParseOperation(object input)
    {
        var value = PSObject.AsPSObject(input);
        var path = GetRequiredString(value, "path");
        var kindText = GetRequiredString(value, "kind")
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        if (!Enum.TryParse<FileEditKind>(kindText, ignoreCase: true, out var kind))
        {
            throw new AtomicFileEditException(
                $"Unknown file edit kind '{GetRequiredString(value, "kind")}'.");
        }

        var valueProperty = FindProperty(value, "value");
        JsonElement? jsonValue = valueProperty is null
            ? null
            : PortablePowerShellValue.ToJsonElement(valueProperty.Value);
        return new AtomicFileEdit(
            path,
            kind,
            Text: GetString(value, "text"),
            Match: GetString(value, "match"),
            Replacement: GetString(value, "replacement"),
            ExpectedCount: GetInt32(value, "expectedCount"),
            JsonPointer: GetString(value, "jsonPointer") ?? GetString(value, "pointer"),
            JsonIndex: GetInt32(value, "jsonIndex") ?? GetInt32(value, "index"),
            Value: jsonValue,
            ExpectedHash: GetString(value, "expectedHash"));
    }

    private static PSPropertyInfo? FindProperty(PSObject value, string name) =>
        value.Properties.FirstOrDefault(property =>
            StringComparer.OrdinalIgnoreCase.Equals(property.Name, name));

    private static string GetRequiredString(PSObject value, string name) =>
        GetString(value, name) is { Length: > 0 } result
            ? result
            : throw new AtomicFileEditException(
                $"A file edit operation requires non-empty '{name}'.");

    private static string? GetString(PSObject value, string name)
    {
        var property = FindProperty(value, name);
        if (property is null || property.Value is null)
        {
            return null;
        }

        return Convert.ToString(property.Value, CultureInfo.InvariantCulture);
    }

    private static int? GetInt32(PSObject value, string name)
    {
        var property = FindProperty(value, name);
        if (property is null || property.Value is null)
        {
            return null;
        }

        return LanguagePrimitives.ConvertTo<int>(property.Value);
    }
}
