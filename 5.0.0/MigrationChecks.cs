using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Alife.Foundation;

namespace presents.PluginTestKit;

/// <summary>
/// 配置迁移防覆盖检查：对比配置的历史版本，找出「旧版有、新版整组消失」的情况。
/// 这正是「同名预设被内置值整组覆盖」那类问题的自动检测。
/// 数据来源优先用 OpsSuite 的 config-index.json；没装 OpsSuite 时退化为结构健康扫描。
/// </summary>
internal static class MigrationChecks
{
    public static List<TestResult> Run()
    {
        List<TestResult> results = new();
        string root = AlifePath.StorageFolderPath;
        string index = Path.Combine(root, "OpsSuite", "config-index.json");
        string historyRoot = Path.Combine(root, "OpsSuite", "ConfigHistory");
        Stopwatch sw = Stopwatch.StartNew();

        if (!File.Exists(index))
        {
            results.Add(TestFactory.Fail("migration.index", "配置历史可用", "配置迁移", "global", 0,
                "未发现运维套件的配置保险库索引，跳过版本对比（安装 OpsSuite 后自动启用）", null, warning: true));
            return StructuralScan(root, results);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(index));
            int compared = 0;
            List<string> warnings = new();

            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                string path = prop.Name;
                if (!prop.Value.TryGetProperty("history", out JsonElement history) ||
                    history.ValueKind != JsonValueKind.Array) continue;

                ConfigVersionInfo[] versions = history.EnumerateArray()
                    .Select(ParseVersion).Where(x => x != null).Select(x => x!).ToArray();
                if (versions.Length < 2) continue;

                for (int i = 0; i < Math.Min(versions.Length - 1, 3); i++)
                {
                    ConfigVersionInfo older = versions[i + 1], newer = versions[i];
                    string? olderFile = FindFile(historyRoot, path, older.File);
                    string? newerFile = FindFile(historyRoot, path, newer.File);
                    if (olderFile == null || newerFile == null) continue;

                    Dictionary<string, string> oldLeaves = Flatten(olderFile);
                    Dictionary<string, string> newLeaves = Flatten(newerFile);
                    compared++;

                    List<string> lost = oldLeaves.Keys.Where(k => !newLeaves.ContainsKey(k)).OrderBy(k => k).ToList();
                    List<string> changed = oldLeaves.Where(kv => newLeaves.TryGetValue(kv.Key, out string? v) && v != kv.Value)
                        .Select(kv => $"{kv.Key}: {kv.Value} → {newLeaves[kv.Key]}").OrderBy(x => x).ToList();

                    if (lost.Count > 0)
                        warnings.Add($"{path} @ {newer.At}: 消失 {lost.Count} 个字段 → {string.Join(", ", lost.Take(5))}");
                    if (lost.Count >= 3 && changed.Count >= 3)
                        warnings.Add($"※ {path} @ {newer.At}: 疑似「整组被替换」（{lost.Count} 消失 + {changed.Count} 改写）");
                }
            }

            sw.Stop();
            results.Add(warnings.Count == 0
                ? TestFactory.Ok("migration.compare", "配置版本防覆盖", "配置迁移", "global", sw.ElapsedMilliseconds,
                    $"对比 {compared} 组相邻版本，未发现整组覆盖")
                : TestFactory.Fail("migration.compare", "配置版本防覆盖", "配置迁移", "global", sw.ElapsedMilliseconds,
                    $"对比 {compared} 组，发现 {warnings.Count} 处可疑改动", warnings, warning: true));
        }
        catch (Exception ex)
        {
            results.Add(TestFactory.Fail("migration.compare", "配置版本防覆盖", "配置迁移", "global", sw.ElapsedMilliseconds,
                "索引解析失败：" + ex.Message, null, warning: true));
        }

        StructuralScan(root, results);
        return results;
    }

    static List<TestResult> StructuralScan(string root, List<TestResult> results)
    {
        Stopwatch sw = Stopwatch.StartNew();
        List<string> bad = new();
        int files = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
            {
                string dir = Path.GetFileName(Path.GetDirectoryName(file) ?? "");
                if (!dir.Equals("Configuration", StringComparison.OrdinalIgnoreCase)) continue;
                files++;
                try { using JsonDocument _ = JsonDocument.Parse(File.ReadAllText(file)); }
                catch (Exception ex) { bad.Add(Path.GetFileName(file) + ": " + ex.Message); }
            }
        }
        catch { }
        sw.Stop();

        results.Add(bad.Count == 0
            ? TestFactory.Ok("migration.json", "配置文件可解析", "配置迁移", "global", sw.ElapsedMilliseconds,
                $"{files} 个配置文件")
            : TestFactory.Fail("migration.json", "配置文件可解析", "配置迁移", "global", sw.ElapsedMilliseconds,
                $"{bad.Count} 个文件损坏", bad));
        return results;
    }

    sealed class ConfigVersionInfo
    {
        public string File { get; set; } = "";
        public string At { get; set; } = "";
    }

    static ConfigVersionInfo? ParseVersion(JsonElement e)
    {
        try
        {
            return new ConfigVersionInfo
            {
                File = Get(e, "File") ?? "",
                At = Get(e, "At") ?? ""
            };
        }
        catch { return null; }
    }

    static string? Get(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (JsonProperty p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
        return null;
    }

    static string? FindFile(string historyRoot, string relative, string versionFile)
    {
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(relative)))[..12];
        string path = Path.Combine(historyRoot, key, Path.GetFileName(versionFile));
        return File.Exists(path) ? path : null;
    }

    /// <summary>把 JSON 展开为「路径=值」，用于检测整组字段是否被替换。</summary>
    static Dictionary<string, string> Flatten(string path)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            Walk(doc.RootElement, "", result, 0);
        }
        catch { }
        return result;
    }

    static void Walk(JsonElement e, string prefix, Dictionary<string, string> result, int depth)
    {
        if (depth > 12) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty p in e.EnumerateObject())
                    Walk(p.Value, prefix.Length == 0 ? p.Name : prefix + "." + p.Name, result, depth + 1);
                break;
            case JsonValueKind.Array:
                int i = 0;
                foreach (JsonElement item in e.EnumerateArray())
                    Walk(item, prefix + "[" + i++ + "]", result, depth + 1);
                break;
            default:
                result[prefix] = e.ToString();
                break;
        }
    }
}
