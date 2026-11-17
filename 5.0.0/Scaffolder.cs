using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace presents.OpsSuite;

/// <summary>
/// 生成一个「新建即可编译、加载即有工具」的最小插件骨架。
/// 命名约定：prefix = Foo 时，产出 FooModule / FooConfig / FooUI，
/// 文件名与类名一一对应（避免出现 FooModuleModule 这种翻倍）。
/// </summary>
internal static class Scaffolder
{
    static readonly Regex PluginIdPattern = new(@"^[A-Za-z0-9._-]{2,64}$", RegexOptions.Compiled);
    static readonly Regex IdentPattern = new(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled);

    public sealed class Result
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public string Directory { get; set; } = "";
        public List<string> Files { get; set; } = new();
    }

    public static Result Create(string pluginsRoot, string pluginId, string? prefixArg, string? title, string? ns)
    {
        Result result = new();

        pluginId = (pluginId ?? "").Trim();
        if (!PluginIdPattern.IsMatch(pluginId))
        {
            result.Message = "插件 id 只能用字母数字点下划线短横线，长度 2~64";
            return result;
        }

        string last = pluginId.Split('.').Last();

        // prefix 是去掉 Module 后缀的类名前缀
        string prefix = string.IsNullOrWhiteSpace(prefixArg) ? last : prefixArg.Trim();
        if (prefix.EndsWith("Module", StringComparison.Ordinal))
            prefix = prefix[..^6];
        if (!IdentPattern.IsMatch(prefix))
        {
            result.Message = "类名前缀必须是合法标识符";
            return result;
        }

        ns = string.IsNullOrWhiteSpace(ns) ? pluginId : ns.Trim();
        if (!Regex.IsMatch(ns, @"^[A-Za-z_][A-Za-z0-9_.]*$"))
        {
            result.Message = "命名空间不合法";
            return result;
        }

        title = string.IsNullOrWhiteSpace(title) ? last : title.Trim();
        string safeTitle = title.Replace("\\", "").Replace("\"", "'");
        string code = prefix.ToLowerInvariant();

        string dir = Path.Combine(pluginsRoot, pluginId);
        if (Directory.Exists(dir))
        {
            result.Message = $"目录已存在：{pluginId}";
            return result;
        }

        try
        {
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(dir, "Resources"));

            Write(dir, "manifest.json", Manifest(), result);
            Write(dir, prefix + "Module.cs", ModuleSource(ns, prefix, safeTitle, code), result);
            Write(dir, prefix + "Config.cs", ConfigSource(ns, prefix), result);
            Write(dir, prefix + "UI.cs", UiSource(ns, prefix, safeTitle), result);
            Write(dir, "Resources/index.html", Html(title), result);

            result.Ok = true;
            result.Directory = dir;
            result.Message = $"已生成 {result.Files.Count} 个文件：{prefix}Module / {prefix}Config / {prefix}UI。" +
                             "重载插件环境后即可加载；记得把 " + ns + "." + prefix + "Module 加进角色 index.json 的 Modules。";
        }
        catch (Exception ex)
        {
            result.Message = "生成失败：" + ex.Message;
        }

        return result;
    }

    static void Write(string dir, string name, string content, Result result)
    {
        File.WriteAllText(Path.Combine(dir, name), content, new UTF8Encoding(false));
        result.Files.Add(name);
    }

    static string Manifest() => """
{
  "Version": "1.0.0",
  "Dependencies": []
}
""";

    static string ModuleSource(string ns, string prefix, string title, string code) => $$"""
using System.ComponentModel;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace {{ns}};

[Module("{{title}}", "{{title}} 插件", defaultCategory: "{{ns}}", editorUI: typeof({{prefix}}UI))]
public class {{prefix}}Module(
    XmlFunctionCaller functionCaller,
    Interactor<{{prefix}}Module> interactor,
    ILogger<{{prefix}}Module> logger) :
    ChatBehaviour,
    IConfigurable<{{prefix}}Config>
{
    public {{prefix}}Config Configuration { get; set; } = new();

    protected override Task OnAwake()
    {
        XmlHandler handler = new(this) { Description = "{{title}} 的工具集" };
        functionCaller.RegisterHandler(handler, DocumentMode.Implicit, DestroyCancellationToken);
        logger.LogInformation("[{{code}}] 已唤醒");
        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        logger.LogInformation("[{{code}}] 已启动");
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot, "{{code}}_ping")]
    [Description("连通性自检，返回插件当前状态。")]
    public void Ping()
    {
        string state = Configuration.Enable ? "开" : "关";
        interactor.Poke($"[{{title}}] 在线，启用开关 = {state}");
    }
}
""";

    static string ConfigSource(string ns, string prefix) => $$"""
using System.ComponentModel;

namespace {{ns}};

public sealed class {{prefix}}Config
{
    [DisplayName("启用")]
    [Description("关闭后停止处理。默认开。")]
    public bool Enable { get; set; } = true;

    [DisplayName("配置版本")]
    [Description("用于配置迁移，不要手改。默认 1。")]
    public int ConfigVersion { get; set; } = 1;
}
""";

    static string UiSource(string ns, string prefix, string title) => $$"""
using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace {{ns}};

/// <summary>模块详情页入口。完整界面建议做成本地 HTTP 面板，再在这里放跳转按钮。</summary>
public sealed class {{prefix}}UI : ModuleUIBase<{{prefix}}Module, {{prefix}}Config>
{
    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        // 必须调基类，否则框架默认的配置面板不会渲染
        base.BuildRenderTree(b);

        int s = 0;
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "padding:18px 20px;font-size:14px;line-height:1.8");

        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "opacity:.75;margin-bottom:12px");
        b.AddContent(s++, "{{title}}：这里写一段给用户看的功能说明。");
        b.CloseElement();

        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "background:rgba(127,127,127,.10);border:1px solid rgba(127,127,127,.28);border-radius:10px;padding:12px 16px");
        b.AddContent(s++, "AI 工具：{{title}} 的 ping 自检。新增工具时在模块里加 [XmlFunction] 方法即可。");
        b.CloseElement();

        b.CloseElement();
    }
}
""";

    static string Html(string title) => $$"""
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>{{title}}</title>
<style>
  :root{--bg:#0f131c;--panel:#161c28;--line:#232b3d;--dim:#8b96ab}
  body{margin:0;padding:24px;background:var(--bg);color:#dfe3ec;font:14px/1.6 -apple-system,"Segoe UI","Microsoft YaHei",sans-serif}
  h1{font-size:19px;margin:0 0 6px}
  .box{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:18px 20px}
  .muted{color:var(--dim);font-size:13px}
</style>
</head>
<body>
<div class="box">
  <h1>{{title}}</h1>
  <div class="muted">骨架已生成。把这里换成你真正要展示的内容，服务端可以复用 MiniServer 或自己写 HttpListener。</div>
</div>
</body>
</html>
""";
}
