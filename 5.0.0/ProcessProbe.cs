using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace presents.OpsSuite;

internal sealed class ProcessRecord
{
    public int Pid { get; set; }
    public int ParentPid { get; set; }
    public string Name { get; set; } = "";
    public string Group { get; set; } = "other";
    public string Type { get; set; } = "";
    public string Owner { get; set; } = "";
    public long WorkingSetMB { get; set; }
    public long PrivateMB { get; set; }
    public long GpuMB { get; set; }
    public int Handles { get; set; }
    public int Threads { get; set; }
    public int Depth { get; set; }
    public int[] Ports { get; set; } = Array.Empty<int>();
    public string CommandLine { get; set; } = "";
}

internal sealed class ProcessSample
{
    public string At { get; set; } = "";
    public long Ticks { get; set; }
    public string Label { get; set; } = "manual";
    public List<ProcessRecord> Processes { get; set; } = new();
    public Dictionary<string, long> GroupWorkingSetMB { get; set; } = new();
    public Dictionary<string, long> GroupGpuMB { get; set; } = new();
    public Dictionary<string, int> GroupCount { get; set; } = new();
    /// <summary>只统计 Alife 进程树，不含外部程序（否则 dwm.exe 等系统进程会淹没数据）。</summary>
    public long TotalWorkingSetMB { get; set; }
    public long TotalGpuMB { get; set; }
    public long SystemWorkingSetMB { get; set; }
    public long SystemGpuMB { get; set; }
    public int ProcessCount => Processes.Count;
    public string GpuAvailable { get; set; } = "unknown";
}

/// <summary>进程 / 显存采样。所有外部依赖都做了降级，采样失败只影响该字段，不抛到模块层。</summary>
internal sealed class ProcessProbe
{
    static readonly Regex WebViewType = new(@"--type=([a-zA-Z0-9_-]+)", RegexOptions.Compiled);
    static readonly Regex UserDataDir = new(@"--user-data-dir=""?([^""\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex GpuInstance = new(@"^pid_(\d+)", RegexOptions.Compiled);

    // GPU 计数器第一次 NextValue 是 0，必须复用实例
    static readonly Dictionary<string, PerformanceCounter> gpuCounters = new();
    static readonly object gpuGate = new();

    public ProcessSample Capture(string label = "manual")
    {
        ProcessSample sample = new()
        {
            At = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            Ticks = DateTime.UtcNow.Ticks,
            Label = label
        };

        Dictionary<int, (int ParentPid, string Name)> tree = NativeMethods.SnapshotProcessTree();
        Dictionary<int, List<int>> ports = NativeMethods.GetListeningPorts();
        Dictionary<int, long> gpu = ReadGpuDedicated(out string gpuState);
        sample.GpuAvailable = gpuState;

        int selfPid = Environment.ProcessId;
        int hostPid = FindHostPid(tree, selfPid);

        // 先按父链算深度与是否属于 Alife 进程树
        Dictionary<int, int> depth = new();
        foreach (int pid in tree.Keys)
        {
            int d = 0;
            int cur = pid;
            HashSet<int> seen = new();
            while (tree.TryGetValue(cur, out var parent) && parent.ParentPid > 0 &&
                   parent.ParentPid != cur && seen.Add(cur) && d < 16)
            {
                cur = parent.ParentPid;
                d++;
            }
            depth[pid] = d;
        }

        HashSet<int> alifeTree = new();
        foreach (int pid in tree.Keys)
        {
            int cur = pid;
            HashSet<int> seen = new();
            for (int i = 0; i < 16 && seen.Add(cur); i++)
            {
                if (cur == hostPid) { alifeTree.Add(pid); break; }
                if (!tree.TryGetValue(cur, out var info) || info.ParentPid <= 0) break;
                cur = info.ParentPid;
            }
        }

        foreach (Process p in SafeProcesses())
        {
            try
            {
                int pid = p.Id;
                tree.TryGetValue(pid, out var info);
                string name = string.IsNullOrEmpty(info.Name) ? SafeName(p) : info.Name;
                string cmd = NativeMethods.GetCommandLine(pid);

                ProcessRecord rec = new()
                {
                    Pid = pid,
                    ParentPid = info.ParentPid,
                    Name = name,
                    Depth = depth.GetValueOrDefault(pid),
                    CommandLine = Shorten(cmd, 500),
                    Ports = ports.TryGetValue(pid, out List<int>? list) ? list.ToArray() : Array.Empty<int>(),
                    GpuMB = gpu.TryGetValue(pid, out long bytes) ? bytes / (1024 * 1024) : 0,
                    Type = ClassifyType(name, cmd),
                    Handles = 0,
                    Threads = 0
                };

                try
                {
                    p.Refresh();
                    rec.WorkingSetMB = p.WorkingSet64 / (1024 * 1024);
                    rec.PrivateMB = p.PrivateMemorySize64 / (1024 * 1024);
                    rec.Handles = p.HandleCount;
                    rec.Threads = p.Threads.Count;
                }
                catch { /* 进程刚好退出 */ }

                rec.Group = ClassifyGroup(name, rec.Type, pid, hostPid, alifeTree.Contains(pid));
                rec.Owner = ResolveOwner(rec, alifeTree.Contains(pid));
                sample.Processes.Add(rec);
            }
            catch { }
            finally { p.Dispose(); }
        }

        sample.Processes = sample.Processes
            .OrderByDescending(x => x.GpuMB).ThenByDescending(x => x.WorkingSetMB).ToList();

        foreach (IGrouping<string, ProcessRecord> g in sample.Processes.GroupBy(x => x.Group))
        {
            sample.GroupWorkingSetMB[g.Key] = g.Sum(x => x.WorkingSetMB);
            sample.GroupGpuMB[g.Key] = g.Sum(x => x.GpuMB);
            sample.GroupCount[g.Key] = g.Count();
        }

        sample.SystemWorkingSetMB = sample.Processes.Sum(x => x.WorkingSetMB);
        sample.SystemGpuMB = sample.Processes.Sum(x => x.GpuMB);
        sample.TotalWorkingSetMB = sample.Processes
            .Where(x => x.Group != "external").Sum(x => x.WorkingSetMB);
        sample.TotalGpuMB = sample.Processes
            .Where(x => x.Group != "external").Sum(x => x.GpuMB);
        return sample;
    }

    static IEnumerable<Process> SafeProcesses()
    {
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { yield break; }

        foreach (Process p in all) yield return p;
    }

    static string SafeName(Process p)
    {
        try { return p.ProcessName; } catch { return "?"; }
    }

    static int FindHostPid(Dictionary<int, (int ParentPid, string Name)> tree, int selfPid)
    {
        // 当前进程就是 Alife 主进程（插件在同一进程内执行）
        if (tree.ContainsKey(selfPid)) return selfPid;

        foreach (KeyValuePair<int, (int ParentPid, string Name)> kv in tree)
            if (kv.Value.Name.Contains("Alife.Client", StringComparison.OrdinalIgnoreCase))
                return kv.Key;

        return selfPid;
    }

    static string ClassifyType(string name, string cmd)
    {
        if (name.Contains("msedgewebview2", StringComparison.OrdinalIgnoreCase))
        {
            Match m = WebViewType.Match(cmd);
            return m.Success ? m.Groups[1].Value : "browser";
        }
        if (name.Contains("python", StringComparison.OrdinalIgnoreCase)) return "python";
        if (name.Contains("Alife.Client", StringComparison.OrdinalIgnoreCase)) return "host";
        if (name.Contains("conhost", StringComparison.OrdinalIgnoreCase)) return "console";
        if (name.Contains("cmd", StringComparison.OrdinalIgnoreCase)) return "console";
        if (name.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)) return "ffmpeg";
        return "other";
    }

    static string ClassifyGroup(string name, string type, int pid, int hostPid, bool inAlifeTree)
    {
        if (pid == hostPid) return "alife-host";
        if (!inAlifeTree) return "external";

        return type switch
        {
            "gpu-process" => "webview-gpu",
            "renderer" => "webview-renderer",
            "utility" => "webview-utility",
            "browser" => "webview-browser",
            "python" => "python",
            "console" => "console",
            "ffmpeg" => "ffmpeg",
            _ => name.Contains("msedgewebview2", StringComparison.OrdinalIgnoreCase)
                ? "webview-other" : "alife-child"
        };
    }

    static string ResolveOwner(ProcessRecord rec, bool inAlifeTree)
    {
        if (!inAlifeTree) return "";

        Match ud = UserDataDir.Match(rec.CommandLine);
        if (ud.Success)
        {
            string dir = ud.Groups[1].Value.Replace('\\', '/').TrimEnd('/');
            string leaf = dir.Split('/').LastOrDefault() ?? "";
            if (leaf.Length > 0) return leaf;
        }

        return rec.Group == "alife-host" ? "Alife" : "";
    }

    static string Shorten(string text, int max)
        => string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max];

    /// <summary>pid → GPU 专用显存字节。计数器不可用时返回空表并说明原因。</summary>
    static Dictionary<int, long> ReadGpuDedicated(out string state)
    {
        Dictionary<int, long> result = new();
        state = "ok";
        try
        {
            PerformanceCounterCategory category = new("GPU Process Memory");
            string[] instances = category.GetInstanceNames();

            lock (gpuGate)
            {
                foreach (string instance in instances)
                {
                    Match m = GpuInstance.Match(instance);
                    if (!m.Success || !int.TryParse(m.Groups[1].Value, out int pid)) continue;

                    if (!gpuCounters.TryGetValue(instance, out PerformanceCounter? counter))
                    {
                        try
                        {
                            counter = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", instance, true);
                            gpuCounters[instance] = counter;
                            _ = counter.NextValue();   // 预热
                        }
                        catch { continue; }
                    }

                    try
                    {
                        float value = counter.NextValue();
                        if (value > 0) result[pid] = Math.Max(result.GetValueOrDefault(pid), (long)value);
                    }
                    catch { /* 实例已消失 */ }
                }

                // 清掉已经不存在的实例，避免计数器无限增长
                HashSet<string> live = instances.ToHashSet();
                foreach (string dead in gpuCounters.Keys.Where(k => !live.Contains(k)).ToList())
                {
                    gpuCounters[dead].Dispose();
                    gpuCounters.Remove(dead);
                }
            }
        }
        catch (Exception ex)
        {
            state = "unavailable: " + ex.GetType().Name;
        }

        return result;
    }
}
