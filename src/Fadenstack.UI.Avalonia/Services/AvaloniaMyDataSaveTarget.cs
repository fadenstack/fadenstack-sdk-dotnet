using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Fadenstack.UI.Settings;

namespace Fadenstack.UI.Avalonia.Services;

/// <summary>
/// Where "Your data on this device" saves an export (<see cref="IMyDataSaveTarget"/>): the system's save picker through the storage
/// provider of the window the control is in. A window without one answers null, as if the person had cancelled.
/// </summary>
public sealed class AvaloniaMyDataSaveTarget : IMyDataSaveTarget
{
    private readonly Func<TopLevel?> _owner;

    public AvaloniaMyDataSaveTarget(Func<TopLevel?> owner) => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public async Task<Stream?> OpenAsync(string suggestedFileName, CancellationToken cancellationToken)
    {
        if (_owner()?.StorageProvider is not { CanSave: true } storage)
        {
            return null;
        }
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "zip",
            FileTypeChoices = new[] { new FilePickerFileType("zip") { Patterns = new[] { "*.zip" } } },
        }).ConfigureAwait(true);
        if (file is null)
        {
            return null;
        }
        var stream = await file.OpenWriteAsync().ConfigureAwait(false);
        if (stream.CanSeek)
        {
            stream.SetLength(0);
        }
        return stream;
    }
}
