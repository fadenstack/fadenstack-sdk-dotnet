using Fadenstack.Agents;
using Fadenstack.Tools;
using Fadenstack.Models;

namespace Fadenstack.Provenance;

/// <summary>
/// Marks what the person copies out of a chat (<see cref="FadenstackAgent.MarkCopyAsync"/>) through the same pipeline a tool
/// places content with (<see cref="ProvenanceCall"/>): one placement per copy, outside a tool call, with the tool named
/// <c>copy</c> in the report and in the transcript's <c>provenance.placed</c> entry (never the content). The sink is
/// <c>clipboard</c>, which keeps <see cref="Carriers.HtmlAttribute"/> only: the HTML gets its mark
/// (<see cref="HtmlAttributeMarker"/>), the plain text stays as it is. Without HTML the copy is Markdown text with no
/// carrier: the record is signed and reported, and the text is the person's. Code and what the policy exempts are
/// recorded as exempt and copied as they are. With the policy <c>refuse</c>, a copy that cannot get a layer it needs is
/// <see cref="MarkedCopy.Refused"/>. ASVS 5.0 V16 (security logging): the report and the transcript hold names, ids and
/// outcomes, never what was copied. TS: <c>copyMarker</c> in <c>@fadenstack/provenance</c>.
/// </summary>
internal sealed class AnswerCopyMarker : IAnswerCopyMarker
{
    /// <summary>The sink kind the report and the transcript name.</summary>
    internal const string SinkKind = "clipboard";

    /// <summary>The sink kind of a saved Markdown export: it carries front matter.</summary>
    internal const string FileSinkKind = "file";

    private readonly ProvenanceState _state;
    private readonly FadenstackAgent _agent;

    public AnswerCopyMarker(ProvenanceState state, FadenstackAgent agent)
    {
        _state = state;
        _agent = agent;
    }

    public async Task<MarkedCopy> MarkAsync(AnswerCopy copy, CancellationToken cancellationToken = default)
    {
        var hasHtml = !copy.IsFile && !string.IsNullOrEmpty(copy.Html);
        var sink = copy.IsFile ? new SinkInfo(FileSinkKind, Carriers.FrontMatter) { Container = true }
            : new SinkInfo(SinkKind, Carriers.HtmlAttribute) { Container = hasHtml };
        var mediaType = copy.IsCode ? "text/x-code" : hasHtml ? "text/html" : "text/markdown";
        var content = new MarkableContent(hasHtml ? copy.Html! : copy.Text, mediaType, sink) { EditKind = EditKind.Generated };
        var call = new ProvenanceCall(_state, _agent.CopyInvocation(copy));
        string? landed = null;
        Placement placement;
        try
        {
            placement = await call.PlaceAsync(content, (marked, _) =>
            {
                landed = marked.Text;
                return Task.FromResult<PlacedContent?>(new PlacedContent(marked) { ReadBack = _ => Task.FromResult(marked.Text) });
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ProvenanceRefusedException)
        {
            return new MarkedCopy(copy.Text, null, null, false) { Refused = true };
        }
        var carried = (hasHtml || copy.IsFile) && !placement.Exempt && placement.Record is not null && landed is not null && landed != (hasHtml ? copy.Html : copy.Text);
        if (copy.IsFile)
        {
            return new MarkedCopy(carried ? landed! : copy.Text, null, placement.RecordId, carried)
            {
                ExemptBecause = placement.Exempt ? placement.ExemptBecause : null,
            };
        }
        var marks = carried;
        return new MarkedCopy(copy.Text, hasHtml ? (marks ? landed : copy.Html) : null, placement.RecordId, marks)
        {
            ExemptBecause = placement.Exempt ? placement.ExemptBecause : null,
        };
    }
}
