using FH6OpenAssist.Core;
using Microsoft.UI.Xaml;

namespace FH6OpenAssist;

public partial class App : Application
{
    private readonly UserPreferences _preferences;
    private Window? _window;

    public App()
    {
        _preferences = UserPreferences.Load(AppPaths.Current);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(_preferences);
        _window.Activate();
    }
}
