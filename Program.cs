using Avalonia;

namespace Notes;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--export-icon")
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            Logo.ExportIco(args[1]);
            return;
        }
        using var mutex = new Mutex(true, "Notes.QuickCapture.SingleInstance", out bool isNew);
        if (!isNew) return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
