using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.JSInterop;

namespace presents.OpsSuite;

/// <summary>
/// 主页面侧桥：客户端会按 [Module(globalUI:)] 自动挂载。
/// 组件不渲染任何 DOM，只把 IJSRuntime 交给 PageAudit。
/// </summary>
public sealed class OpsGlobalUI : ComponentBase, IDisposable
{
    [Inject] IJSRuntime JS { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder) { }

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        // 每次渲染都刷新引用：页面重载 / 热重载后旧的 IJSRuntime 已失效
        PageAudit.Runner = RunJsAsync;
        return Task.CompletedTask;
    }

    Task<string> RunJsAsync(string script) => JS.InvokeAsync<string>("eval", script).AsTask();

    public void Dispose()
    {
        if (PageAudit.Runner is { } runner && ReferenceEquals(runner.Target, this))
            PageAudit.Runner = null;
    }
}
