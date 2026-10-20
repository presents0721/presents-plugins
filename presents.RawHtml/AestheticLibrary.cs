using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace presents.RawHtml;

/// <summary>一个美学风格的元信息（从风格文档 .md 里解析，界面与提示词共用）。</summary>
public record StyleInfo(
    string Slug,
    string Name,
    string Scene,
    string Tags,
    string Palette,
    string[] Colors,
    string Fonts,
    string Desc,
    bool Builtin,
    string Path);

/// <summary>
/// 美学风格库 + 字体库 + 提示词协议构建。
///
/// 目录约定：
///   - 内置风格：&lt;插件目录&gt;/Resources/RawHtml/styles/*.md （随插件分发，来自 dsh-raw-html-v2）
///   - 自定义风格：Storage/&lt;角色&gt;/RawHtml/Styles/*.md（面板里新建/编辑，可写；改动不丢）
///   - 外置风格：配置里的 ExtraStyleDirs（只读挂载）
/// 风格文档格式与 DSH 版一致（标题/适用场景/标签/色板/字体 + ## 小节），因此两边风格包可以互相拷贝。
/// </summary>
public class AestheticLibrary(string builtinStylesDir, string builtinDocsDir, string[] writableDirs, string[] extraDirs, string fontsDir)
{
    public string BuiltinStylesDir => builtinStylesDir;
    public string DocsDir => builtinDocsDir;
    public string FontsDir => fontsDir;
    public IReadOnlyList<string> WritableDirs => writableDirs;

    // ---------- 扫描 / 解析 ----------

    public List<StyleInfo> ListStyles()
    {
        List<StyleInfo> list = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string dir, bool builtin) in EnumerateDirs())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string slug = Path.GetFileNameWithoutExtension(file);
                if (slug.StartsWith('_')) continue;               // _INDEX/_BASELINE/_FONTS 等工具文件不是风格
                if (seen.Add(slug) == false) continue;            // 同名以更靠前的目录为准（自定义优先于内置）
                try { list.Add(ParseStyle(file, builtin)); }
                catch { /* 单个文档坏了不影响整体 */ }
            }
        }
        return list.OrderByDescending(s => s.Builtin).ThenBy(s => s.Slug, StringComparer.OrdinalIgnoreCase).ToList();
    }

    IEnumerable<(string Dir, bool Builtin)> EnumerateDirs()
    {
        foreach (string d in writableDirs) yield return (d, false);   // 自定义优先：同名可覆盖内置
        foreach (string d in extraDirs) yield return (d, false);
        yield return (builtinStylesDir, true);
    }

    /// <summary>按 slug 找到风格文档路径（自定义 → 外置 → 内置）。</summary>
    public string? FindStyleFile(string slug)
    {
        slug = NormalizeSlug(slug);
        if (slug.Length == 0) return null;
        foreach ((string dir, bool _) in EnumerateDirs())
        {
            string file = Path.Combine(dir, slug + ".md");
            if (File.Exists(file)) return file;
        }
        return null;
    }

    public string? ReadStyleDoc(string slug)
    {
        string? file = FindStyleFile(slug);
        if (file == null) return null;
        try { return File.ReadAllText(file); } catch { return null; }
    }

    public StyleInfo? FindStyle(string slug) =>
        ListStyles().FirstOrDefault(s => string.Equals(s.Slug, NormalizeSlug(slug), StringComparison.OrdinalIgnoreCase));

    /// <summary>读内置的工具文档（_INDEX.md / _BASELINE.md / _FONTS.md，以及 DESIGN.md 等）。</summary>
    public string? ReadBuiltinDoc(string name)
    {
        foreach (string dir in new[] { builtinStylesDir, builtinDocsDir })
        {
            string file = Path.Combine(dir, name);
            try { if (File.Exists(file)) return File.ReadAllText(file); } catch { /* 忽略 */ }
        }
        return null;
    }

    // ---------- 自定义风格：写 / 删 ----------

    public bool IsBuiltin(string slug) =>
        File.Exists(Path.Combine(builtinStylesDir, NormalizeSlug(slug) + ".md"));

    /// <summary>把风格文档写到第一个可写目录（Storage/&lt;角色&gt;/RawHtml/Styles）。返回错误信息，null=成功。</summary>
    public string? WriteStyleDoc(string slug, string markdown)
    {
        slug = NormalizeSlug(slug);
        if (slug.Length == 0) return "风格 slug 不能为空（只允许 a-z 0-9 - _）";
        if (writableDirs.Length == 0) return "没有可写的自定义风格目录";
        try
        {
            string dir = writableDirs[0];
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, slug + ".md"), markdown, new UTF8Encoding(false));
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>删除自定义风格（内置风格不可删）。返回错误信息，null=成功。</summary>
    public string? DeleteStyle(string slug)
    {
        slug = NormalizeSlug(slug);
        foreach (string dir in writableDirs)
        {
            string file = Path.Combine(dir, slug + ".md");
            if (File.Exists(file))
            {
                try { File.Delete(file); return null; }
                catch (Exception ex) { return ex.Message; }
            }
        }
        return IsBuiltin(slug) ? "内置风格不支持删除（可在面板里新建同名风格覆盖它）" : "未找到该自定义风格";
    }

    /// <summary>按面板表单生成一份风格文档（结构对齐 DSH 版，便于互相拷贝）。</summary>
    public static string BuildStyleDoc(string slug, string name, string scene, string tags, string colors, string fonts, string notes)
    {
        string[] palette = SplitTokens(colors).Take(8).ToArray();
        string colorLine = palette.Length > 0 ? string.Join(" ", palette) : "#F0EFEB #1C1C1A #334EAC";
        StringBuilder sb = new();
        sb.AppendLine($"# {name}（{slug}）");
        sb.AppendLine();
        sb.AppendLine($"> 适用场景：{(string.IsNullOrWhiteSpace(scene) ? "通用" : scene.Trim())}");
        sb.AppendLine($"> 标签：{(string.IsNullOrWhiteSpace(tags) ? "自定义" : tags.Trim())}");
        sb.AppendLine($"> 色板（推荐 3-5 色，按重要性排列）：{colorLine}");
        sb.AppendLine($"> 字体（标题/副标题/正文/装饰）：{(string.IsNullOrWhiteSpace(fonts) ? "标题：Lanxi-鱼尾行书；正文：系统无衬线" : fonts.Trim())}");
        sb.AppendLine();
        sb.AppendLine("> 继承公约：本风格继承常驻【审美底线·公约】（对齐成网/一屏一焦点/明度即层级/吝啬即重量/留白分卡/色彩 60-30-10/字体三员/图必有义/禁 Emoji/数据诚实/字级阶梯），以下只写差异化准则。");
        sb.AppendLine();
        sb.AppendLine("## 这是什么");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(notes)
            ? $"本风格的核心命题：用统一的视觉语言，把内容装进一种「{(string.IsNullOrWhiteSpace(scene) ? "独特" : scene.Trim())}」的气质里。"
            : notes.Trim());
        sb.AppendLine();
        sb.AppendLine("## 判断准则");
        sb.AppendLine();
        sb.AppendLine("1. 一眼能说出「这是什么风格」——差异化来自明确的材质感与版式骨架，不是堆装饰。");
        sb.AppendLine("2. 删掉装饰内容仍成立：装饰只强化气质，不承担信息。");
        sb.AppendLine("3. 色彩 60-30-10 且全卡 ≤5 色：主色铺底、次色分区、强调色全卡至多一处。");
        sb.AppendLine();
        sb.AppendLine("## 色板");
        sb.AppendLine();
        sb.AppendLine("| 角色 | 色值 | 用法 |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine($"| 底色 | `{First(palette, 0, "#F0EFEB")}` | 卡片背景/大面铺底 |");
        sb.AppendLine($"| 墨色 | `{First(palette, 1, "#1C1C1A")}` | 正文与标题 |");
        sb.AppendLine($"| 次色 | `{First(palette, 2, "#334EAC")}` | 分区/次级信息 |");
        sb.AppendLine($"| 强调 | `{First(palette, 3, "#D4A017")}` | 全卡至多一处点睛 |");
        sb.AppendLine();
        sb.AppendLine("## 骨架");
        sb.AppendLine();
        sb.AppendLine("badge 胶囊 → 结论标题 → 副题/图例（`·` 分隔）→ 主体（数据/图文/图表）→ 来源行。留白分卡，不靠边框阴影堆层级。");
        sb.AppendLine();
        sb.AppendLine("## 字体");
        sb.AppendLine();
        sb.AppendLine((string.IsNullOrWhiteSpace(fonts) ? "标题：Lanxi-鱼尾行书；正文：系统无衬线；数字：serif + tabular-nums" : fonts.Trim()));
        sb.AppendLine();
        sb.AppendLine("## 点睛技法");
        sb.AppendLine();
        sb.AppendLine("- 在 `<style>` 里用 `#vcp-root .t{...}` 类规则锁字体，不要写子元素行内 font-family。");
        sb.AppendLine("- 用 `data-vcp-preset` 或 `--vcp-*` 变量统一色彩，避免逐个手写 hex。");
        sb.AppendLine("- 数据用可数单位图型（点阵/刻度环/发丝线），图表不断轴。");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"> 用户自定义风格 · {DateTime.Now:yyyy-MM-dd}");
        return sb.ToString();
    }

    public static string NormalizeSlug(string? raw)
    {
        string s = (raw ?? "").Trim().ToLowerInvariant();
        s = Regex.Replace(s, @"[^a-z0-9\-_]+", "-");
        return s.Trim('-');
    }

    static IEnumerable<string> SplitTokens(string raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([' ', ',', '、', ';', '，', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static string First(string[] arr, int index, string fallback) => index < arr.Length ? arr[index] : fallback;

    // ---------- 解析（对齐 DSH lib/index.js 的 parseStyleMeta） ----------

    static StyleInfo ParseStyle(string file, bool builtin)
    {
        string raw = File.ReadAllText(file);
        string slug = Path.GetFileNameWithoutExtension(file);
        string title = Regex.Match(raw, @"^#\s+(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
        if (title.Length == 0) title = slug;
        Match nameMatch = Regex.Match(title, @"^(.+?)\s*[（(]");
        string name = StripMarks(nameMatch.Success ? nameMatch.Groups[1].Value : title);
        string scene = StripMarks(Regex.Match(raw, @"适用场景[：:]\s*(.+)").Groups[1].Value);
        string tags = StripMarks(Regex.Match(raw, @"标签[：:]\s*(.+)").Groups[1].Value);
        string palette = StripMarks(Regex.Match(raw, @"色板[^）)]*[)）]?\s*[：:]?\s*(.+)").Groups[1].Value);
        string[] colors = Regex.Matches(raw, @"#[0-9a-fA-F]{3,8}\b")
            .Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        string fonts = StripMarks(Regex.Match(raw, @"字体[（(][^）)]*[)）]\s*[：:]?\s*(.+)").Groups[1].Value);
        if (fonts.Length == 0) fonts = StripMarks(Regex.Match(raw, @"字体[：:]\s*(.+)").Groups[1].Value);
        string desc = Section(raw, "这是什么");
        if (desc.Length == 0) desc = Section(raw, "判断准则");
        return new StyleInfo(slug, name, scene, tags, palette, colors, fonts, desc, builtin, file);
    }

    static string Section(string raw, string heading)
    {
        Match m = Regex.Match(raw, @"##\s*" + Regex.Escape(heading) + @"[^\n]*\n+([\s\S]*?)(?=\n##\s|$)");
        return m.Success ? CleanMarkdown(m.Groups[1].Value) : "";
    }

    /// <summary>去掉元信息行里的 Markdown 记号与多余空白（面板卡片一行显示，不该露出 ** ` ---）。</summary>
    static string StripMarks(string text)
    {
        string s = (text ?? "")
            .Replace("**", "").Replace("`", "").Replace("\r", "").Replace("\n", " ")
            .Replace("\u3000", " ");
        s = Regex.Replace(s, @"-{3,}", " ");
        s = Regex.Replace(s, @"[ \t]+", " ");
        return s.Trim(' ', '|', '>', '-');
    }

    /// <summary>把文档节选里的 Markdown 记号去掉，供面板卡片直接显示（界面里不该露出 ** 和反引号）。</summary>
    static string CleanMarkdown(string text)
    {
        string s = text
            .Replace("**", "").Replace("`", "").Replace("\r", "")
            .Replace("\u3000", " ");
        s = Regex.Replace(s, @"^\s*>\s?", "", RegexOptions.Multiline);   // 引用前缀
        s = Regex.Replace(s, @"^\s*[-*+]\s+", "", RegexOptions.Multiline); // 列表符号
        s = Regex.Replace(s, @"[ \t]+", " ");
        s = Regex.Replace(s, @"\n{2,}", "\n");
        s = s.Replace("\n", " ");                                        // 卡片里单行显示更整齐
        return s.Trim();
    }

    // ---------- 面板 JSON ----------

    public string BuildStateJson(bool render, bool aesthetic, string lockedSlug,
        List<KeyValuePair<string, string>>? history = null, string? saveDir = null, bool autoSave = true, string? error = null)
    {
        List<StyleInfo> styles = ListStyles();
        StyleInfo? locked = lockedSlug.Length == 0 ? null : styles.FirstOrDefault(s => s.Slug.Equals(lockedSlug, StringComparison.OrdinalIgnoreCase));
        object payload = new {
            render,
            aesthetic,
            locked = locked?.Slug ?? "",
            lockedName = locked?.Name ?? "",
            styles = styles.Select(s => new {
                s.Slug, s.Name, s.Scene, s.Tags, s.Palette, s.Colors, s.Fonts, s.Desc, s.Builtin,
                locked = s.Slug.Equals(lockedSlug, StringComparison.OrdinalIgnoreCase)
            }).ToArray(),
            fonts = ListFonts(),
            history = (history ?? []).Select(h => new { ts = h.Key, html = h.Value }).ToArray(),
            customDir = writableDirs.Length > 0 ? writableDirs[0] : "",
            builtinDir = builtinStylesDir,
            saveDir = saveDir ?? "",
            autoSave,
            error
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public List<string> ListFonts()
    {
        try
        {
            string manifest = Path.Combine(Path.GetDirectoryName(fontsDir)!, "fonts.json");
            if (File.Exists(manifest))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.TryGetProperty("fonts", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                    return arr.EnumerateArray()
                        .Select(f => f.TryGetProperty("alias", out JsonElement a) ? a.GetString() ?? "" : "")
                        .Where(s => s.Length > 0).ToList();
            }
        }
        catch { /* 退化为目录扫描 */ }

        try
        {
            if (Directory.Exists(fontsDir))
                return Directory.GetFiles(fontsDir, "*.woff2")
                    .Select(f => Path.GetFileNameWithoutExtension(f))
                    .Select(n => n.StartsWith("Lanxi-") ? n : "Lanxi-" + n)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { /* 忽略 */ }
        return [];
    }

    // ---------- 导出：自包含 HTML（@font-face 内嵌用）----------

    /// <summary>把字体/图片文件读成 data URI（导出文件可脱机打开）。读不到时返回空串，调用方会退回原始 url。</summary>
    public static string DataUri(string path, string mime)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
        }
        catch { return ""; }
    }

    /// <summary>为卡片里实际用到的 Lanxi 字体生成内嵌 @font-face（只算用到的）。</summary>
    public string FontFaceCss(IEnumerable<string> aliases)
    {
        StringBuilder sb = new();
        foreach (string alias in aliases.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? file = FindFontFile(alias);
            if (file == null) continue;
            sb.AppendLine("@font-face{font-family:'" + alias + "';src:url(" + DataUri(file, "font/woff2") + ");font-display:swap;}");
        }
        return sb.ToString();
    }

    /// <summary>按别名定位字体文件（别名即文件名，如 Lanxi-叮叮）。</summary>
    public string? FindFontFile(string alias)
    {
        string name = alias.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase) ? alias : alias + ".woff2";
        string direct = Path.Combine(fontsDir, name);
        if (File.Exists(direct)) return direct;
        if (name.StartsWith("Lanxi-", StringComparison.OrdinalIgnoreCase))
        {
            string stripped = Path.Combine(fontsDir, name["Lanxi-".Length..]);
            if (File.Exists(stripped)) return stripped;
        }
        return null;
    }

    /// <summary>从卡片 HTML 里抽取所有 Lanxi-* 字体别名。</summary>
    public static string[] ExtractFontAliases(string html)
    {
        if (string.IsNullOrEmpty(html)) return [];
        return Regex.Matches(html, @"Lanxi-[A-Za-z0-9_\-\u4e00-\u9fff]+")
            .Select(m => m.Value.Trim('\''))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    static readonly JsonSerializerOptions JsonOptions = new() {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase   // 面板 JS 按 camelCase 读取
    };

    // ---------- 提示词协议 ----------

    /// <summary>渲染层协议（渲染开关打开时注入）。</summary>
    public string BuildRenderProtocol(bool includeDocPaths = true)
    {
        StringBuilder sb = new();
        sb.AppendLine("## VCP 视觉通感协议（已启用）");
        sb.AppendLine("你具备把内容做成视觉容器（裸 HTML/CSS/SVG）的能力，但**默认用纯文字回答**。只有当内容本身明显受益于结构化 / 可视化呈现时才调用 <RenderHtmlCard>——不要把普通对话、问答、闲聊转成卡片，也不要为了好看而硬套视觉容器。");
        sb.AppendLine();
        sb.AppendLine("【交付方式 · 最高优先级】");
        sb.AppendLine("- 视觉内容一律写成裸 HTML，并整段包进 <RenderHtmlCard>…</RenderHtmlCard>（唯一交付方式，面板会立刻渲染）。");
        sb.AppendLine("- 一次调用 = 一张卡；多张卡就多次调用。标签内只放 HTML：不要 ``` 围栏、不要缩进成代码块、不要夹杂解释文字。");
        sb.AppendLine("- 不要把 HTML 写进 .html 文件再丢路径（用户要的是画面本身）；只有用户明确要求导出文件时才写文件，正文仍要渲染核心内容。");
        sb.AppendLine("- 卡片是自包含文档：做汇总 / 对比 / 说明类卡片时，把「该部分的详细说明、文学性文字」写进同一张卡（用 <p>/<h2>/<ul> 等排版），和它配套的图表 / 表格 / 公式放在一起——不要只丢一个光秃秃的图、把解释文字留在聊天窗口（否则文字和图分家）。但**长报告（如技术汇总）绝不塞进一张巨卡**：按章节拆成多张卡（一次调用一张、多次调用多张），每张只承载一个主题（背景 / 架构对比 / 数据图 / 结论…），单卡高度保持适中。面板里它们是一串可滚动的「文字+图」整卡；发 QQ 时按需挑对应几张发，避免生成一张又高又糊的大长图。");
        sb.AppendLine("- 把卡片发到 QQ 群时，**只调用 SendCardToQQ 函数**（它会自动把面板最近一张卡栅格化成图片文件、再用 QImage 发群）。**绝对不要用 Python/PIL/代码自己重画卡片当图发**——那会丢失真实的渲染效果（字体、Mermaid、KaTeX、配色全错），而且栅格化本身已经够用。若 SendCardToQQ 报错（如卡片含跨域外链图导致导出失败），如实告诉用户原因，也不要退回去用 PIL 画。");
        sb.AppendLine("- 同一张卡片只渲染一次：渲染成功后不要因为系统提示「未使用函数调用」而重复渲染同一张卡；不需要调用任何函数时直接输出 <!---->。");
        sb.AppendLine();
        sb.AppendLine("【何时用视觉 · 默认不用】只有满足下面任一情形才出卡，其余一律纯文字：");
        sb.AppendLine("汇总 / 列表 / 对比 / 排名 / 进度 → 卡片、表格、层级、SVG 图表；");
        sb.AppendLine("数理 / 推导 / 公式 → KaTeX（$…$ 与 $$…$$）；流程 / 结构 / 关系 → Mermaid（<pre class=\"language-mermaid\"><code>…</code></pre>）；");
        sb.AppendLine("文学 / 故事 / 情感 → 纸质纹理 + 衬线 + SVG 装帧；警告 / 错误 / 系统状态 → 高对比警示色。");
        sb.AppendLine("【何时不用】闲聊、问答、一句两句能说清的小事、纯文本讨论、以及用户没要求视觉化时——全部纯文字，不出卡。出卡门槛要高，宁少勿滥。");
        sb.AppendLine();
        sb.AppendLine("【结构铁律（保「不崩」）】");
        sb.AppendLine("- 根容器：<div id=\"vcp-root\" style=\"背景/字色/字体族/字号（只放短的关键值）\">，长样式全部写进紧随其后的 <style>。");
        sb.AppendLine("- 卡片用固定基准宽度设计：#vcp-root 写死一个宽度（建议 width:760px，可在此附近微调），内部照常用 px 正常排版即可。不要写 width:100% / max-width 让卡片自适应面板——渲染层会按面板宽度把整张卡片等比 scale 贴合（左右统一留白、面板变大卡片放大、变小缩小）。");
        sb.AppendLine("- 卡头/标题克制（硬性）：禁止在卡片顶部强加伪命令行/文件名标题条（如 `CNN.exe --intro`、`NOEL.exe --neon-mode-ON`、`user@host:~$ ./connect`），也禁止在卡片任意角落加 `status: online` 之类的伪状态角标——渲染层会把这些一律当噪音直接清除，你加了也是白加。真正的终端/代码内容用 <pre><code> 承载；普通卡片标题用正常视觉标题（badge 胶囊 → 结论标题），不要伪装成命令提示符。");
        sb.AppendLine("- 选择器一律以 #vcp-root 前缀书写（渲染层会作用域化成 #vcp-msg-N，隔离其它卡；裸类名不受保护）。");
        sb.AppendLine("- vcp-root 内不出现空行；<style> 内一条规则一行、行间无空行；根容器与设宽容器 box-sizing:border-box。");
        sb.AppendLine("- 字体：Lanxi-* 已全局注册，直接写 font-family:'Lanxi-XXX'（不要自建 @font-face）；子元素字体要写进 `#vcp-root .x{font-family:'Lanxi-XX'}` 类规则，");
        sb.AppendLine("  不要写进子元素行内 style（会被字体链兜底压成系统黑体）；只有根容器开标签可以内联 font-family。");
        sb.AppendLine("- 交互：按钮用 onclick=\"window.RawHtmlBridge.sendToHost('文本')\"（点击会作为一条消息回到对话）；能零 JS 就零 JS（<details>、radio）；");
        sb.AppendLine("  卡内 <script> 在可信模式下会执行但不是默认选择，脚本要写在 </div> 之后。");
        sb.AppendLine("- 禁：backdrop-filter、flex-wrap:wrap、margin:0 auto、入场 transform 位移与 animation-delay、Emoji（装饰图标用内联 SVG）、");
        sb.AppendLine("  position:fixed、z-index≥1000、CSS > 约 200 行、类选择器 > 12 个；图片用 <img>（卡内不解析 Markdown 图片语法）；SVG 必带 width/height + viewBox。");
        sb.AppendLine("- 代码用 <pre><code>，代码里的尖括号必须转义（< 写 &lt;、> 写 &gt;）。");
        sb.AppendLine("- 深色容器内不要出现白块亮块抢戏；正文对比度 ≥ 4.5:1。");
        sb.AppendLine();
        sb.AppendLine("【声明式配色（免写 hex）】根容器加 data-vcp-preset=\"editorial|chiaroscuro|fauvism|cyberpunk|wabi_sabi\"");
        sb.AppendLine("（可选 data-vcp-mode）即自动生成整套 --vcp-* 色板（调色引擎已内置）；也可自定义 --vcp-* 变量。");
        if (includeDocPaths && File.Exists(Path.Combine(builtinDocsDir, "DESIGN.md")))
        {
            sb.AppendLine();
            sb.AppendLine("【细节手册 · 需要时自己去读】");
            sb.AppendLine($"- 技术与安全铁律：{Path.Combine(builtinDocsDir, "DESIGN.md")}");
            sb.AppendLine($"- 图型库：{Path.Combine(builtinDocsDir, "EDITORIAL.md")}｜故事装帧：{Path.Combine(builtinDocsDir, "FRAMING.md")}｜交互细则：{Path.Combine(builtinDocsDir, "VCP-INTERACTIONS.md")}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>美学层协议（美学注入打开时注入；lockedSlug 非空则内联该风格文档全文）。</summary>
    public string BuildAestheticProtocol(string lockedSlug)
    {
        StringBuilder sb = new();
        sb.AppendLine("【VCP 美学系统（已启用）】");
        sb.AppendLine("先感受再动笔：材质（信纸/黑板/墨入水/终端）、光（晨光/烛光/屏幕光）、心绪（安心/惊叹/共鸣/行动）——从感受选风格，从心绪定结构。");
        sb.AppendLine("美学注入开启 = 在「值得呈现」的内容上多给一点形式感，但**不降低出卡门槛**：仍按上面「何时用视觉」判定，普通对话、问答、琐碎内容一律纯文字，不要为了美感硬套视觉容器。");
        sb.AppendLine();
        sb.AppendLine("【审美底线 · 公约】（内化即可，不逐条核对）");
        sb.AppendLine("对齐成网（间距取 4/8 步长）· 一屏一焦点 · 明度即层级（最重要=最深墨/最亮光）· 吝啬即重量（强调色全卡至多一处）·");
        sb.AppendLine("留白分卡（分组靠间距，不堆边框阴影）· 色彩 60-30-10（主六成/次三成/强调一成，全卡 ≤5 色）· 字体三员（标题艺术体 + 正文系统无衬线 + 数字衬线，全卡 ≤3 种）·");
        sb.AppendLine("图必有义（删掉装饰内容仍成立即多余）· 禁 Emoji · 数据诚实（聚合摊回可数单位、图表不断轴）· 字级阶梯（主标题>副标题>正文>后记>装饰）。");
        sb.AppendLine();
        sb.AppendLine("【可用字体（本地已注册，直接写 font-family:'Lanxi-XXX'）】");
        sb.AppendLine("- 标题/艺术：Lanxi-鱼尾行书（刊头首选）· Lanxi-狂侠体 · Lanxi-超粗黑 · Lanxi-春兰茅坤 · Lanxi-点黑 · Lanxi-静黑超细 · Lanxi-MaShanZheng");
        sb.AppendLine("- 宋体系：Lanxi-朗宋（标题）· Lanxi-品宋（副题）· Lanxi-颜宋 · Lanxi-可宋（宋体正文场景）");
        sb.AppendLine("- 黑体/正文：系统无衬线（默认）· Lanxi-HeiTi / Lanxi-HeiTiLight / Lanxi-HeiTiBold；数字用 serif + tabular-nums");
        sb.AppendLine("- 手账/萌系（仅限手写手账语境，禁用于数据/商务/学术）：Lanxi-秀英体 · Lanxi-暗恋初夏 · Lanxi-初心少女 · Lanxi-丫丫体 · Lanxi-喵喵 · Lanxi-叮叮 · Lanxi-卡通");
        sb.AppendLine("- 楷书：Lanxi-WenKai / Lanxi-WenKaiLight（机械楷，不算手写感）｜英文花体：Lanxi-GreatVibes｜繁体竖排：Lanxi-鱼尾行书繁（必须配繁体内容）");
        List<string> fonts = ListFonts();
        if (fonts.Count > 0) sb.AppendLine($"- 全部别名（{fonts.Count} 款）：{string.Join("、", fonts)}");
        sb.AppendLine();
        sb.AppendLine("【字体三戒】克制（单卡最多 1-2 款艺术字，正文永不艺术字）· 对味（字体气质匹配内容气质）· 防呆（文字声明无需手动 !important，渲染层已自动 boost）。");
        sb.AppendLine();

        string slug = NormalizeSlug(lockedSlug);
        string? doc = slug.Length == 0 ? null : ReadStyleDoc(slug);
        if (doc != null)
        {
            StyleInfo? info = FindStyle(slug);
            sb.AppendLine($"【风格锁定】用户已锁定风格「{info?.Name ?? slug}」(slug: {slug})——本会话所有视觉输出一律以它为主：");
            sb.AppendLine("无论主题是文学、数据还是工程审查，都按它的色板、骨架、字体、技法组织每一张卡，不得因「主题不匹配」「任务偏理性」等观感自行回退或更换。");
            sb.AppendLine("风格文档全文如下（写卡前内化，不要逐条照抄参数）：");
            sb.AppendLine();
            sb.AppendLine(doc.Trim());
        }
        else
        {
            if (slug.Length > 0)
                sb.AppendLine($"[提示] 配置里锁定的风格「{slug}」没找到对应文档，已回退为「按主题自选」，可在面板『美学系统』里重新选择。");
            sb.AppendLine("【风格库】（未锁定风格：按主题/情绪命中 1 个，并在本次会话内保持一致；换风格要换得有理由）");
            string? index = ReadBuiltinDoc("_INDEX.md");
            if (index != null) sb.AppendLine(index.Trim());
            else sb.AppendLine("（风格索引缺失：直接用兜底基准库的配色与骨架。）");
            sb.AppendLine();
            sb.AppendLine("【兜底基准库（没命中风格时用；参数是示例，随语境重造、禁照抄）】");
            sb.AppendLine("四色系：Mono 纸 #F0EFEB/#1C1C1A（通用）｜青瓷蓝 #F7F2EB/#081F5C + #334EAC→#7096D1→#BAD6EB 有序单序列（数据/时序）｜");
            sb.AppendLine("椰林绿 #F0EFEB/#58402E + #43593B→#ACAD79 + 琥珀 #D4A017（类目 ≤4）｜编辑部红 #F0F0EE/#1F1E1C + 荧光橙 #F5572F（新闻/榜单）。");
            sb.AppendLine("骨架：badge 胶囊 → 结论标题 → 副题/图例（`·` 分隔）→ 主体 → 来源行；圆角 24px、靠留白分卡、卡间 18-22px。");
            sb.AppendLine("图型：可数梯子 / 刻度环 / 点阵（替代饼图）/ 发丝线 0.5-0.7px / 分歧条（0 轴为界）；聚合数摊回可数单位，图表不断轴。");
            sb.AppendLine("动效：quarticOut 快进快停、点阵 stagger 8-15ms、条形 80-130ms；流式期只用 opacity 淡入，带 prefers-reduced-motion。");
            string? baseline = Path.Combine(builtinStylesDir, "_BASELINE.md");
            if (File.Exists(baseline)) sb.AppendLine($"（完整基准库见 {baseline}）");
        }
        return sb.ToString().TrimEnd();
    }
}
