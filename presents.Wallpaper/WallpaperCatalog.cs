using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace presents.Wallpaper;

/// <summary>
/// 扫描本机 Wallpaper Engine 壁纸库：
///   1) Steam 创意工坊 431960（订阅的壁纸）；
///   2) Wallpaper Engine 本地项目 myprojects / defaultprojects；
///   3) 用户配置的额外目录。
/// 只读取 project.json，不启动 Wallpaper Engine，也不修改任何文件。
/// </summary>
internal sealed class WallpaperCatalog
{
    readonly List<WallpaperItem> items = new();

    public int Count => items.Count;
    public IReadOnlyList<WallpaperItem> Items => items;

    /// <summary>最近一次扫描的可读摘要，便于排查“为什么是 0 张”。</summary>
    public string LastSummary { get; private set; } = "";

    public void Refresh(WallpaperConfig config)
    {
        var found = new Dictionary<string, WallpaperItem>(StringComparer.OrdinalIgnoreCase);

        foreach (string library in SteamLibraries())
        {
            Scan(Path.Combine(library, "steamapps", "workshop", "content", "431960"), 1, "workshop", found);

            string projects = Path.Combine(library, "steamapps", "common", "wallpaper_engine", "projects");
            Scan(Path.Combine(projects, "myprojects"), 1, "project", found);
            Scan(Path.Combine(projects, "defaultprojects"), 1, "project", found);
        }

        foreach (string extra in SplitDirectories(config.ExtraDirectories))
            Scan(extra, 2, "custom", found);

        string[] excludes = SplitKeywords(config.ExcludeKeywords);

        items.Clear();
        foreach (WallpaperItem item in found.Values.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            if (config.SkipScenes && !item.IsPlayable) continue;
            if (excludes.Any(keyword => item.Title.Contains(keyword, StringComparison.CurrentCultureIgnoreCase))) continue;
            items.Add(item);
        }

        int workshop = items.Count(x => x.Source == "workshop");
        int project = items.Count(x => x.Source == "project");
        int custom = items.Count(x => x.Source == "custom");
        LastSummary = $"共 {items.Count} 张（创意工坊 {workshop}，本地项目 {project}，自定义 {custom}）";
    }

    public WallpaperItem? Find(string? query)
    {
        if (items.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(query)) return items.FirstOrDefault(x => x.IsPlayable) ?? items[0];

        query = query.Trim();
        WallpaperItem? byId = items.FirstOrDefault(x => x.Id.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (byId != null) return byId;

        WallpaperItem? exact = items.FirstOrDefault(x => x.Title.Equals(query, StringComparison.CurrentCultureIgnoreCase));
        if (exact != null) return exact;

        return items.FirstOrDefault(x => x.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    public WallpaperItem? FindByTag(string? tag)
    {
        if (items.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(tag)) return items.FirstOrDefault(x => x.IsPlayable) ?? items[0];

        tag = tag.Trim();

        WallpaperItem? byTag = items.FirstOrDefault(x =>
            x.Tags.Any(t => t.Contains(tag, StringComparison.CurrentCultureIgnoreCase)));
        if (byTag != null) return byTag;

        return items.FirstOrDefault(x => x.Title.Contains(tag, StringComparison.CurrentCultureIgnoreCase));
    }

    public WallpaperItem? Step(int currentIndex, int delta)
    {
        if (items.Count == 0) return null;
        int n = items.Count;

        if (currentIndex < 0)
            return delta >= 0 ? items[0] : items[n - 1];

        int i = (currentIndex + delta) % n;
        if (i < 0) i += n;
        return items[i];
    }

    public int IndexOf(WallpaperItem item) => items.IndexOf(item);

    // ---------------- 内部实现 ----------------

    static void Scan(string root, int maxDepth, string source, Dictionary<string, WallpaperItem> found)
    {
        if (!Directory.Exists(root)) return;

        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            (string directory, int depth) = queue.Dequeue();
            try
            {
                string projectJson = Path.Combine(directory, "project.json");
                if (File.Exists(projectJson))
                {
                    WallpaperItem? item = Parse(projectJson, source);
                    if (item != null) found.TryAdd(item.Root, item);
                    continue;   // 项目目录不再继续向下钻取
                }

                if (depth >= maxDepth) continue;
                foreach (string sub in Directory.EnumerateDirectories(directory))
                    queue.Enqueue((sub, depth + 1));
            }
            catch
            {
                // 无权限 / 目录消失：跳过
            }
        }
    }

    static WallpaperItem? Parse(string projectJson, string source)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(projectJson));
            JsonElement root = document.RootElement;
            string projectRoot = Path.GetDirectoryName(projectJson)!;

            string title = GetString(root, "title") ?? new DirectoryInfo(projectRoot).Name;
            string type = (GetString(root, "type") ?? "").Trim().ToLowerInvariant();
            if (type == "application") return null;   // 需要启动外部程序，明确不支持

            string file = NormalizeRelative(projectRoot, GetString(root, "file") ?? "");
            string preview = NormalizeRelative(projectRoot, GetString(root, "preview") ?? "");

            // 有项目不写 type（或写空）：按实际资源推断，否则视频壁纸会被当成场景壁纸，只能显示一张糊掉的封面
            if (type.Length == 0)
            {
                if (file.Length > 0 && IsVideoFile(file)) type = "video";
                else if (file.Length > 0 && file.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) type = "web";
                else
                {
                    string? video = FindVideoFile(projectRoot);
                    if (video != null) { type = "video"; file = video; }
                    else type = "scene";
                }
            }

            // 部分项目没写 preview 字段，按常见文件名回退
            if (preview.Length == 0)
            {
                foreach (string candidate in new[] { "preview.jpg", "preview.png", "preview.gif", "preview.webp" })
                {
                    string relative = NormalizeRelative(projectRoot, candidate);
                    if (relative.Length > 0 && File.Exists(Path.Combine(projectRoot, relative)))
                    {
                        preview = relative;
                        break;
                    }
                }
            }

            if (type == "video" && file.Length == 0) return null;
            if (type == "web" && file.Length == 0) file = "index.html";

            string id = "wp_" + Hash(projectRoot);
            return new WallpaperItem(id, title.Trim(), type, projectRoot, file, preview, source)
            {
                Tags = ParseTags(root)
            };
        }
        catch
        {
            return null;
        }
    }

    static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    static IReadOnlyList<string> ParseTags(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("tags", out JsonElement tags) ||
            tags.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var result = new List<string>();
        foreach (JsonElement tag in tags.EnumerateArray())
        {
            if (tag.ValueKind != JsonValueKind.String) continue;
            string? value = tag.GetString();
            if (!string.IsNullOrWhiteSpace(value)) result.Add(value.Trim());
        }
        return result;
    }

    /// <summary>把 project.json 里的相对路径规范化为 / 分隔；越界路径直接判空。</summary>
    static string NormalizeRelative(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return "";
        relative = relative.Replace('\\', '/').TrimStart('/');

        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))); }
        catch { return ""; }

        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? relative : "";
    }

    static string Hash(string text)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).Substring(0, 12).ToLowerInvariant();

    static bool IsVideoFile(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>项目没写 type 时，找项目里第一个视频文件（深度受限，避免扫大目录）。</summary>
    static string? FindVideoFile(string root)
    {
        try
        {
            var queue = new Queue<(string Directory, int Depth)>();
            queue.Enqueue((root, 0));

            while (queue.Count > 0)
            {
                (string directory, int depth) = queue.Dequeue();

                foreach (string candidate in Directory.EnumerateFiles(directory))
                    if (IsVideoFile(candidate)) return Path.GetRelativePath(root, candidate).Replace('\\', '/');

                if (depth >= 2) continue;
                foreach (string sub in Directory.EnumerateDirectories(directory))
                    queue.Enqueue((sub, depth + 1));
            }
        }
        catch
        {
            // 无权限 / 目录消失：跳过
        }

        return null;
    }

    static IEnumerable<string> SplitDirectories(string raw)
        => (raw ?? "")
            .Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim().Trim('"'))
            .Where(Directory.Exists);

    static string[] SplitKeywords(string raw)
        => (raw ?? "")
            .Split(new[] { ',', '，', ';', '；', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>读取 Steam 安装目录与 libraryfolders.vdf 里的所有库。</summary>
    static IEnumerable<string> SteamLibraries()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? steam = null;
        try { steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; } catch { }
        if (string.IsNullOrWhiteSpace(steam))
            try { steam = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string; } catch { }
        if (string.IsNullOrWhiteSpace(steam))
            steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");

        Add(libraries, steam);

        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                Add(libraries, match.Groups[1].Value.Replace("\\\\", "\\"));
        }

        return libraries;
    }

    static void Add(HashSet<string> libraries, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            string full = Path.GetFullPath(path);
            if (Directory.Exists(full)) libraries.Add(full);
        }
        catch { }
    }
}
