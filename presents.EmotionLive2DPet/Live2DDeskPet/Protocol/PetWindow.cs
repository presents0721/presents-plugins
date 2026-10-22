using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using ElectronNET.API;
using ElectronNET.API.Entities;

namespace presents.EmotionLive2DPet;

/// <summary>按角色隔离的存储键前缀，值为角色的 StorageKey（如 "Character\<角色名>"）。</summary>
public record PetStorageKey(string Value);

/// <summary>
/// 桌宠 Electron 浏览器窗口封装：负责创建透明置顶窗口、定位/移动/缩放、DPI 与脚本执行，
/// 并通过 StorageSystem 持久化/恢复窗口位置大小（按角色隔离）。
/// 网页资源位于插件 Resources/Live2D/（随插件分发或内嵌到客户端输出目录）。
/// </summary>
public sealed class PetWindow(StorageSystem storage, PetStorageKey storageKey) : IDisposable
{
    public event Action? MouseMoved;

    public BrowserWindow Window => window;
    public double Dpi => dpi;
    public Rectangle Bounds => bounds;
    public Point Position => position;
    public Point CursorScreenPoint => cursorScreenPoint;

    /// <summary>该桌宠实例独有的 IPC 频道名，避免多开时各桌宠消息互相串扰。</summary>
    public string ChannelId { get; } = "pet-" + Guid.NewGuid().ToString("N");

    public void ResetBounds()
    {
        window.SetBounds(defaultBounds);
    }

    BrowserWindow window = null!;
    double dpi;
    Rectangle bounds = null!;
    Point position = null!;
    Point cursorScreenPoint = null!;
    CancellationTokenSource cancellationTokenSource = null!;

    readonly string windowBoundsKey = $"{storageKey.Value}/Live2DDeskPet/WindowBounds";
    Rectangle defaultBounds = null!;

    public async Task CreateAsync(string wwwRoot, PetBridge bridge)
    {
        if (!OperatingSystem.IsWindows())
            throw new NotSupportedException("不支持的平台。");

        //获取基础属性
        Display primary = await Electron.Screen.GetPrimaryDisplayAsync();
        defaultBounds = new Rectangle {
            X = primary.WorkArea.X + primary.WorkArea.Width - 710,
            Y = primary.WorkArea.Y + primary.WorkArea.Height - 215,
            Width = 360,
            Height = 540,
        };

        dpi = primary.ScaleFactor;
        bounds = storage.GetObject<Rectangle>(windowBoundsKey) ?? new Rectangle {
            X = defaultBounds.X,
            Y = defaultBounds.Y,
            Width = defaultBounds.Width,
            Height = defaultBounds.Height,
        };
        // 位置钳制：只在窗口【完全】移出屏幕工作区时才拉回。
        // 用户可自由把窗口部分拖出屏幕边缘（如桌宠半挂在屏幕左侧，X 为负但右缘仍在屏内），
        // 这类"部分出屏"位置必须精确保存/恢复，不能被误判为"跑出屏幕"。
        Rectangle wa = primary.WorkArea;
        const int MinVisible = 60; // 拉回后至少露出 60px
        if (bounds.X + bounds.Width <= wa.X) bounds.X = wa.X;                      // 整个窗口在屏幕左侧外
        if (bounds.X >= wa.X + wa.Width) bounds.X = wa.X + wa.Width - MinVisible;  // 整个窗口在屏幕右侧外
        if (bounds.Y + bounds.Height <= wa.Y) bounds.Y = wa.Y;                     // 整个在屏幕上侧外
        if (bounds.Y >= wa.Y + wa.Height) bounds.Y = wa.Y + wa.Height - MinVisible;// 整个在下侧外
        position = new Point {
            X = bounds.X + bounds.Width / 2,
            Y = bounds.Y + bounds.Height / 2,
        };
        cursorScreenPoint = await Electron.Screen.GetCursorScreenPointAsync();

        //注册 init 监听：必须在窗口创建（开始加载页面）之前完成，避免错过渲染进程在脚本末尾发送的 init 消息
        TaskCompletionSource initTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnInit(string type, JsonElement _)
        {
            if (type == "init")
            {
                bridge.OnMessage -= OnInit;
                initTcs.TrySetResult();
            }
        }
        bridge.OnMessage += OnInit;
        try
        {
            //加载窗口（URL 携带本实例独有的 IPC 频道，避免多开串扰）
            string url = $"{new Uri(Path.Combine(wwwRoot, "index.html")).AbsoluteUri}?petChannel={ChannelId}";
            window = await Electron.WindowManager.CreateWindowAsync(new BrowserWindowOptions {
                Title = "Live2D 桌宠",
                X = bounds.X,
                Y = bounds.Y,
                Width = bounds.Width,
                Height = bounds.Height,
                AlwaysOnTop = true,
                SkipTaskbar = true,
                Frame = false,
                Transparent = true,
                HasShadow = false,
                Show = false,
                Movable = true,
                Resizable = false,
                Fullscreenable = false,
                BackgroundColor = "#00000000",
                WebPreferences = new WebPreferences {
                    NodeIntegration = true,
                    ContextIsolation = false,
                    Sandbox = false,
                    DevTools = true
                }
            }, url);

            TaskCompletionSource tcs = new TaskCompletionSource();
            window.OnReadyToShow += () => {
                //提升窗口置顶层级到最高（screen-saver），确保盖过全屏/无边框窗口。
                window.SetAlwaysOnTop(true, (OnTopLevel)7, 1);
                window.Show();
                tcs.SetResult();
            };
            await tcs.Task;

            //等待渲染进程初始化完成，确保 JS 已加载并能通讯后再继续后续消息交互
            using CancellationTokenSource initTimeout = new(TimeSpan.FromSeconds(30));
            await initTcs.Task.WaitAsync(initTimeout.Token);
        }
        finally
        {
            bridge.OnMessage -= OnInit;
        }

        //订阅窗口位置/大小变化（每窗口独立推送，无 IPC 往返，多开无串扰）
        window.OnBoundsChanged += OnBoundsChanged;
        //处理鼠标信息捕获
        cancellationTokenSource = new CancellationTokenSource();
        Loop(cancellationTokenSource.Token);
    }

    public void Dispose()
    {
        // 空安全：初始化中途失败时 window/cancellationTokenSource 可能还没赋值，
        // 否则 Dispose 自己抛 NRE 会把真正的失败原因盖掉，还会连累 Electron 侧报「窗口 id 不存在」。
        try
        {
            if (window != null)
            {
                window.OnBoundsChanged -= OnBoundsChanged;
                window.Destroy();
            }
        }
        catch (Exception e) { AlifeLog.LogWarning("PetWindow 关闭窗口失败: " + e.Message); }

        try { cancellationTokenSource?.Cancel(); cancellationTokenSource?.Dispose(); }
        catch (Exception e) { AlifeLog.LogWarning("PetWindow 停止轮询失败: " + e.Message); }

        try { if (bounds != null) storage.SetObject(windowBoundsKey, bounds); }
        catch (Exception e) { AlifeLog.LogWarning("PetWindow 保存位置失败: " + e.Message); }
    }

    void OnBoundsChanged(Rectangle newBounds)
    {
        bounds = newBounds;
        position.X = bounds.X + bounds.Width / 2;
        position.Y = bounds.Y + bounds.Height / 2;
    }

    async void Loop(CancellationToken cancellationToken = default)
    {
        // 单次 IPC 超时不终止循环：Electron 侧在穿透切换/高频消息下可能短暂拥堵（>1s），
        // 一次超时就退出会让光标位置永久停更 → 穿透/靠近判定用旧坐标 → 窗口卡死。
        while (cancellationToken.IsCancellationRequested == false)
        {
            try
            {
                await Task.Delay(30, cancellationToken);

                Point newCursorPoint = await Electron.Screen.GetCursorScreenPointAsync();
                if (cursorScreenPoint.X != newCursorPoint.X || cursorScreenPoint.Y != newCursorPoint.Y)
                {
                    cursorScreenPoint = newCursorPoint;
                    MouseMoved?.Invoke();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                // IPC 瞬时拥堵（如穿透切换瞬间）：等 500ms 重试，不退出循环
                AlifeLog.LogWarning("PetWindow 轮询瞬时失败，500ms 后重试: " + e.Message);
                try { await Task.Delay(500, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
