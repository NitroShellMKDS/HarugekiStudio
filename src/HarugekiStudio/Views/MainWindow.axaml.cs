using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using HarugekiStudio.Rendering;
using HarugekiStudio.ViewModels;

namespace HarugekiStudio.Views;

public partial class MainWindow : Window
{
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
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        vm.ResetViewRequested += () =>
            this.FindControl<GlViewport>("Viewport")?.ResetCamera();

        // Keep the console pinned to the newest line.
        vm.Console.CollectionChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                this.FindControl<ScrollViewer>("ConsoleScroll")?.ScrollToEnd());
    }
}
