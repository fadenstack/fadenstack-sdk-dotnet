using System.Net;
using System.Text;
using Fadenstack.Models;
using Fadenstack.UI;

namespace Fadenstack.Provenance;

/// <summary>
/// A visible label above the content ("AI-generated" in the user's language), for deployers who chose to label
/// what people read: off unless the agent's policy has <c>visible_label</c> on or the module's options add it. It
/// complements the machine-readable marks and replaces none of them. Before HTML
/// <c>&lt;p data-ai-label="record id"&gt;label&lt;/p&gt;</c>, before Markdown <c>*label*</c> and a blank line (after its
/// front matter), before plain text the label and a blank line, as the TypeScript SDK writes it. The deployer may give
/// the markup of an icon to go before the words (an HTML label only).
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
        var label = (content.Texts ?? UiStrings.Current).AiGenerated;
        var text = content.Text ?? "";
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        if (Is(content.MediaType, "text/html"))
        {
            return Task.FromResult(content.WithText(
                $"<p {Attribute}=\"{HtmlText.Attribute(record.Record.Id)}\">{IconHtml}{HtmlText.Escape(label)}</p>{text}"));
        }
        var line = Is(content.MediaType, "text/markdown") ? $"*{label}*" : label;
        var bom = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        if (Is(content.MediaType, "text/markdown") && FrontMatter.Split(text.Substring(bom)) is { } front)
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
        var label = (placed.Content.Texts ?? UiStrings.Current).AiGenerated;
        return text.Contains(label) || text.Contains(HtmlText.Escape(label))
            ? MarkOutcome.Held(MarkLayer.Label, MarkerName)
            : MarkOutcome.Missing(MarkLayer.Label, MarkerName, ProvenanceReasons.StrippedByHost);
    }
}

/// <summary>
/// The record's id in invisible characters at the very end of plain or Markdown text (Unicode variation selectors, as
/// C2PA's text embedding A.8 uses them, with a payload of our own): opt-in only, never on by default. Security
/// filters and AI guardrails flag or strip such characters, reflowing text can drop them, and screen readers are
/// untested with them; Gmail's draft save strips them. Add it per agent with <c>AddMarker(new UnicodeTextMarker())</c>
/// for a sink that carries <see cref="Carriers.UnicodeText"/>; the placement report records whether the characters
/// survived. The format is <see cref="Encode"/>'s, the same as the TypeScript SDK's <c>invisibleMark</c>.
/// </summary>
public sealed class UnicodeTextMarker : ContentMarker
{
    public const string MarkerName = "unicode-text";

    /// <summary>Starts the mark, as C2PA's text wrapper does: U+FEFF, zero width no-break space.</summary>
    private const char Start = '﻿';

    // "FDNP" and version 1: ours, not a C2PA manifest store, which C2PA readers would look for.
    private static readonly byte[] Magic = { 0x46, 0x44, 0x4E, 0x50, 0x01 };

    public override string Name => MarkerName;

    public override MarkLayer Layer => MarkLayer.Metadata;

    public override bool Supports(SinkInfo sink, string mediaType) =>
        sink.CanCarry(Carriers.UnicodeText) && (Is(mediaType, "text/plain") || Is(mediaType, "text/markdown"));

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

    /// <summary>
    /// The id as invisible characters, the format both SDKs write: U+FEFF, then the bytes <c>46 44 4E 50 01</c> ("FDNP"
    /// and version 1) and the id's UTF-8 bytes, each byte as one variation selector -- 0 to 15 as U+FE00 to U+FE0F, 16 to
    /// 255 as U+E0100 to U+E01EF. For a UUID: 1 + 5 + 36 = 42 code points.
    /// </summary>
    public static string Encode(string recordId)
    {
        var builder = new StringBuilder().Append(Start);
        foreach (var b in Magic.Concat(Encoding.UTF8.GetBytes(recordId ?? throw new ArgumentNullException(nameof(recordId)))))
        {
            builder.Append(b < 16 ? ((char)(0xFE00 + b)).ToString() : char.ConvertFromUtf32(0xE0100 + b - 16));
        }
        return builder.ToString();
    }

    /// <summary>The ids that marks of ours in <paramref name="text"/> name, in order: a U+FEFF followed by our bytes and a valid id.</summary>
    public static IReadOnlyList<string> Decode(string text)
    {
        var found = new List<string>();
        for (var start = text.IndexOf(Start); start >= 0; start = text.IndexOf(Start, start + 1))
        {
            var bytes = new List<byte>();
            for (var i = start + 1; i < text.Length; i++)
            {
                var c = text[i];
                if (c is >= '︀' and <= '️')
                {
                    bytes.Add((byte)(c - 0xFE00));
                }
                else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                         && char.ConvertToUtf32(c, text[i + 1]) is var cp and >= 0xE0100 and <= 0xE01EF)
                {
                    bytes.Add((byte)(cp - 0xE0100 + 16));
                    i++;
                }
                else
                {
                    break;
                }
            }
            if (bytes.Count > Magic.Length && bytes.Take(Magic.Length).SequenceEqual(Magic))
            {
                var id = Encoding.UTF8.GetString(bytes.Skip(Magic.Length).ToArray());
                if (ProvenanceJson.IsRecordId(id))
                {
                    found.Add(id);
                }
            }
        }
        return found;
    }
}
