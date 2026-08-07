using CommunityToolkit.Mvvm.ComponentModel;
using Harugeki.Formats;

namespace HarugekiStudio.ViewModels;

/// <summary>
/// One row in the Outliner. Children are built on first expand, which keeps a
/// 133 MB archive instant to open.
/// </summary>
public partial class TreeItemViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    private volatile bool _loaded;
    public object LoadLock { get; } = new();

    public TreeItemViewModel(string header, string detail = "", string kind = "")
    {
        Header = header;
        Detail = detail;
        Kind = kind;
        HeaderSegments.Add(new TextSegment(header, false));
    }

    public string Header { get; }
    public string Detail { get; }
    public string Kind { get; }

    /// <summary>The domain object this row stands for, used to drive the panes.</summary>
    public object? Payload { get; init; }
    public RingNode? Node { get; init; }

    public ObservableCollection<TreeItemViewModel> Children { get; } = [];
    public ObservableCollection<TextSegment> HeaderSegments { get; } = [];

    /// <summary>
    /// Populates <see cref="Children"/> once, on first expand. Setting a loader
    /// also parks a placeholder child so the expander arrow is visible before load.
    /// </summary>
    public Func<IEnumerable<TreeItemViewModel>>? Loader
    {
        get;
        init
        {
            field = value;
            if (value is not null)
            {
                Children.Add(new TreeItemViewModel("…"));
            }
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
            EnsureLoaded();
    }

    public void EnsureLoaded()
    {
        if (_loaded || Loader is null)
        {
            return;
        }

        lock (LoadLock)
        {
            if (_loaded || Loader is null)
            {
                return;
            }

            try
            {
                List<TreeItemViewModel> items = Loader().ToList();
                Children.Clear();
                foreach (TreeItemViewModel c in items)
                {
                    Children.Add(c);
                }

                _loaded = true;
            }
            catch (Exception ex)
            {
                Children.Clear();
                Children.Add(new TreeItemViewModel("Error", ex.Message));
                _loaded = true;
            }
        }
    }

    public void UpdateSearchHighlight(string? searchText)
    {
        HeaderSegments.Clear();

        if (string.IsNullOrEmpty(Header))
        {
            HeaderSegments.Add(new TextSegment("", false));
            return;
        }

        if (string.IsNullOrEmpty(searchText))
        {
            HeaderSegments.Add(new TextSegment(Header, false));
            return;
        }

        int startIndex = 0;
        int matchIndex;
        while ((matchIndex = Header.IndexOf(searchText, startIndex, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (matchIndex > startIndex)
            {
                HeaderSegments.Add(new TextSegment(Header[startIndex..matchIndex], false));
            }

            HeaderSegments.Add(new TextSegment(Header.Substring(matchIndex, searchText.Length), true));
            startIndex = matchIndex + searchText.Length;
        }

        if (startIndex < Header.Length)
        {
            HeaderSegments.Add(new TextSegment(Header[startIndex..], false));
        }
    }

    public override string ToString()
    {
        return Header;
    }
}
