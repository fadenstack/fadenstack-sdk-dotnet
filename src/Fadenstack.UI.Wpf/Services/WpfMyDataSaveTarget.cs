using System.IO;
using System.Windows;
using Fadenstack.UI.Settings;
using Microsoft.Win32;

namespace Fadenstack.UI.Wpf.Services;

/// <summary>
/// Where "Your data on this device" saves an export (<see cref="IMyDataSaveTarget"/>): the system's save dialog on the window the
/// control is in. The dialog is modal and runs on the UI thread, and asks before it replaces a file. A file that cannot be created is
/// an exception, which the view model says as a sentence.
/// </summary>
public sealed class WpfMyDataSaveTarget : IMyDataSaveTarget
{
    private readonly Func<Window?> _owner;

    public WpfMyDataSaveTarget(Func<Window?> owner) => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public Task<Stream?> OpenAsync(string suggestedFileName, CancellationToken cancellationToken)
    {
        var dialog = new SaveFileDialog { Filter = "zip|*.zip", FileName = suggestedFileName, DefaultExt = ".zip", OverwritePrompt = true };
        var chosen = (_owner() is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true;
        return Task.FromResult<Stream?>(chosen ? new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None) : null);
    }
}
