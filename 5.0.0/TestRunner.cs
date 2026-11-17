using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Foundation;

namespace presents.PluginTestKit;

/// <summary>调度各类检查、汇总报告、落盘历史。</summary>
internal sealed class TestRunner
{
    readonly PluginSystem pluginSystem;
    readonly TestKitConfig config;
    readonly string pluginsRoot;
    readonly string reportsDir;
    readonly string harnessPath;

    TestReport? latest;
    readonly object gate = new();

    public TestRunner(PluginSystem pluginSystem, TestKitConfig config, string resourcesDir)
    {
        this.pluginSystem = pluginSystem;
        this.config = config;
        harnessPath = Path.Combine(resourcesDir, "dom-harness.cjs");

        try { pluginsRoot = pluginSystem.PluginContext.PluginRootDirectory; } catch { }
        if (string.IsNullOrWhiteSpace(pluginsRoot))
            pluginsRoot = Path.Combine(AlifePath.StorageFolderPath, "Plugins");

        reportsDir = Path.Combine(AlifePath.StorageFolderPath, "PluginTestKit", "Reports");
        Directory.CreateDirectory(reportsDir);

        try
        {
            FileInfo? last = new DirectoryInfo(reportsDir).GetFiles("*.json")
                .OrderByDescending(x => x.Name).FirstOrDefault();
            if (last != null)
                latest = JsonSerializer.Deserialize<TestReport>(File.ReadAllText(last.FullName));
        }
        catch { }
    }

    public string PluginsRoot => pluginsRoot;
    public string ReportsDir => reportsDir;
    public TestReport? Latest => latest;

    public IReadOnlyList<string> ReportFiles()
    {
        try
        {
            return new DirectoryInfo(reportsDir).GetFiles("*.json")
                .OrderByDescending(x => x.Name).Take(50).Select(x => x.Name).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    public TestReport? Load(string name)
    {
        try
        {
            string safe = Path.GetFileName(name);
            string path = Path.Combine(reportsDir, safe);
            if (!path.StartsWith(reportsDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
            return JsonSerializer.Deserialize<TestReport>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    /// <summary>
    /// rootOverride 非空时只扫这个目录；save=false 时不落盘（单插件快测用，
    /// 避免一堆局部报告污染历史与回归对比）。
    /// </summary>
    public async Task<TestReport> RunAllAsync(string? rootOverride = null, bool save = true)
    {
        string root = string.IsNullOrWhiteSpace(rootOverride)
            ? pluginsRoot
            : Path.GetFullPath(rootOverride);

        Stopwatch sw = Stopwatch.StartNew();
        List<TestResult> results = new();

        if (!Directory.Exists(root))
            results.Add(new TestResult
            {
                Id = "root.missing", Name = "扫描目录存在", Category = "结构", Scope = "global",
                Passed = false, Message = "目录不存在：" + root
            });
        else
        {
            if (config.EnableStaticChecks)
                await Task.Run(() => results.AddRange(StaticChecks.Run(root)));

            if (config.EnableJsTests)
            {
                JsTestRunner js = new(config.NodePath, harnessPath, config.EnableCustomTests);
                try { await Task.Run(() => results.AddRange(js.Run(root))); }
                finally { js.Dispose(); }
            }
        }

        // 真编译闸门必须排在所有静态检查之前：后面的检查都以「能编译」为前提，
        // 而这一步就是在验证这个前提。缺 using 这类问题只有它能抓到。
        if (config.EnableCompileChecks)
        {
            string? only = null;

            try
            {
                // 单插件快测（root 直接指向某个插件目录）时只编那一个
                if (File.Exists(Path.Combine(root, "manifest.json")))
                    only = Path.GetFileName(root);
            }
            catch { }

            await Task.Run(() => results.AddRange(CompileChecks.Run(root, only)));
        }

        if (config.EnableXmlFunctionChecks)
            await Task.Run(() => results.AddRange(XmlFunctionChecks.Run(root)));

        if (config.EnableContractChecks)
        {
            string? compiledRoot = ResolveCompiledRoot(root);
            await Task.Run(() => results.AddRange(ContractChecks.Run(root, compiledRoot)));
        }

        if (config.EnableMigrationChecks)
            await Task.Run(() => results.AddRange(MigrationChecks.Run()));

        // UI 冒烟会弹浏览器、每个面板 5 秒左右，默认关闭，只在配置里显式打开时跑
        if (config.EnableUiSmokeChecks)
            results.AddRange(await UiSmokeCheck.RunAsync(config.UiBrowserCommand, 20000));

        if (config.ReloadIterations > 0)
            results.AddRange(await ReloadRegression.RunAsync(pluginSystem,
                config.ReloadTarget, config.ReloadIterations));

        sw.Stop();
        int failed = results.Count(x => !x.Passed && !x.Warning);
        int warn = results.Count(x => !x.Passed && x.Warning);
        TestReport report = new()
        {
            At = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            DurationMs = sw.ElapsedMilliseconds,
            Total = results.Count,
            Passed = results.Count(x => x.Passed),
            Failed = failed,
            Warnings = warn,
            Results = results,
            Summary = failed == 0
                ? $"全部通过（{results.Count} 项，{warn} 条提示）"
                : $"{failed} 项失败，{warn} 条提示，共 {results.Count} 项"
        };

        if (save) Save(report);
        lock (gate) latest = report;
        return report;
    }

    /// <summary>
    /// 对比两份报告：哪些是「这次改出来的新失败」、哪些「修好了」。
    /// 按 TestResult.Id 对齐，改完插件跑一次就知道有没有引入回归。
    /// </summary>
    public TestDiff Compare(TestReport before, TestReport after)
    {
        TestDiff diff = new()
        {
            BaseAt = before.At, LatestAt = after.At,
            BaseFailed = before.Failed, LatestFailed = after.Failed,
            BaseTotal = before.Total, LatestTotal = after.Total
        };

        Dictionary<string, TestResult> oldMap = before.Results
            .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        Dictionary<string, TestResult> newMap = after.Results
            .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());

        foreach (KeyValuePair<string, TestResult> kv in newMap)
        {
            TestResult now = kv.Value;
            bool nowFail = !now.Passed && !now.Warning;
            bool nowWarn = !now.Passed && now.Warning;

            if (!oldMap.TryGetValue(kv.Key, out TestResult? was))
            {
                diff.Added.Add($"{now.Scope} · {now.Name}");
                continue;
            }

            bool wasFail = !was.Passed && !was.Warning;
            string label = $"{now.Scope} · {now.Name}: {now.Message}";

            if (nowFail && !wasFail) diff.NewFailures.Add(label);
            else if (!nowFail && wasFail) diff.Fixed.Add($"{now.Scope} · {now.Name}");
            else if (nowFail && wasFail) diff.StillFailing.Add(label);
            else if (nowWarn && !was.Warning) diff.NewWarnings.Add(label);
        }

        foreach (KeyValuePair<string, TestResult> kv in oldMap)
            if (!newMap.ContainsKey(kv.Key))
                diff.Removed.Add($"{kv.Value.Scope} · {kv.Value.Name}");

        diff.Summary = diff.NewFailures.Count > 0
            ? $"引入 {diff.NewFailures.Count} 项新失败，修复 {diff.Fixed.Count} 项"
            : diff.Fixed.Count > 0
                ? $"没有新失败，修复 {diff.Fixed.Count} 项"
                : "与上次一致";

        return diff;
    }

    /// <summary>
    /// 找到运行时编译产物目录：Storage/Plugins 的上两级 + Runtime/CompiledPlugins。
    /// 用来判断「源码改了但 DLL 没重建」—— 这是最隐蔽的一类问题：
    /// 编译通过、加载成功、行为却是旧的。
    /// </summary>
    static string? ResolveCompiledRoot(string pluginsRoot)
    {
        try
        {
            string storage = Path.GetDirectoryName(pluginsRoot.TrimEnd(Path.DirectorySeparatorChar)) ?? "";
            string alife = Path.GetDirectoryName(storage) ?? "";
            if (alife.Length == 0) return null;

            string compiled = Path.Combine(alife, "Runtime", "CompiledPlugins");
            return Directory.Exists(compiled) ? compiled : null;
        }
        catch { return null; }
    }

    void Save(TestReport report)
    {
        try
        {
            string name = "report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json";
            File.WriteAllText(Path.Combine(reportsDir, name),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

            List<FileInfo> files = new DirectoryInfo(reportsDir).GetFiles("report-*.json")
                .OrderByDescending(x => x.Name).ToList();
            foreach (FileInfo old in files.Skip(Math.Max(1, config.ReportHistory)))
            {
                try { old.Delete(); } catch { }
            }
        }
        catch { }
    }
}
