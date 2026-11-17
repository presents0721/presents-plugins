using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace presents.PluginTestKit;

/// <summary>
/// 抽取 C# raw string 里的注入脚本，在 Node + DOM 桩里跑语法与运行期健康检查；
/// 同时执行插件自带的 Tests/*.test.cjs 用例。所有临时文件用完即删。
/// </summary>
internal sealed class JsTestRunner
{
    static readonly Regex RawScript = new(
        @"(?:const|static)\s+string\s+(\w*Script\w*)\s*=\s*""""""\s*\r?\n([\s\S]*?)\r?\n\s*"""""";",
        RegexOptions.Compiled);

    readonly string node;
    readonly string harness;
    readonly string workDir;

    readonly bool enableCustomTests;

    public JsTestRunner(string nodePath, string harnessPath, bool enableCustomTests = true)
    {
        node = string.IsNullOrWhiteSpace(nodePath) ? ResolveNode() : nodePath;
        harness = harnessPath;
        this.enableCustomTests = enableCustomTests;
        workDir = Path.Combine(Path.GetTempPath(), "alife-testkit-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(workDir);
    }

    public string NodePath => node;
    public bool NodeAvailable => File.Exists(node) || node.Equals("node", StringComparison.OrdinalIgnoreCase);

    public List<TestResult> Run(string pluginsRoot)
    {
        List<TestResult> results = new();

        if (!File.Exists(harness))
            return new List<TestResult> { TestFactory.Fail("js.harness", "DOM 桩可用", "JS", "global", 0,
                "dom-harness.cjs 不存在：" + harness) };

        string? probe = Exec($"\"{harness}\" --help", 5000);
        if (probe == null)
            results.Add(TestFactory.Fail("js.node", "Node 运行时可用", "JS", "global", 0,
                "无法启动 node（" + node + "）", null, warning: true));
        else
            results.Add(TestFactory.Ok("js.node", "Node 运行时可用", "JS", "global", 0, node));

        foreach (string dir in Directory.EnumerateDirectories(pluginsRoot).OrderBy(x => x))
        {
            string id = Path.GetFileName(dir);
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string text;
                try { text = File.ReadAllText(file); } catch { continue; }

                foreach (Match m in RawScript.Matches(text))
                {
                    string name = m.Groups[1].Value;
                    string code = m.Groups[2].Value;
                    string temp = Path.Combine(workDir, id + "-" + name + ".js");
                    try { File.WriteAllText(temp, code, new UTF8Encoding(false)); }
                    catch (Exception ex)
                    {
                        results.Add(TestFactory.Fail("js.write." + name, "脚本抽取", "JS", id, 0, ex.Message));
                        continue;
                    }

                    var sw = Stopwatch.StartNew();
                    string? check = Exec($"--check \"{temp}\"", 15000);
                    sw.Stop();
                    if (check == null)
                    {
                        results.Add(TestFactory.Fail("js.syntax." + name, "注入脚本语法", "JS", id,
                            sw.ElapsedMilliseconds, $"{name} 未通过 node --check",
                            new[] { LastError }));
                        continue;
                    }

                    results.Add(TestFactory.Ok("js.syntax." + name, "注入脚本语法", "JS", id,
                        sw.ElapsedMilliseconds, $"{name}（{code.Length} 字符）"));

                    string? healthJson = Exec($"\"{harness}\" \"{temp}\"", 15000);
                    if (healthJson == null)
                    {
                        results.Add(TestFactory.Fail("js.runtime." + name, "注入脚本运行", "JS", id, 0,
                            "DOM 桩执行失败", new[] { LastError }, warning: true));
                        continue;
                    }

                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(healthJson);
                        JsonElement h = doc.RootElement;
                        bool ok = h.TryGetProperty("ok", out JsonElement o) && o.GetBoolean();
                        string error = h.TryGetProperty("error", out JsonElement e) && e.ValueKind == JsonValueKind.String
                            ? e.GetString() ?? "" : "";

                        List<string> details = new();
                        if (h.TryGetProperty("unscopedGlobals", out JsonElement ug))
                            foreach (JsonElement x in ug.EnumerateArray()) details.Add("未命名空间的全局：" + x.GetString());
                        if (h.TryGetProperty("listenersWithoutSignal", out JsonElement ls) && ls.GetInt32() > 0)
                            details.Add($"{ls.GetInt32()} 个事件监听未挂 AbortController（热重载可能残留）");
                        if (h.TryGetProperty("observers", out JsonElement ob))
                            details.Add("MutationObserver: " + ob.GetInt32());
                        if (h.TryGetProperty("globalsAdded", out JsonElement ga))
                            details.Add("新增 window 全局 " + ga.GetArrayLength() + " 个");

                        results.Add(ok
                            ? TestFactory.Ok("js.runtime." + name, "注入脚本运行", "JS", id, 0,
                                "DOM 桩执行通过；" + string.Join("，", details))
                            : TestFactory.Fail("js.runtime." + name, "注入脚本运行", "JS", id, 0,
                                "DOM 桩抛异常：" + error, details));
                    }
                    catch (Exception ex)
                    {
                        results.Add(TestFactory.Fail("js.runtime." + name, "注入脚本运行", "JS", id, 0,
                            "结果解析失败：" + ex.Message, new[] { healthJson }, warning: true));
                    }
                }
            }

            // 插件自带用例
            string testsDir = Path.Combine(dir, "Tests");
            if (!Directory.Exists(testsDir)) continue;

            // EnableCustomTests = false 时不跑：之前这个配置只定义没读取，面板开关是摆设
            if (!enableCustomTests) continue;

            foreach (string testFile in Directory.EnumerateFiles(testsDir, "*.test.cjs", SearchOption.AllDirectories))
            {
                string? json = Exec($"\"{harness}\" --test \"{testFile}\"", 20000);
                if (json == null)
                {
                    results.Add(TestFactory.Fail("js.custom." + Path.GetFileName(testFile), "自定义用例", "JS", id, 0,
                        "运行失败", new[] { LastError }, warning: true));
                    continue;
                }
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement r = doc.RootElement;
                    bool ok = r.TryGetProperty("ok", out JsonElement o) && o.GetBoolean();
                    List<string> lines = new();
                    int passed = 0, failed = 0;
                    if (r.TryGetProperty("tests", out JsonElement ts))
                        foreach (JsonElement t in ts.EnumerateArray())
                        {
                            bool p = t.GetProperty("passed").GetBoolean();
                            if (p) passed++; else failed++;
                            lines.Add((p ? "PASS " : "FAIL ") + t.GetProperty("name").GetString() +
                                      (p ? "" : " — " + (t.TryGetProperty("message", out JsonElement me)
                                          ? me.GetString() : "")));
                        }
                    results.Add(ok
                        ? TestFactory.Ok("js.custom." + Path.GetFileName(testFile), "自定义用例", "JS", id, 0,
                            $"{passed} 通过 / {failed} 失败")
                        : TestFactory.Fail("js.custom." + Path.GetFileName(testFile), "自定义用例", "JS", id, 0,
                            $"{passed} 通过 / {failed} 失败", lines));
                }
                catch (Exception ex)
                {
                    results.Add(TestFactory.Fail("js.custom", "自定义用例", "JS", id, 0,
                        "结果解析失败：" + ex.Message, new[] { json }, warning: true));
                }
            }
        }

        return results;
    }

    static string LastError = "";

    string? Exec(string arguments, int timeoutMs)
    {
        try
        {
            ProcessStartInfo info = new(node)
            {
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using Process p = Process.Start(info) ?? throw new InvalidOperationException("进程未启动");
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return null; }

            LastError = stderr.Trim();
            if (p.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            {
                LastError = string.IsNullOrWhiteSpace(LastError)
                    ? "exit " + p.ExitCode : LastError + " (exit " + p.ExitCode + ")";
                return null;
            }
            return stdout.Trim();
        }
        catch (Exception ex) { LastError = ex.Message; return null; }
    }

    static string ResolveNode()
    {
        foreach (string candidate in new[]
        {
            @"C:\Program Files\nodejs\node.exe",
            @"C:\Program Files (x86)\nodejs\node.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node.exe")
        })
            if (File.Exists(candidate)) return candidate;

        try
        {
            ProcessStartInfo info = new("cmd.exe")
            {
                Arguments = "/c where node",
                UseShellExecute = false, RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using Process? p = Process.Start(info);
            string? line = p?.StandardOutput.ReadToEnd().Split('\n').FirstOrDefault(x => x.Trim().EndsWith(".exe"));
            p?.WaitForExit(3000);
            if (!string.IsNullOrWhiteSpace(line)) return line!.Trim();
        }
        catch { }

        return "node";
    }

    public void Dispose()
    {
        try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { }
    }
}
