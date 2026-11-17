using System.Collections.Generic;

namespace presents.OpsSuite;

/// <summary>一条体检结论：问题 → 证据 → 怎么修。</summary>
internal sealed class HealthItem
{
    public string Level { get; set; } = "ok";   // bad | warn | ok
    public string Area { get; set; } = "";      // 页面 / 插件 / 资源 / 自检
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Fix { get; set; } = "";
}

internal sealed class HealthReport
{
    public string At { get; set; } = "";
    public string Level { get; set; } = "ok";
    public int Bad { get; set; }
    public int Warn { get; set; }
    public int Ok { get; set; }
    public string Summary { get; set; } = "";
    public List<HealthItem> Items { get; set; } = new();
    public long GpuMB { get; set; }
    public long SysGpuMB { get; set; }
    public int ProcessCount { get; set; }
    public int ConfigVersions { get; set; }
    public string? TestSummary { get; set; }
}
