using Avalonia.Platform.Storage;

namespace HarugekiStudio.Services;

/// <summary>
/// Abstraction for file-picker operations. Decouples ViewModels from Avalonia's
/// <c>TopLevel.StorageProvider</c> so they can be tested without a visual tree.
/// </summary>
public interface IAppStorageProvider
{
    Task<IReadOnlyList<IStorageFile>> OpenFilePickerAsync(FilePickerOpenOptions options);
    Task<IStorageFile?> SaveFilePickerAsync(FilePickerSaveOptions options);
}

/// <summary>Adapts an Avalonia <c>IStorageProvider</c> to our interface.</summary>
internal sealed class WindowStorageProvider(IStorageProvider inner) : IAppStorageProvider
{
    public Task<IReadOnlyList<IStorageFile>> OpenFilePickerAsync(FilePickerOpenOptions options)
    {
        return inner.OpenFilePickerAsync(options);
    }

    public Task<IStorageFile?> SaveFilePickerAsync(FilePickerSaveOptions options)
    {
        return inner.SaveFilePickerAsync(options);
    }
}
