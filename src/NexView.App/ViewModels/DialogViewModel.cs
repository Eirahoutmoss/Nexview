using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NexView.App.ViewModels;

public enum DialogChoice { Cancel, Primary, Secondary }

/// <summary>Pencere içi modal iletişim kutusu (onay, metin girişi, üç seçenekli).</summary>
public sealed partial class DialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<DialogChoice> _tcs = new();

    public DialogViewModel(string title, string message, string primary, string? secondary = null, string? cancel = null)
    {
        Title = title;
        Message = message;
        PrimaryText = primary;
        SecondaryText = secondary;
        CancelText = cancel ?? Core.Localization.L.T("İptal");
    }

    public string Title { get; }
    public string Message { get; }
    public string PrimaryText { get; }
    public string? SecondaryText { get; }
    public string CancelText { get; }
    public bool HasSecondary => SecondaryText != null;
    public bool IsDanger { get; init; }
    public bool HasInput { get; init; }
    /// <summary>Girdi doğrulayıcı: hata metni ya da null döndürür.</summary>
    public Func<string, string?>? Validator { get; init; }

    [ObservableProperty] private string _input = "";
    [ObservableProperty] private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public Task<DialogChoice> Result => _tcs.Task;

    [RelayCommand]
    private void Primary()
    {
        if (HasInput && Validator?.Invoke(Input) is { } err)
        {
            Error = err;
            return;
        }
        _tcs.TrySetResult(DialogChoice.Primary);
    }

    [RelayCommand] private void Secondary() => _tcs.TrySetResult(DialogChoice.Secondary);
    [RelayCommand] private void Cancel() => _tcs.TrySetResult(DialogChoice.Cancel);
}
