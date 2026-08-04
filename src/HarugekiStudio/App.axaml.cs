using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HarugekiStudio.ViewModels;
using HarugekiStudio.Views;

namespace HarugekiStudio;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindow window = new();
            MainViewModel vm = new(window);
            window.DataContext = vm;
            desktop.MainWindow = window;
            // Archives named on the command line open straight away.
            foreach (string arg in desktop.Args ?? [])
            {
                if (File.Exists(arg))
                {
                    vm.OpenPath(arg);
                }
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
