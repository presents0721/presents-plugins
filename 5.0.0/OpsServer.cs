using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace presents.OpsSuite;

/// <summary>
/// 只读本地面板服务（TcpListener 手写 HTTP，避免 HttpListener 的 URL ACL）。
/// 支持 GET / POST + JSON body，静态文件从 Resources 目录出。
/// 端口写入 %TEMP%/alife-ops.port，方便外部工具与 EventFlow 发现。
/// </summary>
internal sealed class OpsServer : IDisposable
{
    public const string PortFileName = "alife-ops.port";

    static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon"
    };

    readonly TcpListener listener;
    readonly CancellationTokenSource cts = new();
    readonly string webRoot;
    readonly string portFile;

    public int Port { get; }
    public string BaseUrl => "http://127.0.0.1:" + Port + "/";

    /// <summary>(path, query, body) → JSON 字符串</summary>
    public Func<string, string, string, Task<string>>? ApiHandler { get; set; }

    public OpsServer(string webRoot, int fixedPort = 0)
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
            try { client = await listener.AcceptTcpClientAsync(cts.Token); }
            catch { break; }
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
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 30000;

                string? head = ReadHead(stream);
                if (head == null) return;

                string[] lines = head.Split("\r\n");
                string[] first = lines[0].Split(' ');
                if (first.Length < 2) return;

                string method = first[0].ToUpperInvariant();
                string target = first[1];
                int contentLength = 0;
                foreach (string line in lines)
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(line[15..].Trim(), out int n)) contentLength = n;

                string body = contentLength is > 0 and < 1_000_000
                    ? ReadBody(stream, contentLength) : "";

                string query = "";
                int q = target.IndexOf('?');
                if (q >= 0) { query = target[(q + 1)..]; target = target[..q]; }
                try { target = Uri.UnescapeDataString(target); } catch { }

                if (method == "OPTIONS") { WriteJson(stream, "{}", "204 No Content"); return; }

                if (target.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && ApiHandler != null)
                {
                    string json;
                    try { json = await ApiHandler(target, query, body); }
                    catch (Exception ex)
                    {
                        json = "{\"ok\":false,\"error\":" +
                               System.Text.Json.JsonSerializer.Serialize(ex.Message) + "}";
                    }
                    WriteJson(stream, json);
                    return;
                }

                string? file = Resolve(target);
                if (file == null || !File.Exists(file))
                {
                    WriteText(stream, "404 Not Found", "no such resource: " + target);
                    return;
                }

                byte[] bytes = File.ReadAllBytes(file);
                string mime = Mime.TryGetValue(Path.GetExtension(file), out string? known)
                    ? known : "application/octet-stream";
                WriteBytes(stream, bytes, mime, method == "HEAD");
            }
        }
        catch { /* 客户端断开属正常 */ }
    }

    string? Resolve(string target)
    {
        string relative = target.TrimStart('/');
        if (relative.Length == 0) relative = "index.html";
        relative = relative.Replace('/', Path.DirectorySeparatorChar);

        string full;
        try { full = Path.GetFullPath(Path.Combine(webRoot, relative)); }
        catch { return null; }

        string prefix = Path.GetFullPath(webRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }

    static string? ReadHead(NetworkStream stream)
    {
        MemoryStream buffer = new();
        byte[] one = new byte[1];
        while (buffer.Length < 65536)
        {
            int read;
            try { read = stream.Read(one, 0, 1); } catch { return null; }
            if (read <= 0) return null;
            buffer.WriteByte(one[0]);
            if (buffer.Length >= 4)
            {
                byte[] data = buffer.GetBuffer();
                int n = (int)buffer.Length;
                if (data[n - 4] == 13 && data[n - 3] == 10 && data[n - 2] == 13 && data[n - 1] == 10)
                    return Encoding.UTF8.GetString(data, 0, n);
            }
        }
        return null;
    }

    static string ReadBody(NetworkStream stream, int length)
    {
        byte[] buffer = new byte[length];
        int read = 0;
        try
        {
            while (read < length)
            {
                int got = stream.Read(buffer, read, length - read);
                if (got <= 0) break;
                read += got;
            }
        }
        catch { }
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    static void WriteJson(NetworkStream stream, string json, string status = "200 OK")
        => WriteBytes(stream, Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8",
            status == "204 No Content", status);

    static void WriteBytes(NetworkStream stream, byte[] bytes, string mime, bool headOnly, string status = "200 OK")
    {
        string header =
            "HTTP/1.1 " + status + "\r\n" +
            "Content-Type: " + mime + "\r\n" +
            "Content-Length: " + bytes.Length + "\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Headers: *\r\n" +
            "Access-Control-Allow-Methods: GET,POST,OPTIONS\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";

        byte[] head = Encoding.ASCII.GetBytes(header);
        stream.Write(head, 0, head.Length);
        if (!headOnly && status != "204 No Content") stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    static void WriteText(NetworkStream stream, string status, string body)
        => WriteBytes(stream, Encoding.UTF8.GetBytes(body), "text/plain; charset=utf-8", false, status);

    public void Dispose()
    {
        try { cts.Cancel(); } catch { }
        try { listener.Stop(); } catch { }
        try { cts.Dispose(); } catch { }
        try { if (File.Exists(portFile)) File.Delete(portFile); } catch { }
    }
}
