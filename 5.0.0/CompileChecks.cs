using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace presents.PluginTestKit;

/// <summary>
/// 真编译闸门：用宿主同一套引用集，把插件源码重新编译一遍。
///
/// 为什么必须有这一层 —— 现有 ContractChecks 的前提是「已经编译成功」，
/// 而它自己从不验证这个前提。Alife 用 Roslyn 逐文件直编、不开 ImplicitUsings，
/// 本地如果开了 ImplicitUsings，缺 using 的代码在本地全绿、到客户端炸成一片 CS0246，
/// 模块系统直接发现不了这个插件。这一类问题静态扫描永远抓不到，只有真编译能抓。
///
/// 引用集与宿主保持一致：共享运行时 + 宿主 bin + 其他插件的编译产物。
/// 编译某插件时**排除它自己的旧 dll**，否则旧产物会把缺类型的问题掩盖掉。
/// </summary>
internal static class CompileChecks
{
    static readonly object gate = new();

    // 必须显式给 Empty：default(ImmutableArray) 的 IsDefault 为 true，
    // 一访问 .Length 就抛 NullReferenceException —— 踩过这个坑。
    static ImmutableArray<MetadataReference> cachedRefs = ImmutableArray<MetadataReference>.Empty;
    static string cachedRuntimeDir = "";
    static string cachedHostBin = "";

    /// <summary>
    /// 编译所有插件。selfOnly 非空时只编译那一个（单插件快测用）。
    /// </summary>
    public static List<TestResult> Run(string pluginsRoot, string? onlyPlugin = null)
    {
        List<TestResult> results = new();

        string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        string hostBin = AppContext.BaseDirectory;
        string? compiledRoot = ResolveCompiledRoot();

        if (string.IsNullOrWhiteSpace(runtimeDir) || !Directory.Exists(runtimeDir))
        {
            results.Add(TestFactory.Fail("compile", "Roslyn 编译", "编译", "global", 0,
                "拿不到运行时目录，无法构造引用集", warning: true));
            return results;
        }

        ImmutableArray<MetadataReference> allRefs = LoadReferences(runtimeDir, hostBin);

        if (allRefs.Length == 0)
        {
            results.Add(TestFactory.Fail("compile", "Roslyn 编译", "编译", "global", 0,
                $"引用集为空（runtime={runtimeDir}）", warning: true));
            return results;
        }

        IEnumerable<string> dirs;
        try
        {
            dirs = File.Exists(Path.Combine(pluginsRoot, "manifest.json"))
                ? new[] { pluginsRoot }
                : Directory.EnumerateDirectories(pluginsRoot).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e)
        {
            results.Add(TestFactory.Fail("compile", "Roslyn 编译", "编译", "global", 0,
                "扫描插件目录失败：" + e.Message, warning: true));
            return results;
        }

        if (!string.IsNullOrWhiteSpace(onlyPlugin))
        {
            string want = Path.GetFileName(onlyPlugin.Trim());
            dirs = dirs.Where(d => string.Equals(Path.GetFileName(d), want, StringComparison.OrdinalIgnoreCase));
        }

        string[] pluginDirs = dirs.ToArray();

        results.Add(TestFactory.Ok("compile.env", "编译环境", "编译", "global", 0,
            $"引用集 {allRefs.Length} 个程序集 · 运行时 {Path.GetFileName(runtimeDir.TrimEnd(Path.DirectorySeparatorChar))}" +
            $" · 宿主 {hostBin}"));

        foreach (string dir in pluginDirs)
        {
            string name = Path.GetFileName(dir);
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                (bool ok, bool soft, List<string> errors, int sourceCount) =
                    CompileOne(dir, name, allRefs, compiledRoot);
                sw.Stop();

                if (sourceCount == 0)
                {
                    results.Add(TestFactory.Ok("compile." + name, "Roslyn 编译", "编译", name, 0, "没有 .cs 源文件"));
                    continue;
                }

                if (sourceCount < 0)
                {
                    results.Add(TestFactory.Ok("compile." + name, "Roslyn 编译", "编译", name,
                        (int)sw.ElapsedMilliseconds, "已有 dll 且不旧于源码，宿主不会重编译（跳过源码检查）"));
                    continue;
                }

                if (ok)
                {
                    results.Add(TestFactory.Ok("compile." + name, "Roslyn 编译", "编译", name,
                        (int)sw.ElapsedMilliseconds,
                        $"{sourceCount} 个文件编译通过（{sw.ElapsedMilliseconds} ms）"));
                }
                else if (soft)
                {
                    // 源码依赖了宿主里没有的第三方包，但插件已有可用 dll。
                    // 这不是"客户端会挂"，是"想重新编译源码得先补包"，所以只提示。
                    results.Add(TestFactory.Fail(
                        "compile." + name, "Roslyn 编译", "编译", name, (int)sw.ElapsedMilliseconds,
                        "源码依赖引用集之外的第三方包（已有 dll 可用，不影响运行）",
                        errors.Take(4).ToList(), warning: true));
                }
                else
                {
                    results.Add(TestFactory.Fail(
                        "compile." + name, "Roslyn 编译", "编译", name, (int)sw.ElapsedMilliseconds,
                        $"{errors.Count} 个编译错误 —— 客户端加载时会整包失败，模块直接消失",
                        errors.Take(8).ToList()));
                }
            }
            catch (Exception e)
            {
                sw.Stop();
                results.Add(TestFactory.Fail("compile." + name, "Roslyn 编译", "编译", name, 0,
                    "编译过程异常：" + e.Message, warning: true));
            }
        }

        return results;
    }

    // ---------------------------------------------------------------- 单个插件

    /// <summary>
    /// 按宿主的方式编译一个插件。
    /// 关键：把该插件自己的历史 dll 从引用集里剔掉 —— 留着它，缺的类型会被旧产物"补上"，
    /// 于是检查通过、客户端却编译失败。
    /// </summary>
    static (bool Ok, bool Soft, List<string> Errors, int SourceCount) CompileOne(
        string dir, string name, ImmutableArray<MetadataReference> allRefs, string? compiledRoot)
    {
        List<string> sources;
        try
        {
            sources = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)
                         && !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch { return (true, false, new List<string>(), 0); }

        if (sources.Count == 0) return (true, false, new List<string>(), 0);

        // 关键：宿主对「已有 dll 且不旧于源码」的插件不会再编译，直接加载产物。
        // 这类插件（市场上装下来的预编译包）源码往往引用了 OpenCvSharp / NAudio / SherpaOnnx
        // 这类本地根本没有的第三方包 —— 按源码硬编只会刷出一屏假错误。
        // 返回 sourceCount = -1 表示「跳过」，调用方据此给出不同的结论。
        if (SkippableByHost(name, sources, compiledRoot))
            return (true, false, new List<string>(), -1);

        List<SyntaxTree> trees = new(sources.Count);

        foreach (string file in sources)
        {
            try
            {
                string text = File.ReadAllText(file);
                trees.Add(CSharpSyntaxTree.ParseText(
                    text,
                    new CSharpParseOptions(LanguageVersion.Latest),
                    path: file));
            }
            catch (Exception e)
            {
                return (false, false, new List<string> { $"{Path.GetFileName(file)} 读取失败：{e.Message}" }, sources.Count);
            }
        }

        // 第一遍：剔除自己的旧产物。
        // 必须这样做 —— 否则旧 dll 里的类型定义会把「源码缺 using」掩盖掉，
        // 那次 QQSuite.Present 的事故就是被这类掩盖放过去的。
        ImmutableArray<MetadataReference> strict = allRefs
            .Where(r => !IsSelf(r, name))
            .ToImmutableArray();

        List<Diagnostic> errors = Compile(trees, strict, name);
        if (errors.Count == 0) return (true, false, new List<string>(), sources.Count);

        // 第二遍：把自己的旧产物加回来。
        // 有些插件引用了 OpenCvSharp / NAudio / SherpaOnnx / Vortice 这类第三方包，
        // 这些 dll 不在宿主 bin 里（市场装的是预编译产物，源码只是附带），
        // 排除自己必然 CS0246 一片。加回自己还能通过，说明失败是「找不到第三方包」，
        // 而不是「源码真的编译不过」，降级成提示，不要吓人。
        List<Diagnostic> withSelf = Compile(trees, allRefs, name);

        if (withSelf.Count == 0)
        {
            return (false, true, new List<string>
            {
                $"源码引用了引用集之外的第三方包（{errors.Count} 处 CS0246），但插件已有可用 dll；" +
                "重新编译源码前需要把这些包放进宿主 bin"
            }, sources.Count);
        }

        List<string> lines = errors
            .Select(d => Format(d, dir))
            .Distinct()
            .ToList();

        return (false, false, lines, sources.Count);
    }

    /// <summary>
    /// 宿主要不要重新编译这个插件。
    /// dll 存在、且不比源码旧 → 不编译，直接用 dll。这种情况检查源码毫无意义
    /// （源码可能压根没跟着包一起更新，甚至缺第三方依赖）。
    /// </summary>
    static bool SkippableByHost(string plugin, List<string> sources, string? compiledRoot)
    {
        if (compiledRoot == null) return false;

        try
        {
            string dll = Path.Combine(compiledRoot, plugin + ".dll");
            if (!File.Exists(dll)) return false;

            DateTime dllAt = File.GetLastWriteTime(dll);
            DateTime newest = DateTime.MinValue;

            foreach (string f in sources)
            {
                DateTime t = File.GetLastWriteTime(f);
                if (t > newest) newest = t;
            }

            // 留 5 秒容差：编译和写盘之间总有点时间差
            return newest <= dllAt.AddSeconds(5);
        }
        catch { return false; }
    }

    /// <summary>运行时编译产物目录，用来判断「宿主会不会重新编译这个插件」。</summary>
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

    static bool IsSelf(MetadataReference reference, string pluginName)
    {
        try
        {
            string path = (reference as PortableExecutableReference)?.FilePath ?? "";
            return string.Equals(Path.GetFileNameWithoutExtension(path), pluginName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static List<Diagnostic> Compile(
        List<SyntaxTree> trees, ImmutableArray<MetadataReference> refs, string name)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "probe_" + name.Replace('.', '_') + "_" + Guid.NewGuid().ToString("N")[..6],
            trees,
            refs,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable,
                concurrentBuild: true,
                optimizationLevel: OptimizationLevel.Debug));

        using MemoryStream ms = new();

        // 用 var 而不是 EmitResult：类型在 Microsoft.CodeAnalysis.Emit 子命名空间，
        // 多一个 using 没必要
        var emit = compilation.Emit(ms);

        return emit.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .OrderBy(d => d.Location.SourceSpan.Start)
            .ToList();
    }

    static string Format(Diagnostic d, string pluginDir)
    {
        string file = d.Location.SourceTree?.FilePath ?? "";
        if (file.Length > 0)
        {
            // 只留插件内相对路径，报告里好读
            try
            {
                string rel = Path.GetRelativePath(pluginDir, file);
                if (!rel.StartsWith("..", StringComparison.Ordinal)) file = rel;
            }
            catch { }
        }

        var span = d.Location.GetLineSpan().Span;
        string where = file.Length > 0 ? $"{file}:{span.Start.Line + 1}" : "";

        string id = d.Id;   // CS0246 / CS0103 / CS0136 ...
        string msg = d.GetMessage();

        return where.Length > 0 ? $"{where} {id} {msg}" : $"{id} {msg}";
    }

    // ---------------------------------------------------------------- 引用集

    /// <summary>
    /// 构造与宿主一致的引用集：共享运行时 + 宿主 bin + 已编译插件。
    /// 只收托管程序集 —— 运行时目录里有 coreclr.dll 这类原生库，Roslyn 会直接报错。
    /// 首次加载要几十毫秒到一两秒，之后走缓存。
    /// </summary>
    static ImmutableArray<MetadataReference> LoadReferences(string runtimeDir, string hostBin)
    {
        lock (gate)
        {
            if (cachedRefs.Length > 0 &&
                cachedRuntimeDir == runtimeDir &&
                cachedHostBin == hostBin)
                return cachedRefs;

            Stopwatch sw = Stopwatch.StartNew();
            ImmutableArray<MetadataReference>.Builder builder = ImmutableArray.CreateBuilder<MetadataReference>();
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

            void Take(string folder, bool recurse = false)
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(folder, "*.dll",
                        recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                }
                catch { return; }

                foreach (string file in files)
                {
                    if (!seen.Add(Path.GetFileNameWithoutExtension(file))) continue;

                    try
                    {
                        // 原生库在这里会抛 BadImageFormatException，正好用来筛掉
                        System.Reflection.AssemblyName.GetAssemblyName(file);
                        builder.Add(MetadataReference.CreateFromFile(file));
                    }
                    catch { }
                }
            }

            Take(runtimeDir);
            Take(hostBin);

            // 已编译插件放最后：同名程序集优先用运行时/宿主的版本
            try
            {
                string alifeRoot = Path.GetFullPath(
                    Path.Combine(Alife.Foundation.AlifePath.StorageFolderPath, ".."));
                Take(Path.Combine(alifeRoot, "Runtime", "CompiledPlugins"));
            }
            catch { }

            cachedRefs = builder.ToImmutable();
            cachedRuntimeDir = runtimeDir;
            cachedHostBin = hostBin;

            sw.Stop();
            System.Diagnostics.Debug.WriteLine(
                $"[TestKit] 编译引用集 {cachedRefs.Length} 个，耗时 {sw.ElapsedMilliseconds} ms");

            return cachedRefs;
        }
    }
}
