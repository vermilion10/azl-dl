using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Platform;

namespace AzlDl.App.Viewer;

// A minimal static file server for the viewer page. Browsers block the XHR requests the Spine and Live2D runtimes
// make for file:// URLs, so the data folder is served over HTTP instead. It listens on 127.0.0.1 only, answers GET
// and HEAD, never serves anything outside the data folder, and serves viewer.html from the app's own resources.
// /voice/<bank>/<cue>.wav decodes a voice line from the downloaded banks on request.
public sealed class LocalFileServer : IDisposable
{
    private const string ViewerPath = "/viewer.html";
    private static readonly Uri ViewerResource = new("avares://azl-dl-app/Assets/Viewer/viewer.html");

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json",
        [".png"] = "image/png",
        [".wav"] = "audio/wav",
    };

    private readonly TcpListener listener;
    private readonly string root;
    private readonly AzlDl.Core.Audio.VoiceLibrary voices;
    private readonly CancellationTokenSource stopping = new();

    public LocalFileServer(string root)
    {
        this.root = Path.GetFullPath(root);
        voices = new AzlDl.Core.Audio.VoiceLibrary(Path.Combine(this.root, "AssetBundles"));
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    public int Port { get; }

    public string Root => root;

    public Uri UrlFor(string pathAndQuery) => new($"http://127.0.0.1:{Port}{pathAndQuery}");

    public static string VoicePath(AzlDl.Core.Audio.VoiceCue cue) => $"/voice/{cue.Bank}/{cue.Cue}.wav";

    public void Dispose()
    {
        stopping.Cancel();
        listener.Stop();
    }

    private async Task AcceptLoopAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stopping.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var requestLine = await ReadRequestLineAsync(stream);
                var parts = requestLine?.Split(' ');
                if (parts is not { Length: >= 2 } || parts[0] is not ("GET" or "HEAD"))
                {
                    await WriteAsync(stream, 405, "text/plain", "Method not allowed"u8.ToArray(), headOnly: false);
                    return;
                }

                var path = Uri.UnescapeDataString(parts[1].Split('?')[0]);
                var headOnly = parts[0] == "HEAD";
                var body = Read(path);
                if (body is null)
                {
                    await WriteAsync(stream, 404, "text/plain", "Not found"u8.ToArray(), headOnly);
                    return;
                }

                var type = ContentTypes.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");
                await WriteAsync(stream, 200, type, body, headOnly);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                // The page went away mid-request; nothing to do.
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                // A bank that doesn't decode; the page sees the connection close and shows no audio.
            }
        }
    }

    private byte[]? Read(string path)
    {
        var voice = path.Split('/');
        if (voice is ["", "voice", var bank, var file] && file.EndsWith(".wav", StringComparison.Ordinal))
        {
            return voices.ReadWav(new AzlDl.Core.Audio.VoiceCue(bank, file[..^4]));
        }

        if (path == ViewerPath)
        {
            using var resource = AssetLoader.Open(ViewerResource);
            using var copy = new MemoryStream();
            resource.CopyTo(copy);
            return copy.ToArray();
        }

        var full = Path.GetFullPath(Path.Combine(root, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            return null;
        }

        return File.ReadAllBytes(full);
    }

    // Reads up to the end of the headers and returns the first line.
    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total));
            if (read == 0)
            {
                break;
            }

            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                return text[..text.IndexOf("\r\n", StringComparison.Ordinal)];
            }
        }

        return null;
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string type, byte[] body, bool headOnly)
    {
        var reason = status switch { 200 => "OK", 404 => "Not Found", _ => "Method Not Allowed" };
        var header = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n" +
                     "Cache-Control: no-cache\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        if (!headOnly)
        {
            await stream.WriteAsync(body);
        }
    }
}
