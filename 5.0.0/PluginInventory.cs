using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Alife.Framework;
using Alife.Foundation;
using Alife.Function.FunctionCaller;

namespace presents.OpsSuite;

internal sealed class PluginInfo
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Directory { get; set; } = "";
    public int SourceFiles { get; set; }
    public int ModuleCount { get; set; }
    public string[] Modules { get; set; } = Array.Empty<string>();
    public string[] Dependencies { get; set; } = Array.Empty<string>();
    public string[] XmlFunctions { get; set; } = Array.Empty<string>();
    public string[] Issues { get; set; } = Array.Empty<string>();
    public long DiskKB { get; set; }
}

internal sealed class InventoryReport
{
    public string At { get; set; } = "";
    public int PluginCount { get; set; }
    public int ModuleCount { get; set; }
    public int FunctionCount { get; set; }
    public List<PluginInfo> Plugins { get; set; } = new();
    public List<string> DuplicateFunctions { get; set; } = new();

    /// <summary>真冲突：同名工具且两个插件都已加载到当前进程（才可能在同一角色里撞名）。</summary>
    public List<string> ActiveConflicts { get; set; } = new();
    public List<string> DuplicateIds { get; set; } = new();
    public List<string> DependencyCycles { get; set; } = new();
    public List<string> HygieneIssues { get; set; } = new();
    public List<string> MissingDependencies { get; set; } = new();
}

/// <summary>
/// 静态插件台账：清单、模块、XmlFunction 名、重复 DOM id、依赖环、目录卫生。
/// 只读扫描，不加载、不修改任何插件。
/// </summary>
internal sealed class PluginInventory
{
    static readonly Regex DomId = new(
        @"(?:id\s*=\s*[""']([\w-]+)[""']|getElementById\(\s*[""']([\w-]+)[""']|#(alife-[\w-]+|[\w-]+-(?:widget|panel|overlay)))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    readonly PluginSystem pluginSystem;
    readonly ModuleSystem moduleSystem;

    public PluginInventory(PluginSystem pluginSystem, ModuleSystem moduleSystem)
    {
        this.pluginSystem = pluginSystem;
        this.moduleSystem = moduleSystem;
    }

    public InventoryReport Build(IReadOnlyCollection<string>? activeModules = null)
    {
        InventoryReport report = new() { At = DateTime.Now.ToString("HH:mm:ss") };

        IReadOnlyDictionary<string, PluginManifest> manifests;
        try { manifests = pluginSystem.GetAllLocalPlugins(); }
        catch { manifests = new Dictionary<string, PluginManifest>(); }

        string root = "";
        try { root = pluginSystem.PluginContext.PluginRootDirectory; } catch { }
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(AlifePath.StorageFolderPath, "Plugins");

        HashSet<string> knownIds = manifests.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>> functionOwners = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>> domIdOwners = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string[]> dependencyGraph = new(StringComparer.OrdinalIgnoreCase);

        List<Type> moduleTypes;
        try { moduleTypes = moduleSystem.GetAllModules().ToList(); }
        catch { moduleTypes = new List<Type>(); }
        report.ModuleCount = moduleTypes.Count;

        Dictionary<string, List<string>> modulesByAssembly = new(StringComparer.OrdinalIgnoreCase);
        foreach (Type type in moduleTypes)
        {
            string asm = "";
            try { asm = type.Assembly.GetName().Name ?? ""; } catch { }
            if (!modulesByAssembly.TryGetValue(asm, out List<string>? list))
                modulesByAssembly[asm] = list = new List<string>();
            list.Add(type.FullName ?? type.Name);

            foreach (MethodInfo method in GetMethods(type))
            {
                XmlFunctionAttribute? attr;
                try { attr = method.GetCustomAttribute<XmlFunctionAttribute>(); }
                catch { continue; }
                if (attr == null) continue;

                string fn = string.IsNullOrWhiteSpace(attr.Name) ? method.Name : attr.Name!;
                if (!functionOwners.TryGetValue(fn, out List<string>? owners))
                    functionOwners[fn] = owners = new List<string>();
                owners.Add(asm);
                report.FunctionCount++;
            }
        }

        foreach (KeyValuePair<string, PluginManifest> kv in manifests.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            string id = kv.Key;
            string dir = Path.Combine(root, id);
            PluginInfo info = new()
            {
                Id = id,
                Version = kv.Value.Version ?? "?",
                Directory = dir,
                Dependencies = kv.Value.Dependencies?.Keys.OrderBy(x => x).ToArray() ?? Array.Empty<string>()
            };

            dependencyGraph[id] = info.Dependencies;

            List<string> issues = new();
            try
            {
                if (Directory.Exists(dir))
                {
                    FileInfo[] sources = new DirectoryInfo(dir).GetFiles("*.cs", SearchOption.AllDirectories);
                    info.SourceFiles = sources.Length;
                    info.DiskKB = new DirectoryInfo(dir).GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;

                    foreach (string bad in ScanHygiene(dir))
                    {
                        issues.Add(bad);
                        report.HygieneIssues.Add($"{id}: {bad}");
                    }

                    foreach (FileInfo file in sources)
                    {
                        string text;
                        try { text = File.ReadAllText(file.FullName); } catch { continue; }
                        foreach (Match m in DomId.Matches(text))
                        {
                            string domId = m.Groups[1].Success ? m.Groups[1].Value
                                : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                            if (domId.Length < 6) continue;
                            if (!domIdOwners.TryGetValue(domId, out List<string>? owners))
                                domIdOwners[domId] = owners = new List<string>();
                            if (!owners.Contains(id)) owners.Add(id);
                        }
                    }
                }
                else issues.Add("目录不存在");
            }
            catch (Exception ex) { issues.Add("扫描失败: " + ex.GetType().Name); }

            foreach (string dep in info.Dependencies)
                if (!knownIds.Contains(dep))
                    report.MissingDependencies.Add($"{id} → {dep}");

            // 编译产物名 = 插件 id，用程序集名反查模块
            if (modulesByAssembly.TryGetValue(id, out List<string>? mods))
            {
                info.ModuleCount = mods.Count;
                info.Modules = mods.OrderBy(x => x).ToArray();
                info.XmlFunctions = functionOwners
                    .Where(x => x.Value.Contains(id, StringComparer.OrdinalIgnoreCase))
                    .Select(x => x.Key).OrderBy(x => x).ToArray();
            }

            info.Issues = issues.ToArray();
            report.Plugins.Add(info);
        }

        report.PluginCount = report.Plugins.Count;
        // 全量同名（跨插件、即使不同时启用）：只是提示，独立 AssemblyLoadContext 下不会互相覆盖
        report.DuplicateFunctions = functionOwners
            .Where(x => x.Value.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(x => $"{x.Key} → {string.Join(", ", x.Value.Distinct())}")
            .OrderBy(x => x).ToList();

        // 真冲突：优先按「当前角色启用的插件」算，只有这些才会在同一会话里让 AI 调错工具。
        // 拿不到角色配置时退回到「已加载插件」，仍是比全量更窄的口径。
        HashSet<string> active = new(StringComparer.OrdinalIgnoreCase);
        if (activeModules != null)
        {
            foreach (string moduleId in activeModules)
            {
                Type? t = moduleTypes.FirstOrDefault(x =>
                    string.Equals(x.FullName, moduleId, StringComparison.OrdinalIgnoreCase));
                if (t == null) continue;

                string asm = "";
                try { asm = t.Assembly.GetName().Name ?? ""; } catch { }
                if (asm.Length > 0) active.Add(asm);
            }
        }
        if (active.Count == 0)
            active = modulesByAssembly.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        report.ActiveConflicts = functionOwners
            .Where(x => x.Value.Where(active.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(x => $"{x.Key} → {string.Join(", ", x.Value.Where(active.Contains).Distinct())}")
            .OrderBy(x => x).ToList();

        report.DuplicateIds = domIdOwners
            .Where(x => x.Value.Count > 1)
            .Select(x => $"#{x.Key} → {string.Join(", ", x.Value)}")
            .OrderBy(x => x).Take(60).ToList();

        report.DependencyCycles = FindCycles(dependencyGraph);
        return report;
    }

    static IEnumerable<MethodInfo> GetMethods(Type type)
    {
        try
        {
            return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static);
        }
        catch { return Array.Empty<MethodInfo>(); }
    }

    static IEnumerable<string> ScanHygiene(string dir)
    {
        foreach (string sub in new[] { "obj", "bin", ".vs" })
            if (Directory.Exists(Path.Combine(dir, sub)))
                yield return $"存在 {sub}/ 构建产物（Release 下会被一起编译，可能重复定义类型）";

        foreach (string pattern in new[] { "*.csproj", "*.sln", "*.dll" })
            if (Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).Any())
                yield return $"存在 {pattern} 文件（插件目录应为纯源码）";

        foreach (string sub in Directory.EnumerateDirectories(dir))
        {
            string name = Path.GetFileName(sub);
            if (name.Contains(".bak", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("-old", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("_", StringComparison.Ordinal))
                yield return $"子目录 {name} 会被当成独立插件加载（重复类型风险）";
        }
    }

    static List<string> FindCycles(Dictionary<string, string[]> graph)
    {
        List<string> cycles = new();
        HashSet<string> done = new();
        HashSet<string> stack = new();
        List<string> path = new();

        void Visit(string node)
        {
            if (stack.Contains(node))
            {
                int at = path.IndexOf(node);
                if (at >= 0) cycles.Add(string.Join(" → ", path.Skip(at).Append(node)));
                return;
            }
            if (!done.Add(node)) return;

            stack.Add(node);
            path.Add(node);
            if (graph.TryGetValue(node, out string[]? deps))
                foreach (string dep in deps)
                    if (graph.ContainsKey(dep)) Visit(dep);
            path.RemoveAt(path.Count - 1);
            stack.Remove(node);
        }

        foreach (string key in graph.Keys) Visit(key);
        return cycles.Distinct().Take(20).ToList();
    }
}
