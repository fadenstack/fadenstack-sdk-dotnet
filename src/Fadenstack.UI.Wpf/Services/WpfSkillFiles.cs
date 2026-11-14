using System.IO;
using System.Windows;
using Fadenstack.SkillAuthoring;
using Fadenstack.UI.Skills;
using Microsoft.Win32;

namespace Fadenstack.UI.Wpf.Services;

/// <summary>
/// The skill editor's file dialogs (<see cref="ISkillFileHost"/>) on the window the editor is in: the system's open and save dialogs.
/// Import takes a zip (a folder goes to <c>SkillLibraryViewModel.ImportFromAsync</c>, e.g. when it is dropped on the window: .NET
/// Framework 4.7.2 has no folder dialog); a file the person adds to a skill is read up to one byte over the module's size limit, so the
/// checks refuse a larger one without it being read whole. The dialogs are modal and run on the UI thread.
/// </summary>
public sealed class WpfSkillFiles : ISkillFileHost
{
    private readonly Func<Window?> _owner;

    public WpfSkillFiles(Func<Window?> owner) => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public Task<string?> PickImportAsync(CancellationToken cancellationToken)
    {
        var dialog = new OpenFileDialog { Filter = "zip|*.zip", CheckFileExists = true, Multiselect = false };
        return Task.FromResult(Show(dialog) ? dialog.FileName : null);
    }

    public Task<bool> SaveExportAsync(string suggestedFileName, byte[] zip, CancellationToken cancellationToken)
    {
        var dialog = new SaveFileDialog { Filter = "zip|*.zip", FileName = suggestedFileName, DefaultExt = ".zip", OverwritePrompt = true };
        if (!Show(dialog))
        {
            return Task.FromResult(false);
        }
        try
        {
            File.WriteAllBytes(dialog.FileName, zip);
            return Task.FromResult(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(false);
        }
    }

    public Task<SkillFile?> PickResourceAsync(CancellationToken cancellationToken)
    {
        var dialog = new OpenFileDialog { CheckFileExists = true, Multiselect = false };
        if (!Show(dialog))
        {
            return Task.FromResult<SkillFile?>(null);
        }
        try
        {
            using var stream = File.OpenRead(dialog.FileName);
            var buffer = new byte[SkillLimits.FileMaxBytes + 1];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0)
                {
                    break;
                }
                read += n;
            }
            Array.Resize(ref buffer, read);
            return Task.FromResult<SkillFile?>(new SkillFile(Path.GetFileName(dialog.FileName), buffer));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<SkillFile?>(null);
        }
    }

    private bool Show(FileDialog dialog) => (_owner() is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true;
}
