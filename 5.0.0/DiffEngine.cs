using System;
using System.Collections.Generic;
using System.Linq;

namespace presents.OpsSuite;

internal sealed class SampleDiff
{
    public string At { get; set; } = "";
    public string Label { get; set; } = "";
    public string BeforeLabel { get; set; } = "";
    public string AfterLabel { get; set; } = "";
    public int BeforeCount { get; set; }
    public int AfterCount { get; set; }
    public int AddedProcesses { get; set; }
    public int RemovedProcesses { get; set; }
    public long WorkingSetDeltaMB { get; set; }
    public long GpuDeltaMB { get; set; }
    public List<string> Added { get; set; } = new();
    public List<string> Removed { get; set; } = new();
    public List<string> GroupChanges { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

internal static class DiffEngine
{
    public static SampleDiff Compare(ProcessSample before, ProcessSample after, string label)
    {
        SampleDiff diff = new()
        {
            At = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Label = label,
            BeforeLabel = before.Label,
            AfterLabel = after.Label,
            BeforeCount = before.ProcessCount,
            AfterCount = after.ProcessCount,
            WorkingSetDeltaMB = after.TotalWorkingSetMB - before.TotalWorkingSetMB,
            GpuDeltaMB = after.TotalGpuMB - before.TotalGpuMB
        };

        // 用 (name,pid) 做身份：pid 复用会让同一进程看起来"没变"，所以两个都记
        HashSet<string> a = before.Processes.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> b = after.Processes.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        diff.AddedProcesses = b.Count - a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
        diff.RemovedProcesses = a.Count - a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
        diff.Added = after.Processes.Where(x => !a.Contains(Key(x)))
            .Select(Describe).Take(30).ToList();
        diff.Removed = before.Processes.Where(x => !b.Contains(Key(x)))
            .Select(Describe).Take(30).ToList();

        foreach (string group in before.GroupWorkingSetMB.Keys
                     .Concat(after.GroupWorkingSetMB.Keys).Distinct().OrderBy(x => x))
        {
            long ws0 = before.GroupWorkingSetMB.GetValueOrDefault(group);
            long ws1 = after.GroupWorkingSetMB.GetValueOrDefault(group);
            long gpu0 = before.GroupGpuMB.GetValueOrDefault(group);
            long gpu1 = after.GroupGpuMB.GetValueOrDefault(group);
            int c0 = before.GroupCount.GetValueOrDefault(group);
            int c1 = after.GroupCount.GetValueOrDefault(group);
            if (ws0 == ws1 && gpu0 == gpu1 && c0 == c1) continue;

            diff.GroupChanges.Add(
                $"{group}: 进程 {c0}→{c1}, 工作集 {ws0}→{ws1} MB ({ws1 - ws0:+0;-0;0}), 显存 {gpu0}→{gpu1} MB ({gpu1 - gpu0:+0;-0;0})");
        }

        if (diff.AddedProcesses > 0 && label.Contains("reload", StringComparison.OrdinalIgnoreCase))
            diff.Notes.Add($"重载后净增 {diff.AddedProcesses} 个进程，检查是否有旧实例残留。");
        if (diff.GpuDeltaMB > 200)
            diff.Notes.Add($"显存上升 {diff.GpuDeltaMB} MB，可能是新挂载的 WebGL 场景或贴图缓存。");
        if (diff.GpuDeltaMB < -200)
            diff.Notes.Add($"显存回落 {-diff.GpuDeltaMB} MB，说明释放路径生效。");
        if (after.GpuAvailable != "ok")
            diff.Notes.Add("GPU 计数器不可用：" + after.GpuAvailable);

        return diff;
    }

    static string Key(ProcessRecord r) => r.Name + "#" + r.Pid;

    static string Describe(ProcessRecord r) =>
        $"{r.Name} (pid {r.Pid}, {r.Group}/{r.Type}, {r.WorkingSetMB} MB, GPU {r.GpuMB} MB)";
}
