using Avalonia;

namespace Wandur.Desktop;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--script-worker"))
        {
            Console.InputEncoding = new System.Text.UTF8Encoding(false);
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            Wandur.Core.Scripting.ScriptWorker.RunAsync(Console.In, Console.Out).GetAwaiter().GetResult();
            return;
        }
        var dataIndex = Array.IndexOf(args, "--data-dir");
        if (dataIndex >= 0 && dataIndex + 1 < args.Length) App.DataDirectory = Path.GetFullPath(args[dataIndex + 1]);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
