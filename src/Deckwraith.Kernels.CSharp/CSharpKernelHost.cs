using System.Text;
using System.Text.Json;
using Deckwraith.Application.State;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;
using Deckwraith.Kernels.Abstractions;

namespace Deckwraith.Kernels.CSharp;

public sealed class CSharpKernelHost
{
    private readonly DurableStateRuntime _state;
    private readonly ArtifactRuntime _artifacts;
    private readonly List<string> _standardOutput = [];
    private readonly List<string> _standardError = [];
    private CellExecutionRequest _request;
    private CancellationToken _cancellation;

    internal CSharpKernelHost(
        DurableStateRuntime state,
        ArtifactRuntime artifacts,
        CellExecutionRequest request,
        CancellationToken cancellationToken)
    {
        _state = state;
        _artifacts = artifacts;
        _request = request;
        _cancellation = cancellationToken;
    }

    public CancellationToken Cancellation => _cancellation;

    public async Task<JsonElement?> GetStateAsync(
        string name,
        DurableValueScope scope = DurableValueScope.Agent)
    {
        var record = await _state.GetAsync(
            _request.Wraith,
            scope,
            name,
            _request.RunId,
            _request.Haunt,
            _cancellation).ConfigureAwait(false);
        return record?.Value.Clone();
    }

    public Task<DurableStateMutation> SetStateAsync<T>(
        string name,
        T value,
        DurableValueScope scope = DurableValueScope.Agent,
        long? expectedVersion = null) =>
        _state.SetAsync(
            _request.Wraith,
            scope,
            name,
            CanonicalJson.ToElement(value),
            _request.RunId,
            _request.Haunt,
            expectedVersion,
            _cancellation);

    public Task<DurableStateMutation> RemoveStateAsync(
        string name,
        DurableValueScope scope = DurableValueScope.Agent,
        long? expectedVersion = null) =>
        _state.RemoveAsync(
            _request.Wraith,
            scope,
            name,
            _request.RunId,
            _request.Haunt,
            expectedVersion,
            _cancellation);

    public Task<ArtifactMutation> PutArtifactAsync(
        byte[] content,
        string? mediaType = null) =>
        _artifacts.StoreAsync(
            _request.Wraith,
            _request.Haunt,
            content,
            mediaType,
            _cancellation);

    public Task<ArtifactMutation> PutArtifactTextAsync(
        string content,
        string mediaType = "text/plain; charset=utf-8") =>
        PutArtifactAsync(Encoding.UTF8.GetBytes(content), mediaType);

    public Task<byte[]> ReadArtifactAsync(string hash) =>
        _artifacts.ReadAsync(_request.Haunt, hash, _cancellation);

    public async Task<string> ReadArtifactTextAsync(string hash) =>
        Encoding.UTF8.GetString(await ReadArtifactAsync(hash).ConfigureAwait(false));

    public void WriteLine(object? value) =>
        _standardOutput.Add(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);

    public void WriteError(object? value) =>
        _standardError.Add(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);

    internal void BeginInvocation(
        CellExecutionRequest request,
        CancellationToken cancellationToken)
    {
        _request = request;
        _cancellation = cancellationToken;
        _standardOutput.Clear();
        _standardError.Clear();
    }

    internal CSharpHostOutput EndInvocation() => new(
        _standardOutput.ToArray(),
        _standardError.ToArray());
}

public sealed class CSharpCellGlobals
{
    internal CSharpCellGlobals(CSharpKernelHost host, JsonElement input)
    {
        Dw = host;
        DwCellInput = input;
    }

    public CSharpKernelHost Dw { get; }

    public JsonElement DwCellInput { get; internal set; }
}

internal sealed record CSharpHostOutput(
    IReadOnlyList<string> StandardOutput,
    IReadOnlyList<string> StandardError);
