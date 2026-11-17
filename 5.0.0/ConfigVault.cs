using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alife.Foundation;

namespace presents.OpsSuite;

internal sealed class ConfigVersion
{
    public string File { get; set; } = "";
    public string At { get; set; } = "";
    public string Hash { get; set; } = "";
    public long Bytes { get; set; }
    public string Reason { get; set; } = "";
}

internal sealed class ConfigEntry
{
    public string Path { get; set; } = "";
    public string CurrentHash { get; set; } = "";
    public long Bytes { get; set; }
    public List<ConfigVersion> History { get; set; } = new();
}

/// <summary>
/// 配置保险库：扫描 Storage 下所有 Configuration/*.json，变化时自动留档，
/// 支持逐行 diff 与一键回滚（回滚前会先把当前版本再留一份，可撤销）。
/// 全部写入都在 Storage 目录内，且用临时文件 + 原子替换，避免写一半把配置弄坏。
/// </summary>
internal sealed class ConfigVault
{
    readonly string root;
    readonly string historyRoot;
    readonly string indexPath;
    readonly object gate = new();
    Dictionary<string, ConfigEntry> index = new(StringComparer.OrdinalIgnoreCase);

    // 相对路径 → 上次读到的修改时间；没变就不读内容
    readonly Dictionary<string, long> mtimeCache = new(StringComparer.OrdinalIgnoreCase);

    public ConfigVault()
    {
        root = Path.GetFullPath(AlifePath.StorageFolderPath);
        historyRoot = Path.Combine(root, "OpsSuite", "ConfigHistory");
        indexPath = Path.Combine(root, "OpsSuite", "config-index.json");
        Directory.CreateDirectory(historyRoot);
        LoadIndex();
    }

    public string Root => root;
    public int TrackedCount => index.Count;
    public int VersionCount => index.Values.Sum(x => x.History.Count);

    public IReadOnlyList<ConfigEntry> Entries()
    {
        lock (gate) return index.Values.OrderBy(x => x.Path).ToList();
    }

    /// <summary>扫描全部配置文件，内容变化则留档。返回本次新增的版本数。</summary>
    public int SnapshotAll(string reason = "poll", int maxPerFile = 20)
    {
        int added = 0;
        lock (gate)
        {
            foreach (string file in EnumerateConfigFiles())
            {
                string relative = Relative(file);

                // 先用修改时间做廉价过滤：内容一样就不必读盘 + 算哈希。
                // 68 个配置每 5 秒全量读取会拖慢采样循环。
                long ticks;
                try { ticks = new FileInfo(file).LastWriteTimeUtc.Ticks; }
                catch { continue; }

                if (mtimeCache.TryGetValue(relative, out long cached) && cached == ticks &&
                    index.TryGetValue(relative, out ConfigEntry? known) && known.History.Count > 0)
                    continue;

                string content;
                try { content = File.ReadAllText(file); }
                catch { continue; }

                mtimeCache[relative] = ticks;

                string hash = Hash(content);
                if (!index.TryGetValue(relative, out ConfigEntry? entry))
                {
                    entry = new ConfigEntry { Path = relative };
                    index[relative] = entry;
                }

                entry.Bytes = new FileInfo(file).Length;
                if (entry.CurrentHash == hash && entry.History.Count > 0) continue;

                ConfigVersion version = SaveVersion(relative, content, hash, reason);
                entry.History.Insert(0, version);
                entry.CurrentHash = hash;

                while (entry.History.Count > maxPerFile)
                {
                    ConfigVersion old = entry.History[^1];
                    TryDelete(VersionAbsolute(relative, old.File));
                    entry.History.RemoveAt(entry.History.Count - 1);
                }
                added++;
            }

            SaveIndex();
        }
        return added;
    }

    public string Diff(string relative, string versionFile)
    {
        ConfigEntry? entry;
        string current;
        lock (gate)
        {
            if (!index.TryGetValue(relative, out entry)) return "找不到该配置文件：" + relative;
            current = SafeRead(Resolve(relative));
        }

        ConfigVersion? version = entry.History.FirstOrDefault(x => x.File == versionFile);
        if (version == null) return "找不到该历史版本：" + versionFile;

        string old = SafeRead(VersionAbsolute(relative, version.File));
        string[] a = old.Replace("\r\n", "\n").Split('\n');
        string[] b = current.Replace("\r\n", "\n").Split('\n');

        // 逐行前缀/后缀裁剪后做简单 LCS 对齐，足够看清"哪些字段被改了"
        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix &&
               a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;

        StringBuilder sb = new();
        sb.AppendLine($"配置差异：{relative}");
        sb.AppendLine($"历史版本：{version.At}（{version.Reason}）");
        sb.AppendLine($"当前哈希：{entry.CurrentHash[..Math.Min(12, entry.CurrentHash.Length)]}  历史哈希：{version.Hash[..Math.Min(12, version.Hash.Length)]}");
        sb.AppendLine();

        int removed = a.Length - prefix - suffix;
        int added = b.Length - prefix - suffix;
        if (removed == 0 && added == 0) return sb.AppendLine("（无差异）").ToString();

        for (int i = prefix; i < a.Length - suffix; i++) sb.AppendLine("- " + a[i]);
        for (int i = prefix; i < b.Length - suffix; i++) sb.AppendLine("+ " + b[i]);

        sb.AppendLine();
        sb.AppendLine($"共 {removed} 行被删除 / 覆盖，{added} 行新增。");
        return sb.ToString();
    }

    /// <summary>回滚。先把当前版本另存（reason=pre-rollback），再原子写回历史版本。</summary>
    public string Rollback(string relative, string versionFile)
    {
        ConfigEntry entry;
        ConfigVersion version;
        string target;
        string historyFile;

        lock (gate)
        {
            if (!index.TryGetValue(relative, out ConfigEntry? found) || found == null)
                return "找不到该配置文件：" + relative;
            entry = found;
            version = entry.History.FirstOrDefault(x => x.File == versionFile)
                      ?? throw new InvalidOperationException("找不到该历史版本：" + versionFile);

            target = Resolve(relative);
            historyFile = VersionAbsolute(relative, version.File);
            if (!File.Exists(historyFile)) return "历史版本文件已丢失：" + version.File;

            string current = SafeRead(target);
            if (current.Length > 0 && Hash(current) != version.Hash)
            {
                ConfigVersion backup = SaveVersion(relative, current, Hash(current), "pre-rollback");
                entry.History.Insert(0, backup);
            }

            string restored = SafeRead(historyFile);
            AtomicWrite(target, restored);
            entry.CurrentHash = Hash(restored);
            entry.Bytes = new FileInfo(target).Length;
            SaveIndex();
        }

        return $"已回滚 {relative} 到 {version.At}，原当前版本已另存为 pre-rollback（可再次回滚）。";
    }

    public IReadOnlyList<ConfigVersion> History(string relative)
    {
        lock (gate)
            return index.TryGetValue(relative, out ConfigEntry? entry)
                ? entry.History.ToList() : Array.Empty<ConfigVersion>();
    }

    IEnumerable<string> EnumerateConfigFiles()
    {
        // 关键：Directory.EnumerateFiles 是惰性枚举，异常发生在 foreach 迭代时，
        // 包在赋值外面的 try/catch 根本抓不到。而 Storage 下还有 74 个插件目录
        // （obj/bin 里常有超长路径或不可访问项），一旦枚举到那里就整批中断 ——
        // 排在后面的配置文件永远轮不到留档。这就是壁纸配置只有 1 个版本的原因。
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,           // 无权限目录直接跳过，不中断
            AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = 3                // Storage/<角色>/Configuration/x.json 正好 3 层
        };

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*.json", options); }
        catch { yield break; }

        List<string> batch;
        try { batch = files.ToList(); }          // 先物化，把迭代异常挡在这里
        catch { yield break; }

        foreach (string file in batch)
        {
            // 只看 Configuration 目录下的 .json，排除本插件自己的历史目录与插件源码树
            string dir = Path.GetFileName(Path.GetDirectoryName(file) ?? "");
            if (!dir.Equals("Configuration", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.StartsWith(historyRoot, StringComparison.OrdinalIgnoreCase)) continue;

            string rel = file.Replace('\\', '/');
            if (rel.Contains("/Plugins/", StringComparison.OrdinalIgnoreCase)) continue;

            yield return file;
        }
    }

    string Resolve(string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("路径越界");
        return full;
    }

    string Relative(string full) => Path.GetRelativePath(root, full).Replace('\\', '/');

    string VersionAbsolute(string relative, string versionFile)
        => Path.Combine(HistoryDir(relative), Path.GetFileName(versionFile));

    string HistoryDir(string relative)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative)))[..12];
        string dir = Path.Combine(historyRoot, key);
        Directory.CreateDirectory(dir);
        return dir;
    }

    ConfigVersion SaveVersion(string relative, string content, string hash, string reason)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        string name = stamp + ".json";
        string path = Path.Combine(HistoryDir(relative), name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return new ConfigVersion
        {
            File = name,
            At = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Hash = hash,
            Bytes = Encoding.UTF8.GetByteCount(content),
            Reason = reason
        };
    }

    void LoadIndex()
    {
        try
        {
            if (!File.Exists(indexPath)) return;
            string json = File.ReadAllText(indexPath);
            Dictionary<string, ConfigEntry>? loaded =
                JsonSerializer.Deserialize<Dictionary<string, ConfigEntry>>(json);
            if (loaded != null) index = new(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch { index = new(StringComparer.OrdinalIgnoreCase); }
    }

    void SaveIndex()
    {
        try { AtomicWrite(indexPath, JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* 索引损坏下次重建，不影响已留档的历史文件 */ }
    }

    /// <summary>
    /// 原子写。两处修正：
    /// 1. 保留目标文件原有的 BOM —— 回滚只该改内容，不该改文件的编码特征
    ///    （有些插件按 BOM 判断编码，丢了会乱码）。
    /// 2. 用 File.Move(overwrite) 而不是 File.Copy —— 同卷上是原子替换，
    ///    断电不会留下写了一半的文件。
    /// </summary>
    static void AtomicWrite(string path, string content)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        bool bom = false;
        try
        {
            if (File.Exists(path))
            {
                using FileStream probe = File.OpenRead(path);
                bom = probe.ReadByte() == 0xEF && probe.ReadByte() == 0xBB && probe.ReadByte() == 0xBF;
            }
        }
        catch { }

        string temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(bom));

        try { File.Move(temp, path, true); }
        catch
        {
            // 极少数情况（跨卷 / 被占用）退回覆盖写
            try { File.Copy(temp, path, true); } catch { }
            TryDelete(temp);
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch { return ""; }
    }

    static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
