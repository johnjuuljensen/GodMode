namespace GodMode.Maui;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    // Windows extends the page into the title bar, which shows nothing until it has a TitleBar: this gives it the
    // app's icon and name (#313), in the dark themes' near-black. Android and iOS have no title bar and ignore it.
    // The icon is the MauiIcon's own output, appiconLogo.scale-*.png beside the exe, not a second copy
    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage())
        {
            Title = "GodMode",
            TitleBar = new TitleBar
            {
                Title = "GodMode",
                Icon = "appiconlogo.png",
                BackgroundColor = Color.FromArgb("#0d0d14"),
                ForegroundColor = Color.FromArgb("#E0FFFFFF"),
            },
        };
}
