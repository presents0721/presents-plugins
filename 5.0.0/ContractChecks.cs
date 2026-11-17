using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace presents.PluginTestKit;

/// <summary>
/// 契约检查：挡的是「编译通过、加载成功、但行为不对」这一类问题。
/// 全部静态扫描，不需要激活角色，每个插件出一条结论（避免几百条噪声淹没报告）。
///
/// 检查项来自实际事故复盘：
///   - 改了源码但运行时还是旧 DLL（F1 / C-01 / C-09）
///   - [Module] 漏写 editorUI，模块能加载但用户找不到入口（F2）
///   - 配置类用 public 字段、集合未初始化、缺默认值（G-02 / C1）
///   - 订阅了 ChatBot 事件但在 OnDestroy 不退订（L-04 / E-03）
/// </summary>
internal static class ContractChecks
{
    static readonly Regex ModuleAttr = new(@"\[Module\s*\(", RegexOptions.Compiled);
    static readonly Regex ConfigClass = new(@"\bclass\s+(\w*Config)\b", RegexOptions.Compiled);
    static readonly Regex AutoProp = new(
        @"^\s*public\s+([\w<>\[\],\?\.\s]+?)\s+(\w+)\s*\{\s*get;\s*set;\s*\}\s*(=\s*[^;]+)?;",
        RegexOptions.Compiled | RegexOptions.Multiline);
    static readonly Regex PublicField = new(
        @"^\s*public\s+(?!class|enum|interface|struct|record|static|const|readonly|override|virtual|abstract|async|event)[\w<>\[\],\?\.\s]+?\s+(\w+)\s*(=[^;]*)?;",
        RegexOptions.Compiled | RegexOptions.Multiline);
    static readonly Regex Subscription = new(@"ChatBot\.(\w+)\s*\+=", RegexOptions.Compiled);

    public static List<TestResult> Run(string pluginsRoot, string? compiledRoot)
    {
        List<TestResult> results = new();

        IEnumerable<string> dirs;
        try
        {
            dirs = File.Exists(Path.Combine(pluginsRoot, "manifest.json"))
                ? new[] { pluginsRoot }
                : Directory.EnumerateDirectories(pluginsRoot);
        }
        catch { return results; }

        // 双份 DLL 的旧副本通常一次涉及几十个插件，按插件逐条报会把报告刷屏，
        // 所以收集到最后汇总成一条。
        List<string> dualStale = new();

        foreach (string dir in dirs)
        {
            string plugin = Path.GetFileName(dir);
            string[] sources = ReadSources(dir);
            if (sources.Length == 0) continue;

            List<string> problems = new();   // 真问题 → 失败
            List<string> warnings = new();   // 技术债 → 提示

            CheckBuildFreshness(plugin, sources, compiledRoot, problems);
            CheckDualDll(plugin, compiledRoot, dualStale);
            CheckModuleUi(plugin, sources, warnings);
            // 死配置审计需要在整个插件范围内搜属性名，先把源码拼一次，避免每个属性都读一遍盘
            string allSource = string.Concat(sources.Select(SafeRead));
            CheckConfigClasses(plugin, sources, allSource, problems, warnings);
            CheckUnsubscribe(plugin, sources, warnings);
            CheckToolRegistration(plugin, sources, problems);
            CheckUiBaseCall(plugin, sources, warnings);
            CheckContentMode(plugin, sources, problems);

            if (problems.Count > 0)
                results.Add(TestFactory.Fail(
                    "contract." + plugin, "契约检查", "契约", plugin, 0,
                    $"{problems.Count} 个问题、{warnings.Count} 条提示",
                    problems.Take(7).Concat(warnings.Take(3)).ToList()));
            else if (warnings.Count > 0)
                results.Add(TestFactory.Fail(
                    "contract." + plugin, "契约检查", "契约", plugin, 0,
                    $"{warnings.Count} 条提示", warnings.Take(9).ToList(), warning: true));
            else
                results.Add(TestFactory.Ok(
                    "contract." + plugin, "契约检查", "契约", plugin, 0, "构建产物、入口、配置、退订均正常"));
        }

        if (dualStale.Count > 0)
            results.Add(TestFactory.Fail(
                "dual-dll", "双份编译产物", "构建", "global", 0,
                $"{dualStale.Count} 个插件的 PluginContext 副本比正式目录旧，" +
                "隔离上下文可能加载到旧版本",
                dualStale.Take(10).ToList(), warning: true));

        return results;
    }

    // ------------------------------------------------------------------ 1. 编译产物

    static void CheckBuildFreshness(string plugin, string[] sources, string? compiledRoot, List<string> problems)
    {
        if (compiledRoot == null) return;

        string dll = Path.Combine(compiledRoot, plugin + ".dll");
        if (!File.Exists(dll)) return;   // 没编译过不算错：可能是纯资源插件

        DateTime dllAt = File.GetLastWriteTime(dll);
        DateTime newest = sources
            .Select(f => { try { return File.GetLastWriteTime(f); } catch { return DateTime.MinValue; } })
            .DefaultIfEmpty(DateTime.MinValue).Max();

        if (newest == DateTime.MinValue || newest <= dllAt.AddSeconds(5)) return;

        problems.Add($"源码比 DLL 新（源码 {newest:MM-dd HH:mm:ss} / DLL {dllAt:MM-dd HH:mm:ss}）：" +
                     "运行时跑的还是旧代码，改完必须 reload_plugin 触发重编译");
    }

    // ------------------------------------------------------------------ 2. 双份 DLL

    /// <summary>
    /// Runtime 下有两份编译产物：CompiledPlugins 和 PluginContext/CompiledPlugins。
    /// 只更新一份时，隔离上下文里的插件会加载到旧版本（文档 20261016 §7.1 标为 P0）。
    /// 只提示：PluginContext 那份可能本来就是上次会话的残留，不影响当前运行。
    /// </summary>
    static void CheckDualDll(string plugin, string? compiledRoot, List<string> stale)
    {
        if (compiledRoot == null) return;

        string main = Path.Combine(compiledRoot, plugin + ".dll");
        if (!File.Exists(main)) return;

        string? runtime = Path.GetDirectoryName(compiledRoot);
        if (runtime == null) return;

        string alt = Path.Combine(runtime, "PluginContext", "CompiledPlugins", plugin + ".dll");
        if (!File.Exists(alt)) return;

        DateTime a = File.GetLastWriteTime(main);
        DateTime b = File.GetLastWriteTime(alt);
        if (a <= b.AddHours(12)) return;

        stale.Add($"{plugin}（旧 {b:MM-dd HH:mm} / 新 {a:MM-dd HH:mm}）");
    }

    // ------------------------------------------------------------------ 3. Module 入口

    static void CheckModuleUi(string plugin, string[] sources, List<string> warnings)
    {
        foreach (string file in sources)
        {
            string text = SafeRead(file);
            foreach (Match m in ModuleAttr.Matches(text))
            {
                string? args = ReadBalanced(text, m.Index + m.Length - 1);
                if (args == null) continue;

                // 只有实现了 IConfigurable<> 的模块才需要设置入口：
                // 基础服务（Browser / Execution / FileService）本来就没有配置页，
                // 对它们报「缺 editorUI」是纯噪音。
                int classAt = text.IndexOf("class ", m.Index, StringComparison.Ordinal);
                if (classAt < 0 || classAt - m.Index > 3000) continue;

                int brace = text.IndexOf('{', classAt);
                if (brace < 0) continue;

                string header = text[classAt..brace];
                if (!header.Contains("IConfigurable<", StringComparison.Ordinal)) continue;

                if (!args.Contains("typeof(", StringComparison.Ordinal))
                    warnings.Add($"{Path.GetFileName(file)}:{LineOf(text, m.Index)} 的 [Module] 没写 editorUI，" +
                                 "有配置模块但没自定义设置页（框架默认面板可兜底，体验较差）");
            }
        }
    }

    // ------------------------------------------------------------------ 3. 配置类

    static void CheckConfigClasses(string plugin, string[] sources, string allSource,
        List<string> problems, List<string> warnings)
    {
        foreach (string file in sources)
        {
            string text = SafeRead(file);
            foreach (Match m in ConfigClass.Matches(text))
            {
                string className = m.Groups[1].Value;
                int brace = text.IndexOf('{', m.Index);
                if (brace < 0) continue;

                string? body = ReadBalanced(text, brace);
                if (body == null) continue;

                string where = $"{Path.GetFileName(file)}:{LineOf(text, m.Index)} {className}";

                foreach (Match f in PublicField.Matches(body))
                {
                    string name = f.Groups[1].Value;
                    if (name is "Configuration" or "Module" or "Id" or "Name") continue;

                    // 用匹配末尾定位所在行：正则的 ^\s* 会吃掉前面的换行，
                    // 拿 f.Index 会落到上一行，导致表达式体属性判断失效。
                    string line = LineAt(body, f.Index + f.Length - 1);
                    if (line.Contains("=>", StringComparison.Ordinal)) continue;

                    // 官方基础插件（OpenAI 模型）也用 public 字段存配置且能正常工作，
                    // 说明框架对配置类有兼容处理。所以这里只作提示，不当失败。
                    warnings.Add($"{where}：{name} 是 public 字段而非属性，若框架未做兼容处理，UI 改了不会持久化");
                }

                foreach (Match p in AutoProp.Matches(body))
                {
                    string type = p.Groups[1].Value.Trim();
                    string name = p.Groups[2].Value;
                    bool hasInit = p.Groups[3].Success;

                    bool isCollection = type.Contains("List<", StringComparison.Ordinal)
                                     || type.Contains("Dictionary<", StringComparison.Ordinal)
                                     || type.EndsWith("[]", StringComparison.Ordinal);

                    if (isCollection && !hasInit)
                        problems.Add($"{where}：集合属性 {name} 没有 = new()，反序列化后可能为 null 直接 NRE");

                    if (IsServiceType(type))
                        problems.Add($"{where}：{name} 是服务类型 {type}，配置类里放服务会序列化失败/循环引用");

                    if (!isCollection && type == "string" && !hasInit)
                        warnings.Add($"{where}：字符串 {name} 没有默认值");

                    string pre = PrecedingLines(body, p.Index, 6);
                    if (!pre.Contains("[DisplayName", StringComparison.Ordinal))
                        warnings.Add($"{where}：{name} 缺 [DisplayName]");
                    if (!pre.Contains("[Description", StringComparison.Ordinal))
                        warnings.Add($"{where}：{name} 缺 [Description]");

                    // 死配置：属性名在整个插件里只出现一次（就是定义本身），说明没人读它。
                    // 这类开关面板上能点、点了没任何反应，比不加还糟。
                    if (!IsCommonConfigName(name) && CountName(allSource, name) <= 1)
                        warnings.Add($"{where}：{name} 在插件里只出现在定义处，没有消费者（面板能改，改了没反应）");
                }
            }
        }
    }

    // ------------------------------------------------------------------ 4. 事件退订

    static void CheckUnsubscribe(string plugin, string[] sources, List<string> warnings)
    {
        foreach (string file in sources)
        {
            string text = SafeRead(file);
            int destroyAt = text.IndexOf("OnDestroy", StringComparison.Ordinal);
            if (destroyAt < 0) continue;

            string destroyPart = text[destroyAt..];

            foreach (Match m in Subscription.Matches(text))
            {
                string evt = m.Groups[1].Value;
                if (!destroyPart.Contains("ChatBot." + evt + " -=", StringComparison.Ordinal))
                    warnings.Add($"{Path.GetFileName(file)}:{LineOf(text, m.Index)} 订阅 ChatBot.{evt} " +
                                 "但 OnDestroy 里没退订，热重载后会重复处理同一条消息");
            }
        }
    }

    // ------------------------------------------------------------------ 5. 工具是否真的注册

    /// <summary>
    /// 只挂 [XmlFunction] 特性不会让工具可用 —— 框架不扫描特性，必须显式 RegisterHandler
    /// （文档 20261016 §2.1）。漏了就是「代码里明明有，AI 永远不知道」。
    /// </summary>
    static void CheckToolRegistration(string plugin, string[] sources, List<string> problems)
    {
        bool hasXmlFunction = false, hasRegister = false;

        foreach (string file in sources)
        {
            string text = SafeRead(file);
            if (text.Contains("[XmlFunction", StringComparison.Ordinal)) hasXmlFunction = true;

            // 有两种注册 API：RegisterHandler（带文档）和 RegisterHandlerWithoutDocument（自己管文档），
            // 只匹配带括号的那个会把这批插件全判成"没注册"。
            if (text.Contains("RegisterHandler", StringComparison.Ordinal)) hasRegister = true;
            if (hasXmlFunction && hasRegister) return;
        }

        if (hasXmlFunction && !hasRegister)
            problems.Add("声明了 [XmlFunction] 但全插件没有 RegisterHandler 调用，这些工具永远不会被 AI 看到");
    }

    // ------------------------------------------------------------------ 6. UI 基类调用

    /// <summary>
    /// 重写了 BuildRenderTree 却没调 base，框架默认配置面板不会渲染（文档 U-02）。
    ///
    /// 但这条只适用于「依赖默认面板」的入口型 UI。全自绘 UI（例如 QQSuite 的中文面板）
    /// 本来就自己渲染每个配置项，调用 base 反而会多出一套重复控件 —— 它有意不调。
    /// 判据：自绘 UI 必然大量读 Configuration.* 来画控件；入口型 UI 只是显示个链接，不会读。
    /// </summary>
    static void CheckUiBaseCall(string plugin, string[] sources, List<string> warnings)
    {
        foreach (string file in sources)
        {
            string name = Path.GetFileName(file);
            string text = SafeRead(file);
            if (!text.Contains("BuildRenderTree", StringComparison.Ordinal)) continue;
            if (!text.Contains("ModuleUIBase", StringComparison.Ordinal)) continue;
            if (text.Contains("base.BuildRenderTree", StringComparison.Ordinal)) continue;

            // Razor 生成的 .g.cs 本来就不调 base，这条约定对它不适用
            if (name.EndsWith("_razor.g.cs", StringComparison.OrdinalIgnoreCase)) continue;

            string stem = name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                ? name[..^5]
                : name;

            // 自绘 UI 的两种典型特征：
            //   a) 直接读了很多 Configuration.* 来画控件
            //   b) 大量 OpenElement / CloseElement —— 整页都是自己搭的
            // 有的自绘 UI 通过 Module 的属性间接读配置（比如 Module.UiXxx），
            // 这时第一条不成立，但第二条照样能认出来。
            if (CountOccurrences(text, "Configuration.") >= 3) continue;
            if (CountOccurrences(text, "OpenElement") >= 10) continue;

            warnings.Add($"{name} 重写 BuildRenderTree 但没调 base，" +
                         "框架默认的配置面板不会显示（若这是全自绘面板可忽略）");
        }
    }

    static int CountOccurrences(string text, string needle)
    {
        int count = 0, at = 0;
        while (at < text.Length)
        {
            at = text.IndexOf(needle, at, StringComparison.Ordinal);
            if (at < 0) break;
            count++;
            at += needle.Length;
        }
        return count;
    }

    // ------------------------------------------------------------------ 7. Content 模式

    /// <summary>
    /// FunctionMode.Content 在流式过程中会多次回调，必须靠 ctx.CallMode == Closing 闸住，
    /// 否则写文件/入库会重复执行（文档 T-03，P0）。
    /// </summary>
    static void CheckContentMode(string plugin, string[] sources, List<string> problems)
    {
        foreach (string file in sources)
        {
            string text = SafeRead(file);
            if (!text.Contains("FunctionMode.Content", StringComparison.Ordinal)) continue;
            if (text.Contains("CallMode.Closing", StringComparison.Ordinal)) continue;

            problems.Add($"{Path.GetFileName(file)} 有 Content 模式工具但没判断 CallMode.Closing，" +
                         "流式回复过程中会被重复调用（重复写文件/重复入库）");
        }
    }

    // ------------------------------------------------------------------ 工具

    /// <summary>这些名字太常见，出现次数低不代表没人用（可能通过反射/配置系统读取）。</summary>
    static bool IsCommonConfigName(string name)
        => name is "Enable" or "Enabled" or "Name" or "Id" or "Version" or "ConfigVersion"
            or "Title" or "Description" or "Note";

    /// <summary>按词边界数出现次数。用 IndexOf 而不是正则，74 个插件 × 几十个属性不能太慢。</summary>
    static int CountName(string text, string name)
    {
        int count = 0, index = 0;

        while (index < text.Length)
        {
            index = text.IndexOf(name, index, StringComparison.Ordinal);
            if (index < 0) break;

            bool leftOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            int end = index + name.Length;
            bool rightOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);

            if (leftOk && rightOk) count++;
            index = end;
        }

        return count;
    }

    static bool IsServiceType(string type)
    {
        string t = type.Replace("?", "").Trim();
        return t.StartsWith("ILogger", StringComparison.Ordinal)
            || t.StartsWith("Interactor<", StringComparison.Ordinal)
            || t.EndsWith("Service", StringComparison.Ordinal)
            || t.EndsWith("Store", StringComparison.Ordinal)
            || t.EndsWith("Module", StringComparison.Ordinal);
    }

    static string[] ReadSources(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch { return ""; }
    }

    static int LineOf(string text, int index)
        => text.Take(Math.Min(index, text.Length)).Count(c => c == '\n') + 1;

    /// <summary>取 index 所在的整行，用来判断这行是不是表达式体属性。</summary>
    static string LineAt(string text, int index)
    {
        int at = Math.Min(index, Math.Max(0, text.Length - 1));
        int start = text.LastIndexOf('\n', Math.Max(0, at - 1));
        int end = text.IndexOf('\n', at);
        start = start < 0 ? 0 : start + 1;
        end = end < 0 ? text.Length : end;
        return text[start..end];
    }

    static string PrecedingLines(string text, int index, int count)
    {
        int at = Math.Min(index, text.Length);
        string[] lines = text[..at].Split('\n');
        return string.Join('\n', lines.Skip(Math.Max(0, lines.Length - count - 1)));
    }

    static string? ReadBalanced(string text, int openIndex)
    {
        if (openIndex < 0 || openIndex >= text.Length) return null;
        char open = text[openIndex];
        char close = open == '{' ? '}' : ')';
        if (open != '{' && open != '(') return null;

        int depth = 0;
        bool inStr = false;
        char quote = '\0';

        for (int i = openIndex; i < text.Length && i < openIndex + 60000; i++)
        {
            char c = text[i];
            if (inStr)
            {
                if (c == '\\') { i++; continue; }
                if (c == quote) inStr = false;
                continue;
            }
            if (c is '"' or '\'') { inStr = true; quote = c; continue; }

            if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0) return text.Substring(openIndex + 1, i - openIndex - 1);
            }
        }
        return null;
    }
}
