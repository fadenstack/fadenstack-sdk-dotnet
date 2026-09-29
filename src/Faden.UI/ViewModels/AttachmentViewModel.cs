using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Faden.Agents;

namespace Faden.UI.ViewModels;

/// <summary>
/// Context waiting in the composer to go with the next message: a chip the user can remove. Its content is
/// read when the message is sent (off the UI thread), so "the current selection" is what is selected then.
/// An <see cref="IsImplicit"/> chip (the current selection or document, as Copilot offers the current file)
/// stays between messages and can be switched off.
/// </summary>
public sealed class AttachmentViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task<MessageAttachment?>> _resolve;
    private string _title;
    private string? _subtitle;
    private bool _isIncluded = true;

    public AttachmentViewModel(string title, string? subtitle, Func<CancellationToken, Task<MessageAttachment?>> resolve, bool isImplicit = false)
    {
        _title = title;
        _subtitle = subtitle;
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        IsImplicit = isImplicit;
        ToggleCommand = new RelayCommand(() => IsIncluded = !IsIncluded, () => IsImplicit);
    }

    /// <summary>An attachment whose content is already known.</summary>
    public static AttachmentViewModel FromContent(MessageAttachment attachment) =>
        new(attachment.Title, attachment.Kind, _ => Task.FromResult<MessageAttachment?>(attachment));

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string? Subtitle
    {
        get => _subtitle;
        set => SetProperty(ref _subtitle, value);
    }

    /// <summary>Offered by the host (the current selection); it stays until the host replaces it.</summary>
    public bool IsImplicit { get; }

    /// <summary>For an implicit chip: whether it goes with the next message.</summary>
    public bool IsIncluded
    {
        get => _isIncluded;
        set => SetProperty(ref _isIncluded, value);
    }

    public IRelayCommand ToggleCommand { get; }

    internal bool Sends => !IsImplicit || _isIncluded;

    internal Task<MessageAttachment?> ResolveAsync(CancellationToken cancellationToken) => _resolve(cancellationToken);
}

/// <summary>What a sent message had attached, as shown under it.</summary>
public sealed record AttachmentChip(string Title, string? Subtitle);
