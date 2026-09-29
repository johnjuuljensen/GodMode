namespace GodMode.Maui;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    // The main window, unlocked: it shows every profile. A profile's own window is opened by AppWindows (#340), and
    // those the app had come back after it (#341)
    protected override Window CreateWindow(IActivationState? activationState) => AppWindows.Start();
}
