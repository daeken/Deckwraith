using System.Management.Automation;
using System.Text;
using Deckwraith.Core.State;
using Deckwraith.PowerShell.Serialization;

namespace Deckwraith.PowerShell.Cmdlets;

[Cmdlet(VerbsCommon.Set, "DwArtifact")]
[OutputType(typeof(ArtifactReference))]
public sealed class SetDwArtifactCommand : DwCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [AllowNull]
    public object? Content { get; set; }

    [Parameter]
    public string? MediaType { get; set; }

    protected override void ProcessRecord()
    {
        var invocation = RuntimeSession.Invocation;
        if (string.IsNullOrWhiteSpace(invocation.Haunt))
        {
            throw new DeckStateException("Artifact writes require a haunt execution context.");
        }

        var (bytes, mediaType) = Encode(Content, MediaType);
        var result = RuntimeSession.Artifacts.StoreAsync(
            invocation.Wraith,
            invocation.Haunt,
            bytes,
            mediaType,
            CancellationToken.None).GetAwaiter().GetResult();
        WriteObject(result.Artifact);
    }

    private static (byte[] Content, string? MediaType) Encode(object? value, string? mediaType)
    {
        value = value is PSObject wrapper ? wrapper.BaseObject : value;
        return value switch
        {
            byte[] bytes => (bytes, mediaType ?? "application/octet-stream"),
            string text => (Encoding.UTF8.GetBytes(text), mediaType ?? "text/plain; charset=utf-8"),
            Stream stream => (ReadStream(stream), mediaType ?? "application/octet-stream"),
            _ => (
                Encoding.UTF8.GetBytes(PortablePowerShellValue.ToJsonElement(value).GetRawText()),
                mediaType ?? "application/json"),
        };
    }

    private static byte[] ReadStream(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
