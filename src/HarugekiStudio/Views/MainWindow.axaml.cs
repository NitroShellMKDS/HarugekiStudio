using System;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using HarugekiStudio.Rendering;
using HarugekiStudio.ViewModels;

namespace HarugekiStudio.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _currentVm;
    private NotifyCollectionChangedEventHandler? _consoleCollectionChangedHandler;
    private Action? _resetViewHandler;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_currentVm is not null)
        {
            if (_resetViewHandler is not null) _currentVm.ResetViewRequested -= _resetViewHandler;
            if (_consoleCollectionChangedHandler is not null) _currentVm.Console.CollectionChanged -= _consoleCollectionChangedHandler;
            _resetViewHandler = null;
            _consoleCollectionChangedHandler = null;
            _currentVm = null;
        }

        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        _currentVm = vm;

        _resetViewHandler = () => this.FindControl<GlViewport>("Viewport")?.ResetCamera();
        vm.ResetViewRequested += _resetViewHandler;

        _consoleCollectionChangedHandler = (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                this.FindControl<ScrollViewer>("ConsoleScroll")?.ScrollToEnd());
        vm.Console.CollectionChanged += _consoleCollectionChangedHandler;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_currentVm is not null)
        {
            if (_resetViewHandler is not null) _currentVm.ResetViewRequested -= _resetViewHandler;
            if (_consoleCollectionChangedHandler is not null) _currentVm.Console.CollectionChanged -= _consoleCollectionChangedHandler;
            _resetViewHandler = null;
            _consoleCollectionChangedHandler = null;
            _currentVm = null;
        }

        DataContextChanged -= OnDataContextChanged;
    }

    private bool _dragging, _panning;
    private Point _lastPointer;

    private void Viewport_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        GlViewport? vp = Viewport;
        if (vp is null) return;

        PointerPoint p = e.GetCurrentPoint(vp);
        _dragging = p.Properties.IsLeftButtonPressed && !e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _panning = p.Properties.IsMiddleButtonPressed ||
                   (p.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        _lastPointer = p.Position;
        e.Pointer.Capture(sender as IInputElement);
        e.Handled = true;
    }

    private void Viewport_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging && !_panning) return;

        GlViewport? vp = Viewport;
        if (vp is null) return;

        Point p = e.GetPosition(vp);
        double dx = p.X - _lastPointer.X, dy = p.Y - _lastPointer.Y;
        _lastPointer = p;

        if (_dragging)
        {
            vp.Orbit(dx, dy);
        }
        else if (_panning)
        {
            vp.Pan(dx, dy);
        }

        e.Handled = true;
    }

    private void Viewport_OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        GlViewport? vp = Viewport;
        if (vp is null) return;

        vp.Zoom((float)e.Delta.Y);
        e.Handled = true;
    }
}
