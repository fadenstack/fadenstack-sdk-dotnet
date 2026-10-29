using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Fadenstack.Agents.Sessions;

namespace Fadenstack.UI.Avalonia.Sample;

/// <summary>
/// A session of N turns written into a session store, for the long-session harness and its tests. The mix follows
/// the research note (17 section 4): 60 % short answers, 25 % medium (a list, a code block, a table), 10 % long (100+
/// lines, one 300-line code block), 5 % with a picture on the question; tool lines in every fifth turn, and a turn
/// now and then with many of them. The same seed gives the same session.
/// </summary>
internal static class SyntheticSession
{
    /// <summary>A picture as a <c>data:</c> URL, 1600 x 1000, the sort a screenshot is.</summary>
    public static readonly string Picture = "data:image/png;base64," + Convert.ToBase64String(Png(1600, 1000));

    public static Task StartAsync(ISessionStore store, string agent, string id) =>
        store.AppendAsync(agent, id, Entry(EntryTypes.SessionStart, null, new { agent, tool_mode = "ask" }));

    public static async Task SeedAsync(ISessionStore store, string agent, string id, int turns, int seed = 7)
    {
        var random = new Random(seed);
        await StartAsync(store, agent, id);
        for (var t = 0; t < turns; t++)
        {
            var turn = $"t{t}";
            var kind = random.NextDouble();
            var withPicture = kind >= 0.95;
            var user = withPicture
                ? new
                {
                    text = $"Question {t}: what is on this screenshot?",
                    attachments = new[] { new { title = "screenshot.png", kind = "screenshot", media_type = "image/png", content = Picture } },
                }
                : (object)new { text = $"Question {t}: " + Words(random, 4 + random.Next(30)) };
            await store.AppendAsync(agent, id, Entry(EntryTypes.UserMessage, turn, user));
            await store.AppendAsync(agent, id, Entry(EntryTypes.TurnStart, turn, new { tool_mode = "ask" }));
            var tools = t % 40 == 3 ? 12 : t % 5 == 0 ? 1 + random.Next(2) : 0;
            for (var k = 0; k < tools; k++)
            {
                var call = $"c{t}-{k}";
                await store.AppendAsync(agent, id, Entry(EntryTypes.ToolStart, turn, new { call_id = call, name = "read_page", source = "host", tool_class = "read" }));
                await store.AppendAsync(agent, id, Entry(EntryTypes.ToolComplete, turn,
                    new { call_id = call, name = "read_page", source = "host", result = Words(random, 20), outcome = "ok", duration_ms = 5 + random.Next(300) }));
            }
            await store.AppendAsync(agent, id, Entry(EntryTypes.AssistantMessage, turn,
                new { text = Answer(random, kind), tool_calls = Array.Empty<object>(), origin = "model" }));
            await store.AppendAsync(agent, id, Entry(EntryTypes.TurnEnd, turn, new { status = "ok", outcome = "Completed" }, TurnStatus.Ok));
        }
    }

    /// <summary>One finished turn: the question, the answer's Markdown.</summary>
    public static async Task AppendTurnAsync(ISessionStore store, string agent, string id, string turn, string question, string answer)
    {
        await store.AppendAsync(agent, id, Entry(EntryTypes.UserMessage, turn, new { text = question }));
        await store.AppendAsync(agent, id, Entry(EntryTypes.TurnStart, turn, new { tool_mode = "ask" }));
        await store.AppendAsync(agent, id, Entry(EntryTypes.AssistantMessage, turn, new { text = answer, tool_calls = Array.Empty<object>(), origin = "model" }));
        await store.AppendAsync(agent, id, Entry(EntryTypes.TurnEnd, turn, new { status = "ok", outcome = "Completed" }, TurnStatus.Ok));
    }

    /// <summary>An answer of the sort <paramref name="kind"/> (0 to 1) picks: short, medium or long.</summary>
    public static string Answer(Random random, double kind)
    {
        var text = new StringBuilder();
        if (kind < 0.60)
        {
            text.Append(Words(random, 12 + random.Next(40))).Append(" **").Append(Words(random, 2)).Append("** and `").Append(Words(random, 1)).Append("`.");
        }
        else if (kind < 0.85)
        {
            text.Append("## ").Append(Words(random, 3)).Append("\n\n").Append(Words(random, 30)).Append("\n\n");
            for (var i = 0; i < 6; i++)
            {
                text.Append("- ").Append(Words(random, 8)).Append('\n');
            }
            text.Append("\n| Name | Value | Note |\n|:-----|------:|:----:|\n");
            for (var i = 0; i < 4; i++)
            {
                text.Append("| ").Append(Words(random, 1)).Append(" | ").Append(random.Next(1000)).Append(" | ").Append(Words(random, 2)).Append(" |\n");
            }
            text.Append('\n').Append(CodeBlock(random, 12 + random.Next(20)));
        }
        else
        {
            text.Append("# ").Append(Words(random, 4)).Append("\n\n");
            for (var p = 0; p < 12; p++)
            {
                text.Append(Words(random, 60)).Append("\n\n");
            }
            text.Append(CodeBlock(random, 300)).Append("\n> ").Append(Words(random, 25)).Append("\n\n");
            for (var p = 0; p < 6; p++)
            {
                text.Append("1. ").Append(Words(random, 14)).Append('\n');
            }
        }
        return text.ToString();
    }

    public static string CodeBlock(Random random, int lines)
    {
        var code = new StringBuilder("```csharp\n");
        for (var i = 0; i < lines; i++)
        {
            code.Append("    var value").Append(i).Append(" = Compute(").Append(random.Next(100000)).Append(", \"").Append(Words(random, 1)).Append("\"); // ").Append(Words(random, 3)).Append('\n');
        }
        return code.Append("```\n").ToString();
    }

    private static readonly string[] Vocabulary =
    {
        "agent", "session", "tool", "page", "model", "result", "document", "answer", "request", "policy", "stream", "turn",
        "approval", "context", "markdown", "thread", "plate", "server", "gateway", "local", "reading", "summary", "draft", "check",
    };

    public static string Words(Random random, int count)
    {
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            text.Append(i == 0 ? "" : " ").Append(Vocabulary[random.Next(Vocabulary.Length)]);
        }
        return text.ToString();
    }

    private static TranscriptEntry Entry(string type, string? turn, object data, string? status = null) => new()
    {
        Type = type,
        TurnId = turn,
        Status = status,
        Data = JsonSerializer.SerializeToElement(data),
    };

    /// <summary>A PNG of smooth colour bands (the Sub filter makes it small: a few kilobytes, whatever its size).</summary>
    private static byte[] Png(int width, int height)
    {
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 3);
            raw[row] = 1; // Sub: each byte is stored as the difference to the pixel on its left
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var left = x == 0 ? 0 : Pixel(x - 1, y, c, width, height);
                    raw[row + 1 + x * 3 + c] = (byte)(Pixel(x, y, c, width, height) - left);
                }
            }
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var header = new byte[13];
        WriteInt(header, 0, width);
        WriteInt(header, 4, height);
        header[8] = 8; // bit depth
        header[9] = 2; // RGB
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static byte Pixel(int x, int y, int channel, int width, int height)
    {
        var band = (x / 200 + y / 125) % 2 == 0;
        return channel switch
        {
            0 => (byte)(40 + 180 * y / height),
            1 => (byte)(60 + 150 * x / width),
            _ => (byte)(band ? 200 : 90),
        };
    }

    private static void WriteInt(byte[] to, int at, int value)
    {
        to[at] = (byte)(value >> 24);
        to[at + 1] = (byte)(value >> 16);
        to[at + 2] = (byte)(value >> 8);
        to[at + 3] = (byte)value;
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        var length = new byte[4];
        WriteInt(length, 0, data.Length);
        png.Write(length);
        var body = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type).CopyTo(body, 0);
        data.CopyTo(body, 4);
        png.Write(body);
        var crc = new byte[4];
        WriteInt(crc, 0, unchecked((int)Crc32(body)));
        png.Write(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return ~crc;
    }
}
