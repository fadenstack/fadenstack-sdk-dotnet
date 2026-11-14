using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Fadenstack.SkillAuthoring;
using Fadenstack.UI.Skills;

namespace Fadenstack.UI.Avalonia.Services;

/// <summary>
/// The skill editor's file dialogs (<see cref="ISkillFileHost"/>) on the window the editor is in: the system's pickers through the window's
/// storage provider. Import takes a zip (a folder goes to <c>SkillLibraryViewModel.ImportFromAsync</c>, e.g. when it is dropped on the
/// window); a file the person adds to a skill is read up to one byte over the module's size limit, so the checks refuse a larger one
/// without it being read whole. A window without a storage provider answers null or false.
/// </summary>
public sealed class AvaloniaSkillFiles : ISkillFileHost
{
    private readonly Func<TopLevel?> _owner;

    public AvaloniaSkillFiles(Func<TopLevel?> owner) => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public async Task<string?> PickImportAsync(CancellationToken cancellationToken)
    {
        if (_owner()?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("zip") { Patterns = new[] { "*.zip" } } },
        }).ConfigureAwait(true);
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<bool> SaveExportAsync(string suggestedFileName, byte[] zip, CancellationToken cancellationToken)
    {
        if (_owner()?.StorageProvider is not { CanSave: true } storage)
        {
            return false;
        }
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "zip",
            FileTypeChoices = new[] { new FilePickerFileType("zip") { Patterns = new[] { "*.zip" } } },
        }).ConfigureAwait(true);
        if (file is null)
        {
            return false;
        }
        try
        {
            using var stream = await file.OpenWriteAsync().ConfigureAwait(false);
            await stream.WriteAsync(zip, 0, zip.Length, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<SkillFile?> PickResourceAsync(CancellationToken cancellationToken)
    {
        if (_owner()?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false }).ConfigureAwait(true);
        if (files.Count == 0)
        {
            return null;
        }
        try
        {
            using var stream = await files[0].OpenReadAsync().ConfigureAwait(false);
            var buffer = new byte[SkillLimits.FileMaxBytes + 1];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer, read, buffer.Length - read, cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }
                read += n;
            }
            Array.Resize(ref buffer, read);
            return new SkillFile(files[0].Name, buffer);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
