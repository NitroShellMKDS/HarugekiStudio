using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using HarugekiStudio.Rendering;
using HarugekiStudio.ViewModels;
using System.Collections.Specialized;

namespace HarugekiStudio.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _currentVm;
    private NotifyCollectionChangedEventHandler? _consoleCollectionChangedHandler;
    private Action? _resetViewHandler;

    // Cached control references — FindControl traverses the tree on every call,
    // so we resolve once after InitializeComponent.
    private readonly GlViewport? _vp;
    private readonly ScrollViewer? _consoleScroll;

    public MainWindow()
    {
        InitializeComponent();
        _vp = this.FindControl<GlViewport>("Viewport");
        _consoleScroll = this.FindControl<ScrollViewer>("ConsoleScroll");
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void UnbindViewModel()
    {
        if (_currentVm is null)
        {
            return;
        }

        if (_resetViewHandler is not null)
        {
            _currentVm.ResetViewRequested -= _resetViewHandler;
        }

        if (_consoleCollectionChangedHandler is not null)
        {
            _currentVm.Console.CollectionChanged -= _consoleCollectionChangedHandler;
        }

        _resetViewHandler = null;
        _consoleCollectionChangedHandler = null;
        _currentVm = null;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnbindViewModel();

        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        _currentVm = vm;

        _resetViewHandler = () => _vp?.ResetCamera();
        vm.ResetViewRequested += _resetViewHandler;

        _consoleCollectionChangedHandler = (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _consoleScroll?.ScrollToEnd());
        vm.Console.CollectionChanged += _consoleCollectionChangedHandler;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        UnbindViewModel();
        DataContextChanged -= OnDataContextChanged;
    }

    private bool _dragging, _panning;
    private Point _lastPointer;

    private void Overlay_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vp is null)
        {
            return;
        }

        PointerPoint p = e.GetCurrentPoint(_vp);
        _dragging = p.Properties.IsLeftButtonPressed && !e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _panning = p.Properties.IsMiddleButtonPressed ||
                   (p.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        if (_dragging || _panning)
        {
            _lastPointer = p.Position;
            e.Pointer.Capture(sender as IInputElement);
        }
        e.Handled = true;
    }

    private void Overlay_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if ((!_dragging && !_panning) || _vp is null)
        {
            return;
        }

        Point pos = e.GetPosition(_vp);
        double dx = pos.X - _lastPointer.X;
        double dy = pos.Y - _lastPointer.Y;
        _lastPointer = pos;

        if (_dragging)
        {
            _vp.Orbit(dx, dy);
        }
        else
        {
            _vp.Pan(dx, dy);
        }

        e.Handled = true;
    }

    private void Overlay_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragging = false;
        _panning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void Overlay_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        _vp?.Zoom((float)e.Delta.Y);
        e.Handled = true;
    }

}
