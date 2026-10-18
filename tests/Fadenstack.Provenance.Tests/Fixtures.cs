using System.ComponentModel;
using System.Text;
using Fadenstack.Agents;
using Fadenstack.Agents.Tests;
using Fadenstack.Tools;

namespace Fadenstack.Provenance.Tests;

/// <summary>A web mail editor: what it keeps of pasted HTML is up to <see cref="Keep"/> (Gmail drops data- attributes).</summary>
public sealed class Editor
{
    public string Html { get; set; } = "";

    public Func<string, string> Keep { get; set; } = html => html;

    public SinkInfo Sink => new("html.editor", Carriers.HtmlAttribute) { Container = true, ReadBack = _ => Task.FromResult<string?>(Html) };
}

/// <summary>A CRM whose notes have one hidden custom field (level 1).</summary>
public sealed class Crm
{
    public List<string> Notes { get; } = new();

    public Dictionary<string, string> Fields { get; } = new();

    public SinkInfo Sink => new("crm.note", Carriers.KeyValue)
    {
        Container = true,
        KeyValue = KeyValueSink.From((k, v) => { Fields[k] = v; return Task.CompletedTask; },
            k => Task.FromResult(Fields.TryGetValue(k, out var v) ? v : null)),
    };
}

/// <summary>Host tools as an integrator writes them with the provenance module.</summary>
public sealed class WritingTools
{
    public const string Contract = """
        [{"name":"insert_reply","risk":"write"},{"name":"translate_field","risk":"write"},{"name":"forget_to_mark","risk":"write"},
         {"name":"add_crm_note","risk":"write"},{"name":"chat_message","risk":"write"},{"name":"insert_picture","risk":"write"},
         {"name":"write_config","risk":"write"},{"name":"improve_text","risk":"write"}]
        """;

    public Editor Editor { get; } = new();

    public Crm Crm { get; } = new();

    public List<string> Plain { get; } = new();

    public List<byte[]> Pictures { get; } = new();

    public List<Placement> Placed { get; } = new();

    /// <summary>The bytes a document keeps of a picture (a host that re-encodes drops the C2PA).</summary>
    public Func<byte[], byte[]> KeepPicture { get; set; } = b => b;

    [AgentTool(ToolClass.Write), Emits("text", "text/html"), Description("Inserts a reply into the mail being written.")]
    public Task<string> InsertReplyAsync(string text, IProvenance provenance, CancellationToken cancellationToken) =>
        provenance.PlaceAsync(new MarkableContent(text, "text/html", Editor.Sink),
            marked => { Editor.Html += Editor.Keep(marked.Text!); return Task.FromResult("Inserted."); }, cancellationToken);

    [AgentTool(ToolClass.Write), Emits("text", "text/plain", EditKind.Translated), Description("Writes a translation into a field.")]
    public async Task<string> TranslateFieldAsync(string text, IProvenance provenance, CancellationToken cancellationToken)
    {
        Placed.Add(await provenance.PlaceAsync(new MarkableContent(text, "text/plain", SinkInfo.Nothing("form.field")),
            marked => { Plain.Add(marked.Text!); return Task.CompletedTask; }, cancellationToken));
        return "Written.";
    }

    [AgentTool(ToolClass.Write), Emits("text", "text/plain"), Description("Writes text without placing it.")]
    public string ForgetToMark(string text)
    {
        Plain.Add(text);
        return "Written.";
    }

    [AgentTool(ToolClass.Write), Emits("note", "text/plain"), Description("Adds a note to the customer's record.")]
    public async Task<string> AddCrmNoteAsync(string note, IProvenance provenance, CancellationToken cancellationToken)
    {
        var placement = await provenance.PlaceAsync(new MarkableContent(note, "text/plain", Crm.Sink),
            marked => { Crm.Notes.Add(marked.Text!); return Task.CompletedTask; }, cancellationToken);
        Placed.Add(placement);
        return placement.Summary;
    }

    [AgentTool(ToolClass.Write), Emits("text", "text/plain"), Description("Posts a chat message.")]
    public async Task<string> ChatMessageAsync(string text, IProvenance provenance, CancellationToken cancellationToken)
    {
        Placed.Add(await provenance.PlaceAsync(new MarkableContent(text, "text/plain", SinkInfo.Nothing("chat.message")),
            marked => { Plain.Add(marked.Text!); return Task.CompletedTask; }, cancellationToken));
        return "Posted.";
    }

    [AgentTool(ToolClass.Write), Emits("picture", "image/png"), Description("Puts the last generated picture on the slide.")]
    public async Task<string> InsertPictureAsync(string picture, IProvenance provenance, CancellationToken cancellationToken)
    {
        var bytes = Convert.FromBase64String(picture);
        var kept = Array.Empty<byte>();
        var sink = new SinkInfo("slide.picture", Carriers.Bytes) { Container = true };
        var placement = await provenance.PlaceAsync(new MarkableContent(bytes, "image/png", sink) { ReceiptId = "rp-1" },
            (marked, _) =>
            {
                kept = KeepPicture(marked.Bytes!);
                Pictures.Add(kept);
                return Task.FromResult<PlacedContent?>(new PlacedContent(marked) { ReadBackBytes = _ => Task.FromResult<byte[]?>(kept) });
            }, cancellationToken);
        Placed.Add(placement);
        return placement.Summary;
    }

    [AgentTool(ToolClass.Write), Emits("json", "application/json"), Description("Writes the settings file.")]
    public async Task<string> WriteConfigAsync(string json, IProvenance provenance, CancellationToken cancellationToken)
    {
        Placed.Add(await provenance.PlaceAsync(new MarkableContent(json, "application/json", SinkInfo.Nothing("file")),
            marked => { Plain.Add(marked.Text!); return Task.CompletedTask; }, cancellationToken));
        return "Saved.";
    }

    [AgentTool(ToolClass.Write), Emits("text", "text/plain", EditKind.Rewritten), Description("Improves the selected text.")]
    public async Task<string> ImproveTextAsync(string original, string text, IProvenance provenance, CancellationToken cancellationToken)
    {
        Placed.Add(await provenance.PlaceAsync(new MarkableContent(text, "text/plain", Crm.Sink) { Original = original },
            marked => { Crm.Notes.Add(marked.Text!); return Task.CompletedTask; }, cancellationToken));
        return "Improved.";
    }
}

/// <summary>Agents for the tests: the scripted gateway, the writing tools, the module.</summary>
public static class TestAgents
{
    public static FadenstackAgent Build(Gateway gateway, WritingTools tools, Action<ProvenanceOptions>? configure = null) =>
        gateway.Builder().AddTools(tools).Use(new ProvenanceModule(configure)).Build();

    /// <summary>A long text (over 200 tokens), as a reply people read.</summary>
    public static string LongText { get; } = string.Join(" ", Enumerable.Repeat(
        "The pump P-200 is due for its seal check next week, and a technician will call you to agree on a time.", 12));
}

/// <summary>Pictures as the server marks them, and as a host that re-encodes them leaves them.</summary>
public static class Pictures
{
    private static readonly byte[] Xmp = Encoding.UTF8.GetBytes(
        "<x:xmpmeta><rdf:Description Iptc4xmpExt:DigitalSourceType=\"http://cv.iptc.org/newscodes/digitalsourcetype/trainedAlgorithmicMedia\"/></x:xmpmeta>");

    private static readonly byte[] Jumbf = Encoding.ASCII.GetBytes("\0\0\0\x20jumb\0\0\0\x18jumdc2pa\0\x11\0\x10manifest-store");

    public static byte[] Png(bool c2pa = true, bool xmp = true)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Chunk(bytes, "IHDR", new byte[13]);
        if (xmp)
        {
            Chunk(bytes, "iTXt", Encoding.ASCII.GetBytes("XML:com.adobe.xmp\0\0\0\0\0").Concat(Xmp).ToArray());
        }
        if (c2pa)
        {
            Chunk(bytes, "caBX", Jumbf);
        }
        Chunk(bytes, "IDAT", new byte[] { 1, 2, 3 });
        Chunk(bytes, "IEND", Array.Empty<byte>());
        return bytes.ToArray();
    }

    public static byte[] Jpeg(bool c2pa = true, bool xmp = true)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        if (xmp)
        {
            Segment(bytes, 0xE1, Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0").Concat(Xmp).ToArray());
        }
        if (c2pa)
        {
            Segment(bytes, 0xEB, new byte[] { (byte)'J', (byte)'P', 0, 1, 0, 0, 0, 1 }.Concat(Jumbf).ToArray());
        }
        Segment(bytes, 0xDB, new byte[64]);
        bytes.AddRange(new byte[] { 0xFF, 0xDA, 0, 2, 9, 9, 0xFF, 0xD9 });
        return bytes.ToArray();
    }

    public static byte[] WebP(bool c2pa = true, bool xmp = true)
    {
        var chunks = new List<byte>();
        RiffChunk(chunks, "VP8 ", new byte[11]);
        if (xmp)
        {
            RiffChunk(chunks, "XMP ", Xmp);
        }
        if (c2pa)
        {
            RiffChunk(chunks, "C2PA", Jumbf);
        }
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("RIFF"));
        bytes.AddRange(BitConverter.GetBytes(chunks.Count + 4));
        bytes.AddRange(Encoding.ASCII.GetBytes("WEBP"));
        bytes.AddRange(chunks);
        return bytes.ToArray();
    }

    private static void Chunk(List<byte> bytes, string type, byte[] data)
    {
        bytes.AddRange(new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length });
        bytes.AddRange(Encoding.ASCII.GetBytes(type));
        bytes.AddRange(data);
        bytes.AddRange(new byte[4]); // CRC, not checked
    }

    private static void Segment(List<byte> bytes, byte marker, byte[] data)
    {
        var length = data.Length + 2;
        bytes.AddRange(new byte[] { 0xFF, marker, (byte)(length >> 8), (byte)length });
        bytes.AddRange(data);
    }

    private static void RiffChunk(List<byte> bytes, string type, byte[] data)
    {
        bytes.AddRange(Encoding.ASCII.GetBytes(type));
        bytes.AddRange(BitConverter.GetBytes(data.Length));
        bytes.AddRange(data);
        if (data.Length % 2 == 1)
        {
            bytes.Add(0);
        }
    }
}
