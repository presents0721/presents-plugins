using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using ElectronNET.API;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace presents.RawHtml;

/// <summary>
/// Raw HTML 卡片：主进程 ⇄ 渲染进程的 Electron IPC 桥。
/// 频道为该面板实例独有的 <see cref="RawHtmlWindow.ChannelId"/>，信封对象为 { type: string, ...payload }。
/// 渲染进程 → 主进程消息经 <see cref="OnMessage"/> 事件分发。
/// （移植自 presents.DeskPet3D/Protocol/PetBridge.cs：原样复制，仅改命名空间与日志类型。）
/// </summary>
public sealed class PetBridge : IDisposable
{
    public event Action<string, JsonElement>? OnMessage;

    /// <summary>向渲染进程发送消息（合并 type 与 payload 为单一信封对象）。</summary>
    public void SendMessage(string type, object? payload = null)
    {
        try
        {
            JObject envelope = new() { ["type"] = type };
            if (payload != null)
            {
                JObject payloadObj = JObject.FromObject(payload);
                foreach (KeyValuePair<string, JToken?> kvp in payloadObj)
                    envelope[kvp.Key] = kvp.Value;
            }
            Electron.IpcMain.Send(window.Window, window.ChannelId, envelope.ToString(Newtonsoft.Json.Formatting.Indented));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "发送 RawHtml 消息失败");
        }
    }

    /// <summary>在渲染进程执行 JS 并返回结果。</summary>
    public async Task<string?> ExecuteJavaScriptAsync(string script)
    {
        try
        {
            return await window.Window.WebContents.ExecuteJavaScriptAsync<string>(script, true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "执行 RawHtml 脚本失败");
            return null;
        }
    }

    readonly RawHtmlWindow window;
    readonly ILogger<RawHtmlService> logger;

    public PetBridge(RawHtmlWindow window, ILogger<RawHtmlService> logger)
    {
        this.window = window;
        this.logger = logger;
        Electron.IpcMain.On(window.ChannelId, OnIpcMessage);
    }
    public void Dispose()
    {
        Electron.IpcMain.RemoveAllListeners(window.ChannelId);
    }

    void OnIpcMessage(object? payload)
    {
        if (payload is not string json)
            return;
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
