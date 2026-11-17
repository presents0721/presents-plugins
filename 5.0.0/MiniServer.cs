using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace presents.PluginTestKit;

/// <summary>精简本地 HTTP 服务：GET/POST + JSON，静态资源只出 Resources 目录。</summary>
internal sealed class MiniServer : IDisposable
{
    public const string PortFileName = "alife-testkit.port";

    static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8", [".json"] = "application/json; charset=utf-8",
        [".cjs"] = "text/plain; charset=utf-8"
    };

    readonly TcpListener listener;
    readonly CancellationTokenSource cts = new();
    readonly string webRoot;
    readonly string portFile;

    public int Port { get; }
    public string BaseUrl => "http://127.0.0.1:" + Port + "/";
    public Func<string, string, string, Task<string>>? ApiHandler { get; set; }

    public MiniServer(string webRoot, int fixedPort = 0)
    {
        this.webRoot = Path.GetFullPath(webRoot);
        Directory.CreateDirectory(this.webRoot);
        listener = new TcpListener(IPAddress.Loopback, fixedPort);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        portFile = Path.Combine(Path.GetTempPath(), PortFileName);
        try { File.WriteAllText(portFile, Port.ToString()); } catch { }
        _ = Task.Run(AcceptLoop);
    }

    async Task AcceptLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cts.Token); } catch { break; }
            _ = Task.Run(() => Handle(client));
        }
    }

    async Task Handle(TcpClient client)
    {
        try
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                client.ReceiveTimeout = 10000; client.SendTimeout = 30000;
                string? head = ReadHead(stream);
                if (head == null) return;
                string[] lines = head.Split("\r\n");
                string[] first = lines[0].Split(' ');
                if (first.Length < 2) return;

                string method = first[0].ToUpperInvariant(), target = first[1];
                int contentLength = 0;
                foreach (string line in lines)
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(line[15..].Trim(), out int n)) contentLength = n;
                string body = contentLength is > 0 and < 1_000_000 ? ReadBody(stream, contentLength) : "";

                string query = "";
                int q = target.IndexOf('?');
                if (q >= 0) { query = target[(q + 1)..]; target = target[..q]; }
                try { target = Uri.UnescapeDataString(target); } catch { }

                if (method == "OPTIONS") { Write(stream, Array.Empty<byte>(), "application/json", false, "204 No Content"); return; }

                if (target.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && ApiHandler != null)
                {
                    string json;
                    try { json = await ApiHandler(target, query, body); }
                    catch (Exception ex) { json = "{\"ok\":false,\"error\":" + System.Text.Json.JsonSerializer.Serialize(ex.Message) + "}"; }
                    Write(stream, Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8", false);
                    return;
                }

                string relative = target.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                if (relative.Length == 0) relative = "index.html";
                string file;
                try { file = Path.GetFullPath(Path.Combine(webRoot, relative)); } catch { file = ""; }
                string prefix = Path.GetFullPath(webRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
                {
                    Write(stream, Encoding.UTF8.GetBytes("404"), "text/plain", false, "404 Not Found");
                    return;
                }

                string mime = Mime.TryGetValue(Path.GetExtension(file), out string? known) ? known : "application/octet-stream";
                Write(stream, File.ReadAllBytes(file), mime, method == "HEAD");
            }
        }
        catch { }
    }

    static string? ReadHead(NetworkStream stream)
    {
        MemoryStream buffer = new(); byte[] one = new byte[1];
        while (buffer.Length < 65536)
        {
            int read; try { read = stream.Read(one, 0, 1); } catch { return null; }
            if (read <= 0) return null;
            buffer.WriteByte(one[0]);
            if (buffer.Length >= 4)
            {
                byte[] d = buffer.GetBuffer(); int n = (int)buffer.Length;
                if (d[n - 4] == 13 && d[n - 3] == 10 && d[n - 2] == 13 && d[n - 1] == 10)
                    return Encoding.UTF8.GetString(d, 0, n);
            }
        }
        return null;
    }

    static string ReadBody(NetworkStream stream, int length)
    {
        byte[] b = new byte[length]; int read = 0;
        try { while (read < length) { int got = stream.Read(b, read, length - read); if (got <= 0) break; read += got; } }
        catch { }
        return Encoding.UTF8.GetString(b, 0, read);
    }

    static void Write(NetworkStream stream, byte[] bytes, string mime, bool headOnly, string status = "200 OK")
    {
        string h = "HTTP/1.1 " + status + "\r\nContent-Type: " + mime + "\r\nContent-Length: " + bytes.Length +
                   "\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Headers: *\r\n" +
                   "Access-Control-Allow-Methods: GET,POST,OPTIONS\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        byte[] head = Encoding.ASCII.GetBytes(h);
        stream.Write(head, 0, head.Length);
        if (!headOnly && status != "204 No Content") stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    public void Dispose()
    {
        try { cts.Cancel(); } catch { }
        try { listener.Stop(); } catch { }
        try { cts.Dispose(); } catch { }
        try { if (File.Exists(portFile)) File.Delete(portFile); } catch { }
    }
}
