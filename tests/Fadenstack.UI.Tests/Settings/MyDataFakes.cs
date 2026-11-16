using Fadenstack.Agents;
using Fadenstack.Agents.Audit;
using Fadenstack.UI.Settings;

namespace Fadenstack.UI.Tests.Settings;

/// <summary>An export's destination that remembers what was written to it and whether it was closed.</summary>
internal sealed class RecordingStream : MemoryStream
{
    public bool Closed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        Closed = true;
        base.Dispose(disposing);
    }
}

/// <summary>
/// "Your data on this device" without an agent: the apps it lists, what a removal says it did, what it exported, and the failures and
/// the wait to make it show every state; and a save target that answers with a stream or with "cancelled". For the view model's
/// and the skins' tests.
/// </summary>
internal sealed class FakeMyData : IMyDataSource, IMyDataSaveTarget, IMyDataAuditSource
{
    public static readonly MyDataApp ThisApp = new("Word add-in", new[] { "docs-assistant" }, null, IsThisApp: true, Recorded: true);
    public static readonly MyDataApp Desktop = new("Desktop agent", new[] { "desktop-assistant" }, null, IsThisApp: false, Recorded: true);
    public static readonly MyDataApp Excel = new("Excel add-in", new[] { "sheet-assistant" }, null, IsThisApp: false, Recorded: true);

    /// <summary>An app on an older SDK: known only by an agent's data, named by that agent.</summary>
    public static readonly MyDataApp Legacy = new("old-assistant", new[] { "old-assistant" }, null, IsThisApp: false, Recorded: false);

    public IReadOnlyList<MyDataApp> Apps { get; set; } = new[] { ThisApp };

    /// <summary>What the check of the record of tool calls finds: 12 entries since 1 August 2026, intact, unless a test says otherwise.</summary>
    public AuditVerification Audit { get; set; } = new(true, 12, 1, new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));

    public Exception? ThrowOnAudit { get; set; }

    public Exception? ThrowOnAuditExport { get; set; }

    /// <summary>How many times the record was exported.</summary>
    public int AuditExports { get; private set; }

    /// <summary>How many times the record was checked.</summary>
    public int AuditReads { get; private set; }

    public Task<AuditVerification> AuditAsync(CancellationToken cancellationToken)
    {
        AuditReads++;
        return ThrowOnAudit is null ? Task.FromResult(Audit) : throw ThrowOnAudit;
    }

    public async Task ExportAuditAsync(Stream destination, CancellationToken cancellationToken)
    {
        if (ThrowOnAuditExport is not null)
        {
            throw ThrowOnAuditExport;
        }
        AuditExports++;
        var bytes = System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE html>");
        await destination.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What a removal reports (the scope is the one asked for unless this says otherwise).</summary>
    public Func<DataScope, DataDeletion> Deletion { get; set; } = scope => new DataDeletion(1, false, false) { Scope = scope };

    public Exception? ThrowOnApps { get; set; }

    public Exception? ThrowOnDelete { get; set; }

    public Exception? ThrowOnExport { get; set; }

    public Exception? ThrowOnOpen { get; set; }

    /// <summary>While set, the apps are not answered: the view model stays busy.</summary>
    public TaskCompletionSource<bool>? Gate { get; set; }

    /// <summary>What the save target gives out; null: the person cancelled.</summary>
    public RecordingStream? Target { get; set; } = new();

    public int AppsReads { get; private set; }

    public List<DataScope> Deleted { get; } = new();

    public List<DataScope> Exported { get; } = new();

    public List<string> Suggested { get; } = new();

    public async Task<IReadOnlyList<MyDataApp>> AppsAsync(CancellationToken cancellationToken)
    {
        AppsReads++;
        if (Gate is not null)
        {
            await Gate.Task.ConfigureAwait(false);
        }
        return ThrowOnApps is null ? Apps : throw ThrowOnApps;
    }

    public Task<DataDeletion> DeleteAsync(DataScope scope, CancellationToken cancellationToken)
    {
        if (ThrowOnDelete is not null)
        {
            throw ThrowOnDelete;
        }
        Deleted.Add(scope);
        return Task.FromResult(Deletion(scope));
    }

    public async Task ExportAsync(Stream destination, DataScope scope, CancellationToken cancellationToken)
    {
        if (ThrowOnExport is not null)
        {
            throw ThrowOnExport;
        }
        Exported.Add(scope);
        var bytes = System.Text.Encoding.UTF8.GetBytes("zip:" + scope);
        await destination.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
    }

    public Task<Stream?> OpenAsync(string suggestedFileName, CancellationToken cancellationToken)
    {
        Suggested.Add(suggestedFileName);
        return ThrowOnOpen is null ? Task.FromResult<Stream?>(Target) : throw ThrowOnOpen;
    }

    /// <summary>The states a view shows, by name.</summary>
    public static IReadOnlyList<string> Names { get; } = new[]
    {
        "idle", "confirm-shared", "confirm-everything", "confirm-all", "busy", "done-kept", "done-everything", "done-all", "error", "exported",
    };

    /// <summary>A source set up so that <see cref="Steps"/> of the name lead to that state.</summary>
    public static FakeMyData Of(string name)
    {
        var fake = new FakeMyData();
        switch (name)
        {
            case "idle":
            case "exported":
                break;
            case "confirm-shared":
            case "done-kept":
                fake.Apps = new[] { ThisApp, Desktop, Excel };
                fake.Deletion = scope => new DataDeletion(2, false, false) { Scope = scope, OtherApps = new[] { Desktop.Name, Excel.Name } };
                break;
            case "confirm-everything":
            case "done-everything":
                fake.Apps = new[] { ThisApp };
                break;
            case "confirm-all":
            case "done-all":
                fake.Apps = new[] { ThisApp, Desktop, Legacy };
                break;
            case "busy":
                fake.Gate = new TaskCompletionSource<bool>();
                break;
            case "error":
                fake.ThrowOnApps = new InvalidOperationException("secret detail of the failure");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(name));
        }
        return fake;
    }

    /// <summary>The presses that lead from the buttons to the state.</summary>
    public static IReadOnlyList<string> Steps(string name) => name switch
    {
        "idle" => Array.Empty<string>(),
        "confirm-shared" or "confirm-everything" or "busy" or "error" => new[] { "remove" },
        "confirm-all" => new[] { "remove-all" },
        "done-kept" or "done-everything" => new[] { "remove", "confirm" },
        "done-all" => new[] { "remove-all", "confirm" },
        "exported" => new[] { "export" },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>One press: the command of the step (a button of the view runs the same one).</summary>
    public static void Press(MyDataSettingsViewModel model, string step)
    {
        switch (step)
        {
            case "remove": model.RemoveCommand.Execute(null); break;
            case "remove-all": model.RemoveAllCommand.Execute(null); break;
            case "confirm": model.ConfirmCommand.Execute(null); break;
            case "cancel": model.CancelCommand.Execute(null); break;
            case "export": model.ExportCommand.Execute(null); break;
            case "export-all": model.ExportAllCommand.Execute(null); break;
            default: throw new ArgumentOutOfRangeException(nameof(step));
        }
    }
}
