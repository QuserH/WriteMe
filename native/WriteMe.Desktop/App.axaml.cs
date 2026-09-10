using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace WriteMe.Desktop;

public sealed class App : Application
{
    public override void Initialize() { AvaloniaXamlLoader.Load(this); Ui.InitializePalette(this); }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(Program.DataDirectory, Program.ImportLegacy);
        base.OnFrameworkInitializationCompleted();
    }
}
