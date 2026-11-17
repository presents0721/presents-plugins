using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace presents.PluginTestKit;

/// <summary>只读静态检查：不需要加载插件，也不会修改任何文件。</summary>
internal static class StaticChecks
{
    static readonly Regex NamespaceDecl = new(@"^\s*namespace\s+([\w.]+)\s*;", RegexOptions.Compiled | RegexOptions.Multiline);
    static readonly Regex TypeDecl = new(
        @"^\s*(?:public|internal|private|protected)?\s*(?:static\s+|sealed\s+|abstract\s+|partial\s+)*(class|struct|interface|record|enum)\s+(\w+)",
        RegexOptions.Compiled | RegexOptions.Multiline);
    static readonly Regex AddListener = new(@"\.addEventListener\s*\(\s*['""](\w+)['""]", RegexOptions.Compiled);
    static readonly Regex RawScript = new(
        @"(?:const|static)\s+string\s+(\w*Script\w*)\s*=\s*""""""\s*\r?\n([\s\S]*?)\r?\n\s*"""""";",
        RegexOptions.Compiled);

    public static List<TestResult> Run(string root)
    {
        List<TestResult> results = new();
        if (!Directory.Exists(root))
            return new List<TestResult> {
                TestFactory.Fail("static.root", "插件根目录", "结构", "global", 0, "插件根目录不存在：" + root)
            };

        // root 可能有两种含义：
        //   1) 插件根目录（下面是几十个插件目录）—— 单插件快测以外都走这里
        //   2) 某个插件自己的目录 —— 单插件快测传的就是它
        // 第 2 种情况下若仍去枚举子目录，会把 Resources/、Audio/ 这些资源目录
        // 当成插件，然后逐个报「缺少 manifest.json」。
        string[] dirs = File.Exists(Path.Combine(root, "manifest.json"))
            ? new[] { root }
            : Directory.EnumerateDirectories(root).OrderBy(x => x).ToArray();
        Dictionary<string, List<string>> typeOwners = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string[]> dependencyGraph = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> ids = dirs.Select(Path.GetFileName).Where(x => x != null)
            .Select(x => x!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string dir in dirs)
        {
            string id = Path.GetFileName(dir);
            Stopwatch sw = Stopwatch.StartNew();

            // 1) manifest 必需字段
            string manifestPath = Path.Combine(dir, "manifest.json");
            List<string> problems = new();
            Dictionary<string, string[]>? deps = null;

            if (!File.Exists(manifestPath)) problems.Add("缺少 manifest.json");
            else
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                    JsonElement m = doc.RootElement;
                    // 框架只用目录名当插件 Id、用 [Module] 特性当名称；
                    // manifest 的必需字段只有 Version。Id / Name 可选，写了才校验。
                    if (!TryGetField(m, "Version", out JsonElement ver) ||
                        (ver.ValueKind != JsonValueKind.String && ver.ValueKind != JsonValueKind.Number))
                        problems.Add("manifest 缺少 Version");

                    if (TryGetField(m, "Id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String &&
                        !string.Equals(idEl.GetString(), id, StringComparison.OrdinalIgnoreCase))
                        problems.Add($"manifest.Id ({idEl.GetString()}) 与目录名 {id} 不一致");

                    if (m.TryGetProperty("Dependencies", out JsonElement depsEl) &&
                        depsEl.ValueKind == JsonValueKind.Object)
                    {
                        deps = depsEl.EnumerateObject().ToDictionary(x => x.Name, _ => Array.Empty<string>(),
                            StringComparer.OrdinalIgnoreCase);
                        dependencyGraph[id] = deps.Keys.ToArray();
                    }
                }
                catch (Exception ex) { problems.Add("manifest.json 解析失败: " + ex.Message); }
            }
            dependencyGraph.TryAdd(id, Array.Empty<string>());

            // 2) 目录卫生
            foreach (string sub in new[] { "obj", "bin", ".vs" })
                if (Directory.Exists(Path.Combine(dir, sub)))
                    problems.Add($"{sub}/ 构建产物会被一起编译（重复类型 / Release 加载风险）");

            foreach (string pattern in new[] { "*.csproj", "*.sln" })
                if (Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).Any())
                    problems.Add($"目录内存在 {pattern}（插件目录应只放源码）");

            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (name.Contains(".bak", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("-old", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"子目录 {name} 会被当成独立插件加载");
            }

            // 3) 源码级检查：类型全名、事件生命周期、注入脚本清理契约
            string[] sources = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).ToArray();
            int listenerWarn = 0;
            bool hasTeardown = false;
            bool hasVersionGuard = false;
            int rawScripts = 0;

            foreach (string file in sources)
            {
                string text;
                try { text = File.ReadAllText(file); } catch { continue; }

                string ns = "";
                Match nsm = NamespaceDecl.Match(text);
                if (nsm.Success) ns = nsm.Groups[1].Value + ".";

                foreach (Match tm in TypeDecl.Matches(text))
                {
                    string full = ns + tm.Groups[2].Value;
                    if (!typeOwners.TryGetValue(full, out List<string>? owners))
                        typeOwners[full] = owners = new List<string>();
                    if (!owners.Contains(id)) owners.Add(id);
                }

                foreach (Match lm in AddListener.Matches(text))
                {
                    int at = lm.Index;
                    string tail = text.Substring(at, Math.Min(220, text.Length - at));
                    if (!tail.Contains("SIG", StringComparison.Ordinal) &&
                        !tail.Contains("signal", StringComparison.Ordinal) &&
                        !tail.Contains("wps(", StringComparison.Ordinal))
                        listenerWarn++;
                }

                if (text.Contains("TeardownScript", StringComparison.Ordinal) ||
                    text.Contains("__alifeWallpaperTeardown", StringComparison.Ordinal)) hasTeardown = true;
                if (text.Contains("__alifeWpVer", StringComparison.Ordinal)) hasVersionGuard = true;
                rawScripts += RawScript.Matches(text).Count;
            }

            sw.Stop();
            results.Add(problems.Count == 0
                ? TestFactory.Ok("static.manifest." + id, "清单与目录结构", "结构", id, sw.ElapsedMilliseconds,
                    $"{sources.Length} 个源文件，{rawScripts} 段注入脚本")
                : TestFactory.Fail("static.manifest." + id, "清单与目录结构", "结构", id, sw.ElapsedMilliseconds,
                    "发现 " + problems.Count + " 个结构问题", problems, warning: true));

            if (listenerWarn > 0)
                results.Add(TestFactory.Fail("static.listeners." + id, "事件生命周期", "热重载", id, 0,
                    $"{listenerWarn} 处 addEventListener 未见 AbortController signal（热重载后可能残留闭包）",
                    null, warning: true));

            if (rawScripts > 0 && !hasTeardown && !hasVersionGuard)
                results.Add(TestFactory.Fail("static.teardown." + id, "注入脚本清理契约", "热重载", id, 0,
                    "存在注入脚本但未见版本守卫或 teardown 入口，重载后旧运行时可能残留", null, warning: true));
        }

        // 4) 全局：重复类型全名
        List<string> duplicates = typeOwners.Where(x => x.Value.Count > 1)
            .Select(x => $"{x.Key} → {string.Join(", ", x.Value)}").OrderBy(x => x).ToList();
        results.Add(duplicates.Count == 0
            ? TestFactory.Ok("static.types", "类型全名唯一", "结构", "global", 0,
                $"{typeOwners.Count} 个类型，无重名")
            // 每个插件有独立 AssemblyLoadContext，同名辅助类（TypeInference 等）不会真的冲突，
            // 因此只作为提示，不算失败。
            : TestFactory.Fail("static.types", "类型全名重复（提示）", "结构", "global", 0,
                $"{duplicates.Count} 组重名类型；独立加载上下文下通常无害", duplicates, warning: true));

        // 5) 全局：依赖环 / 缺失依赖
        List<string> cycles = FindCycles(dependencyGraph);
        results.Add(cycles.Count == 0
            ? TestFactory.Ok("static.dependencies", "依赖图无环", "结构", "global", 0,
                $"{dependencyGraph.Count} 个插件")
            : TestFactory.Fail("static.dependencies", "依赖图无环", "结构", "global", 0,
                "存在依赖环", cycles));

        List<string> missing = dependencyGraph
            .SelectMany(x => x.Value.Where(d => !ids.Contains(d)).Select(d => $"{x.Key} → {d}"))
            .OrderBy(x => x).ToList();
        results.Add(missing.Count == 0
            ? TestFactory.Ok("static.missing", "依赖可解析", "结构", "global", 0)
            : TestFactory.Fail("static.missing", "依赖可解析", "结构", "global", 0,
                $"{missing.Count} 个依赖未找到", missing, warning: true));

        return results;
    }

    static bool TryGetField(JsonElement m, string name, out JsonElement value)
    {
        if (m.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty p in m.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                { value = p.Value; return true; }
        value = default;
        return false;
    }

    static List<string> FindCycles(Dictionary<string, string[]> graph)
    {
        List<string> cycles = new();
        HashSet<string> done = new(), stack = new();
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
            stack.Add(node); path.Add(node);
            if (graph.TryGetValue(node, out string[]? deps))
                foreach (string d in deps) if (graph.ContainsKey(d)) Visit(d);
            path.RemoveAt(path.Count - 1); stack.Remove(node);
        }

        foreach (string key in graph.Keys) Visit(key);
        return cycles.Distinct().Take(20).ToList();
    }
}
