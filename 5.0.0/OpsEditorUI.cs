using System;
using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace presents.OpsSuite;

/// <summary>模块详情页：只放必要入口，完整面板走本地 HTTP（可独立开浏览器、可分享给远程）。</summary>
public sealed class OpsEditorUI : ModuleUIBase<OpsModule, OpsConfig>
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
        b.AddContent(s++, "插件体检 + 资源哨兵。所有采集均为只读；配置回滚会先自动留档，可撤销。");
        b.CloseElement();

        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "display:flex;gap:10px;align-items:center;flex-wrap:wrap;margin-bottom:14px");

        b.OpenElement(s++, "button");
        b.AddAttribute(s++, "style", "padding:7px 16px;border-radius:8px;border:1px solid #4a7fd0;background:#2b4a7a;color:#fff;cursor:pointer");
        b.AddAttribute(s++, "onclick", EventCallback.Factory.Create(this, OpenPanel));
        b.AddContent(s++, "打开运维面板");
        b.CloseElement();

        b.OpenElement(s++, "code");
        b.AddAttribute(s++, "style", "padding:5px 10px;background:rgba(127,127,127,.16);border-radius:6px;font-size:12px");
        b.AddContent(s++, Module?.PanelUrl ?? "(面板未启动)");
        b.CloseElement();
        b.CloseElement();

        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "background:rgba(127,127,127,.10);border:1px solid rgba(127,127,127,.28);border-radius:10px;padding:12px 16px");
        b.AddContent(s++, "面板包含：资源总览与趋势、进程/显存归因、热重载前后 diff、插件冲突检测、配置快照与一键回滚、页面残留层审计、资源告警。");
        b.CloseElement();

        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "margin-top:12px;opacity:.6;font-size:12px");
        b.AddContent(s++, "AI 可直接调用：opsstatus / opssnapshot / opsdiff / opsconflicts / opsaudit / opsconfigs / opsconfigdiff / opsrollback / opsbudget / opspanel");
        b.CloseElement();

        b.CloseElement();
    }

    void OpenPanel() => Module?.OpenPanel();
}
