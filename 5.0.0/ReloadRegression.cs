using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Alife.Framework;

namespace presents.PluginTestKit;

internal sealed class ReloadPoint
{
    public int Round { get; set; }
    public int TotalProcesses { get; set; }
    public int WebViewProcesses { get; set; }
    public long WorkingSetMB { get; set; }
}

/// <summary>
/// 热重载泄漏回归：真实重载目标插件 N 次，比对进程数 / WebView2 子进程数 / 工作集。
/// 默认关闭（会打断界面），只能由 AI 或面板显式触发，且不允许重载测试框架自己。
/// </summary>
internal static class ReloadRegression
{
    public static async Task<List<TestResult>> RunAsync(PluginSystem pluginSystem, string target, int iterations)
    {
        List<TestResult> results = new();
        if (iterations <= 0)
            return new List<TestResult> { TestFactory.Ok("reload.skip", "热重载回归", "热重载", target, 0, "已按配置跳过") };

        if (string.IsNullOrWhiteSpace(target) ||
            target.Contains("PluginTestKit", StringComparison.OrdinalIgnoreCase))
            return new List<TestResult> { TestFactory.Fail("reload.target", "热重载回归", "热重载", target, 0,
                "目标插件无效，或不允许重载测试框架自身") };

        Stopwatch sw = Stopwatch.StartNew();
        List<ReloadPoint> points = new() { Snapshot(0) };
        List<string> errors = new();

        for (int i = 1; i <= Math.Min(iterations, 10); i++)
        {
            try
            {
                await pluginSystem.ReloadPlugin(target);
                await Task.Delay(1600);
                points.Add(Snapshot(i));
            }
            catch (Exception ex) { errors.Add($"第 {i} 次重载失败：{ex.Message}"); break; }
        }
        sw.Stop();

        int first = points[0].WebViewProcesses;
        int last = points[^1].WebViewProcesses;
        int max = points.Max(x => x.WebViewProcesses);
        List<string> trace = points.Select(x =>
            $"#{x.Round}: 进程 {x.TotalProcesses}, WebView2 {x.WebViewProcesses}, 工作集 {x.WorkingSetMB} MB").ToList();

        bool leak = errors.Count == 0 && (last - first >= 4 || max - first >= 6);
        results.Add(leak
            ? TestFactory.Fail("reload.leak", "热重载不残留进程", "热重载", target, sw.ElapsedMilliseconds,
                $"WebView2 子进程 {first} → {last}（峰值 {max}），疑似旧实例未释放", trace)
            : TestFactory.Ok("reload.leak", "热重载不残留进程", "热重载", target, sw.ElapsedMilliseconds,
                $"{points.Count - 1} 次重载，WebView2 子进程 {first} → {last}（峰值 {max}）；" +
                string.Join("；", trace.TakeLast(2))));

        if (errors.Count > 0)
            results.Add(TestFactory.Fail("reload.errors", "热重载过程无异常", "热重载", target, 0,
                string.Join("；", errors), trace, warning: true));

        return results;
    }

    static ReloadPoint Snapshot(int round)
    {
        int total = 0, webview = 0;
        long ws = 0;
        try
        {
            foreach (Process p in Process.GetProcesses())
            {
                using (p)
                {
                    total++;
                    string name = "";
                    try { name = p.ProcessName; } catch { }
                    if (name.Contains("msedgewebview2", StringComparison.OrdinalIgnoreCase)) webview++;
                    try { ws += p.WorkingSet64 / (1024 * 1024); } catch { }
                }
            }
        }
        catch { }
        return new ReloadPoint { Round = round, TotalProcesses = total, WebViewProcesses = webview, WorkingSetMB = ws };
    }
}
