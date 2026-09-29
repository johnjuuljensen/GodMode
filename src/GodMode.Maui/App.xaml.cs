namespace GodMode.Maui;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    // The main window, unlocked: it shows every profile. A profile's own window is opened by AppWindows (#340)
    protected override Window CreateWindow(IActivationState? activationState) => AppWindows.New(profile: null);
}
