using ForzaFarm.Core;
using Microsoft.UI.Xaml;

namespace ForzaFarm;

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
