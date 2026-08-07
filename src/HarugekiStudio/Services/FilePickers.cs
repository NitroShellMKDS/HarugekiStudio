using Avalonia.Platform.Storage;

namespace HarugekiStudio.Services;

/// <summary>
/// Shows a file picker and returns a local path, or <see langword="null"/> if the
/// user cancelled or picked something with no filesystem path.
///
/// <para>
/// Every extract, export and replace command used to spell out the same six
/// steps: build the options record, await the picker, pull the first result,
/// call <c>TryGetLocalPath</c>, null-check it, and only then do the actual work.
/// That preamble lives here now, so the commands are the part that differs.
/// </para>
/// </summary>
internal static class FilePickers
{
    public static async Task<string?> SaveAsync(
        IAppStorageProvider storage, string title, string suggestedName, string extension, string typeLabel)
    {
        string bare = extension.TrimStart('.');
        IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = bare,
            FileTypeChoices = [new FilePickerFileType(typeLabel) { Patterns = [$"*.{bare}"] }],
        });

        return file?.TryGetLocalPath();
    }

    public static async Task<string?> OpenAsync(
        IAppStorageProvider storage, string title, string typeLabel, params string[] patterns)
    {
        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(typeLabel) { Patterns = patterns }],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
