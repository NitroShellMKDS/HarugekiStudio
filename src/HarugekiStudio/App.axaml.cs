using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using HarugekiStudio.Services;
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
            MainViewModel vm = new(new WindowStorageProvider(window.StorageProvider));
            vm.RequestClose += window.Close;
            window.DataContext = vm;
            desktop.MainWindow = window;
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
