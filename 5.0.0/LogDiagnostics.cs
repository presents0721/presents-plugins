using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace presents.OpsSuite;

/// <summary>
/// 从 alife.log 里捞编译错误。
///
/// 这是唯一「客户端亲自说的」证据：插件编译失败时，宿主会把 Roslyn 的诊断原样写进日志，
/// 而此时模块系统直接发现不了这个插件 —— 功能列表里没有它，界面上也不会有任何提示。
/// 界面上看不出来的东西，只能回到日志里找。
/// </summary>
internal static class LogDiagnostics
{
    /// <summary>一条编译错误。</summary>
    public sealed class CompileError
    {
        public string Plugin { get; set; } = "";
        public string File { get; set; } = "";
        public int Line { get; set; }
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
        public string At { get; set; } = "";

        /// <summary>
        /// 这条错误是否已经被修掉了。
        /// 判断依据很硬：编译失败不会产出 dll。所以插件 dll 存在、且比这次报错新，
        /// 就说明后来编译成功过 —— 留着它报红只会把人骗去看已经修好的东西。
        /// </summary>
        public bool Fixed { get; set; }
    }

    public sealed class Report
    {
        public string LogPath { get; set; } = "";
        public DateTime LogTime { get; set; }
        public bool LogExists { get; set; }
        public int ScannedLines { get; set; }
        public List<CompileError> Errors { get; set; } = new();

        /// <summary>按插件聚合，插件名 → 错误数。</summary>
        public Dictionary<string, int> ByPlugin { get; set; } = new();

        public int PluginCount => ByPlugin.Count;
    }

    // Roslyn 的诊断行，宿主原样写进日志：
    // C:\...\Storage\Plugins\presents.X\Y.cs(23,35): error CS0246: The type or namespace name ...
    static readonly Regex ErrorLine = new(
        @"^(?<path>[A-Za-z]:\\[^(]+?)\((?<line>\d+),\d+\):\s*error\s+(?<code>CS\d+):\s*(?<msg>.*)$",
        RegexOptions.Compiled);

    // 日志行首的时间戳：[2026-11-15 21:53:14]
    static readonly Regex TimePrefix = new(
        @"^\[(?<t>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]", RegexOptions.Compiled);

    /// <summary>
    /// 扫日志尾部若干行。只看尾部有两个原因：
    /// 1. 几十万行的日志全读会卡住体检；
    /// 2. 更早的错误多半已经修了，留着只会制造噪音。
    /// </summary>
    public static Report Scan(string logPath, int tailLines = 4000)
    {
        Report report = new() { LogPath = logPath };

        try
        {
            if (!File.Exists(logPath)) return report;

            report.LogExists = true;
            report.LogTime = File.GetLastWriteTime(logPath);

            string[] lines = ReadTail(logPath, Math.Clamp(tailLines, 200, 50000));
            report.ScannedLines = lines.Length;

            string lastAt = "";

            foreach (string raw in lines)
            {
                string line = raw.TrimEnd('\r', '\n');
                if (line.Length == 0) continue;

                Match tm = TimePrefix.Match(line);
                if (tm.Success) lastAt = tm.Groups["t"].Value;

                Match m = ErrorLine.Match(line.Trim());
                if (!m.Success) continue;

                string file = m.Groups["path"].Value;

                int lineNo = 0;
                int.TryParse(m.Groups["line"].Value, out lineNo);

                report.Errors.Add(new CompileError
                {
                    Plugin = ResolvePlugin(file),
                    File = Path.GetFileName(file),
                    Line = lineNo,
                    Code = m.Groups["code"].Value,
                    Message = Trim(m.Groups["msg"].Value, 180),
                    At = lastAt
                });
            }

            // 同一个插件同一行同一条码只留一条（一次编译失败往往重复报很多遍）
            report.Errors = report.Errors
                .GroupBy(e => e.Plugin + "|" + e.File + "|" + e.Line + "|" + e.Code)
                .Select(g => g.Last())
                .OrderBy(e => e.Plugin, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.File, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Line)
                .ToList();

            MarkFixed(report.Errors);

            // 只统计还没修好的：已经产出 dll 的插件不该继续报红
            report.ByPlugin = report.Errors
                .Where(e => !e.Fixed)
                .GroupBy(e => e.Plugin, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        }
        catch { }

        return report;
    }

    /// <summary>
    /// 给每条错误标上「是否已修」。
    /// 依据：编译失败不产出 dll。所以插件 dll 存在且明显比报错时间新，就是后来编译成功过了。
    /// 时间不明时不标 Fixed —— 宁可多报一条，也不能把真错误吞掉。
    /// </summary>
    static void MarkFixed(List<CompileError> errors)
    {
        if (errors.Count == 0) return;

        string? compiledRoot = ResolveCompiledRoot();
        if (compiledRoot == null) return;

        Dictionary<string, DateTime> dllTimes = new(StringComparer.OrdinalIgnoreCase);

        foreach (CompileError e in errors)
        {
            if (e.Plugin.Length == 0) continue;

            if (!dllTimes.TryGetValue(e.Plugin, out DateTime dllAt))
            {
                try
                {
                    string dll = Path.Combine(compiledRoot, e.Plugin + ".dll");
                    dllAt = File.Exists(dll) ? File.GetLastWriteTime(dll) : DateTime.MinValue;
                }
                catch { dllAt = DateTime.MinValue; }

                dllTimes[e.Plugin] = dllAt;
            }

            // 没有 dll 或时间不明 → 保守地当作仍未修复
            if (dllAt == DateTime.MinValue) continue;
            if (!DateTime.TryParse(e.At, out DateTime at)) continue;

            // 日志时间只精确到秒，留两秒容差避免"同一秒内编译成功"被误判成失败
            if (dllAt > at.AddSeconds(2)) e.Fixed = true;
        }
    }

    /// <summary>运行时编译产物目录。</summary>
    static string? ResolveCompiledRoot()
    {
        try
        {
            string alife = Path.GetFullPath(
                Path.Combine(Alife.Foundation.AlifePath.StorageFolderPath, ".."));
            string dir = Path.Combine(alife, "Runtime", "CompiledPlugins");
            return Directory.Exists(dir) ? dir : null;
        }
        catch { return null; }
    }

    /// <summary>从绝对路径里抠出插件目录名：...\Storage\Plugins\&lt;这里&gt;\X.cs</summary>
    static string ResolvePlugin(string path)
    {
        try
        {
            string[] parts = path.Split(
                new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!string.Equals(parts[i], "Plugins", StringComparison.OrdinalIgnoreCase)) continue;
                return parts[i + 1];
            }

            return "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 只读文件尾部。日志可能很大，全读会把体检拖到十几秒。
    /// 从后往前按块读，凑够行数就停。
    /// </summary>
    static string[] ReadTail(string path, int tailLines)
    {
        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length == 0) return Array.Empty<string>();

            const int chunk = 256 * 1024;
            long pos = fs.Length;
            List<byte> buffer = new();
            int newlines = 0;

            while (pos > 0 && newlines <= tailLines)
            {
                int size = (int)Math.Min(chunk, pos);
                pos -= size;

                byte[] block = new byte[size];
                fs.Position = pos;
                int read = fs.Read(block, 0, size);
                if (read <= 0) break;

                buffer.InsertRange(0, new ArraySegment<byte>(block, 0, read));

                newlines = 0;
                foreach (byte b in buffer)
                    if (b == (byte)'\n') newlines++;
            }

            string text = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            string[] all = text.Split('\n');

            int skip = Math.Max(0, all.Length - tailLines);
            return all.Skip(skip).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    static string Trim(string value, int max)
    {
        string text = (value ?? "").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>体检用的一句话摘要：哪个插件、几条错误、典型是什么。</summary>
    public static string Summary(Report report)
    {
        if (!report.LogExists) return "没有找到 alife.log";
        if (report.PluginCount == 0) return "";

        IEnumerable<string> parts = report.ByPlugin
            .OrderByDescending(x => x.Value)
            .Take(4)
            .Select(x => $"{x.Key}（{x.Value}）");

        return $"{report.PluginCount} 个插件编译失败：" + string.Join("；", parts);
    }
}
