namespace NexView.App.ViewModels;

/// <summary>ViewModel'in pencereye ait işlemlere (dosya seçici, pano, tam ekran) erişimi.</summary>
public interface IUiHost
{
    Task<IReadOnlyList<string>> PickFilesAsync();
    Task<string?> PickFolderAsync();
    Task<string?> PickSaveFileAsync(string suggestedName, string? folder);
    Task CopyTextAsync(string text);
    void SetFullScreen(bool on);
    void ResetView();
}
