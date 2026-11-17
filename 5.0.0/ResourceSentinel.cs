using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace presents.OpsSuite;

internal sealed class ResourceAlert
{
    public string At { get; set; } = "";
    public string Level { get; set; } = "info";   // info | warn | critical
    public string Kind { get; set; } = "";

    /// <summary>去重用的稳定标识（如 gpu|1234）。不能包含当前数值，否则每次采样都被当成新告警。</summary>
    public string Key { get; set; } = "";
    public string Message { get; set; } = "";
    public long Value { get; set; }
    public long Threshold { get; set; }
    public string Suggestion { get; set; } = "";
}

internal sealed class BudgetState
{
    public int Level { get; set; }              // 0 正常 / 1 警告 / 2 超限 / 3 严重
    public string At { get; set; } = "";
    public long TotalGpuMB { get; set; }
    public long TotalWorkingSetMB { get; set; }
    public long SystemGpuMB { get; set; }
    public long SystemWorkingSetMB { get; set; }
    public long GpuThresholdMB { get; set; }
    public long WorkingSetThresholdMB { get; set; }
    public List<ResourceAlert> Alerts { get; set; } = new();
}

/// <summary>
/// 资源哨兵：把采样结果变成告警和分级降级等级，并把事件广播出去。
/// 广播走 HTTP（EventFlow 的 /api/emit），因此两个插件之间没有任何编译依赖。
/// </summary>
internal sealed class ResourceSentinel
{
    public const string PortFile = "alife-eventflow.port";
    public const string EventName = "ops.resource.alert";

    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(1) };
    readonly List<ResourceAlert> recent = new();

    // 去重冷却表：key = kind|message，value = 上次上报时间
    readonly Dictionary<string, DateTime> lastAlertAt = new(StringComparer.OrdinalIgnoreCase);
    readonly object gate = new();

    public event Action<ResourceAlert>? AlertRaised;
    public BudgetState State { get; private set; } = new();

    public IReadOnlyList<ResourceAlert> Recent()
    {
        lock (gate) return recent.Take(50).ToList();
    }

    static IEnumerable<ProcessRecord> AlifeOwned(IEnumerable<ProcessRecord> all)
        => all.Where(x => x.Group != "external");

    public BudgetState Evaluate(ProcessSample now, ProcessSample? before, OpsConfig cfg)
    {
        List<ResourceAlert> alerts = new();
        int level = 0;

        void Raise(string key, string kind, string levelName, string message, long value, long threshold, string suggestion)
        {
            alerts.Add(new ResourceAlert
            {
                At = now.At, Level = levelName, Kind = kind, Key = key, Message = message,
                Value = value, Threshold = threshold, Suggestion = suggestion
            });
            level = Math.Max(level, levelName == "critical" ? 2 : 1);
        }

        if (cfg.GpuAlertMB > 0)
        {
            ProcessRecord? top = AlifeOwned(now.Processes)
                .OrderByDescending(x => x.GpuMB).FirstOrDefault();
            if (top != null && top.GpuMB >= cfg.GpuAlertMB)
                Raise("gpu|" + top.Pid, "gpu", "warn",
                    $"{top.Name} (pid {top.Pid}, {top.Type}) 专用显存 {top.GpuMB} MB 超过阈值",
                    top.GpuMB, cfg.GpuAlertMB,
                    "可启用壁纸「后台立即释放显存」或切到省显存画质档；窗口隐藏时应自动回落。");
        }

        if (cfg.WorkingSetAlertMB > 0)
        {
            ProcessRecord? top = AlifeOwned(now.Processes)
                .OrderByDescending(x => x.WorkingSetMB).FirstOrDefault();
            if (top != null && top.WorkingSetMB >= cfg.WorkingSetAlertMB)
                Raise("working-set|" + top.Pid, "working-set", "warn",
                    $"{top.Name} (pid {top.Pid}) 工作集 {top.WorkingSetMB} MB 超过阈值",
                    top.WorkingSetMB, cfg.WorkingSetAlertMB,
                    "检查是否是热重载残留的旧进程，或场景包 / 模型常驻。");
        }

        if (before != null && cfg.ProcessGrowthAlert > 0)
        {
            Dictionary<string, int> old = AlifeOwned(before.Processes).GroupBy(x => x.Name)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<string, ProcessRecord> g in AlifeOwned(now.Processes).GroupBy(x => x.Name))
            {
                int grew = g.Count() - old.GetValueOrDefault(g.Key);
                if (grew >= cfg.ProcessGrowthAlert)
                    Raise("process-growth|" + g.Key, "process-growth", "warn",
                        $"{g.Key} 进程数在两次采样间增加 {grew} 个（{old.GetValueOrDefault(g.Key)} → {g.Count()}）",
                        grew, cfg.ProcessGrowthAlert,
                        "连续重载插件时留意旧 WebView2 子进程是否未退出。");
            }
        }

        // 同一个宿主进程组里出现多个监听端口，往往是热重载残留的静态服务
        foreach (IGrouping<string, ProcessRecord> g in now.Processes
                     .Where(x => x.Ports.Length > 0).GroupBy(x => x.Group))
        {
            int[] ports = g.SelectMany(x => x.Ports).Distinct().OrderBy(x => x).ToArray();
            if (g.Key.StartsWith("webview", StringComparison.Ordinal) && ports.Length > 3)
                Raise("port-growth|" + g.Key, "port-growth", "info",
                    $"{g.Key} 下有 {ports.Length} 个监听端口：{string.Join(", ", ports.Take(8))}",
                    ports.Length, 3, "端口文件可能还指向已销毁的插件实例。");
        }

        if (alerts.Count > 0 && now.TotalGpuMB > cfg.GpuAlertMB * 2L && cfg.GpuAlertMB > 0)
            level = 3;

        BudgetState state = new()
        {
            Level = level,
            At = now.At,
            TotalGpuMB = now.TotalGpuMB,
            TotalWorkingSetMB = now.TotalWorkingSetMB,
            SystemGpuMB = now.SystemGpuMB,
            SystemWorkingSetMB = now.SystemWorkingSetMB,
            GpuThresholdMB = cfg.GpuAlertMB,
            WorkingSetThresholdMB = cfg.WorkingSetAlertMB,
            Alerts = alerts
        };

        State = state;

        // 告警去重：同一条告警（kind + 具体消息）在冷却期内只记一次。
        // 之前每 5 秒采样都会重复写一条完全相同的内容，几轮下来列表就被刷屏了。
        List<ResourceAlert> fresh = new();
        if (alerts.Count > 0)
        {
            DateTime nowTime = DateTime.Now;
            int cooldown = Math.Clamp(cfg.AlertCooldownSeconds, 0, 3600);

            lock (gate)
            {
                foreach (ResourceAlert alert in alerts)
                {
                    string key = string.IsNullOrEmpty(alert.Key)
                        ? alert.Kind + "|" + alert.Message
                        : alert.Key;
                    if (cooldown > 0 && lastAlertAt.TryGetValue(key, out DateTime last) &&
                        (nowTime - last).TotalSeconds < cooldown)
                        continue;

                    lastAlertAt[key] = nowTime;
                    fresh.Add(alert);
                }

                if (fresh.Count > 0)
                {
                    recent.InsertRange(0, fresh);
                    while (recent.Count > 200) recent.RemoveAt(recent.Count - 1);
                }
            }

            foreach (ResourceAlert alert in fresh)
            {
                AlertRaised?.Invoke(alert);
                if (cfg.NotifyOnBudgetExceeded) _ = Publish(alert);
            }
        }
        else
        {
            // 恢复正常后清空冷却表，下次再超阈值能立刻报出来
            lock (gate) lastAlertAt.Clear();
        }

        return state;
    }

    /// <summary>把告警投递给 EventFlow。端口文件不存在就静默跳过，不产生任何依赖。</summary>
    public async Task Publish(ResourceAlert alert)
    {
        try
        {
            string portFile = Path.Combine(Path.GetTempPath(), PortFile);
            if (!File.Exists(portFile)) return;
            string port = (await File.ReadAllTextAsync(portFile)).Trim();
            if (port.Length == 0) return;

            string json = System.Text.Json.JsonSerializer.Serialize(new
            {
                name = EventName,
                source = "presents.OpsSuite",
                level = alert.Level,
                kind = alert.Kind,
                message = alert.Message,
                value = alert.Value,
                threshold = alert.Threshold
            });

            using StringContent content = new(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage _ = await http.PostAsync(
                $"http://127.0.0.1:{port}/api/emit", content);
        }
        catch { /* EventFlow 未安装 / 正在重载都属正常 */ }
    }

    public void Dispose() => http.Dispose();
}
