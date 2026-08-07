using Harugeki.Formats;

namespace HarugekiStudio.ViewModels;

/// <summary>An archive the session has open, and whether it still matches disk.</summary>
public sealed record OpenArchive(RingArchive Archive, string Path)
{
    public bool Dirty { get; set; }

    /// <summary>Whether a <c>.bak</c> has already been taken this session.</summary>
    public bool BackedUp { get; set; }
}

/// <summary>
/// Owns the open archives: loading them, tracking which have unsaved edits, and
/// writing them back behind a one-time backup.
/// </summary>
public sealed class ArchiveSession
{
    private readonly List<OpenArchive> _open = [];

    public event Action<string>? Logged;

    public IReadOnlyList<OpenArchive> Open => _open;

    public bool HasChanges => _open.Exists(o => o.Dirty);

    /// <summary>
    /// Loads an archive off the UI thread, replacing anything already open.
    /// Returns null and logs if it could not be read.
    /// </summary>
    public async Task<RingArchive?> OpenAsync(string path)
    {
        string name = Path.GetFileName(path);

        try
        {
            RingArchive archive = await Task.Run(() => RingArchive.Load(path));
            _open.Clear();
            _open.Add(new OpenArchive(archive, path));
            Logged?.Invoke(
                $"Opened {name} ({TreeBuilder.Size(archive.Data.Length)}), " +
                $"{archive.Root.Children.Count} entries.");
            return archive;
        }
        catch (Exception ex)
        {
            Logged?.Invoke($"Failed to open {name}: {ex.Message}");
            return null;
        }
    }

    public void Clear()
    {
        _open.Clear();
    }

    /// <summary>Records that <paramref name="archive"/> has edits pending a save.</summary>
    public void MarkDirty(RingArchive? archive)
    {
        if (archive is null)
        {
            return;
        }

        OpenArchive? owner = _open.Find(o => ReferenceEquals(o.Archive, archive));
        owner?.Dirty = true;
    }

    /// <summary>
    /// Rebuilds and writes every modified archive.
    ///
    /// <para>
    /// The original is copied to <c>.bak</c> once per session before the first
    /// write, and only if no backup is already sitting there — so repeated saves
    /// never overwrite the pristine copy with an edited one.
    /// </para>
    /// </summary>
    public async Task SaveAsync()
    {
        List<OpenArchive> dirty = _open.FindAll(o => o.Dirty);
        if (dirty.Count == 0)
        {
            Logged?.Invoke("Nothing to save.");
            return;
        }

        foreach (OpenArchive entry in dirty)
        {
            try
            {
                await BackUpAsync(entry);

                byte[] bytes = await Task.Run(entry.Archive.Save);
                await File.WriteAllBytesAsync(entry.Path, bytes);

                entry.Dirty = false;
                Logged?.Invoke($"Saved {Path.GetFileName(entry.Path)} ({TreeBuilder.Size(bytes.Length)}).");
            }
            catch (Exception ex)
            {
                Logged?.Invoke($"Save failed: {ex.Message}");
            }
        }
    }

    private async Task BackUpAsync(OpenArchive entry)
    {
        if (entry.BackedUp)
        {
            return;
        }

        string backup = entry.Path + ".bak";
        if (!File.Exists(backup))
        {
            await Task.Run(() => File.Copy(entry.Path, backup));
            Logged?.Invoke($"Backed up to {Path.GetFileName(backup)}.");
        }

        entry.BackedUp = true;
    }
}
