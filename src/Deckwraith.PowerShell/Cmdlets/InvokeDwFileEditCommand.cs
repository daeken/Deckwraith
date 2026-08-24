using System.Globalization;
using System.Management.Automation;
using System.Text.Json;
using Deckwraith.Application.Files;
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
            var root = MyInvocation.BoundParameters.ContainsKey(nameof(RootPath))
                ? RootPath
                : SessionState.Path.CurrentFileSystemLocation.Path;
            var operations = Operation.Select(ParseOperation).ToArray();
            var result = AtomicFileEditor.ApplyAsync(
                new AtomicFileEditBatch(operations, root, CommitSubject, CommitBody),
                CancellationToken.None).GetAwaiter().GetResult();
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
