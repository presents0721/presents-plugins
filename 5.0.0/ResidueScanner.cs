using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

namespace presents.OpsSuite;

internal sealed class PortResidue
{
    public string Name { get; set; } = "";
    public int Port { get; set; }
    /// <summary>false 表示端口文件还在、但没人监听 —— 典型的重载/退出残留。</summary>
    public bool Alive { get; set; }
    public string WrittenAt { get; set; } = "";
}

internal sealed class DataResidue
{
    public string Path { get; set; } = "";
    public double SizeMB { get; set; }
    public string LastWrite { get; set; } = "";
    public string Note { get; set; } = "";
}

internal sealed class ConfigResidue
{
    public string Character { get; set; } = "";
    public string Module { get; set; } = "";
    public string Reason { get; set; } = "";
}

internal sealed class ResidueReport
{
    public string At { get; set; } = "";
    public List<PortResidue> Ports { get; set; } = new();
    public List<DataResidue> Data { get; set; } = new();
    public List<ConfigResidue> Configs { get; set; } = new();
    public int DeadPorts => Ports.Count(x => !x.Alive);
    public int ProblemCount => DeadPorts + Data.Count + Configs.Count;
}

/// <summary>
/// 扫描「插件跑了但没收拾干净」留下的三类残渣：
/// 1. 指向死实例的端口文件（重载/退出时没删干净，会让排查者对着旧端口查半天）
/// 2. 没有对应插件的 Storage 数据目录（卸载插件后残留）
/// 3. 角色配置里引用了不存在的模块（插件改名/卸载后，角色启动会静默跳过）
/// 只读，不删任何东西；清理由用户确认后执行。
/// </summary>
internal static class ResidueScanner
{
    static readonly HashSet<string> KnownDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Character", "Plugins", "Runtime", "Temp", "Configuration",
        "CharacterArchives", "Backups", "PluginMarket", "logs", "obj", "bin",
        // 框架 / 其它插件自建的公共目录，名字本来就不对应某个插件
        "PluginBackups", "PluginData", "PluginPublish", "PluginTestKit", "OpsSuite", "EventFlow"
    };

    public static ResidueReport Scan(
        string storageRoot,
        IReadOnlyCollection<string> pluginIds,
        IReadOnlyCollection<string> moduleIds)
    {
        ResidueReport report = new() { At = DateTime.Now.ToString("HH:mm:ss") };

        ScanPorts(report);
        ScanDataDirs(report, storageRoot, pluginIds);
        ScanConfigRefs(report, storageRoot, moduleIds);

        return report;
    }

    static void ScanPorts(ResidueReport report)
    {
        string temp = Path.GetTempPath();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(temp, "alife-*.port"); }
        catch { return; }

        foreach (string file in files)
        {
            try
            {
                string text = File.ReadAllText(file).Trim();
                if (!int.TryParse(text, out int port) || port <= 0) continue;

                report.Ports.Add(new PortResidue
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Port = port,
                    Alive = IsListening(port),
                    WrittenAt = File.GetLastWriteTime(file).ToString("MM-dd HH:mm")
                });
            }
            catch { }
        }

        report.Ports = report.Ports.OrderBy(x => x.Alive).ThenBy(x => x.Name).ToList();
    }

    static void ScanDataDirs(ResidueReport report, string storageRoot, IReadOnlyCollection<string> pluginIds)
    {
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(storageRoot); }
        catch { return; }

        foreach (string dir in dirs)
        {
            string name = Path.GetFileName(dir);
            if (KnownDirs.Contains(name)) continue;

            if (MatchesPlugin(name, pluginIds)) continue;

            report.Data.Add(new DataResidue
            {
                Path = name,
                SizeMB = Math.Round(FolderSizeMB(dir, 4000), 1),
                LastWrite = SafeLastWrite(dir),
                Note = "没有同名插件；可能是已卸载插件的数据，或插件的自定义缓存目录"
            });
        }

        report.Data = report.Data.OrderByDescending(x => x.SizeMB).ToList();
    }

    static void ScanConfigRefs(ResidueReport report, string storageRoot, IReadOnlyCollection<string> moduleIds)
    {
        string charRoot = Path.Combine(storageRoot, "Character");
        if (!Directory.Exists(charRoot)) return;

        HashSet<string> known = new(moduleIds, StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(charRoot); }
        catch { return; }

        foreach (string dir in dirs)
        {
            string index = Path.Combine(dir, "index.json");
            if (!File.Exists(index)) continue;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(index));
                if (!doc.RootElement.TryGetProperty("Modules", out JsonElement mods) ||
                    mods.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (JsonElement m in mods.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.String) continue;
                    string id = m.GetString() ?? "";
                    if (id.Length == 0 || known.Contains(id)) continue;

                    report.Configs.Add(new ConfigResidue
                    {
                        Character = Path.GetFileName(dir),
                        Module = id,
                        Reason = "模块不存在：插件被卸载或改名后，角色启动会静默跳过它"
                    });
                }
            }
            catch { }
        }
    }

    static bool MatchesPlugin(string dirName, IReadOnlyCollection<string> pluginIds)
    {
        foreach (string id in pluginIds)
        {
            if (string.Equals(id, dirName, StringComparison.OrdinalIgnoreCase)) return true;

            // 数据目录常去掉命名空间前缀（Storage/EventFlow ⇄ presents.EventFlow）
            int dot = id.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < id.Length &&
                string.Equals(id[(dot + 1)..], dirName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static bool IsListening(int port)
    {
        try
        {
            using TcpClient client = new();
            Task task = client.ConnectAsync("127.0.0.1", port);
            return task.Wait(250) && client.Connected;
        }
        catch { return false; }
    }

    static double FolderSizeMB(string dir, int maxFiles)
    {
        try
        {
            long total = 0;
            int count = 0;
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (++count > maxFiles) break;
                try { total += new FileInfo(f).Length; } catch { }
            }
            return total / 1048576.0;
        }
        catch { return 0; }
    }

    static string SafeLastWrite(string dir)
    {
        try { return Directory.GetLastWriteTime(dir).ToString("yyyy-MM-dd HH:mm"); }
        catch { return ""; }
    }
}
