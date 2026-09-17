using Avalonia;

namespace Briefcase.ServerManager;

internal static class Program
{
    public static string ApplicationDirectory { get; private set; } = AppContext.BaseDirectory;

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--root")
        {
            ApplicationDirectory = Path.GetFullPath(args[1]);
            args = [];
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
