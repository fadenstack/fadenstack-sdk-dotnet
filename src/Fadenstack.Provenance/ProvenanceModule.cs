using System.Diagnostics;
using System.Globalization;
using Fadenstack.Agents;
using Fadenstack.Models;
using Fadenstack.Tools;
using Fadenstack.UI;

namespace Fadenstack.Provenance;

/// <summary>
/// Content marking for an agent, the module named <c>provenance</c> that an agent's profile can require:
/// <c>builder.Use(new ProvenanceModule(o =&gt; o.AddMarker(new CrmNoteMarker())))</c>. It keeps each turn's generation
/// receipts (a chat stage), gives each tool call an <see cref="IProvenance"/> to place content with, and checks after
/// each call that the content a tool declared (<c>[Emits]</c>) went through it (a tool stage); a call that placed
/// nothing is recorded as <c>provenance.unmarked</c>, and in development mode fails.
/// </summary>
public sealed class ProvenanceModule : IFadenstackModule
{
    public const string ModuleName = "provenance";

    public ProvenanceModule(Action<ProvenanceOptions>? configure = null)
    {
        Options = new ProvenanceOptions();
        configure?.Invoke(Options);
    }

    public string? Name => ModuleName;

    public ProvenanceOptions Options { get; }

    public void Configure(AgentBuilder builder)
    {
        var state = new ProvenanceState(Options);
        builder.AddChatStage(StageOrder.Provenance, inner => new ReceiptStage(inner, state.Receipts));
        builder.AddToolStage(StageOrder.Provenance, new OutputCheckStage(state));
        builder.AddToolService<IProvenance>(invocation => new ProvenanceCall(state, invocation));
    }
}

/// <summary>
/// What the module does beyond its defaults. Every built-in marker is on and applies where a sink carries what it
/// needs; options add markers and hooks, and may only make the agent's policy stricter: a looser setting is ignored
/// and logged.
/// </summary>
public sealed class ProvenanceOptions
{
    private readonly List<IContentMarker> _markers = new()
    {
        new ServerSignedMarker(), new ImageProvenanceGuard(), new WatermarkStatus(), new HtmlAttributeMarker(),
        new MarkdownFrontMatterMarker(), new KeyValueMarker(), new SidecarMarker(), new HttpHeaderMarker(),
    };

    private readonly List<IRecordEnricher> _enrichers = new();
    private readonly List<IPlacementObserver> _observers = new();
    private readonly HashSet<EditKind> _marked = new();
    private bool _refuse;
    private int? _shortTextTokens;
    private VisibleLabelMarker? _label;
    private Func<string?, UiStrings>? _texts;

    /// <summary>The markers, in the order they were added: the built-ins first.</summary>
    public IReadOnlyList<IContentMarker> Markers => _markers;

    public IEditClassifier? Classifier { get; private set; }

    public IReadOnlyList<IRecordEnricher> Enrichers => _enrichers;

    public IReadOnlyList<IPlacementObserver> Observers => _observers;

    public IProvenanceBackend Backend { get; private set; } = ServerProvenanceBackend.Instance;

    /// <summary>Counts a text's tokens; about four characters a token (one per CJK character) unless a tokenizer is given.</summary>
    public Func<string, int> CountTokens { get; private set; } = TokenEstimate.Of;

    /// <summary>
    /// A declared output the tool did not place fails the call (<see cref="ProvenanceUnmarkedException"/>) instead of
    /// only being recorded. On when <c>DOTNET_ENVIRONMENT</c> or <c>ASPNETCORE_ENVIRONMENT</c> is <c>Development</c>.
    /// </summary>
    public bool DevelopmentMode { get; private set; } = IsDevelopment();

    /// <summary>A marker for the integrator's host. One named like a built-in takes its place.</summary>
    public ProvenanceOptions AddMarker(IContentMarker marker)
    {
        if (marker is null)
        {
            throw new ArgumentNullException(nameof(marker));
        }
        var same = _markers.FindIndex(m => m.Name == marker.Name);
        if (same >= 0)
        {
            _markers[same] = marker;
        }
        else
        {
            _markers.Add(marker);
        }
        return this;
    }

    /// <summary>Decides <c>corrected</c> or <c>rewritten</c> for the integrator's domain (<see cref="SimilarityEditClassifier"/>).</summary>
    public ProvenanceOptions UseClassifier(IEditClassifier classifier)
    {
        Classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        return this;
    }

    public ProvenanceOptions AddEnricher(IRecordEnricher enricher)
    {
        _enrichers.Add(enricher ?? throw new ArgumentNullException(nameof(enricher)));
        return this;
    }

    public ProvenanceOptions AddObserver(IPlacementObserver observer)
    {
        _observers.Add(observer ?? throw new ArgumentNullException(nameof(observer)));
        return this;
    }

    /// <summary>Where policies, signatures and reports go; the agent's Fadenstack server by default.</summary>
    public ProvenanceOptions UseBackend(IProvenanceBackend backend)
    {
        Backend = backend ?? throw new ArgumentNullException(nameof(backend));
        return this;
    }

    /// <summary>Refuses to place content when a layer it needs is not possible, whatever the agent's policy says.</summary>
    public ProvenanceOptions RefuseWhenMissing()
    {
        _refuse = true;
        return this;
    }

    /// <summary>A lower short-text threshold than the policy's; a higher one would loosen it and is ignored.</summary>
    public ProvenanceOptions UseShortTextTokens(int tokens)
    {
        _shortTextTokens = tokens < 0 ? throw new ArgumentOutOfRangeException(nameof(tokens)) : tokens;
        return this;
    }

    /// <summary>Marks <paramref name="kind"/> although the policy lists it as exempt (a translation the integrator wants marked).</summary>
    public ProvenanceOptions MarkEditKind(EditKind kind)
    {
        _marked.Add(kind);
        return this;
    }

    /// <summary>A visible label on what the agent places, whatever the policy says (<see cref="VisibleLabelMarker"/>).</summary>
    public ProvenanceOptions UseVisibleLabel(VisibleLabelMarker? marker = null)
    {
        _label = marker ?? new VisibleLabelMarker();
        return this;
    }

    /// <summary>Counts tokens with the model's tokenizer, for the short-text threshold.</summary>
    public ProvenanceOptions UseTokenCounter(Func<string, int> countTokens)
    {
        CountTokens = countTokens ?? throw new ArgumentNullException(nameof(countTokens));
        return this;
    }

    public ProvenanceOptions UseDevelopmentMode(bool on = true)
    {
        DevelopmentMode = on;
        return this;
    }

    /// <summary>The texts marks show people, by the user's language (null: the host's); with a host's own words.</summary>
    public ProvenanceOptions UseTexts(Func<string?, UiStrings> forLanguage)
    {
        _texts = forLanguage ?? throw new ArgumentNullException(nameof(forLanguage));
        return this;
    }

    /// <summary>The agent's policy with these options applied: stricter only.</summary>
    internal ProvenancePolicy Tighten(ProvenancePolicy policy)
    {
        if (_refuse)
        {
            policy = policy with { OnMissing = MissingLayerPolicy.Refuse };
        }
        if (_shortTextTokens is { } tokens)
        {
            if (tokens <= policy.ShortTextTokens)
            {
                policy = policy with { ShortTextTokens = tokens };
            }
            else
            {
                Trace.TraceWarning($"Provenance: a short-text threshold of {tokens} tokens would loosen the policy's {policy.ShortTextTokens}; ignored.");
            }
        }
        if (_marked.Count > 0)
        {
            policy = policy with { ExemptEditKinds = policy.ExemptEditKinds.Where(k => !_marked.Contains(k)).ToList() };
        }
        if (_label is not null)
        {
            policy = policy with { VisibleLabel = true };
        }
        return policy;
    }

    /// <summary>The markers a placement under <paramref name="policy"/> may use: with the visible label when the policy has it on.</summary>
    internal IReadOnlyList<IContentMarker> MarkersFor(ProvenancePolicy policy)
    {
        if (!policy.VisibleLabel || _markers.Any(m => m.Layer == MarkLayer.Label))
        {
            return _markers;
        }
        return _markers.Concat(new[] { _label ?? new VisibleLabelMarker() }).ToList();
    }

    internal UiStrings TextsFor(string? language)
    {
        if (_texts is not null)
        {
            return _texts(language);
        }
        if (string.IsNullOrWhiteSpace(language))
        {
            return UiStrings.Current;
        }
        try
        {
            return UiStrings.For(CultureInfo.GetCultureInfo(language));
        }
        catch (CultureNotFoundException)
        {
            return UiStrings.Current;
        }
    }

    private static bool IsDevelopment() =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Development", StringComparison.OrdinalIgnoreCase);
}
