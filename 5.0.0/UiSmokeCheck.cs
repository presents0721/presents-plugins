using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace presents.PluginTestKit;

/// <summary>
/// 面板 UI 冒烟：用 agent-browser 真打开每个在线面板，抓 console 报错。
/// 这是唯一能覆盖「字段契约对不上、运行时函数被遮蔽、渲染崩溃」的手段 ——
/// 静态检查永远看不到，因为语法没错、接口也返回 200。
///
/// 依赖外部 CLI（agent-browser）。找不到就返回一条提示，不算失败。
/// </summary>
internal static class UiSmokeCheck
{
    public sealed class PanelResult
    {
        public string Name { get; set; } = "";
        public int Port { get; set; }
        public int ErrorCount { get; set; }
        public bool HasContent { get; set; }
        public int TextLength { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    public static async Task<List<TestResult>> RunAsync(string browserCommand, int perPanelTimeoutMs)
    {
        List<TestResult> results = new();

        List<(string Name, int Port)> panels = FindPanels();
        if (panels.Count == 0)
        {
            results.Add(TestFactory.Ok("ui.none", "面板冒烟", "UI", "global", 0, "没有发现正在监听的面板"));
            return results;
        }

        if (!CommandExists(browserCommand))
        {
            results.Add(TestFactory.Fail("ui.noprog", "面板冒烟", "UI", "global", 0,
                $"找不到 {browserCommand}，跳过面板冒烟",
                new List<string> { "安装：npm i -g agent-browser，或在模块配置里改「浏览器命令」" },
                warning: true));
            return results;
        }

        Run(browserCommand, "close --all", 8000);

        try
        {
            foreach ((string name, int port) in panels)
            {
                PanelResult r = new() { Name = name, Port = port };

                try
                {
                    Run(browserCommand, "errors --clear", 6000);
                    Run(browserCommand, $"open http://127.0.0.1:{port}/", perPanelTimeoutMs);
                    await Task.Delay(2200);

                    string json = Run(browserCommand, "errors --json", 12000);
                    r.Errors = ParseErrors(json);
                    r.ErrorCount = r.Errors.Count;

                    string len = Run(browserCommand, "eval document.body.innerText.length", 12000);
                    r.TextLength = ParseInt(len);
                    r.HasContent = r.TextLength > 30;
                }
                catch (Exception ex)
                {
                    r.Errors.Add("检查过程异常：" + ex.Message);
                    r.ErrorCount = 1;
                }

                string scope = $"{name}:{port}";

                if (r.ErrorCount > 0)
                    results.Add(TestFactory.Fail(
                        "ui." + name, "面板冒烟", "UI", scope, 0,
                        $"{r.ErrorCount} 条运行时错误",
                        r.Errors.Take(6).ToList()));
                else if (!r.HasContent)
                    results.Add(TestFactory.Fail(
                        "ui." + name, "面板冒烟", "UI", scope, 0,
                        $"页面打开但几乎没有内容（{r.TextLength} 字符）",
                        new List<string> { "可能是接口全挂了，或 HTML 结构被改坏" },
                        warning: true));
                else
                    results.Add(TestFactory.Ok(
                        "ui." + name, "面板冒烟", "UI", scope, 0,
                        $"无报错，正文 {r.TextLength} 字符"));
            }
        }
        finally
        {
            Run(browserCommand, "close --all", 8000);
        }

        return results;
    }

    /// <summary>找 %TEMP% 下所有 alife-*.port 且端口真的在监听的面板。</summary>
    static List<(string, int)> FindPanels()
    {
        List<(string, int)> panels = new();

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(Path.GetTempPath(), "alife-*.port"); }
        catch { return panels; }

        foreach (string file in files)
        {
            try
            {
                string text = File.ReadAllText(file).Trim();
                if (!int.TryParse(text, out int port) || port <= 0) continue;
                if (!IsListening(port)) continue;

                panels.Add((Path.GetFileNameWithoutExtension(file), port));
            }
            catch { }
        }

        return panels;
    }

    static bool IsListening(int port)
    {
        try
        {
            using System.Net.Sockets.TcpClient client = new();
            Task t = client.ConnectAsync("127.0.0.1", port);
            return t.Wait(250) && client.Connected;
        }
        catch { return false; }
    }

    static bool CommandExists(string command)
    {
        try
        {
            using Process p = Process.Start(new ProcessStartInfo("cmd.exe", "/c where " + command)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            p.WaitForExit(5000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// 跑一条命令并取输出。
    /// 关键：Kill 之后管道不一定关闭，直接 ReadToEnd().Result 会永久阻塞 ——
    /// 之前就是卡在这里，把 running 卡成 true，后续调用全被"已有测试在运行"挡掉。
    /// </summary>
    static string Run(string command, string args, int timeoutMs)
    {
        ProcessStartInfo psi = new("cmd.exe", $"/c {command} {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using Process p = Process.Start(psi)!;
        Task<string> so = p.StandardOutput.ReadToEndAsync();
        Task<string> se = p.StandardError.ReadToEndAsync();

        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            p.WaitForExit(2000);
        }

        // 读取也给上限：进程被强杀时管道可能不再产生数据，不能无限等
        Task.WhenAny(Task.WhenAll(so, se), Task.Delay(3000)).Wait();

        string output = "";
        if (so.IsCompletedSuccessfully) output += so.Result;
        if (se.IsCompletedSuccessfully) output += se.Result;

        return output;
    }

    static List<string> ParseErrors(string json)
    {
        List<string> list = new();
        if (string.IsNullOrWhiteSpace(json)) return list;

        int start = json.IndexOf('{');
        if (start < 0) return list;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json[start..]);
            if (!doc.RootElement.TryGetProperty("data", out JsonElement data)) return list;
            if (!data.TryGetProperty("errors", out JsonElement errors) ||
                errors.ValueKind != JsonValueKind.Array)
                return list;

            HashSet<string> seen = new(StringComparer.Ordinal);

            foreach (JsonElement item in errors.EnumerateArray())
            {
                string text = item.TryGetProperty("text", out JsonElement t) ? t.GetString() ?? "" : "";
                if (text.Length == 0) continue;

                // 同一个错误在一次渲染里会出现很多次，只留一条
                string key = text.Split('\n')[0];
                if (!seen.Add(key)) continue;

                list.Add(key.Length > 150 ? key[..150] : key);
                if (list.Count >= 20) break;
            }
        }
        catch { }

        return list;
    }

    static int ParseInt(string raw)
    {
        // eval 返回带引号的字符串，例如 "1234"
        string digits = new(raw.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int v) ? v : 0;
    }
}
