using Fadenstack.Chat;
using Fadenstack.Models;
using Fadenstack.Tools;

namespace Fadenstack.Provenance.Testing;

/// <summary>
/// An <see cref="IProvenance"/> that puts content in as it is, for a tool method called by hand in a test:
/// <c>await tools.InsertReplyAsync("Dear Ms Huber,", new PassThroughProvenance(), ct)</c>. <see cref="PlaceAsync"/> keeps
/// the content (<see cref="Requests"/>), calls the insert with it unchanged and returns an unmarked placement -- no
/// record, no layers, nothing missing. No server, no marks, no reports. To test the marks themselves, run the tool in an
/// agent with <see cref="ProvenanceModule"/> and <see cref="TestProvenanceBackend"/>, or a marker with
/// <see cref="MarkerTestKit"/>. The TypeScript SDK's <c>@fadenstack/testing/provenance</c> has the same.
/// </summary>
public sealed class PassThroughProvenance : IProvenance
{
    private readonly List<Placement> _placements = new();

    /// <param name="policy">What <see cref="GetPolicyAsync"/> answers; the contract's default when null.</param>
    /// <param name="receipt">The receipt the call's answer had.</param>
    public PassThroughProvenance(ProvenancePolicy? policy = null, ProvenanceReceiptContent? receipt = null)
    {
        Policy = policy ?? new ProvenancePolicy();
        Receipt = receipt;
    }

    public ProvenancePolicy Policy { get; }

    public ProvenanceReceiptContent? Receipt { get; }

    /// <summary>The tool name the placements carry.</summary>
    public string Tool { get; init; } = "test_tool";

    /// <summary>What the tool placed, in order, as it handed it over.</summary>
    public List<MarkableContent> Requests { get; } = new();

    public IReadOnlyList<Placement> Placements
    {
        get
        {
            lock (_placements)
            {
                return _placements.ToList();
            }
        }
    }

    public async Task<Placement> PlaceAsync(MarkableContent content, Func<MarkableContent, CancellationToken, Task<PlacedContent?>> insert,
        CancellationToken cancellationToken = default)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }
        if (insert is null)
        {
            throw new ArgumentNullException(nameof(insert));
        }
        lock (_placements)
        {
            Requests.Add(content);
        }
        var landed = await insert(content, cancellationToken).ConfigureAwait(false) ?? new PlacedContent(content);
        var placement = new Placement
        {
            Tool = Tool, SinkKind = content.Sink.Kind, MediaType = content.MediaType, EditKind = content.EditKind ?? EditKind.Generated,
            Argument = content.Argument, Placed = landed,
        };
        lock (_placements)
        {
            _placements.Add(placement);
        }
        return placement;
    }

    public Task<ProvenancePolicy> GetPolicyAsync(CancellationToken cancellationToken = default) => Task.FromResult(Policy);
}
