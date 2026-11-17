using System.Collections.Generic;

namespace presents.PluginTestKit;

/// <summary>两次自检报告之间的差异，用来判断「这次改动有没有引入回归」。</summary>
internal sealed class TestDiff
{
    public string BaseAt { get; set; } = "";
    public string LatestAt { get; set; } = "";
    public int BaseTotal { get; set; }
    public int LatestTotal { get; set; }
    public int BaseFailed { get; set; }
    public int LatestFailed { get; set; }
    public string Summary { get; set; } = "";

    /// <summary>上次通过 / 只是提示，这次变成硬失败 —— 就是这次改出来的问题。</summary>
    public List<string> NewFailures { get; set; } = new();

    /// <summary>上次失败，这次通过。</summary>
    public List<string> Fixed { get; set; } = new();

    public List<string> StillFailing { get; set; } = new();

    /// <summary>从通过变成提示（技术债新增）。</summary>
    public List<string> NewWarnings { get; set; } = new();

    public List<string> Added { get; set; } = new();
    public List<string> Removed { get; set; } = new();
}
