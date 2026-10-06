using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AzlDl.App.Settings;
using AzlDl.App.Theming;
using AzlDl.App.ViewModels;
using AzlDl.App.Views;

namespace AzlDl.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Settings are applied before the first window exists, so it never flashes the default style.
        var settings = AppSettings.Load();
        StyleManager.Apply(this, settings.Style, settings.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(this, settings) };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
