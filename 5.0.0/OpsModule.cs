using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Foundation;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace presents.OpsSuite;

[Module("Alife 运维套件",
    "插件体检 + 资源哨兵：进程与显存按插件归因、热重载前后 diff、配置快照与一键回滚、页面注入层审计、冲突检测、资源预算与分级降级。提供本地可视化面板与 AI 工具集。",
    null, typeof(OpsEditorUI), typeof(OpsGlobalUI), 60, "系统工具/运维")]
public sealed class OpsModule(
    XmlFunctionCaller functionCaller,
    ILogger<OpsModule> logger,
    Interactor<OpsModule> interactor,
    ConfigurationSystem configurationSystem,
    PluginSystem pluginSystem,
    ModuleSystem moduleSystem) : ChatBehaviour, IConfigurable<OpsConfig>
{
    public OpsConfig Configuration { get; set; } = new();

    readonly ProcessProbe probe = new();
    readonly ResourceSentinel sentinel = new();
    readonly object gate = new();

    PluginInventory? inventory;
    ConfigVault? vault;
    OpsServer? server;
    CancellationTokenSource? loopCts;

    readonly List<ProcessSample> samples = new();
    readonly List<SampleDiff> diffs = new();
    ProcessSample? preReload;
    DateTime preReloadAt;
    string lastReloadNote = "尚未观测到重载";

    public string PanelUrl => server?.BaseUrl ?? "(面板未启动)";
    public static OpsModule? Active { get; private set; }

    internal BudgetState BudgetState => sentinel.State;

    protected override Task OnAwake()
    {
        Active = this;

        try { inventory = new PluginInventory(pluginSystem, moduleSystem); }
        catch (Exception ex) { logger.LogWarning(ex, "运维套件：插件台账初始化失败"); }

        try { vault = new ConfigVault(); }
        catch (Exception ex) { logger.LogWarning(ex, "运维套件：配置保险库初始化失败"); }

        try
        {
            string resources = Path.Combine(
                pluginSystem.PluginContext.GetPluginDirectoryPath("presents.OpsSuite"), "Resources");
            server = new OpsServer(resources, Configuration.PanelPort);
            server.ApiHandler = HandleApiAsync;
        }
        catch (Exception ex) { logger.LogWarning(ex, "运维套件：面板服务启动失败"); }

        if (Configuration.AutoReloadDiff)
        {
            try
            {
                pluginSystem.PluginContext.PluginUnloadedAsync += OnPluginUnloaded;
                pluginSystem.PluginContext.PluginLoadedAsync += OnPluginLoaded;
            }
            catch (Exception ex) { logger.LogWarning(ex, "运维套件：订阅插件生命周期失败"); }
        }

        if (Configuration.EnableAiFunctions)
        {
            XmlHandler handler = new(this)
            {
                Description = "Alife 运维工具：查进程/显存占用、拍资源快照、比对热重载前后差异、检查插件冲突、审计页面残留层、查看配置变更并一键回滚。",
                Explanation = "只在你明确要排查「插件为什么占内存/显存」「重载后是不是留了旧进程」「配置被谁改了」「界面上有个点不动的透明层」时调用。平时保持静默，不要主动汇报运维数据。"
            };
            functionCaller.RegisterHandler(handler, DocumentMode.Implicit, DestroyCancellationToken);
        }

        loopCts = new CancellationTokenSource();
        if (Configuration.SampleIntervalSeconds > 0)
            _ = Task.Run(() => SampleLoop(loopCts.Token));

        logger.LogInformation("Alife 运维套件已启动，面板：{Url}", PanelUrl);
        return Task.CompletedTask;
    }

    protected override async Task OnDestroy()
    {
        try
        {
            pluginSystem.PluginContext.PluginUnloadedAsync -= OnPluginUnloaded;
            pluginSystem.PluginContext.PluginLoadedAsync -= OnPluginLoaded;
        }
        catch { }

        try { loopCts?.Cancel(); } catch { }
        try { server?.Dispose(); } catch { }
        try { sentinel.Dispose(); } catch { }
        try { loopCts?.Dispose(); } catch { }
        if (ReferenceEquals(Active, this)) Active = null;

        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------ 采样

    async Task SampleLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, Configuration.SampleIntervalSeconds)), token);
                if (token.IsCancellationRequested) break;

                ProcessSample? previous;
                lock (gate) previous = samples.LastOrDefault();
                ProcessSample now = await Task.Run(() => probe.Capture("auto"), token);

                lock (gate)
                {
                    samples.Add(now);
                    int keep = Math.Max(10, Configuration.HistoryLength);
                    while (samples.Count > keep) samples.RemoveAt(0);
                }

                sentinel.Evaluate(now, previous, Configuration);
                if (Configuration.WatchConfigChanges)
                    vault?.SnapshotAll("poll", Math.Max(3, Configuration.ConfigHistoryPerFile));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "运维套件：采样失败"); }
        }
    }

    Task OnPluginUnloaded(string id, PluginLoadContext context)
    {
        try { preReload = probe.Capture("pre-reload"); preReloadAt = DateTime.UtcNow; }
        catch { }
        return Task.CompletedTask;
    }

    Task OnPluginLoaded(string id, PluginLoadContext context)
    {
        if (preReload == null || (DateTime.UtcNow - preReloadAt).TotalSeconds > 90)
            return Task.CompletedTask;

        try
        {
            ProcessSample after = probe.Capture("post-reload");
            SampleDiff diff = DiffEngine.Compare(preReload, after, "reload " + id);
            lock (gate)
            {
                diffs.Insert(0, diff);
                while (diffs.Count > 30) diffs.RemoveAt(diffs.Count - 1);
                samples.Add(after);
                while (samples.Count > Math.Max(10, Configuration.HistoryLength)) samples.RemoveAt(0);
            }
            lastReloadNote = $"{id}: 进程 {diff.BeforeCount}→{diff.AfterCount}, 显存 {diff.GpuDeltaMB:+0;-0;0} MB, 工作集 {diff.WorkingSetDeltaMB:+0;-0;0} MB";
        }
        catch { }
        finally { preReload = null; }

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ 快照 / 台账 / 配置

    internal ProcessSample TakeSnapshot(string label)
    {
        ProcessSample sample = probe.Capture(label);
        ProcessSample? previous;
        lock (gate) { previous = samples.LastOrDefault(); samples.Add(sample); }
        sentinel.Evaluate(sample, previous, Configuration);
        return sample;
    }

    internal IReadOnlyList<ProcessSample> Samples()
    {
        lock (gate) return samples.ToList();
    }

    internal IReadOnlyList<SampleDiff> Diffs()
    {
        lock (gate) return diffs.ToList();
    }

    internal SampleDiff? CompareLastTwo(string label = "manual")
    {
        lock (gate)
        {
            if (samples.Count < 2) return null;
            SampleDiff d = DiffEngine.Compare(samples[^2], samples[^1], label);
            diffs.Insert(0, d);
            while (diffs.Count > 30) diffs.RemoveAt(diffs.Count - 1);
            return d;
        }
    }

    internal InventoryReport? Inventory() => inventory?.Build(ReadActiveModules());

    // ------------------------------------------------------------------ 残留扫描

    string? residueCache;
    DateTime residueAt;
    readonly SemaphoreSlim residueGate = new(1, 1);

    internal async Task<string> ResidueAsync(bool force)
    {
        if (!force && residueCache != null && (DateTime.Now - residueAt).TotalSeconds < 30)
            return residueCache;

        await residueGate.WaitAsync();
        try
        {
            if (!force && residueCache != null && (DateTime.Now - residueAt).TotalSeconds < 30)
                return residueCache;

            ResidueReport report = ScanResidue();
            residueCache = Json(report);
            residueAt = DateTime.Now;
            return residueCache;
        }
        finally { residueGate.Release(); }
    }

    /// <summary>宿主日志的位置：Storage 的上一级 + Temp/alife.log。</summary>
    static string AlifeLogPath()
    {
        try
        {
            string alife = Path.GetFullPath(Path.Combine(AlifePath.StorageFolderPath, ".."));
            return Path.Combine(alife, "Temp", "alife.log");
        }
        catch { return ""; }
    }

    ResidueReport ScanResidue()
    {
        string[] pluginIds;
        try
        {
            pluginIds = Directory.GetDirectories(Path.Combine(AlifePath.StorageFolderPath, "Plugins"))
                .Select(Path.GetFileName).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToArray();
        }
        catch { pluginIds = Array.Empty<string>(); }

        string[] moduleIds;
        try
        {
            moduleIds = moduleSystem.GetAllModules()
                .Select(t => t.FullName).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToArray();
        }
        catch { moduleIds = Array.Empty<string>(); }

        return ResidueScanner.Scan(AlifePath.StorageFolderPath, pluginIds, moduleIds);
    }

    /// <summary>只删除「指向已停止实例」的端口文件，不动任何数据。</summary>
    internal (int Removed, List<string> Names) CleanDeadPorts()
    {
        List<string> names = new();
        int removed = 0;

        try
        {
            foreach (PortResidue p in ScanResidue().Ports.Where(x => !x.Alive))
            {
                try
                {
                    string file = Path.Combine(Path.GetTempPath(), p.Name + ".port");
                    if (File.Exists(file)) { File.Delete(file); removed++; names.Add($"{p.Name}:{p.Port}"); }
                }
                catch { }
            }
        }
        catch { }

        residueCache = null;
        return (removed, names);
    }

    // ------------------------------------------------------------------ 一键体检

    string? healthCache;
    DateTime healthCacheAt;
    readonly SemaphoreSlim healthGate = new(1, 1);

    /// <summary>
    /// 把页面审计、插件台账、资源状态、自检报告聚合成一张「问题清单」，
    /// 每条都带证据和一句可执行的修法 —— 面板上直接看，不用再逐个页翻。
    /// </summary>
    async Task<string> BuildHealthAsync(bool force)
    {
        if (!force && healthCache != null && (DateTime.Now - healthCacheAt).TotalSeconds < 20)
            return healthCache;

        await healthGate.WaitAsync();
        try
        {
            if (!force && healthCache != null && (DateTime.Now - healthCacheAt).TotalSeconds < 20)
                return healthCache;

            List<HealthItem> items = new();

            // ---------- 页面层：点不动 / 发白 / 残留
            try
            {
                if (!Configuration.EnablePageAudit)
                    throw new InvalidOperationException("页面审计已在模块配置中关闭（EnablePageAudit = false）");

                string auditJson = await PageAudit.RunAsync();
                using JsonDocument doc = JsonDocument.Parse(auditJson);
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("clickBlockers", out JsonElement blockers) &&
                    blockers.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement b in blockers.EnumerateArray())
                    {
                        string id = HStr(b, "id");
                        bool looksLikeDebug = id.StartsWith("__", StringComparison.Ordinal) ||
                                              id.Contains("test", StringComparison.OrdinalIgnoreCase) ||
                                              id.Contains("probe", StringComparison.OrdinalIgnoreCase);
                        items.Add(new HealthItem
                        {
                            Level = "bad", Area = "页面",
                            Title = "有点不动的遮挡层：" + id,
                            Detail = $"{HStr(b, "w")}×{HStr(b, "h")} · z-index {HStr(b, "z")} · {HStr(b, "why")}",
                            Fix = looksLikeDebug
                                ? $"看着是调试残留层，控制台执行 document.getElementById('{id}')?.remove()"
                                : "确认它属于哪个插件：是插件自己的层就关掉该插件，或是把它改成 pointer-events:none"
                        });
                    }
                }

                int residue = HCount(root, "initialResidue");
                if (residue > 0)
                    items.Add(new HealthItem
                    {
                        Level = "warn", Area = "页面",
                        Title = $"{residue} 个元素残留 background-* : initial",
                        Detail = "通常是某个插件用 background 简写写样式留下的，会把卡片原本的背景图/渐变永久压成 none（看着发白）",
                        Fix = "让插件改成只写 background-color；临时恢复可执行 window.__alifeWallpaperRestore?.()，或重启 Alife"
                    });

                int suspicious = HCount(root, "suspicious");
                if (suspicious > 0)
                    items.Add(new HealthItem
                    {
                        Level = "warn", Area = "页面",
                        Title = $"{suspicious} 个可疑测试 DOM",
                        Detail = "id 里带 test/probe/debug 之类的元素，多半是调试时插进去忘了删",
                        Fix = "在页面审计页确认具体 id，然后 remove 掉；这类层常常同时是点击拦截的元凶"
                    });

                int globals = HCount(root, "globals");
                if (globals > 60)
                    items.Add(new HealthItem
                    {
                        Level = "warn", Area = "页面",
                        Title = $"window.__* 全局变量多达 {globals} 个",
                        Detail = "插件卸载时不清理全局变量，热重载会不断累积，旧闭包也退不掉",
                        Fix = "在页面审计页看是谁的（按前缀分组），让对应插件在清理契约里置 null 或 delete"
                    });
            }
            catch (Exception ex)
            {
                items.Add(new HealthItem
                {
                    Level = "warn", Area = "页面",
                    Title = "页面审计没跑起来",
                    Detail = ex.Message,
                    Fix = "确认主界面窗口存在且未最小化；审计需要一个可注入脚本的页面"
                });
            }

            // ---------- 插件层：冲突 / 依赖 / 卫生
            InventoryReport? inv = Inventory();
            if (inv != null)
            {
                if (inv.DependencyCycles.Count > 0)
                    items.Add(new HealthItem
                    {
                        Level = "bad", Area = "插件",
                        Title = $"{inv.DependencyCycles.Count} 组循环依赖",
                        Detail = string.Join("；", inv.DependencyCycles.Take(3)),
                        Fix = "在 manifest.json 的 Dependencies 里打断环，通常把公共部分拆成第三个插件最省事"
                    });

                if (inv.MissingDependencies.Count > 0)
                    items.Add(new HealthItem
                    {
                        Level = "bad", Area = "插件",
                        Title = $"{inv.MissingDependencies.Count} 个依赖指向不存在的插件",
                        Detail = string.Join("；", inv.MissingDependencies.Take(4)),
                        Fix = "删掉 manifest 里那几条，或者把缺的插件装上；否则该插件在独立加载上下文里会缺类型"
                    });

                if (inv.ActiveConflicts.Count > 0)
                    items.Add(new HealthItem
                    {
                        Level = "warn", Area = "插件",
                        Title = $"{inv.ActiveConflicts.Count} 个工具名在当前角色启用的插件里重名",
                        Detail = string.Join("；", inv.ActiveConflicts.Take(3)),
                        Fix = "AI 调用同名工具时可能落到另一个插件。只启用其中一个，或给其中一个改成显式命名"
                    });

                if (inv.HygieneIssues.Count > 0)
                    items.Add(new HealthItem
                    {
                        Level = "warn", Area = "插件",
                        Title = $"{inv.HygieneIssues.Count} 处目录卫生问题",
                        Detail = string.Join("；", inv.HygieneIssues.Take(4)),
                        Fix = "插件目录里不该有 obj/bin/*.csproj，删掉即可；不影响运行，但会被打进分发包"
                    });

                if (inv.DuplicateIds.Count > 0)
                    items.Add(new HealthItem
                    {
                        Level = "warn", Area = "插件",
                        Title = $"{inv.DuplicateIds.Count} 个重复 DOM id",
                        Detail = string.Join("；", inv.DuplicateIds.Take(3)),
                        Fix = "两个插件用同一个 id 时 getElementById 只能拿到一个，改掉其中一个的前缀"
                    });
            }

            // ---------- 资源层
            BudgetState gpu = sentinel.State;
            if (gpu.Level >= 2)
                items.Add(new HealthItem
                {
                    Level = "bad", Area = "资源",
                    Title = $"资源等级 {gpu.Level}：Alife 进程树显存 {gpu.TotalGpuMB} MB",
                    Detail = $"阈值 {gpu.GpuThresholdMB} MB，全机 {gpu.SystemGpuMB} MB",
                    Fix = "壁纸开「后台立即释放显存」或切省显存画质档；场景包大的话考虑换视频壁纸"
                });
            else if (gpu.Level == 1)
                items.Add(new HealthItem
                {
                    Level = "warn", Area = "资源",
                    Title = $"Alife 显存 {gpu.TotalGpuMB} MB 接近阈值",
                    Detail = $"阈值 {gpu.GpuThresholdMB} MB",
                    Fix = "先看进程页哪个进程占得最多，再决定是降画质还是关插件"
                });

            // ---------- 编译失败：日志里的 Roslyn 诊断
            try
            {
                LogDiagnostics.Report log = LogDiagnostics.Scan(AlifeLogPath());

                if (log.PluginCount > 0)
                    items.Add(new HealthItem
                    {
                        Level = "bad", Area = "插件",
                        Title = $"{log.PluginCount} 个插件编译失败（日志里 {log.Errors.Count} 条诊断）",
                        Detail = string.Join("；", log.ByPlugin.OrderByDescending(x => x.Value).Take(4)
                            .Select(x => $"{x.Key}（{x.Value}）")),
                        Fix = "这类插件在客户端里**根本加载不了**，功能列表里不会有它，界面上也看不出问题。" +
                              "打开日志看具体 CS 编号：CS0246=缺 using 或类型名拼错，CS0103=字段/方法名不存在，" +
                              "CS0136=变量名在同一作用域重复。改完必须重载插件，光编译不重载不算生效"
                    });
            }
            catch { }

            // ---------- 残留：端口文件 / 孤儿数据 / 失效模块引用
            try
            {
                ResidueReport res = ScanResidue();
                residueCache = Json(res);
                residueAt = DateTime.Now;

                if (res.DeadPorts > 0)
                    items.Add(new HealthItem
                    {
                        Level = "warn", Area = "残留",
                        Title = $"{res.DeadPorts} 个端口文件指向已停止的实例",
                        Detail = string.Join("，", res.Ports.Where(x => !x.Alive).Take(6).Select(x => $"{x.Name}:{x.Port}")),
                        Fix = "只是残留标记，删掉即可（残留页有一键清理）；留着会让人误以为插件还在跑"
                    });

                if (res.Configs.Count > 0)
                    items.Add(new HealthItem
                    {
                        Level = "bad", Area = "残留",
                        Title = $"{res.Configs.Count} 处角色配置引用了不存在的模块",
                        Detail = string.Join("；", res.Configs.Take(3).Select(x => $"{x.Character} → {x.Module}")),
                        Fix = "从角色 index.json 的 Modules 里删掉这几项；否则角色启动时会静默跳过，插件看起来“明明启用了却没反应”"
                    });

                // 数据目录故意不进体检：插件常自建不同名的缓存目录（ThreeDeskPet / QQEmojis 等），
                // 按名字匹配必然误报，列出来只会制造噪音。完整的在「残留」页里作为参考展示。
            }
            catch { }

            // ---------- 自检报告（只读最近一次，不主动触发）
            string? testSummary = null;
            try
            {
                string portFile = Path.Combine(Path.GetTempPath(), "alife-testkit.port");
                if (File.Exists(portFile))
                {
                    string port = (await File.ReadAllTextAsync(portFile)).Trim();
                    if (port.Length > 0)
                    {
                        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
                        string json = await http.GetStringAsync($"http://127.0.0.1:{port}/api/report");
                        using JsonDocument doc = JsonDocument.Parse(json);
                        JsonElement r = doc.RootElement;
                        int failed = HInt(r, "failed");
                        int warn2 = HInt(r, "warnings");
                        testSummary = $"自检：{HInt(r, "total")} 项 / 通过 {HInt(r, "passed")} / 失败 {failed} / 提示 {warn2}";

                        if (failed > 0)
                            items.Add(new HealthItem
                            {
                                Level = "bad", Area = "自检",
                                Title = $"插件自检有 {failed} 项失败",
                                Detail = HStr(r, "summary"),
                                Fix = "打开测试面板看失败明细；也可以让 AI 调 runtests 重跑一次拿最新结果"
                            });
                        else if (warn2 > 0)
                            items.Add(new HealthItem
                            {
                                Level = "warn", Area = "自检",
                                Title = $"插件自检有 {warn2} 条提示",
                                Detail = HStr(r, "summary"),
                                Fix = "提示一般是技术债（事件监听没挂 signal、目录里有构建产物），不阻断运行"
                            });
                    }
                }
            }
            catch { /* 测试框架没装/没运行，不算问题 */ }

            int bad = items.Count(x => x.Level == "bad");
            int warn = items.Count(x => x.Level == "warn");

            HealthReport report = new()
            {
                At = DateTime.Now.ToString("HH:mm:ss"),
                Level = bad > 0 ? "bad" : warn > 0 ? "warn" : "ok",
                Bad = bad,
                Warn = warn,
                Ok = items.Count(x => x.Level == "ok"),
                Summary = bad > 0
                    ? $"{bad} 项需要处理，{warn} 项注意"
                    : warn > 0 ? $"没有严重问题，{warn} 项注意" : "一切正常",
                Items = items.OrderBy(x => x.Level == "bad" ? 0 : x.Level == "warn" ? 1 : 2).ToList(),
                GpuMB = gpu.TotalGpuMB,
                SysGpuMB = gpu.SystemGpuMB,
                ProcessCount = samples.Count > 0 ? samples[^1].ProcessCount : 0,
                ConfigVersions = vault?.VersionCount ?? 0,
                TestSummary = testSummary
            };

            healthCache = Json(report);
            healthCacheAt = DateTime.Now;
            return healthCache;
        }
        finally { healthGate.Release(); }
    }

    static string HStr(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return "";
        foreach (JsonProperty p in el.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.String
                    ? (p.Value.GetString() ?? "")
                    : p.Value.ToString();
        return "";
    }

    static int HInt(JsonElement el, string name)
    {
        string s = HStr(el, name);
        return int.TryParse(s, out int v) ? v : 0;
    }

    static int HCount(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty p in el.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    return p.Value.ValueKind == JsonValueKind.Array ? p.Value.GetArrayLength() : 0;
        return 0;
    }

    /// <summary>
    /// 从当前角色的 index.json 读 Modules，用来把「工具名冲突」限定在本角色真正启用的插件之间。
    /// 不读文件的话只能拿到全部已加载模块，数字会偏大（测试角色只启用 5 个插件却报 9 个冲突）。
    /// </summary>
    string[] ReadActiveModules()
    {
        try
        {
            string key = Character?.StorageKey ?? "";
            if (string.IsNullOrWhiteSpace(key)) return Array.Empty<string>();

            string path = Path.Combine(
                AlifePath.StorageFolderPath,
                key.Replace('/', Path.DirectorySeparatorChar),
                "index.json");
            if (!File.Exists(path)) return Array.Empty<string>();

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("Modules", out JsonElement mods) ||
                mods.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            return mods.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? "")
                .Where(x => x.Length > 0)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    internal ConfigVault? Vault => vault;

    public string ReloadNote => lastReloadNote;

    public void PersistConfig()
    {
        try { configurationSystem.SetConfiguration(GetType(), Configuration, Character?.StorageKey ?? ""); }
        catch (Exception ex) { logger.LogWarning(ex, "运维套件：配置落盘失败"); }
    }

    public void OpenPanel()
    {
        try
        {
            Process.Start(new ProcessStartInfo(PanelUrl) { UseShellExecute = true });
        }
        catch (Exception ex) { logger.LogWarning(ex, "运维套件：打开面板失败"); }
    }

    // ------------------------------------------------------------------ HTTP API

    async Task<string> HandleApiAsync(string path, string query, string body)
    {
        Dictionary<string, string> q = ParseQuery(query);
        string p = path.ToLowerInvariant();

        if (p == "/api/state") return Json(new
        {
            ok = true,
            panel = PanelUrl,
            gpu = sentinel.State,
            samples = Samples().Count,
            diffs = Diffs().Count,
            trackedConfigs = vault?.TrackedCount ?? 0,
            configVersions = vault?.VersionCount ?? 0,
            reloadNote = lastReloadNote,
            alerts = sentinel.Recent().Take(10)
        });

        if (p == "/api/samples") return Json(Samples().Select(s => new
        {
            s.At, s.Label, s.ProcessCount, s.TotalWorkingSetMB, s.TotalGpuMB,
            s.GroupWorkingSetMB, s.GroupGpuMB, s.GpuAvailable
        }));

        if (p == "/api/processes")
        {
            ProcessSample? latest = Samples().LastOrDefault();
            return Json(latest?.Processes ?? new List<ProcessRecord>());
        }

        if (p == "/api/diffs") return Json(Diffs());
        if (p == "/api/inventory") return Json(Inventory() ?? new object());
        if (p == "/api/budget") return Json(sentinel.State);
        if (p == "/api/alerts") return Json(sentinel.Recent());
        // 一键体检：聚合页面审计 / 台账 / 资源 / 自检，默认 20 秒缓存，?refresh 强制重算
        if (p == "/api/health")
            return await BuildHealthAsync(q.ContainsKey("refresh") || q.ContainsKey("force"));

        // 新建插件骨架：生成 manifest + 模块 + 配置 + 入口 UI + 占位页
        if (p == "/api/scaffold")
        {
            string pluginsRoot = Path.Combine(AlifePath.StorageFolderPath, "Plugins");
            Scaffolder.Result r = Scaffolder.Create(
                pluginsRoot,
                q.GetValueOrDefault("name", ""),
                q.GetValueOrDefault("module", ""),
                q.GetValueOrDefault("title", ""),
                q.GetValueOrDefault("ns", ""));

            return Json(r);
        }

        // 编译错误：插件编译失败时模块会直接消失，界面上看不到任何痕迹，
        // 唯一证据是宿主写进 alife.log 的 Roslyn 诊断。
        if (p == "/api/logerrors")
        {
            LogDiagnostics.Report r = LogDiagnostics.Scan(AlifeLogPath());
            List<LogDiagnostics.CompileError> active = r.Errors.Where(x => !x.Fixed).ToList();
            List<LogDiagnostics.CompileError> fixedList = r.Errors.Where(x => x.Fixed).ToList();

            return Json(new
            {
                ok = true,
                exists = r.LogExists,
                logPath = r.LogPath,
                logTime = r.LogTime.ToString("MM-dd HH:mm:ss"),
                scanned = r.ScannedLines,

                // plugins / errors 只算还没修好的：已经产出 dll 的不该继续报红
                plugins = r.PluginCount,
                errors = active.Count,
                fixedCount = fixedList.Count,
                summary = LogDiagnostics.Summary(r),
                byPlugin = r.ByPlugin,
                items = active.Take(200).ToList(),

                // 已修复的单独列，方便确认"确实修过"而不是"没扫到"
                fixedItems = fixedList
                    .GroupBy(x => x.Plugin)
                    .Select(g => new { plugin = g.Key, count = g.Count(), at = g.Max(x => x.At) })
                    .ToList()
            });
        }

        if (p == "/api/residue")
            return await ResidueAsync(q.ContainsKey("refresh"));

        // 插件档案：一张表看清每个插件的大小、改动时间、在哪些角色启用、依赖、欠测
        if (p == "/api/dossier")
        {
            InventoryReport? inv = Inventory();
            if (inv == null) return "{\"ok\":false,\"error\":\"台账未初始化\"}";

            List<PluginDossier> list = DossierBuilder.Build(inv, AlifePath.StorageFolderPath);
            return Json(new
            {
                ok = true,
                count = list.Count,
                stale = list.Count(x => x.StaleTest.Length > 0),
                problems = list.Count(x => x.Problems.Count > 0),
                plugins = list
            });
        }

        if (p == "/api/residue/clean")
        {
            (int removed, List<string> names) = CleanDeadPorts();
            return Json(new
            {
                ok = true, removed,
                names = names.Take(20).ToList(),
                text = removed == 0 ? "没有需要清理的残留端口文件" : $"已删除 {removed} 个失效端口文件（只删 %TEMP% 里的标记，不动数据）"
            });
        }

        if (p == "/api/audit")
        {
            // 配置项必须真的生效，否则面板上那个开关就是骗人的
            if (!Configuration.EnablePageAudit)
                return "{\"ok\":false,\"disabled\":true,\"error\":\"页面审计已在模块配置中关闭\"}";

            return await PageAudit.RunAsync();
        }
        if (p == "/api/configs") return Json(vault?.Entries() ?? new List<ConfigEntry>());

        if (p == "/api/config/history")
            return Json(vault?.History(q.GetValueOrDefault("path", "")) ?? Array.Empty<ConfigVersion>());

        if (p == "/api/config/diff")
            return Json(new
            {
                ok = true,
                text = vault?.Diff(q.GetValueOrDefault("path", ""), q.GetValueOrDefault("version", "")) ?? "保险库不可用"
            });

        if (p == "/api/snapshot")
        {
            string label = q.GetValueOrDefault("label", "manual");
            ProcessSample s = TakeSnapshot(label);
            return Json(new { ok = true, s.At, s.ProcessCount, s.TotalWorkingSetMB, s.TotalGpuMB });
        }

        if (p == "/api/config/rollback")
        {
            string file = q.GetValueOrDefault("path", "");
            string version = q.GetValueOrDefault("version", "");
            if (file.Length == 0 || version.Length == 0)
                return "{\"ok\":false,\"error\":\"缺少 path 或 version\"}";
            string text = vault?.Rollback(file, version) ?? "保险库不可用";
            return Json(new { ok = true, text });
        }

        if (p == "/api/open") { OpenPanel(); return "{\"ok\":true}"; }

        return "{\"ok\":false,\"error\":\"未知接口\"}";
    }

    static Dictionary<string, string> ParseQuery(string query)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in (query ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = pair.IndexOf('=');
            string key = at < 0 ? pair : pair[..at];
            string value = at < 0 ? "" : pair[(at + 1)..];
            try { result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace("+", " ")); }
            catch { result[key] = value; }
        }
        return result;
    }

    // HTTP 输出统一 camelCase：模型属性是 PascalCase，直接序列化会让面板读
    // g.totalGpuMB / inv.pluginCount 之类全是 undefined（页面看着像没数据）。
    // 落盘文件仍用默认 PascalCase，加载端大小写不敏感，互不影响。
    static string Json(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    });

    // ------------------------------------------------------------------ AI 工具

    [XmlFunction(FunctionMode.OneShot, "opsstatus")]
    [Description("查看 Alife 当前资源总览：进程数、工作集、GPU 专用显存、告警与最近一次重载差异。")]
    public void OpsStatus()
    {
        ProcessSample s = TakeSnapshot("ai-status");
        interactor.Poke(
            $"[运维] 进程 {s.ProcessCount} 个，工作集 {s.TotalWorkingSetMB} MB，GPU 专用显存 {s.TotalGpuMB} MB" +
            $"（计数器：{s.GpuAvailable}）；预算等级 {sentinel.State.Level}/3，告警 {sentinel.State.Alerts.Count} 条。" +
            $"\n最近重载：{lastReloadNote}\n配置留档：{vault?.TrackedCount ?? 0} 个文件 / {vault?.VersionCount ?? 0} 个版本。" +
            $"\n面板：{PanelUrl}");
    }

    [XmlFunction(FunctionMode.OneShot, "opssnapshot")]
    [Description("拍一张资源快照，用于前后对比。")]
    public void OpsSnapshot([Description("快照标签")] string label = "ai")
    {
        ProcessSample s = TakeSnapshot(label);
        interactor.Poke($"[运维] 快照 {s.At}（{label}）：进程 {s.ProcessCount}，工作集 {s.TotalWorkingSetMB} MB，显存 {s.TotalGpuMB} MB。");
    }

    [XmlFunction(FunctionMode.OneShot, "opsdiff")]
    [Description("对比最近两次资源快照，列出新增/消失的进程与显存变化。")]
    public void OpsDiff()
    {
        SampleDiff? d = CompareLastTwo("ai-diff") ?? Diffs().FirstOrDefault();
        if (d == null) { interactor.Poke("[运维] 快照不足两张，暂时无法对比。"); return; }

        interactor.Poke($"[运维] {d.Label}：进程 {d.BeforeCount}→{d.AfterCount}（+{d.AddedProcesses}/-{d.RemovedProcesses}），" +
                        $"显存 {d.GpuDeltaMB:+0;-0;0} MB，工作集 {d.WorkingSetDeltaMB:+0;-0;0} MB。" +
                        (d.GroupChanges.Count > 0 ? "\n" + string.Join("\n", d.GroupChanges.Take(8)) : "") +
                        (d.Notes.Count > 0 ? "\n" + string.Join("\n", d.Notes) : ""));
    }

    [XmlFunction(FunctionMode.OneShot, "opsconflicts")]
    [Description("检查插件冲突：重复的 XmlFunction 名、重复的 DOM id、依赖环、缺失依赖、目录卫生问题。")]
    public void OpsConflicts()
    {
        InventoryReport? r = Inventory();
        if (r == null) { interactor.Poke("[运维] 插件台账不可用。"); return; }

        List<string> lines = new()
        {
            $"[运维] 插件 {r.PluginCount} 个 / 模块 {r.ModuleCount} 个 / 工具函数 {r.FunctionCount} 个",
            $"重复函数 {r.DuplicateFunctions.Count}，重复 DOM id {r.DuplicateIds.Count}，依赖环 {r.DependencyCycles.Count}，目录卫生 {r.HygieneIssues.Count}，缺失依赖 {r.MissingDependencies.Count}"
        };
        foreach (string group in new[] { "重复函数", "依赖环", "缺失依赖", "目录卫生" })
        {
            IEnumerable<string> items = group switch
            {
                "重复函数" => r.DuplicateFunctions,
                "依赖环" => r.DependencyCycles,
                "缺失依赖" => r.MissingDependencies,
                _ => r.HygieneIssues
            };
            foreach (string item in items.Take(6)) lines.Add($"{group}: {item}");
        }
        interactor.Poke(string.Join("\n", lines));
    }

    [XmlFunction(FunctionMode.OneShot, "opsaudit")]
    [Description("审计主页面残留：window.__* 全局、遮挡点击的 fixed 层、测试用 DOM、background initial 残留。")]
    public async Task OpsAudit()
    {
        string result = await PageAudit.RunAsync();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(result);
            JsonElement root = doc.RootElement;
            int globals = root.TryGetProperty("globals", out JsonElement g) ? g.GetArrayLength() : 0;
            int overlays = root.TryGetProperty("overlays", out JsonElement o) ? o.GetArrayLength() : 0;
            int suspicious = root.TryGetProperty("suspicious", out JsonElement s) ? s.GetArrayLength() : 0;
            int initial = root.TryGetProperty("initialResidue", out JsonElement i) ? i.GetArrayLength() : 0;

            List<string> blockers = new();
            if (root.TryGetProperty("overlays", out JsonElement ov))
                foreach (JsonElement e in ov.EnumerateArray())
                    if (e.TryGetProperty("blocksClicks", out JsonElement b) && b.GetBoolean())
                        blockers.Add($"{e.GetProperty("tag")}#{e.GetProperty("id")} {e.GetProperty("w")}×{e.GetProperty("h")} z={e.GetProperty("z")}");

            interactor.Poke($"[运维] 页面审计：全局 {globals} 个，fixed 大层 {overlays} 个（其中 {blockers.Count} 个可能拦截点击），" +
                            $"可疑测试 DOM {suspicious} 个，样式 initial 残留 {initial} 个。" +
                            (blockers.Count > 0 ? "\n可疑遮挡：" + string.Join("；", blockers.Take(5)) : "") +
                            $"\n完整结果：{PanelUrl}api/audit");
        }
        catch { interactor.Poke("[运维] 页面审计失败：" + result); }
    }

    [XmlFunction(FunctionMode.OneShot, "opsconfigs")]
    [Description("列出被保险库跟踪的配置文件及其历史版本数。")]
    public void OpsConfigs()
    {
        IReadOnlyList<ConfigEntry>? entries = vault?.Entries();
        if (entries == null || entries.Count == 0) { interactor.Poke("[运维] 尚未跟踪到配置文件。"); return; }

        IEnumerable<string> lines = entries.Take(25).Select(x => $"{x.Path} — {x.History.Count} 个版本，当前 {x.Bytes} 字节");
        interactor.Poke("[运维] 配置文件：\n" + string.Join("\n", lines) + $"\n面板：{PanelUrl}");
    }

    [XmlFunction(FunctionMode.OneShot, "opsconfigdiff")]
    [Description("对比某份配置的当前内容与指定历史版本，看哪些字段被覆盖/改动。")]
    public void OpsConfigDiff(
        [Description("配置相对路径，先用 opsconfigs 查")] string path,
        [Description("历史版本文件名，可在面板查看")] string version)
    {
        interactor.Poke("[运维] " + (vault?.Diff(path, version) ?? "保险库不可用"));
    }

    [XmlFunction(FunctionMode.OneShot, "opsrollback")]
    [Description("把某份配置回滚到指定历史版本（会先把当前版本另存，可撤销）。")]
    public void OpsRollback(
        [Description("配置相对路径")] string path,
        [Description("历史版本文件名")] string version)
    {
        interactor.Poke("[运维] " + (vault?.Rollback(path, version) ?? "保险库不可用"));
    }

    [XmlFunction(FunctionMode.OneShot, "opsbudget")]
    [Description("查看资源预算等级与最近告警。")]
    public void OpsBudget()
    {
        BudgetState s = sentinel.State;
        interactor.Poke($"[运维] 预算等级 {s.Level}/3，显存 {s.TotalGpuMB}/{s.GpuThresholdMB} MB，" +
                        $"工作集 {s.TotalWorkingSetMB}/{s.WorkingSetThresholdMB} MB，告警 {s.Alerts.Count} 条。" +
                        (s.Alerts.Count > 0 ? "\n" + string.Join("\n", s.Alerts.Take(5).Select(a => $"[{a.Level}] {a.Message}")) : ""));
    }

    [XmlFunction(FunctionMode.OneShot, "opscompile")]
    [Description("查编译失败：插件在客户端里编译不过时，模块会直接从功能列表消失，界面上完全看不出原因。"
        + "证据只有宿主写进 alife.log 的 Roslyn 诊断，这个工具就是去把它捞出来。")]
    public void OpsCompile()
    {
        LogDiagnostics.Report r = LogDiagnostics.Scan(AlifeLogPath());
        List<LogDiagnostics.CompileError> active = r.Errors.Where(x => !x.Fixed).ToList();

        if (!r.LogExists)
        {
            interactor.Poke("[运维] 没有找到 alife.log，无法判断编译情况。");
            return;
        }

        if (active.Count == 0)
        {
            string fixedNote = r.Errors.Count > 0
                ? $"（日志里另有 {r.Errors.Count} 条历史错误，之后已产出 dll，属于修好了）"
                : "";

            interactor.Poke("[运维] 没有编译失败的插件。" + fixedNote);
            return;
        }

        List<string> blocks = new();

        foreach (IGrouping<string, LogDiagnostics.CompileError> g in
                 active.GroupBy(x => x.Plugin).OrderByDescending(x => x.Count()))
        {
            LogDiagnostics.CompileError first = g.First();
            blocks.Add($"{g.Key}（{g.Count()} 处）\n     首条：{first.File}:{first.Line} {first.Code} — " +
                       first.Message);
        }

        interactor.Poke(
            $"[运维] {r.PluginCount} 个插件编译失败，这些在客户端里根本不会出现：\n" +
            string.Join("\n", blocks.Take(5)) +
            (blocks.Count > 5 ? $"\n…还有 {blocks.Count - 5} 个插件" : "") +
            "\n\n常见原因：CS0246 缺 using（Alife 逐文件直编，没有隐式 using）" +
            "／CS0103 名称不存在／CS0136 变量重名。改完必须重载插件才生效。");
    }

    [XmlFunction(FunctionMode.OneShot, "opscheck")]
    [Description("一键体检：页面遮挡层、插件冲突、依赖问题、失效模块引用、资源告警、自检失败，每条都带怎么修。")]
    public async Task OpsCheck()
    {
        string json = await BuildHealthAsync(true);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement r = doc.RootElement;

        string summary = HStr(r, "summary");
        List<string> lines = new();

        if (r.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
            foreach (JsonElement it in items.EnumerateArray())
                lines.Add($"[{HStr(it, "level")}] {HStr(it, "area")} · {HStr(it, "title")}\n   怎么修：{HStr(it, "fix")}");

        interactor.Poke(lines.Count == 0
            ? "[运维] 体检通过，没有发现问题。"
            : $"[运维] {summary}\n" + string.Join("\n", lines.Take(8)) +
              (lines.Count > 8 ? $"\n…还有 {lines.Count - 8} 项，面板里看全部" : ""));
    }

    [XmlFunction(FunctionMode.OneShot, "opspanel")]
    [Description("获取运维面板地址并打开。")]
    public void OpsPanel()
    {
        OpenPanel();
        interactor.Poke("[运维] 面板地址：" + PanelUrl);
    }
}
