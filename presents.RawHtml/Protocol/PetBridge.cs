using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Alife.Client;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace presents.RawHtml;

/// <summary>
/// Raw HTML 卡片：C# ⇄ 渲染进程的消息桥。
///
/// Alife 5 没有 Electron 的 IpcMain：每个面板窗口都是独立的 WebView2，
/// 宿主在页面里注入 window.alifeWindow，C# 侧对应
/// <see cref="IAlifeWindow.PostMessageAsync"/>（下行）与
/// <see cref="IAlifeWindow.MessageReceived"/>（上行）。
/// 因为消息只投递给本窗口，<see cref="RawHtmlWindow.ChannelId"/> 现在只作为
/// 前端旧版 ipc 兜底时的频道标识保留，不再用于 C# 侧过滤。
/// </summary>
public sealed class PetBridge : IDisposable
{
    public event Action<string, JsonElement>? OnMessage;

    readonly RawHtmlWindow window;
    readonly ILogger<RawHtmlService> logger;
    IAlifeWindow? bound;

    public PetBridge(RawHtmlWindow window, ILogger<RawHtmlService> logger)
    {
        this.window = window;
        this.logger = logger;
    }

    /// <summary>窗口创建完成后绑定消息通道（必须在页面 Launch 之前调用，否则会漏掉前端 init）。</summary>
    public void Attach()
    {
        IAlifeWindow? target = window.Window;
        if (target == null || ReferenceEquals(bound, target)) return;

        Detach();
        bound = target;
        target.MessageReceived += OnIpcMessage;
    }

    /// <summary>向渲染进程发送消息（合并 type 与 payload 为单一信封对象）。</summary>
    public void SendMessage(string type, object? payload = null)
    {
        IAlifeWindow? target = bound;
        if (target == null) return;   // 面板还没起来：服务层自己会排队/提示

        try
        {
            JObject envelope = new() { ["type"] = type };
            if (payload != null)
            {
                JObject payloadObj = JObject.FromObject(payload);
                foreach (KeyValuePair<string, JToken?> kvp in payloadObj)
                    envelope[kvp.Key] = kvp.Value;
            }

            string json = envelope.ToString(Newtonsoft.Json.Formatting.None);
            _ = PostAsync(target, json);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "发送 RawHtml 消息失败");
        }
    }

    async Task PostAsync(IAlifeWindow target, string json)
    {
        try
        {
            await target.PostMessageAsync(json);
        }
        catch (Exception ex)
        {
            // 窗口关闭 / 页面重载时消息会失败，这里只记一笔，不影响上层流程
            logger.LogWarning("发送 RawHtml 消息失败: {Message}", ex.Message);
        }
    }

    /// <summary>在渲染进程执行 JS 并返回结果。</summary>
    public async Task<string?> ExecuteJavaScriptAsync(string script)
    {
        IAlifeWindow? target = bound;
        if (target == null) return null;

        try
        {
            return await target.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "执行 RawHtml 脚本失败");
            return null;
        }
    }

    void Detach()
    {
        IAlifeWindow? target = bound;
        bound = null;
        if (target == null) return;

        try { target.MessageReceived -= OnIpcMessage; } catch { /* 忽略 */ }
    }

    public void Dispose() => Detach();

    void OnIpcMessage(object? payload)
    {
        // 宿主可能投递 string（JSON）或已解析的 JsonElement
        string? json = payload switch
        {
            null => null,
            string text => text,
            JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText(),
            _ => payload.ToString(),
        };

        if (string.IsNullOrEmpty(json))
            return;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("type", out JsonElement typeProp) == false)
                return;

            string? type = typeProp.GetString();
            if (type == null)
                return;

            try
            {
                OnMessage?.Invoke(type, root);
            }
            catch (Exception e)
            {
                logger.LogError(e, "执行 RawHtml 前端消息回调失败");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "解析 RawHtml 前端消息失败");
        }
    }
}
