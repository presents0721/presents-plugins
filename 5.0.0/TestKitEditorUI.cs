using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace presents.PluginTestKit;

public sealed class TestKitEditorUI : ModuleUIBase<TestKitModule, TestKitConfig>
{
    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        // 必须调基类：否则框架默认的配置面板不会渲染（文档 U-02）
        base.BuildRenderTree(b);

        int s = 0;
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "padding:18px 20px;font-size:14px;line-height:1.8");

        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "opacity:.75;margin-bottom:14px");
        b.AddContent(s++, "只读自检：结构、注入脚本、配置迁移、热重载回归与自定义用例，不修改任何插件源码。");
        b.CloseElement();

        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "display:flex;gap:10px;align-items:center;flex-wrap:wrap;margin-bottom:14px");
        b.OpenElement(s++, "button");
        b.AddAttribute(s++, "style", "padding:7px 16px;border-radius:8px;border:1px solid #4a7fd0;background:#2b4a7a;color:#fff;cursor:pointer");
        b.AddAttribute(s++, "onclick", EventCallback.Factory.Create(this, OpenPanel));
        b.AddContent(s++, "打开测试面板");
        b.CloseElement();
        b.OpenElement(s++, "code");
        b.AddAttribute(s++, "style", "padding:5px 10px;background:rgba(127,127,127,.16);border-radius:6px;font-size:12px");
        b.AddContent(s++, Module?.PanelUrl ?? "(面板未启动)");
        b.CloseElement();
        b.CloseElement();

        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "background:rgba(127,127,127,.10);border:1px solid rgba(127,127,127,.28);border-radius:10px;padding:12px 16px");
        b.AddContent(s++, "想加自己的用例：在插件目录建 Tests/*.test.cjs，用 test(name, fn) + assert(cond) 写即可，面板会自动发现并执行。");
        b.CloseElement();

        b.CloseElement();
    }

    void OpenPanel() => Module?.OpenPanel();
}
