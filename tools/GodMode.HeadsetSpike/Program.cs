namespace GodMode.HeadsetSpike;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        // An exception in a UI event goes to the log (MainForm), not to WinForms' dialog
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.Run(new MainForm());
    }
}
