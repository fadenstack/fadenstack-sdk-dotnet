using System.Net;
using System.Text;
using Fadenstack.Models;
using Fadenstack.UI;

namespace Fadenstack.Provenance;

/// <summary>
/// A visible label above the content ("AI-generated" in the user's language), for deployers who chose to label
/// what people read: off unless the agent's policy has <c>visible_label</c> on or the module's options add it. It
/// complements the machine-readable marks and replaces none of them. For HTML, Markdown and plain text; the deployer
/// may give the markup of an icon to go before the words (an HTML label only).
/// </summary>
public sealed class VisibleLabelMarker : ContentMarker
{
    public const string MarkerName = "visible-label";

    /// <summary>The attribute an HTML label carries the record's id in.</summary>
    public const string Attribute = "data-ai-label";

    /// <param name="iconHtml">Markup that goes before the words of an HTML label (an <c>&lt;img&gt;</c> or inline SVG), or null.</param>
    public VisibleLabelMarker(string? iconHtml = null)
    {
        IconHtml = iconHtml;
    }

    public string? IconHtml { get; }

    public override string Name => MarkerName;

    public override MarkLayer Layer => MarkLayer.Label;

    public override bool Supports(SinkInfo sink, string mediaType) =>
        Is(mediaType, "text/html") || Is(mediaType, "text/markdown") || Is(mediaType, "text/plain");

    public override Task<MarkableContent> PrepareAsync(MarkableContent content, SignedRecord record, CancellationToken cancellationToken)
    {
        var label = (content.Texts ?? UiStrings.Current).ProvenanceLabel;
        var text = content.Text ?? "";
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        if (Is(content.MediaType, "text/html"))
        {
            return Task.FromResult(content.WithText(
                $"<p {Attribute}=\"{WebUtility.HtmlEncode(record.Record.Id)}\">{IconHtml}{WebUtility.HtmlEncode(label)}</p>{text}"));
        }
        var line = Is(content.MediaType, "text/markdown") ? $"*{label}*" : label;
        if (Is(content.MediaType, "text/markdown") && FrontMatter.Split(text) is { } front)
        {
            // After the front matter, which must stay first.
            var head = text.Substring(0, text.Length - front.Rest.Length);
            return Task.FromResult(content.WithText(head + line + newline + newline + front.Rest.TrimStart('\r', '\n')));
        }
        return Task.FromResult(content.WithText(line + newline + newline + text));
    }

    public override async Task<MarkOutcome> AttachAsync(PlacedContent placed, SignedRecord record, CancellationToken cancellationToken)
    {
        var text = await placed.ReadBackAsync(cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return MarkOutcome.Written(MarkLayer.Label, MarkerName, "not read back");
        }
        var label = (placed.Content.Texts ?? UiStrings.Current).ProvenanceLabel;
        return text.Contains(label) || text.Contains(WebUtility.HtmlEncode(label))
            ? MarkOutcome.Held(MarkLayer.Label, MarkerName)
            : MarkOutcome.Missing(MarkLayer.Label, MarkerName, ProvenanceReasons.StrippedByHost);
    }
}

/// <summary>
/// The record's id in invisible characters at the end of plain or Markdown text (Unicode variation selectors, as
/// C2PA's text binding uses them): opt-in only, never on by default. Security filters and AI guardrails flag or strip
/// such characters, reflowing text can drop them, and screen readers are untested with them; Gmail's draft save
/// strips them. Add it per agent with <c>AddMarker(new UnicodeTextMarker())</c>; the placement report records
/// whether the characters survived.
/// </summary>
public sealed class UnicodeTextMarker : ContentMarker
{
    public const string MarkerName = "unicode-text";

    // "FPV1", then the id's UTF-8: so a reader tells our characters from other uses of variation selectors.
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FPV1");

    public override string Name => MarkerName;

    public override MarkLayer Layer => MarkLayer.Metadata;

    public override bool Supports(SinkInfo sink, string mediaType) => Is(mediaType, "text/plain") || Is(mediaType, "text/markdown");

    public override Task<MarkableContent> PrepareAsync(MarkableContent content, SignedRecord record, CancellationToken cancellationToken) =>
        Task.FromResult(content.WithText((content.Text ?? "") + Encode(record.Record.Id)));

    public override async Task<MarkOutcome> AttachAsync(PlacedContent placed, SignedRecord record, CancellationToken cancellationToken)
    {
        var text = await placed.ReadBackAsync(cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return MarkOutcome.Written(MarkLayer.Metadata, MarkerName, "not read back");
        }
        return Decode(text).Contains(record.Record.Id)
            ? MarkOutcome.Held(MarkLayer.Metadata, MarkerName)
            : MarkOutcome.Missing(MarkLayer.Metadata, MarkerName, ProvenanceReasons.StrippedByHost, "the invisible characters were dropped");
    }

    /// <summary>The id as invisible characters: each byte a variation selector (U+FE00-FE0F for 0-15, U+E0100-E01EF above).</summary>
    public static string Encode(string recordId)
    {
        var builder = new StringBuilder();
        foreach (var b in Magic.Concat(Encoding.UTF8.GetBytes(recordId)))
        {
            builder.Append(b < 16 ? ((char)(0xFE00 + b)).ToString() : char.ConvertFromUtf32(0xE0100 + b - 16));
        }
        return builder.ToString();
    }

    /// <summary>The ids encoded in <paramref name="text"/>'s variation selectors that read as ours.</summary>
    public static IReadOnlyList<string> Decode(string text)
    {
        var found = new List<string>();
        var bytes = new List<byte>();
        for (var i = 0; i <= text.Length; i++)
        {
            int? value = null;
            if (i < text.Length)
            {
                var c = text[i];
                if (c is >= '︀' and <= '️')
                {
                    value = c - 0xFE00;
                }
                else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                         && char.ConvertToUtf32(c, text[i + 1]) is var cp and >= 0xE0100 and <= 0xE01EF)
                {
                    value = cp - 0xE0100 + 16;
                    i++;
                }
            }
            if (value is { } v)
            {
                bytes.Add((byte)v);
                continue;
            }
            if (bytes.Count > Magic.Length && bytes.Take(Magic.Length).SequenceEqual(Magic))
            {
                var id = Encoding.UTF8.GetString(bytes.Skip(Magic.Length).ToArray());
                if (ProvenanceJson.IsRecordId(id))
                {
                    found.Add(id);
                }
            }
            bytes.Clear();
        }
        return found;
    }
}
