using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace presents.OpsSuite;

/// <summary>单个插件的维护视图：改了什么、在哪些角色跑、谁依赖它、有没有欠测。</summary>
internal sealed class PluginDossier
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public double SizeMB { get; set; }
    public int SourceFiles { get; set; }
    public int ModuleCount { get; set; }
    public int FunctionCount { get; set; }
    public string LastModified { get; set; } = "";
    public List<string> Roles { get; set; } = new();
    public List<string> DependsOn { get; set; } = new();
    public List<string> DependedBy { get; set; } = new();
    public List<string> Problems { get; set; } = new();

    /// <summary>改完但没跑过自检时的提示，空串表示没欠测。</summary>
    public string StaleTest { get; set; } = "";

    /// <summary>0=正常 1=有提示 2=有问题（参与依赖环 / 缺失依赖 / 目录不干净）</summary>
    public int Severity { get; set; }
}

internal static class DossierBuilder
{
    public static List<PluginDossier> Build(InventoryReport inventory, string storageRoot)
    {
        Dictionary<string, List<string>> roleModules = LoadRoleModules(storageRoot);
        DateTime lastTest = LastTestTime(storageRoot);

        HashSet<string> inCycle = new(StringComparer.OrdinalIgnoreCase);
        foreach (string cycle in inventory.DependencyCycles)
            foreach (string part in cycle.Split('→', '>', '-'))
                if (part.Trim().Length > 0) inCycle.Add(part.Trim());

        HashSet<string> missingDep = new(StringComparer.OrdinalIgnoreCase);
        foreach (string md in inventory.MissingDependencies)
        {
            int arrow = md.IndexOf('→');
            if (arrow > 0) missingDep.Add(md[..arrow].Trim());
        }

        List<PluginDossier> list = new();

        foreach (PluginInfo p in inventory.Plugins)
        {
            PluginDossier d = new()
            {
                Id = p.Id,
                Version = p.Version,
                SizeMB = Math.Round(p.DiskKB / 1024.0, 1),
                SourceFiles = p.SourceFiles,
                ModuleCount = p.ModuleCount,
                FunctionCount = p.XmlFunctions.Length,
                DependsOn = p.Dependencies.Where(x => x.Length > 0).ToList(),
                Problems = p.Issues.ToList()
            };

            DateTime newest = NewestFile(p.Directory);
            d.LastModified = newest == DateTime.MinValue ? "—" : newest.ToString("MM-dd HH:mm");

            // 在哪些角色启用：角色的 Modules 里包含这个插件的任一模块
            foreach (KeyValuePair<string, List<string>> kv in roleModules)
            {
                bool hit = kv.Value.Any(m => p.Modules.Contains(m, StringComparer.OrdinalIgnoreCase));
                if (hit) d.Roles.Add(kv.Key);
            }

            d.DependedBy = inventory.Plugins
                .Where(x => !string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase) &&
                            x.Dependencies.Contains(p.Id, StringComparer.OrdinalIgnoreCase))
                .Select(x => x.Id).ToList();

            if (inCycle.Contains(p.Id)) d.Problems.Add("参与依赖环");
            if (missingDep.Contains(p.Id)) d.Problems.Add("有依赖指向不存在的插件");

            // 欠测提醒：源码/资源比最后一次自检更新。容差 1 分钟即可（自检只要 5 秒），
            // 太宽会让「改完 4 分钟」这种真欠测漏掉。
            if (lastTest != DateTime.MinValue && newest != DateTime.MinValue &&
                newest > lastTest.AddMinutes(1))
                d.StaleTest = $"{newest:MM-dd HH:mm} 改过，之后没跑自检";

            d.Severity = d.Problems.Count > 0 ? 2 : (d.StaleTest.Length > 0 ? 1 : 0);
            list.Add(d);
        }

        return list
            .OrderByDescending(x => x.Severity)
            .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static Dictionary<string, List<string>> LoadRoleModules(string storageRoot)
    {
        Dictionary<string, List<string>> result = new(StringComparer.OrdinalIgnoreCase);
        string charRoot = Path.Combine(storageRoot, "Character");
        if (!Directory.Exists(charRoot)) return result;

        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(charRoot); }
        catch { return result; }

        foreach (string dir in dirs)
        {
            string index = Path.Combine(dir, "index.json");
            if (!File.Exists(index)) continue;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(index));
                if (!doc.RootElement.TryGetProperty("Modules", out JsonElement mods) ||
                    mods.ValueKind != JsonValueKind.Array) continue;

                result[Path.GetFileName(dir)] = mods.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString() ?? "")
                    .Where(x => x.Length > 0).ToList();
            }
            catch { }
        }
        return result;
    }

    /// <summary>从 TestKit 的报告文件名里取最后一次自检时间（文件名形如 report-20261114-112047.json）。</summary>
    static DateTime LastTestTime(string storageRoot)
    {
        string dir = Path.Combine(storageRoot, "PluginTestKit", "Reports");
        if (!Directory.Exists(dir)) return DateTime.MinValue;

        try
        {
            DateTime newest = DateTime.MinValue;
            foreach (FileInfo f in new DirectoryInfo(dir).GetFiles("report-*.json"))
            {
                string stamp = f.Name.Replace("report-", "").Replace(".json", "");
                if (DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out DateTime at) && at > newest)
                    newest = at;
            }
            return newest;
        }
        catch { return DateTime.MinValue; }
    }

    /// <summary>
    /// 只看插件根目录和 Resources/，避免遍历 74 个插件的 obj/bin（几万次 stat 会拖到秒级）。
    /// 插件改动的入口基本都在这两处。
    /// </summary>
    static bool IsSource(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".cs" or ".html" or ".js" or ".cjs" or ".mjs" or ".css" or ".json" or ".csproj";
    }

    static DateTime NewestFile(string dir)
    {
        DateTime newest = DateTime.MinValue;

        void Take(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (string f in Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly))
                {
                    // 只认源码与资源：插件运行时写的日志/缓存会把 mtime 刷新成“刚改过”，
                    // 那种改动并不会让自检结论失效。
                    if (!IsSource(f)) continue;

                    try
                    {
                        DateTime t = File.GetLastWriteTime(f);
                        if (t > newest) newest = t;
                    }
                    catch { }
                }
            }
            catch { }
        }

        Take(dir);
        Take(Path.Combine(dir, "Resources"));
        return newest;
    }
}
