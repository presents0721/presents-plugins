using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using Alife.Client;
using Alife.Foundation;
using Alife.Framework;

namespace presents.RawHtml;

/// <summary>
/// Raw HTML 卡片面板窗口：普通（非透明、带标题栏、可缩放）宿主窗口，
/// 加载插件 Resources/RawHtml/index.html，通过 <see cref="PetBridge"/> 与 C# 宿主通信。
/// 网页资源路径由 <see cref="CreateAsync"/> 的 wwwRoot 指定。
///
/// Alife 5 去掉了 Electron.NET：窗口改用 <see cref="IAlifeApplication.CreateWindowAsync"/>，
/// 渲染页由 WebView2 承载；所以没有 NodeIntegration / TitleBarOverlay / RemoveMenu，
/// 窗口样式由宿主决定（深色一体化标题栏由宿主统一处理）。
/// </summary>
public sealed class RawHtmlWindow(IAlifeApplication alifeApplication) : IAsyncDisposable
{
    /// <summary>窗口创建完成后的宿主窗口句柄；未创建/已释放时为 null。</summary>
    public IAlifeWindow? Window { get; private set; }

    public bool IsCreated => Window != null;

    /// <summary>该面板实例独有的 IPC 频道名，避免多开时各面板消息互相串扰。</summary>
    public string ChannelId { get; } = "rawhtml-" + Guid.NewGuid().ToString("N");
    public int WindowWidth { get; set; } = 760;
    public int WindowHeight { get; set; } = 820;

    public async Task CreateAsync(string wwwRoot, PetBridge bridge)
    {
        if (!OperatingSystem.IsWindows())
            throw new NotSupportedException("不支持的平台。");

        try
        {
            // 频道号经 URL query 传给渲染进程（alife-bridge.js 从中读取，旧版 ipc 兜底时用）
            string url = $"{new Uri(Path.Combine(wwwRoot, "index.html")).AbsoluteUri}?panelChannel={ChannelId}";

            IAlifeWindow created = await alifeApplication.CreateWindowAsync(new AlifeWindowOptions {
                Title = "Raw HTML 卡片面板",
                Bounds = GetBounds(),
                Resizable = true,
                Transparent = false,
                TopMost = false,
                Url = url,
            });

            Window = created;

            // 必须在 Launch 之前绑好消息通道：页面一起来就会发 init，晚了会漏掉（面板会一直排队不渲染）
            bridge.Attach();

            await created.Launch();
        }
        catch (Exception ex)
        {
            AlifeLog.LogWarning("RawHtml 窗口创建失败: " + ex.Message);
            throw;
        }
    }

    /// <summary>停靠右上角（旧版 Electron 用 X=右-40 / Y=40）。Bounds 是逻辑像素，要先把物理尺寸除 DPI。</summary>
    Rectangle GetBounds()
    {
        try
        {
            Size physical = alifeApplication.GetScreenSize();
            double dpi = Math.Max(0.1, alifeApplication.GetDpiScale());
            int screenWidth = Math.Max(480, (int)Math.Round(physical.Width / dpi));
            int screenHeight = Math.Max(360, (int)Math.Round(physical.Height / dpi));

            int width = Math.Min(WindowWidth, screenWidth - 80);
            int height = Math.Min(WindowHeight, screenHeight - 80);
            int x = Math.Max(0, screenWidth - width - 40);
            int y = Math.Max(0, Math.Min(40, screenHeight - height - 40));

            return new Rectangle(x, y, width, height);
        }
        catch
        {
            return new Rectangle(200, 40, WindowWidth, WindowHeight);
        }
    }

    /// <summary>隐藏面板（用户点标题栏「—」）。窗口和页面都保留，下次有卡片时直接 Show。</summary>
    public async Task HideAsync()
    {
        IAlifeWindow? created = Window;
        if (created == null) return;

        try { await created.HideAsync(); }
        catch (Exception ex) { AlifeLog.LogWarning("RawHtml 隐藏窗口失败: " + ex.Message); }
    }

    /// <summary>把已隐藏的窗口重新显示。窗口已被关掉时返回 false，让服务层重建。</summary>
    public async Task<bool> TryShowAsync()
    {
        IAlifeWindow? created = Window;
        if (created == null) return false;

        try
        {
            await created.ShowAsync();
            return true;
        }
        catch (Exception ex)
        {
            AlifeLog.LogWarning("RawHtml 显示窗口失败: " + ex.Message);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        IAlifeWindow? created = Window;
        Window = null;
        if (created == null) return;

        // 先隐藏再释放：宿主 DisposeAsync 若只清理 WebView 而不销毁窗体，至少窗口会立刻消失
        try { await created.HideAsync(); } catch { /* 已关闭时忽略 */ }
        try { await created.DisposeAsync(); } catch { /* 窗口已关闭时忽略 */ }
    }
}
