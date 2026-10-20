using System;
using System.IO;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using ElectronNET.API;
using ElectronNET.API.Entities;

namespace presents.RawHtml;

/// <summary>
/// Raw HTML 卡片面板窗口：普通（非透明、带标题栏、可缩放）Electron 窗口，
/// 加载插件 Resources/RawHtml/index.html，通过 <see cref="PetBridge"/> 与 C# 宿主通信。
/// 网页资源路径由 <see cref="CreateAsync"/> 的 wwwRoot 指定。
/// （基于 presents.DeskPet3D/Protocol/PetWindow.cs 简化：去掉 3D 落座 / 鼠标穿透 / 光标广播等逻辑。）
/// </summary>
public sealed class RawHtmlWindow(StorageSystem storage) : IDisposable
{
    public BrowserWindow Window => window;
    /// <summary>该面板实例独有的 IPC 频道名，避免多开时各面板消息互相串扰。</summary>
    public string ChannelId { get; } = "rawhtml-" + Guid.NewGuid().ToString("N");
    public int WindowWidth { get; set; } = 760;
    public int WindowHeight { get; set; } = 820;

    BrowserWindow window = null!;

    public async Task CreateAsync(string wwwRoot, PetBridge bridge)
    {
        if (!OperatingSystem.IsWindows())
            throw new NotSupportedException("不支持的平台。");

        Rectangle workArea;
        Display? primary = null;
        try { primary = await Electron.Screen.GetPrimaryDisplayAsync(); } catch { }
        if (primary is null)
        {
            // 主进程无响应时，用系统 API 兜底，避免面板因取不到屏幕就硬崩。
            var sysWa = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                        ?? new System.Drawing.Rectangle(0, 0, 1280, 800);
            workArea = new Rectangle { X = sysWa.X, Y = sysWa.Y, Width = sysWa.Width, Height = sysWa.Height };
        }
        else
        {
            workArea = primary.WorkArea;
        }

        int x = workArea.X + Math.Max(0, workArea.Width - WindowWidth - 40);
        int y = workArea.Y + 40;

        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // 频道号经 URL query 传给渲染进程（alife-bridge.js 从中读取）
            string url = $"{new Uri(Path.Combine(wwwRoot, "index.html")).AbsoluteUri}?panelChannel={ChannelId}";
            window = await Electron.WindowManager.CreateWindowAsync(new BrowserWindowOptions
            {
                Title = "Raw HTML 卡片面板",
                X = x,
                Y = y,
                Width = WindowWidth,
                Height = WindowHeight,
                AlwaysOnTop = false,
                SkipTaskbar = false,
                Frame = true,
                // 去掉 Electron 默认菜单（File/Edit/View/Window/Help）——对卡片面板毫无用处，还是一条白条
                AutoHideMenuBar = true,
                // 把系统那条白色标题栏换成深色一体化标题栏：仍保留最小化/最大化/关闭按钮，但整体融入面板深色主题
                TitleBarStyle = TitleBarStyle.hidden,
                TitleBarOverlay = new TitleBarOverlay { Color = "#171a21", SymbolColor = "#e7e7e7", Height = 36 },
                Transparent = false,
                HasShadow = true,
                Show = false,
                Movable = true,
                Resizable = true,
                Fullscreenable = false,
                BackgroundColor = "#0f1115",
                WebPreferences = new WebPreferences
                {
                    // 允许渲染进程 require('electron') 拿到 ipcRenderer，并可直接执行卡片内 <script>
                    NodeIntegration = true,
                    ContextIsolation = false,
                    Sandbox = false,
                    DevTools = true
                }
            }, url);

            // 双保险：某些 Electron 版本 AutoHideMenuBar 仍会留一条，直接摘掉菜单
            try { window.RemoveMenu(); } catch { /* 平台不支持时忽略 */ }

            window.OnReadyToShow += () => { window.Show(); tcs.SetResult(); };
            await tcs.Task;
        }
        catch (Exception ex)
        {
            AlifeLog.LogWarning("RawHtml 窗口创建失败: " + ex.Message);
            throw;
        }
    }

    public void Dispose()
    {
        try { window?.Destroy(); } catch { }
    }
}
