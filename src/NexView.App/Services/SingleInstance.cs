using System.IO.Pipes;
using System.Text;
using NexView.Core.Infrastructure;

namespace NexView.App.Services;

/// <summary>Tek pencere: ikinci başlatma dosya yollarını çalışan örneğe iletir ve kapanır.</summary>
public static class SingleInstance
{
    private static string PipeName
    {
        get
        {
            var user = new string(Environment.UserName.Where(char.IsLetterOrDigit).ToArray());
            return $"NexView-{user}";
        }
    }

    public static bool TrySendToExisting(IReadOnlyList<string> files, string? tab = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(300);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            if (tab != null) writer.WriteLine("::tab=" + tab);
            if (tab != null) writer.WriteLine("--tab=" + tab);
            foreach (var f in files) writer.WriteLine(Path.GetFullPath(f));
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void StartServer(Action<string[]> onReceived, CancellationToken ct)
    {
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync(ct);
                    var files = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                    onReceived(files);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Debug($"Tek pencere kanalı hatası: {ex.Message}");
                    try { await Task.Delay(500, ct); } catch { break; }
                }
            }
        }, ct);
    }
}
