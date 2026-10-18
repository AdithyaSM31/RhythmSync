using System.Diagnostics;
using System.Windows;

namespace RhythmSync.App.Tests.Ui;

/// <summary>Records WPF data-binding warnings and errors (for example "System.Windows.Data Error: 40") while it is alive.</summary>
internal sealed class BindingErrorRecorder : TraceListener
{
    private readonly object _gate = new();
    private readonly List<string> _errors = [];
    private readonly SourceLevels _previousLevel;
    private bool _detached;

    public BindingErrorRecorder()
    {
        // Without a debugger attached WPF only traces once the sources have been refreshed.
        PresentationTraceSources.Refresh();
        var source = PresentationTraceSources.DataBindingSource;
        _previousLevel = source.Switch.Level;
        source.Switch.Level = SourceLevels.Warning;
        source.Listeners.Add(this);
    }

    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_gate)
                return _errors.ToArray();
        }
    }

    public override void Write(string? message) => Record(message);

    public override void WriteLine(string? message) => Record(message);

    private void Record(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return;
        lock (_gate)
            _errors.Add(message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_detached)
        {
            _detached = true;
            var source = PresentationTraceSources.DataBindingSource;
            source.Listeners.Remove(this);
            source.Switch.Level = _previousLevel;
        }
        base.Dispose(disposing);
    }
}
