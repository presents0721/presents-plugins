using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace presents.PluginTestKit;

internal sealed class XmlParam
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public bool HasDescription { get; set; }
    public bool Optional { get; set; }

    /// <summary>框架注入的参数（CancellationToken / 执行上下文 / 进度回调），AI 不需要也不该填。</summary>
    public bool Injected { get; set; }
}

internal sealed class XmlFunctionInfo
{
    public string Name { get; set; } = "";
    public bool ExplicitName { get; set; }
    public string Method { get; set; } = "";
    public string ReturnType { get; set; } = "";
    public string File { get; set; } = "";
    public int Line { get; set; }
    public bool HasDescription { get; set; }
    public List<XmlParam> Params { get; set; } = new();
}

/// <summary>
/// XmlFunction 冒烟：静态解析插件源码里的 [XmlFunction] 声明，检查 AI 调用时会踩的坑。
/// 这类问题编译期不报、运行时也不报，只有真的让 AI 调用才暴露，所以值得单独查。
/// </summary>
internal static class XmlFunctionChecks
{
    static readonly Regex Attr = new(@"\[XmlFunction\s*\(([^)]*)\)\]", RegexOptions.Compiled);
    static readonly Regex MethodHead = new(
        @"\b(?:public|internal)\s+(?:async\s+)?([\w<>,\[\]\?\.]+)\s+(\w+)\s*\(",
        RegexOptions.Compiled);
    static readonly Regex NameArg = new("\"([^\"]+)\"", RegexOptions.Compiled);
    static readonly Regex DescAttr = new(@"\[Description\s*\(", RegexOptions.Compiled);
    static readonly Regex LegalName = new(@"^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    public static List<TestResult> Run(string pluginsRoot)
    {
        List<TestResult> results = new();

        // 传进来的可能是「插件根目录」，也可能就是「单个插件目录」（单插件快测走的就是后者）
        IEnumerable<string> dirs;
        try
        {
            dirs = File.Exists(Path.Combine(pluginsRoot, "manifest.json"))
                ? new[] { pluginsRoot }
                : Directory.EnumerateDirectories(pluginsRoot);
        }
        catch { return results; }

        foreach (string dir in dirs)
        {
            string plugin = Path.GetFileName(dir);
            List<XmlFunctionInfo> funcs;
            try { funcs = Extract(dir); }
            catch { continue; }

            if (funcs.Count == 0) continue;

            List<string> problems = new();
            List<string> warnings = new();

            // 1. 同一插件内工具重名 —— AI 只能调到其中一个
            foreach (IGrouping<string, XmlFunctionInfo> g in funcs
                         .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
                problems.Add($"工具名重复「{g.Key}」：{string.Join("、", g.Select(x => x.Method))}");

            foreach (XmlFunctionInfo f in funcs)
            {
                // 脚手架/代码生成器里的模板字符串不是真实工具名（如 {{code}}_ping），
                // 按普通规则校验会一直误报。
                if (f.Name.Contains("{{", StringComparison.Ordinal) ||
                    f.Name.Contains("}}", StringComparison.Ordinal))
                    continue;

                string where = $"{f.File}:{f.Line}";

                // 2. 工具名合法性
                if (!LegalName.IsMatch(f.Name))
                    problems.Add($"「{f.Name}」含非法字符（只允许字母数字下划线短横线）· {where}");

                // 3. 方法级说明
                if (!f.HasDescription)
                    warnings.Add($"「{f.Name}」的方法没有 [Description]，AI 不知道什么时候该用它 · {where}");

                // 4. 参数检查（只算 AI 真正要填的，注入参数跳过）
                List<XmlParam> visible = f.Params.Where(x => !x.Injected).ToList();

                if (visible.Count > 5)
                    warnings.Add($"「{f.Name}」有 {visible.Count} 个参数，AI 容易漏填 · {where}");

                foreach (XmlParam p in visible)
                {
                    if (!IsAiFriendly(p.Type))
                    {
                        // 分两档：object / Dictionary 这类 AI 根本生成不了，算硬问题；
                        // 枚举、DateTime 这些只是要特定格式，通常还能转换，算提示。
                        if (IsHardType(p.Type))
                            problems.Add($"「{f.Name}」参数 {p.Name} 的类型 {p.Type} AI 无法生成实参（建议 string / int / bool / string[]）· {where}");
                        else
                            warnings.Add($"「{f.Name}」参数 {p.Name} 的类型 {p.Type} 需要特定格式，AI 可能填错 · {where}");
                    }

                    if (!p.HasDescription)
                        warnings.Add($"「{f.Name}」参数 {p.Name} 没有 [Description]，AI 只能靠名字猜含义 · {where}");
                }
            }

            string scope = plugin;
            if (problems.Count > 0)
                results.Add(TestFactory.Fail(
                    "xml." + plugin, "XmlFunction 契约", "工具", scope, 0,
                    $"{funcs.Count} 个工具，{problems.Count} 个问题", problems.Concat(warnings).ToList()));
            else if (warnings.Count > 0)
                results.Add(TestFactory.Fail(
                    "xml." + plugin, "XmlFunction 契约", "工具", scope, 0,
                    $"{funcs.Count} 个工具，{warnings.Count} 条提示", warnings, warning: true));
            else
                results.Add(TestFactory.Ok(
                    "xml." + plugin, "XmlFunction 契约", "工具", scope, 0,
                    $"{funcs.Count} 个工具，写法规范"));
        }

        return results;
    }

    /// <summary>
    /// 框架自动注入、AI 不参与生成的参数类型。
    /// 这些出现在签名里是正常写法，不该算作「类型不适合 AI」。
    /// </summary>
    static bool IsInjected(string type)
    {
        string t = type.Replace("?", "").Trim();
        return t is "CancellationToken" or "XmlExecutorContext" or "XmlContext"
            || t.Contains("XmlExecutorContext", StringComparison.Ordinal)
            || t.Contains("CancellationToken", StringComparison.Ordinal)
            || t.StartsWith("IProgress<", StringComparison.Ordinal);
    }

    /// <summary>AI 连"大概能写成什么"都无法确定的类型：对象、字典、二进制流、委托。</summary>
    static bool IsHardType(string type)
    {
        string t = type.Replace("?", "").Trim();
        return t is "object" or "dynamic" or "byte[]" or "Stream" or "FileStream"
            || t.StartsWith("Dictionary<", StringComparison.Ordinal)
            || t.StartsWith("IDictionary<", StringComparison.Ordinal)
            || t.StartsWith("Action", StringComparison.Ordinal)
            || t.StartsWith("Func<", StringComparison.Ordinal);
    }

    /// <summary>AI 能稳定生成实参的类型：基础类型、字符串、以及字符串集合。</summary>
    static bool IsAiFriendly(string type)
    {
        string t = type.Replace("?", "").Replace(" ", "").Trim();
        if (t.Length == 0) return false;

        if (t is "string" or "int" or "long" or "double" or "float" or "decimal" or "bool") return true;
        if (t is "string[]" or "int[]" or "long[]" or "double[]") return true;

        return t.StartsWith("List<string>", StringComparison.Ordinal)
            || t.StartsWith("IEnumerable<string>", StringComparison.Ordinal)
            || t.StartsWith("IReadOnlyList<string>", StringComparison.Ordinal)
            || t.StartsWith("IList<string>", StringComparison.Ordinal);
    }

    static List<XmlFunctionInfo> Extract(string pluginDir)
    {
        List<XmlFunctionInfo> list = new();

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(pluginDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"));
        }
        catch { return list; }

        foreach (string file in files)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            foreach (Match m in Attr.Matches(text))
            {
                int attrEnd = m.Index + m.Length;

                // 特性后面可能还有别的特性（[Description] 等），跳过去找方法签名
                Match head = MethodHead.Match(text, attrEnd);
                if (!head.Success) continue;
                if (head.Index - attrEnd > 400) continue;   // 太远，不是这个特性的方法

                string between = text.Substring(attrEnd, head.Index - attrEnd);
                bool hasDesc = DescAttr.IsMatch(between);

                int open = head.Index + head.Length - 1;
                string? argText = ReadBalanced(text, open);
                if (argText == null) continue;

                string method = head.Groups[2].Value;
                string returnType = head.Groups[1].Value;

                Match nameMatch = NameArg.Match(m.Groups[1].Value);
                string name = nameMatch.Success ? nameMatch.Groups[1].Value : method;

                list.Add(new XmlFunctionInfo
                {
                    Name = name,
                    ExplicitName = nameMatch.Success,
                    Method = method,
                    ReturnType = returnType,
                    File = Path.GetFileName(file),
                    Line = text.Take(m.Index).Count(c => c == '\n') + 1,
                    HasDescription = hasDesc,
                    Params = ParseParams(argText)
                });
            }
        }

        return list;
    }

    /// <summary>从 '(' 开始取到配对的 ')'，跳过字符串字面量。</summary>
    static string? ReadBalanced(string text, int openIndex)
    {
        if (openIndex < 0 || openIndex >= text.Length || text[openIndex] != '(') return null;

        int depth = 0;
        bool inString = false;
        char quote = '\0';

        for (int i = openIndex; i < text.Length && i < openIndex + 4000; i++)
        {
            char c = text[i];

            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == quote) inString = false;
                continue;
            }

            if (c is '"' or '\'') { inString = true; quote = c; continue; }

            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return text.Substring(openIndex + 1, i - openIndex - 1);
            }
        }
        return null;
    }

    static List<XmlParam> ParseParams(string args)
    {
        List<XmlParam> list = new();
        if (string.IsNullOrWhiteSpace(args)) return list;

        foreach (string raw in SplitTopLevel(args))
        {
            string p = raw.Trim();
            if (p.Length == 0) continue;

            bool hasDesc = DescAttr.IsMatch(p);

            // 整体剥离特性：Description 里可能含括号和引号，正则截不干净会污染类型
            p = StripAttributes(p).Trim();

            bool optional = p.Contains('=');
            if (optional) p = p[..p.IndexOf('=')].Trim();

            string[] parts = p.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            string type = parts[0];
            list.Add(new XmlParam
            {
                Type = type,
                Name = parts[^1],
                HasDescription = hasDesc,
                Optional = optional,
                Injected = IsInjected(type)
            });
        }
        return list;
    }

    /// <summary>删掉所有 [Xxx(...)] 特性，按括号配平，忽略字符串里的括号。</summary>
    static string StripAttributes(string s)
    {
        StringBuilder sb = new();
        int i = 0;

        while (i < s.Length)
        {
            if (s[i] != '[') { sb.Append(s[i]); i++; continue; }

            int depth = 0;
            bool inStr = false;
            char quote = '\0';
            int start = i;

            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == quote) inStr = false;
                    continue;
                }
                if (c is '"' or '\'') { inStr = true; quote = c; continue; }
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) { i++; break; }
                }
            }

            if (i == start) i++;   // 防御：没推进就强行前进
        }

        return sb.ToString();
    }

    static List<string> SplitTopLevel(string s)
    {
        List<string> list = new();
        int depth = 0;
        bool inString = false;
        char quote = '\0';
        StringBuilder cur = new();

        foreach (char c in s)
        {
            if (inString)
            {
                cur.Append(c);
                if (c == quote) inString = false;
                continue;
            }

            if (c is '"' or '\'') { inString = true; quote = c; cur.Append(c); continue; }

            if (c is '(' or '<' or '[') depth++;
            else if (c is ')' or '>' or ']') depth--;

            if (c == ',' && depth == 0) { list.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(c);
        }
        if (cur.Length > 0) list.Add(cur.ToString());
        return list;
    }
}
