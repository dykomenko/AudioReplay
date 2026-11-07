using AudioReplay.App.Platform;
using AudioReplay.Core.Config;

namespace AudioReplay.App;

internal static class Program
{
    private static readonly string InstanceMutexName = @"Local\AudioReplay.SingleInstance" + AppPaths.InstanceSuffix;

    [STAThread]
    private static int Main(string[] args)
    {
        var command = ParseCommand(args);

        using var mutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Already running: forward the request (default: open settings - the way back in when the tray icon is hidden).
            ControlPipe.TrySend(command ?? ControlPipe.SettingsCommand);
            return 0;
        }

        if (command == ControlPipe.ExitCommand) return 0;

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        Application.Run(new TrayApplicationContext());
        return 0;
    }

    private static string? ParseCommand(string[] args)
    {
        if (args.Length == 0) return null;
        return args[0].TrimStart('-', '/').ToLowerInvariant() switch
        {
            "settings" => ControlPipe.SettingsCommand,
            "save" => ControlPipe.SaveCommand,
            "record" => ControlPipe.RecordCommand,
            "exit" or "quit" => ControlPipe.ExitCommand,
            _ => null,
        };
    }
}
