using CommunityToolkit.Mvvm.ComponentModel;
using System.Text;

namespace HarugekiStudio.ViewModels;

/// <summary>
/// The timestamped transcript shown in the Console pane, capped so a long session
/// cannot grow without bound.
/// </summary>
public sealed partial class ConsoleLog : ObservableObject
{
    private const int MaxLines = 500;

    private readonly StringBuilder _text = new();
    private int _lines;

    public string Text => _text.ToString();

    /// <summary>The most recent line, for the status bar.</summary>
    public string Last { get; private set; } = string.Empty;

    public void Append(string line)
    {
        _ = _text.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ").AppendLine(line);
        _lines++;
        Last = line;

        Trim();

        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Last));
    }

    public void Clear()
    {
        _ = _text.Clear();
        _lines = 0;
        Last = string.Empty;
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Last));
    }

    /// <summary>
    /// Drops the oldest line once the cap is reached. The line count is tracked as
    /// lines are added rather than recounted by scanning the buffer, which made
    /// every log call cost a pass over the whole transcript.
    /// </summary>
    private void Trim()
    {
        while (_lines > MaxLines)
        {
            int firstBreak = -1;
            for (int i = 0; i < _text.Length; i++)
            {
                if (_text[i] == '\n')
                {
                    firstBreak = i;
                    break;
                }
            }

            if (firstBreak < 0)
            {
                return;
            }

            _ = _text.Remove(0, firstBreak + 1);
            _lines--;
        }
    }
}
