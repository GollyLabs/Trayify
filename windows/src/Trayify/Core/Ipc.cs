using System.IO.Pipes;
using System.Text;

namespace Trayify.Core;

/// <summary>
/// Tiny line-based control channel (named pipe, current session). Used by a second launch of
/// Trayify.exe ("show the window"), by `Trayify.exe --cmd ...`, and by the test harness.
/// </summary>
public sealed class IpcServer : IDisposable
{
    private readonly Func<string, Task<string>> _handler;
    private readonly CancellationTokenSource _cts = new();

    public IpcServer(Func<string, Task<string>> handler) { _handler = handler; }

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var server = new NamedPipeServerStream(Paths.PipeName, PipeDirection.InOut, 4,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token);
                _ = Task.Run(() => ServeAsync(server));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Error("IPC accept failed", ex); await Task.Delay(500); }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream server)
    {
        using (server)
        {
            try
            {
                var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
                var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync();
                if (line == null) return;
                string reply;
                try { reply = await _handler(line.Trim()); }
                catch (Exception ex) { reply = "error: " + ex.Message; }
                await writer.WriteAsync(reply.TrimEnd() + "\n.\n");
                server.WaitForPipeDrain();
            }
            catch (Exception ex) { Log.Warn($"IPC session error: {ex.Message}"); }
        }
    }

    public void Dispose() => _cts.Cancel();
}

public static class IpcClient
{
    public static string? Send(string command, int timeoutMs = 5000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Paths.PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            var reader = new StreamReader(client, Encoding.UTF8);
            writer.WriteLine(command);
            var sb = new StringBuilder();
            string? l;
            while ((l = reader.ReadLine()) != null && l != ".") sb.AppendLine(l);
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            Log.Warn($"IPC send '{command}' failed: {ex.Message}");
            return null;
        }
    }
}
