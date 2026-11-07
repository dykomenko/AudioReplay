using System.IO.Pipes;
using System.Security.Principal;
using AudioReplay.Core.Config;

namespace AudioReplay.App.Platform;

/// <summary>
/// Tiny per-user command channel to the running instance ("AudioReplay.exe --save", "--record", "--settings",
/// "--exit"). Lets a second launch open the settings, and lets scripts / Stream Deck trigger actions.
/// </summary>
internal sealed class ControlPipe : IDisposable
{
    public const string SettingsCommand = "settings";
    public const string SaveCommand = "save";
    public const string RecordCommand = "record";
    public const string ExitCommand = "exit";

    private static readonly string PipeName =
        "AudioReplay.Control." + (WindowsIdentity.GetCurrent().User?.Value ?? "user") + AppPaths.InstanceSuffix;

    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string> _onCommand;

    public ControlPipe(Action<string> onCommand)
    {
        _onCommand = onCommand;
        _ = Task.Run(ServeAsync);
    }

    public static bool TrySend(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(command);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line)) _onCommand(line.Trim());
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
    }
}
