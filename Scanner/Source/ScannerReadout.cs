using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WrongWay;

sealed class ScannerReadout : IDisposable
{
    private readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource stop = new();

    private static string DirectoryPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime ? runtime :
        Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } state ? state :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"),
        "wrongway-suite");

    private static UnixDomainSocketEndPoint Endpoint => new(Path.Combine(DirectoryPath, "scanner-status.sock"));

    public ScannerReadout(Func<Task<object>> readStatus)
    {
        try
        {
            var directory = Directory.CreateDirectory(DirectoryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            const UnixFileMode shared = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(directory.FullName) & shared) != 0)
                throw new IOException("Scanner readout requires a private wrongway-suite runtime directory.");

            var endpoint = Endpoint;
            string path = endpoint.ToString();
            if (File.Exists(path))
            {
                using var current = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { current.Connect(endpoint); }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    File.Delete(path);
                }
            }
            listener.Bind(endpoint);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(4);
            _ = Serve(readStatus, stop.Token);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private async Task Serve(Func<Task<object>> readStatus, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var client = await listener.AcceptAsync(token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var stream = new NetworkStream(client);
                object state = await readStatus().WaitAsync(timeout.Token).ConfigureAwait(false);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state) + "\n"), timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                if (token.IsCancellationRequested)
                    return;
            }
        }
    }

    public static async Task<string> Read()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await socket.ConnectAsync(Endpoint, timeout.Token); }
        catch (SocketException ex)
        {
            throw new IOException("No scanner readout is available for this user. Open WrongWay Scanner first.", ex);
        }
        using var stream = new NetworkStream(socket);
        using var reader = new StreamReader(stream);
        return await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("The scanner did not return status.");
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Dispose();
        stop.Dispose();
    }
}
