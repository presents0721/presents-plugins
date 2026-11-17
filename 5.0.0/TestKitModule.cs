using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace presents.PluginTestKit;

[Module("Alife 插件测试框架",
    "给插件作者的自检套件：静态结构检查、注入脚本 DOM 桩测试、配置迁移防覆盖、热重载泄漏回归、Tests/*.test.cjs 自定义用例，以及本地测试报告面板。",
    null, typeof(TestKitEditorUI), null, 60, "开发者工具/测试")]
public sealed class TestKitModule(
    XmlFunctionCaller functionCaller,
    ILogger<TestKitModule> logger,
    Interactor<TestKitModule> interactor,
    ConfigurationSystem configurationSystem,
    PluginSystem pluginSystem) : ChatBehaviour, IConfigurable<TestKitConfig>
{
    public TestKitConfig Configuration { get; set; } = new();

    TestRunner? runner;
    MiniServer? server;
    bool running;

    public static TestKitModule? Active { get; private set; }
    public string PanelUrl => server?.BaseUrl ?? "(面板未启动)";
    public bool IsRunning => running;

    protected override Task OnAwake()
    {
        Active = this;

        try
        {
            string resources = Path.Combine(
                pluginSystem.PluginContext.GetPluginDirectoryPath("presents.PluginTestKit"), "Resources");
            runner = new TestRunner(pluginSystem, Configuration, resources);
        }
        catch (Exception ex) { logger.LogWarning(ex, "测试框架：测试运行器初始化失败"); }

        try
        {
            string resources = Path.Combine(
                pluginSystem.PluginContext.GetPluginDirectoryPath("presents.PluginTestKit"), "Resources");
            server = new MiniServer(resources, Configuration.PanelPort);
            server.ApiHandler = HandleApiAsync;
        }
        catch (Exception ex) { logger.LogWarning(ex, "测试框架：面板服务启动失败"); }

        if (Configuration.EnableAiFunctions)
        {
            XmlHandler handler = new(this)
            {
                Description = "Alife 插件自检工具：跑静态结构检查、注入脚本 DOM 桩测试、配置防覆盖检查、热重载泄漏回归，查看测试报告。",
                Explanation = "只在你明确要「检查插件有没有问题」「刚改完插件跑一遍自测」「验证热重载有没有残留进程」时调用。"
            };
            functionCaller.RegisterHandler(handler, DocumentMode.Implicit, DestroyCancellationToken);
        }

        logger.LogInformation("Alife 插件测试框架已启动，面板：{Url}", PanelUrl);
        return Task.CompletedTask;
    }

    protected override async Task OnDestroy()
    {
        try { server?.Dispose(); } catch { }
        if (ReferenceEquals(Active, this)) Active = null;
        await Task.CompletedTask;
    }

    public void PersistConfig()
    {
        try { configurationSystem.SetConfiguration(GetType(), Configuration, Character?.StorageKey ?? ""); }
        catch (Exception ex) { logger.LogWarning(ex, "测试框架：配置落盘失败"); }
    }

    public void OpenPanel()
    {
        try { Process.Start(new ProcessStartInfo(PanelUrl) { UseShellExecute = true }); }
        catch (Exception ex) { logger.LogWarning(ex, "测试框架：打开面板失败"); }
    }

    async Task<TestReport> RunAsync(string? root = null, bool save = true)
    {
        if (runner == null) throw new InvalidOperationException("测试运行器未初始化");
        running = true;
        try { return await runner.RunAllAsync(root, save); }
        finally { running = false; }
    }

    async Task<string> HandleApiAsync(string path, string query, string body)
    {
        Dictionary<string, string> q = ParseQuery(query);
        string p = path.ToLowerInvariant();

        if (p == "/api/state") return Json(new
        {
            ok = true, panel = PanelUrl, running,
            root = runner?.PluginsRoot, reports = runner?.ReportFiles(),
            latest = runner?.Latest is { } l ? new { l.At, l.Total, l.Passed, l.Failed, l.Warnings, l.Summary } : null
        });

        if (p == "/api/run")
        {
            if (running) return "{\"ok\":false,\"error\":\"已有测试在运行\"}";

            // ?root=<目录>   只扫指定目录（验证检出能力）
            // ?plugin=<插件> 只测一个插件，且不落盘（改完插件快速自查，不污染历史）
            string runRoot = q.GetValueOrDefault("root", "");
            string onePlugin = q.GetValueOrDefault("plugin", "");

            string? target = null;
            bool save = true;

            if (onePlugin.Length > 0)
            {
                string safe = Path.GetFileName(onePlugin.Trim());
                target = Path.Combine(runner?.PluginsRoot ?? "", safe);
                save = false;
            }
            else if (runRoot.Length > 0) target = runRoot;

            TestReport report = await RunAsync(target, save);
            return Json(new
            {
                ok = true, report.At, report.Total, report.Passed, report.Failed,
                report.Warnings, report.Summary, saved = save
            });
        }

        if (p == "/api/compare")
        {
            IReadOnlyList<string> files = runner?.ReportFiles() ?? Array.Empty<string>();
            if (files.Count < 2)
                return "{\"ok\":false,\"error\":\"至少需要两份报告，先完整跑一次自检\"}";

            string latestName = q.GetValueOrDefault("latest", files[0]);
            string baseName = q.GetValueOrDefault("base", files[1]);

            TestReport? before = runner!.Load(baseName);
            TestReport? after = runner!.Load(latestName);
            if (before == null || after == null)
                return "{\"ok\":false,\"error\":\"报告读取失败\"}";

            return Json(runner.Compare(before, after));
        }

        // 面板 UI 冒烟：真浏览器打开每个本地面板抓 JS 错误（慢、会弹窗口，所以独立接口）
        if (p == "/api/uismoke")
        {
            if (running) return "{\"ok\":false,\"error\":\"已有测试在运行\"}";

            running = true;
            try
            {
                List<TestResult> uiResults = await UiSmokeCheck.RunAsync(Configuration.UiBrowserCommand, 20000);
                return Json(new
                {
                    ok = true,
                    count = uiResults.Count,
                    failed = uiResults.Count(x => !x.Passed && !x.Warning),
                    results = uiResults
                });
            }
            finally { running = false; }
        }

        if (p == "/api/report")
        {
            string name = q.GetValueOrDefault("name", "");
            TestReport? report = name.Length == 0 ? runner?.Latest : runner?.Load(name);
            return report == null ? "{\"ok\":false,\"error\":\"暂无报告\"}" : Json(report);
        }

        if (p == "/api/reports") return Json(runner?.ReportFiles() ?? Array.Empty<string>());
        if (p == "/api/open") { OpenPanel(); return "{\"ok\":true}"; }

        return "{\"ok\":false,\"error\":\"未知接口\"}";
    }

    static Dictionary<string, string> ParseQuery(string query)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in (query ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = pair.IndexOf('=');
            string key = at < 0 ? pair : pair[..at];
            string value = at < 0 ? "" : pair[(at + 1)..];
            try { result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace("+", " ")); }
            catch { result[key] = value; }
        }
        return result;
    }

    // HTTP 输出统一 camelCase，和面板 JS 的字段读取保持一致
    static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    static string Json(object value) => JsonSerializer.Serialize(value, WriteOptions);

    // ------------------------------------------------------------------ AI 工具

    [XmlFunction(FunctionMode.OneShot, "runtests")]
    [Description("跑一遍插件自检：结构、JS 注入脚本、配置迁移、热重载（若开启），输出通过/失败摘要。")]
    public async Task RunTests()
    {
        if (runner == null) { interactor.Poke("[测试] 运行器未初始化。"); return; }
        interactor.Poke("[测试] 开始自检…");
        try
        {
            TestReport r = await RunAsync();
            IEnumerable<TestResult> bad = r.Results.Where(x => !x.Passed && !x.Warning);
            IEnumerable<TestResult> warn = r.Results.Where(x => !x.Passed && x.Warning);

            string text = $"[测试] {r.Summary}（{r.DurationMs} ms）";
            if (bad.Any()) text += "\n失败项：\n" + string.Join("\n", bad.Take(8).Select(x => $"- [{x.Scope}] {x.Name}: {x.Message}"));
            if (warn.Any()) text += "\n提示项：\n" + string.Join("\n", warn.Take(6).Select(x => $"- [{x.Scope}] {x.Name}: {x.Message}"));
            text += $"\n完整报告：{PanelUrl}";
            interactor.Poke(text);
        }
        catch (Exception ex) { interactor.Poke("[测试] 自检失败：" + ex.Message); }
    }

    [XmlFunction(FunctionMode.OneShot, "testreport")]
    [Description("查看最近一次测试报告的失败项与提示项明细。")]
    public void TestReportSummary()
    {
        TestReport? r = runner?.Latest;
        if (r == null) { interactor.Poke("[测试] 还没有测试报告，先调用 runtests。"); return; }

        List<string> lines = new() { $"[测试] {r.At} · {r.Summary}" };
        foreach (TestResult x in r.Results.Where(x => !x.Passed).Take(15))
        {
            lines.Add($"{(x.Warning ? "提示" : "失败")} [{x.Scope}] {x.Name}: {x.Message}");
            foreach (string d in x.Details.Take(3)) lines.Add("    " + d);
        }
        interactor.Poke(string.Join("\n", lines));
    }

    [XmlFunction(FunctionMode.OneShot, "testreload")]
    [Description("对指定插件做热重载泄漏回归：连续重载 N 次，检查 WebView2 子进程数是否持续增长。会打断该插件界面。")]
    public async Task TestReload(
        [Description("目标插件 Id，例如 presents.Wallpaper")] string plugin = "presents.Wallpaper",
        [Description("重载次数，1–10")] int rounds = 3)
    {
        rounds = Math.Clamp(rounds, 1, 10);
        interactor.Poke($"[测试] 即将连续重载 {plugin} {rounds} 次，期间它的界面会闪烁…");
        try
        {
            List<TestResult> results = await ReloadRegression.RunAsync(pluginSystem, plugin, rounds);
            interactor.Poke("[测试] " + string.Join("\n", results.Select(x =>
                (x.Passed ? "通过 " : "异常 ") + x.Name + "：" + x.Message)));
        }
        catch (Exception ex) { interactor.Poke("[测试] 热重载回归失败：" + ex.Message); }
    }

    [XmlFunction(FunctionMode.OneShot, "testpanel")]
    [Description("获取测试报告面板地址并打开。")]
    public void TestPanel()
    {
        OpenPanel();
        interactor.Poke("[测试] 面板地址：" + PanelUrl);
    }

    [XmlFunction(FunctionMode.OneShot, "testui")]
    [Description("用真实浏览器打开每个本地面板，抓取 JavaScript 运行时错误。字段名对不上、函数被变量遮蔽、渲染崩溃这类问题，静态检查永远看不到，只有真跑一遍才知道。")]
    public async Task TestUi()
    {
        try
        {
            List<TestResult> list = await UiSmokeCheck.RunAsync(Configuration.UiBrowserCommand, 20000);

            int bad = list.Count(x => !x.Passed && !x.Warning);
            if (list.Count == 0)
            {
                interactor.Poke("[测试] 没有发现正在监听的面板。");
                return;
            }

            List<string> lines = list
                .Select(x => $"{(x.Passed ? "OK" : x.Warning ? "提示" : "失败")} · {x.Scope} · {x.Message}")
                .ToList();

            interactor.Poke($"[测试] 面板冒烟 {list.Count} 个，{bad} 个有报错\n" +
                            string.Join("\n", lines.Take(8)) +
                            (lines.Count > 8 ? $"\n…还有 {lines.Count - 8} 个" : ""));
        }
        catch (Exception ex)
        {
            interactor.Poke("[测试] 面板冒烟失败：" + ex.Message);
        }
    }
}
