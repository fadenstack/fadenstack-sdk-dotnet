namespace Fadenstack.Provenance;

/// <summary>
/// A place content lands, described once by the integrator: what it is (<see cref="Kind"/>), whether it is a
/// container (a document, a file, an HTML page: it can hold signed metadata), and which carriers of a mark it keeps.
/// The pipeline picks the markers that fit. <c>new SinkInfo("crm.note", Carriers.KeyValue) { KeyValue = noteField }</c>.
/// A sink that carries nothing still gets the model's watermark and the server's record (level 0).
/// </summary>
public sealed record SinkInfo
{
    public SinkInfo(string kind, params string[] carries)
    {
        Kind = string.IsNullOrWhiteSpace(kind) ? throw new ArgumentException("A sink has a kind, such as word.range.", nameof(kind)) : kind;
        Carries = carries ?? Array.Empty<string>();
    }

    /// <summary><c>word.range</c>, <c>html.editor</c>, <c>crm.note</c>, <c>nextcloud.file</c>: reported, never shown.</summary>
    public string Kind { get; }

    /// <summary>
    /// The content lands inside a structured file or page (a document, an HTML editor, a file): the marking needs
    /// metadata in it, not only the watermark. Free-form text (a chat message, a plain field) is not a container.
    /// </summary>
    public bool Container { get; init; }

    /// <summary>The carriers of a mark the host keeps (<see cref="Carriers"/>, or the host's own names).</summary>
    public IReadOnlyList<string> Carries { get; init; }

    /// <summary>One place to keep a string (a hidden field, a custom property), for <see cref="Carriers.KeyValue"/>.</summary>
    public IKeyValueSink? KeyValue { get; init; }

    /// <summary>The headers of an HTTP response the content is served in, for <see cref="Carriers.HttpHeader"/>.</summary>
    public IKeyValueSink? Headers { get; init; }

    /// <summary>The file the content is written to, for <see cref="Carriers.Sidecar"/>.</summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// What the host holds now where the content went (the HTML around it, the file's text), so a marker can check
    /// that its mark held. Null when the host cannot be read back: marks are then reported as unverified.
    /// </summary>
    public Func<CancellationToken, Task<string?>>? ReadBack { get; init; }

    public bool CanCarry(string carrier) => Carries.Contains(carrier, StringComparer.Ordinal);

    /// <summary>A sink that keeps no mark: the content gets the watermark and the server's record only.</summary>
    public static SinkInfo Nothing(string kind) => new(kind);
}

/// <summary>The carriers the built-in markers use; a host marker may name its own (<c>part</c>, <c>range-tag</c>).</summary>
public static class Carriers
{
    /// <summary>An attribute on the HTML the content is wrapped in (<see cref="HtmlAttributeMarker"/>).</summary>
    public const string HtmlAttribute = "html-attribute";

    /// <summary>YAML front matter of a Markdown file (<see cref="MarkdownFrontMatterMarker"/>).</summary>
    public const string FrontMatter = "front-matter";

    /// <summary>One string the host keeps for the content (<see cref="KeyValueMarker"/>, <see cref="SinkInfo.KeyValue"/>).</summary>
    public const string KeyValue = "key-value";

    /// <summary>A file next to the file written (<see cref="SidecarMarker"/>, <see cref="SinkInfo.FilePath"/>).</summary>
    public const string Sidecar = "sidecar";

    /// <summary>A header of the HTTP response the content is served in (<see cref="HttpHeaderMarker"/>).</summary>
    public const string HttpHeader = "http-header";

    /// <summary>The bytes of a picture or file placed as they are, so their own metadata stays (<see cref="ImageProvenanceGuard"/>).</summary>
    public const string Bytes = "bytes";

    /// <summary>Invisible characters in the text itself (<see cref="UnicodeTextMarker"/>, opt-in).</summary>
    public const string Text = "text";
}

/// <summary>
/// One place a host keeps a string for the content it holds: a hidden CRM field, a ticket's custom field, an
/// object's metadata, a database column, a response header. The integrator writes about ten lines; the
/// <see cref="KeyValueMarker"/> does the rest.
/// </summary>
public interface IKeyValueSink
{
    Task WriteAsync(string key, string value, CancellationToken cancellationToken);

    /// <summary>The value kept under <paramref name="key"/>, or null; a host that cannot read throws <see cref="NotSupportedException"/>.</summary>
    Task<string?> ReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>The longest value it keeps (255 for a Word document property); null when there is no limit.</summary>
    int? MaxValueLength { get; }
}

/// <summary>An <see cref="IKeyValueSink"/> from two functions.</summary>
public sealed class KeyValueSink : IKeyValueSink
{
    private readonly Func<string, string, CancellationToken, Task> _write;
    private readonly Func<string, CancellationToken, Task<string?>>? _read;

    /// <param name="write">Keeps a value under a key.</param>
    /// <param name="read">Reads it back; null when the host cannot (marks are then unverified).</param>
    /// <param name="maxValueLength">The longest value the host keeps, if it has a limit.</param>
    public KeyValueSink(Func<string, string, CancellationToken, Task> write, Func<string, CancellationToken, Task<string?>>? read = null,
        int? maxValueLength = null)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _read = read;
        MaxValueLength = maxValueLength;
    }

    /// <summary>A sink from functions without a cancellation token: <c>KeyValueSink.From((k, v) => crm.SetAsync(id, k, v), k => crm.GetAsync(id, k))</c>.</summary>
    public static KeyValueSink From(Func<string, string, Task> write, Func<string, Task<string?>>? read = null, int? maxValueLength = null) =>
        new((k, v, _) => write(k, v), read is null ? null : (k, _) => read(k), maxValueLength);

    public int? MaxValueLength { get; }

    public Task WriteAsync(string key, string value, CancellationToken cancellationToken) => _write(key, value, cancellationToken);

    public Task<string?> ReadAsync(string key, CancellationToken cancellationToken) =>
        _read is null ? throw new NotSupportedException("This sink cannot be read back.") : _read(key, cancellationToken);
}
