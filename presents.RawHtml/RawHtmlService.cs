using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.QChat;
using Alife.Function.QChatSender;
using Microsoft.Extensions.Logging;

namespace presents.RawHtml;

/// <summary>
/// Raw HTML 卡片插件（VCP 协议 · Alife 移植版）。
///
/// 来源：dsh-raw-html-v2（DeepSeek Harness 网页聊天端插件）。原插件由两部分组成：
///   1) 渲染内核 vcp-engine-v1.js（纯浏览器 JS，原样复用）；
///   2) 美学系统 —— 「渲染 HTML」与「美学注入」两个开关 + 12 个内置风格（styles/*.md）+ 26 款 Lanxi 字体，
///      由 Host 侧按开关把协议与风格文档注入系统提示词，让 AI 按选定风格写卡。
/// Alife 没有对话 DOM，因此改为「AI 用 &lt;RenderHtmlCard&gt;…&lt;/RenderHtmlCard&gt; 包裹裸 HTML →
/// C# 转发到 webview 面板渲染」；两个开关与美学系统做进面板窗口，配置与注入由本文件的 C# 侧负责。
/// </summary>
[Module("Raw HTML 卡片",
    "让 AI 直接输出裸 HTML（VCP 协议），在独立面板里渲染成富卡片：KaTeX 公式 / Mermaid 图表 / SVG / 内置字体 / JS 动画。" +
    "自带美学系统：12 个风格 + 26 款 Lanxi 字体，可开关「渲染 / 美学注入」并锁定风格。",
    defaultCategory: "presents")]
public class RawHtmlService(
    PluginSystem pluginSystem,
    StorageSystem storageSystem,
    XmlFunctionCaller functionService,
    ConfigurationSystem configurationSystem,
    ILogger<RawHtmlService> logger,
    Interactor<RawHtmlService> interactor,
    QChatSenderService? qSender = null,        // 软依赖：未安装 / 未加载「QQ单向发送」时为 null
    QChatService? qChat = null) :              // 软依赖：未安装 / 未加载「QQ聊天」(双向) 时为 null；优先用双向
    ChatBehaviour,
    IConfigurable<RawHtmlConfig>
{
    public RawHtmlConfig Configuration { get; set; } = null!;

    private RawHtmlWindow? window;
    private PetBridge? bridge;

    /// <summary>面板前端就绪前收到的卡片（alife-bridge.js 加载完成后会发来 type=init），带序号便于核对。</summary>
    private readonly List<(int Seq, string Html)> pendingCards = new();
    private volatile bool panelReady;

    /// <summary>卡片序号：日志与回执都带序号，便于核对「一次调用 / 一次渲染」。</summary>
    private int cardSeq;

    private AestheticLibrary? library;
    private XmlHandler? handler;

    /// <summary>AI 触发「发到 QQ 群」时，等待前端把最近一张卡片栅格化成 PNG 回传的完成源（req → TCS）。</summary>
    private readonly Dictionary<string, TaskCompletionSource<string>> qqSendWaiters = new();

    /// <summary>前端栅格化失败时的真实错误（由 lastCardImage 的 error 字段回填），供 SendCardToQQ 给出精确提示。</summary>
    private string? lastRasterizeError;

    /// <summary>面板网页资源目录（Resources/RawHtml），取字体与风格文档用。</summary>
    private string resourcesDir = "";

    /// <summary>
    /// 上一次实例创建的面板窗口。热更/重载时若旧实例的 OnDestroy 没跑到（例如 OnAwake 中途抛异常），
    /// 旧窗口会残留在桌面上（表现为「面板弹了好几个」）。这里在创建新面板前顺手收掉它。
    /// </summary>
    private static RawHtmlWindow? leakedWindow;

    protected override async Task OnAwake()
    {
        // 0) 收掉可能泄漏的旧面板（热更残留）
        RawHtmlWindow? stale = Interlocked.Exchange(ref leakedWindow, null);
        if (stale != null)
        {
            logger.LogWarning("发现残留的 RawHtml 面板窗口（上次重载未释放），先关闭它");
            try { stale.Dispose(); } catch { /* 忽略 */ }
        }

        string pluginDir = pluginSystem.PluginContext.GetPluginDirectoryPath("presents.RawHtml");
        resourcesDir = Path.Combine(pluginDir, "Resources", "RawHtml");
        string customDir = ResolveCustomStyleDir();
        library = new AestheticLibrary(
            builtinStylesDir: Path.Combine(resourcesDir, "styles"),
            builtinDocsDir: Path.Combine(resourcesDir, "styles", "docs"),
            writableDirs: [customDir],
            extraDirs: RawHtmlConfig.SplitDirs(Configuration.ExtraStyleDirs)
                .Concat(RawHtmlConfig.SplitDirs(Configuration.CustomStyleDirs))
                .Where(d => !string.Equals(d, customDir, StringComparison.OrdinalIgnoreCase)).ToArray(),
            fontsDir: Path.Combine(resourcesDir, "fonts"));

        // 1) 启动 webview 面板，加载承载 vcp 引擎的 index.html
        try
        {
            window = new RawHtmlWindow(storageSystem);
            bridge = new PetBridge(window, logger);
            bridge.OnMessage += OnBridgeMessage;
            await window.CreateAsync(resourcesDir, bridge);
            leakedWindow = window;   // 登记：万一本实例没被正常销毁，下次启动能收掉
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RawHtml 面板启动失败，函数调用仍会提示面板未就绪");
        }

        // 2) 注册 AI 可调用函数（渲染关闭时一个都不注册）。
        //    注意：这里的异常不能外抛 —— ChatBehaviour 只在 OnAwake 成功后才保证调用 OnDestroy，
        //    一旦抛出，上面刚创建的窗口就永远泄漏（面板残留）。
        try
        {
            ApplyConfigToHandler();
            functionService.AddPlainAreas(nameof(RenderHtmlCard));
            RememberConfigStamp();   // 记录配置基线：OnUpdate 里据此发现「设置页里的改动」
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RawHtml 函数注册失败");
        }
    }

    /// <summary>导出目录（保存卡片产物）：Storage/&lt;角色&gt;/RawHtml/Saved。</summary>
    private string SaveDir
    {
        get
        {
            string[] dirs = RawHtmlConfig.SplitDirs(Configuration.SaveDirectory);
            return dirs.Length > 0
                ? dirs[0]
                : Path.Combine(AlifePath.StorageFolderPath, Character.StorageKey, "RawHtml", "Saved");
        }
    }

    /// <summary>卡片历史（cards.jsonl），用于面板重启后恢复。一行一张卡片：ts / style / html（json 编码）。</summary>
    private string HistoryFile => Path.Combine(SaveDir, "cards.jsonl");

    private List<KeyValuePair<string, string>> LoadHistory(int limit)
    {
        List<KeyValuePair<string, string>> result = [];
        try
        {
            string file = HistoryFile;
            if (File.Exists(file) == false) return result;
            foreach (string line in File.ReadAllLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    JsonDocument doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("html", out JsonElement htmlElement) && htmlElement.ValueKind == JsonValueKind.String)
                    {
                        string ts = doc.RootElement.TryGetProperty("ts", out JsonElement tsElement) ? tsElement.GetString() ?? "" : "";
                        result.Add(new(ts, htmlElement.GetString() ?? ""));
                    }
                }
                catch { /* 忽略单行损坏 */ }
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "读取 RawHtml 卡片历史失败"); }

        if (limit > 0 && result.Count > limit) result = result.Skip(result.Count - limit).ToList();
        return result;
    }

    private void AppendHistory(string ts, string style, string html)
    {
        try
        {
            Directory.CreateDirectory(SaveDir);
            // 去重：连续重复内容不重复落盘（模型重复调用时不会堆一堆同名文件）
            string[] existing = File.Exists(HistoryFile) ? File.ReadAllLines(HistoryFile) : [];
            foreach (string line in existing)
            {
                try
                {
                    JsonDocument doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("html", out JsonElement e) && e.GetString() == html) return;
                }
                catch { /* 忽略 */ }
            }
            File.AppendAllText(HistoryFile, JsonSerializer.Serialize(new { ts, style, html }) + "\n");
            // 留存上限：超出只保留最近 N 条（N=0 表示不限制）
            int cap = Configuration.RestoreCount;
            if (cap > 0)
            {
                string[] all = File.ReadAllLines(HistoryFile);
                if (all.Length > cap) File.WriteAllLines(HistoryFile, all.Skip(all.Length - cap).ToArray());
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "保存 RawHtml 卡片历史失败"); }
    }

    private void ClearHistory()
    {
        try { if (File.Exists(HistoryFile)) File.Delete(HistoryFile); }
        catch (Exception ex) { logger.LogWarning(ex, "清空 RawHtml 卡片历史失败"); }
    }

    /// <summary>按 ts 删除单条历史（卡片内容去重时 ts 唯一）。</summary>
    private void DeleteHistoryItem(string ts)
    {
        try
        {
            string file = HistoryFile;
            if (File.Exists(file) == false || string.IsNullOrEmpty(ts)) return;
            var kept = new List<string>();
            foreach (string line in File.ReadAllLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                bool drop = false;
                try
                {
                    JsonDocument doc = JsonDocument.Parse(line);
                    string lts = doc.RootElement.TryGetProperty("ts", out JsonElement e) ? e.GetString() ?? "" : "";
                    if (lts == ts) drop = true;
                }
                catch { /* 损坏行保留，避免误删 */ }
                if (!drop) kept.Add(line);
            }
            File.WriteAllLines(file, kept);
        }
        catch (Exception ex) { logger.LogWarning(ex, "删除 RawHtml 卡片历史项失败"); }
    }

    /// <summary>自定义风格目录：Storage/&lt;角色&gt;/RawHtml/Styles（面板里新建/编辑的风格存这里，插件更新不丢）。</summary>
    private string ResolveCustomStyleDir()
    {
        string[] configured = RawHtmlConfig.SplitDirs(Configuration.CustomStyleDirs);
        if (configured.Length > 0) return configured[0];
        return Path.Combine(AlifePath.StorageFolderPath, Character.StorageKey, "RawHtml", "Styles");
    }

    /// <summary>按当前配置重建函数文档（渲染协议 + 美学协议 + 可见函数），渲染关闭则整组注销。</summary>
    private void ApplyConfigToHandler()
    {
        if (handler != null)
        {
            functionService.UnregisterHandler(handler);
            handler = null;
        }

        if (Configuration.RenderHtml == false)
        {
            logger.LogInformation("[RawHtml] 渲染已关闭：不注册任何函数（AI 输出裸 HTML 只会作为文本）");
            return;
        }

        System.Text.StringBuilder explanation = new();
        explanation.AppendLine(library?.BuildRenderProtocol(Configuration.InjectDocPaths) ?? "");
        if (Configuration.AestheticInjection && library != null)
        {
            explanation.AppendLine();
            explanation.AppendLine(library.BuildAestheticProtocol(Configuration.LockedStyle));
        }
        else
        {
            explanation.AppendLine();
            explanation.AppendLine("【美学注入已关闭】风格完全自由：按你自己的判断配色与排版，仍须遵守上面的结构铁律。");
        }

        handler = new XmlHandler(this) {
            Description = "把 AI 写出的裸 HTML（VCP 协议卡片）渲染成富卡片：支持 KaTeX 公式、Mermaid 图表、SVG、26 款 Lanxi 字体与交互控件，并可选按锁定风格输出。",
            Explanation = explanation.ToString().TrimEnd()
        };
        functionService.RegisterHandler(handler, DocumentMode.Explicit, DestroyCancellationToken);
        logger.LogInformation("[RawHtml] 函数已注册：渲染={Render}，美学注入={Aesthetic}，锁定风格={Style}",
            Configuration.RenderHtml, Configuration.AestheticInjection,
            string.IsNullOrWhiteSpace(Configuration.LockedStyle) ? "(自动)" : Configuration.LockedStyle);
    }

    // ================= AI 可调用函数 =================

    /// <summary>渲染一张卡片。内容模式（开闭标签包裹）+ 纯文本区：整段 HTML 原样到达，不做 XML 解析。</summary>
    [XmlFunction(FunctionMode.Content)]
    [Description("渲染一张裸 HTML 卡片：把标签内的 HTML 原样送到 Raw HTML 面板渲染成富卡片（KaTeX 公式 / Mermaid 图表 / SVG / 字体 / JS 动画 / 交互控件）。用法：<RenderHtmlCard><div id=\"vcp-root\">……</div></RenderHtmlCard>（HTML 直接写在标签内，不要放进 html 属性；一张卡一次调用）。")]
    public Task RenderHtmlCard(XmlExecutorContext context, CancellationToken cancellationToken = default)
    {
        // 开标签/正文分片都先攒着，等闭标签时一次性拿到完整 HTML（见 XmlStreamExecutor.OnTagClosed）
        if (context.CallMode != CallMode.Closing)
            return Task.CompletedTask;

        string html = context.FullContent.Trim();
        if (html.Length == 0)
        {
            interactor.Poke("未收到卡片内容，已忽略。");
            return Task.CompletedTask;
        }
        if (bridge == null || window == null)
        {
            interactor.Poke("Raw HTML 面板未启动，无法渲染。请检查插件日志。");
            return Task.CompletedTask;
        }
        int seq = Interlocked.Increment(ref cardSeq);
        try
        {
            // 与 ThreeDeskPet 的 bridge.SendMessage(...) 同构
            if (panelReady == false)
            {
                // 前端还没挂上 ipc.on，先排队，避免第一张卡片石沉大海
                lock (pendingCards) pendingCards.Add((seq, html));
                logger.LogInformation("[RawHtml] 卡片 #{Seq}（{Length} 字符）面板未就绪，已排队", seq, html.Length);
                interactor.Poke($"面板仍在加载，卡片 #{seq} 已排队，就绪后会自动渲染。");
                return Task.CompletedTask;
            }
            logger.LogInformation("[RawHtml] 卡片 #{Seq}（{Length} 字符）已下发渲染", seq, html.Length);
            bridge.SendMessage("renderHtml", new { html = html, seq = seq });
            interactor.Poke($"卡片 #{seq}（{html.Length} 字符）已下发渲染，等待前端回执…");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[RawHtml] 卡片 #{Seq} 下发失败", seq);
            interactor.Poke("渲染指令发送失败：" + ex.Message);
        }
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("把面板里最近渲染的那张卡片截图，发到 QQ（当用户在 QQ 群里或私聊里说「把这张卡发群里 / 发到群 / 私发给我 / 上图」时调用，AI 自己完成，不需要用户手动操作）。" +
        "本函数会自动把面板最近一张卡栅格化成图片文件、再用 QImage 发出，AI 不要自己用代码 / PIL 重画卡片（会丢失真实渲染效果且常失败）。" +
        "目标选取优先级：① 填了 targetUserId 则私发给该 QQ；② 填了 targetGroupId 则发到该群；③ 都不填则自动发往「QChat 中最近活跃(且已开启)的群」——通常就是当前正在聊天的群，因此最常用、无需填任何号码；④ 若连开启的群都没有，再用模块设置里的「QQ 群号（发图用）」。" +
        "若自动选错群，你也可直接从聊天上下文的「[群聊消息(群号)]」标签里读取群号，传给 targetGroupId 显式指定。" +
        "需已安装并连接「QQ聊天」(QChat 双向) 或「QQ单向发送」(QChatSender)。")]
    public async Task SendCardToQQ(
        [Description("要发送到的 QQ 群号；留 0 则发往当前聊天的群（最常用，可省略）")] long targetGroupId = 0,
        [Description("要私发到的 QQ 号；留 0 则按群号或当前会话决定")] long targetUserId = 0,
        CancellationToken cancellationToken = default)
    {
        // 优先用双向 QChat，未安装则退回单向 QChatSender
        bool useQChat = qChat != null && qChat.IsConnected;
        bool useSender = qSender != null && qSender.IsConnected;
        if (!useQChat && !useSender)
        {
            interactor.Poke("QQ 未连接：请先启动 OneBot 并连接「QQ聊天」(QChat) 或「QQ单向发送」(QChatSender)。");
            return;
        }
        // 决定目标：① 私聊QQ ② 群号 ③ 当前聊天会话（群/私聊）④ 模块默认群
        // OneBotMessageType：Private=0, Group=1
        int targetType;   // 0=私聊, 1=群
        long targetId;
        if (targetUserId != 0)
        {
            targetType = (int)Alife.Function.QChat.OneBotMessageType.Private;   // 0
            targetId = targetUserId;
        }
        else if (targetGroupId != 0)
        {
            targetType = (int)Alife.Function.QChat.OneBotMessageType.Group;     // 1
            targetId = targetGroupId;
        }
        else
        {
            // 自动选「当前/最近活跃的群」：只读 QChat 官方公开 API（GroupStates），不依赖任何改动过的官方插件，便于分发
            long? autoGroup = null;
            if (qChat != null)
            {
                var mostRecent = qChat.GroupStates.Values
                    .Where(g => g.IsEnabled)
                    .OrderByDescending(g => g.LastUsingTime)
                    .FirstOrDefault();
                if (mostRecent != null) autoGroup = mostRecent.Id;
            }
            if (autoGroup.HasValue)
            {
                targetType = (int)Alife.Function.QChat.OneBotMessageType.Group;   // 1
                targetId = autoGroup.Value;
            }
            else
            {
                targetType = (int)Alife.Function.QChat.OneBotMessageType.Group;   // 1
                targetId = Configuration.QQGroupId;   // 退回模块默认群
            }
        }
        if (targetId == 0)
        {
            interactor.Poke("未指定发送目标：请给 SendCardToQQ 填 targetGroupId（群号）或 targetUserId（私聊QQ号），或直接在 QQ 里说「发这张卡」（会自动发到当前聊天的群/私聊），或在模块设置里填「QQ 群号（发图用）」默认群。");
            return;
        }
        if (bridge == null || window == null || panelReady == false)
        {
            interactor.Poke("Raw HTML 面板未就绪，无法截图。");
            return;
        }

        string req = "qq" + Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (qqSendWaiters) qqSendWaiters[req] = tcs;

        lastRasterizeError = null;   // 重置，避免上一次的错误串味
        bridge.SendMessage("rasterizeLast", new { req = req });
        interactor.Poke("正在把最近一张卡片截图，准备发送" + (targetType == 1 ? $"到 QQ 群 {targetId}" : $"给 QQ {targetId}（私聊）") + "…");

        Task completed;
        using (cancellationToken.Register(() => tcs.TrySetCanceled()))
            completed = await Task.WhenAny(tcs.Task, Task.Delay(30000));   // 30s：给大图栅格化 + OneBot 上传留足时间

        string? base64 = (completed == tcs.Task && tcs.Task.Status == TaskStatus.RanToCompletion) ? tcs.Task.Result : null;
        lock (qqSendWaiters) qqSendWaiters.Remove(req);

        if (string.IsNullOrEmpty(base64))
        {
            string why = string.IsNullOrEmpty(lastRasterizeError)
                ? "前端未在 30 秒内回传图片（可能卡片为空或面板未就绪）"
                : lastRasterizeError;
            logger.LogInformation("[RawHtml] SendCardToQQ 失败（base64 为空）: targetType={TargetType} targetId={TargetId} reason={Why}", targetType, targetId, why);
            interactor.Poke("截图发送" + (targetType == 1 ? $"到 QQ 群 {targetId}" : $"给 QQ {targetId}（私聊）") + " 失败：" + why + "。可改用「导出 HTML 文件」后手动发，不要用代码/PIL 重画卡片。");
            return;
        }

        string data = base64.IndexOf(',') >= 0 ? base64.Substring(base64.IndexOf(',') + 1) : base64;
        string dir = Path.Combine(SaveDir, "qq");
        Directory.CreateDirectory(dir);
        string fileName = "RawHtml-QQ-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png";
        string path = Path.Combine(dir, fileName);
        try
        {
            byte[] bytes = Convert.FromBase64String(data);
            File.WriteAllBytes(path, bytes);
            var qchatType = (Alife.Function.QChat.OneBotMessageType)targetType;
            var senderType = (Alife.Function.QChatSender.OneBotMessageType)targetType;
            if (useQChat)
                await qChat!.QImage(qchatType, targetId, path.Replace('\\', '/'));
            else
                await qSender!.QImage(senderType, targetId, path.Replace('\\', '/'));
            // 发完即清：QImage 已把图上传给 QQ，本地临时 PNG 立即删除，缓存不堆积
            try { File.Delete(path); } catch { /* 删除失败不阻塞，交给 CleanQqCache 兜底 */ }
            CleanQqCache(dir);   // 兜底：删 7 天前的、且最多留 50 张，清掉任何遗留 / 失败产生的文件
            logger.LogInformation("[RawHtml] 已发送卡片图片到 QQ（{Type} {Id}）", targetType == 1 ? "群" : "私聊", targetId);
            interactor.Poke(targetType == 1
                ? $"已把最近一张卡片发到 QQ 群 {targetId}。"
                : $"已把最近一张卡片私发给 QQ {targetId}。");
        }
        catch (Exception e)
        {
            logger.LogError(e, "[RawHtml] 发送卡片到 QQ 群失败");
            interactor.Poke("发送到 QQ 失败：" + e.Message);
        }
    }

    /// <summary>清理 Saved/qq 缓存：删 7 天前的，且最多保留最近 50 张，避免 AI 反复发图把磁盘占满。</summary>
    private void CleanQqCache(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            var files = Directory.GetFiles(dir, "*.png").Select(f => new FileInfo(f)).ToArray();
            DateTime cutoff = DateTime.Now.AddDays(-7);
            foreach (var f in files.Where(f => f.LastWriteTime < cutoff))
            {
                try { f.Delete(); } catch { /* 忽略 */ }
            }
            var recent = files.Where(f => f.LastWriteTime >= cutoff)
                              .OrderByDescending(f => f.LastWriteTime).ToArray();
            for (int i = 50; i < recent.Length; i++)
            {
                try { recent[i].Delete(); } catch { /* 忽略 */ }
            }
        }
        catch (Exception e) { logger.LogWarning(e, "[RawHtml] 清理 QQ 缓存失败"); }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("列出可用的美学风格（slug — 名称 · 适用场景），需要自选风格时先看这个。")]
    public Task ListAestheticStyles(CancellationToken cancellationToken = default)
    {
        if (library == null)
        {
            interactor.Poke("美学库未就绪。");
            return Task.CompletedTask;
        }
        List<StyleInfo> styles = library.ListStyles();
        if (styles.Count == 0)
        {
            interactor.Poke("没有找到任何风格文档。");
            return Task.CompletedTask;
        }
        string locked = AestheticLibrary.NormalizeSlug(Configuration.LockedStyle);
        string body = string.Join("\n", styles.Select(s =>
            $"- {s.Slug}{(s.Slug == locked ? " ←当前锁定" : "")} — {s.Name}{(s.Scene.Length > 0 ? " · " + s.Scene : "")}{(s.Builtin ? "" : "（自定义）")}"));
        interactor.Poke($"可用美学风格（{styles.Count} 个；锁定状态：{(locked.Length == 0 ? "未锁定，可自选" : locked)}）：\n{body}\n\n" +
                       "要锁定某个风格用 <LockAestheticStyle slug=\"...\"/>；要看文档全文用 <ReadAestheticStyle slug=\"...\"/>。");
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("锁定一个美学风格（之后所有卡片都按它输出；slug 留空=解除锁定）。")]
    public Task LockAestheticStyle([Description("风格 slug，如 porcelain-data / brutalism / ink-letter，留空解除")] string? slug = null, CancellationToken cancellationToken = default)
    {
        string want = AestheticLibrary.NormalizeSlug(slug);
        if (want.Length > 0 && library?.FindStyle(want) == null)
        {
            interactor.Poke($"没有风格「{want}」。可用：{string.Join("、", library?.ListStyles().Select(s => s.Slug) ?? [])}");
            return Task.CompletedTask;
        }
        SetLockedStyle(want, fromAi: true);
        interactor.Poke(want.Length == 0
            ? "已解除风格锁定，接下来由你按主题自选风格。"
            : $"已锁定风格「{library?.FindStyle(want)?.Name ?? want}」，之后所有卡片都按它输出。");
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("读取某个美学风格文档的全文（配色/骨架/字体/技法细则）。")]
    public Task ReadAestheticStyle([Description("风格 slug")] string slug, CancellationToken cancellationToken = default)
    {
        string? doc = library?.ReadStyleDoc(slug);
        if (doc == null)
        {
            interactor.Poke($"没有风格文档：{slug}");
            return Task.CompletedTask;
        }
        interactor.Poke($"风格「{AestheticLibrary.NormalizeSlug(slug)}」文档全文：\n\n{doc}");
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("新建一个自定义美学风格（会写进用户的风格库，之后可在面板里锁定/编辑）。把想法提炼成可复用的风格规范时调用。")]
    public Task CreateAestheticStyle(
        [Description("风格中文名，如「青灰档案」")] string name,
        [Description("英文小写 slug（可空，系统按名字生成）")] string? slug = null,
        [Description("适用场景，如「数据/学术/理性」")] string? scene = null,
        [Description("标签，如「极简/档案/冷调」")] string? tags = null,
        [Description("色板 hex（空格分隔，3-5 个，按重要性排列），如 #F0EFEB #1C1C1A #334EAC")] string? colors = null,
        [Description("字体安排，如「标题：Lanxi-鱼尾行书；正文：系统无衬线」")] string? fonts = null,
        [Description("风格说明：核心命题与差异化准则 2-4 句")] string? notes = null,
        CancellationToken cancellationToken = default)
    {
        if (library == null)
        {
            interactor.Poke("美学库未就绪。");
            return Task.CompletedTask;
        }
        string want = AestheticLibrary.NormalizeSlug(string.IsNullOrWhiteSpace(slug) ? name : slug);
        if (want.Length == 0)
        {
            interactor.Poke("风格名无效，无法生成 slug。");
            return Task.CompletedTask;
        }
        string doc = AestheticLibrary.BuildStyleDoc(want, name.Trim(), scene ?? "", tags ?? "", colors ?? "", fonts ?? "", notes ?? "");
        string? error = library.WriteStyleDoc(want, doc);
        if (error != null)
        {
            interactor.Poke("写入风格失败：" + error);
            return Task.CompletedTask;
        }
        logger.LogInformation("[RawHtml] 已新建自定义风格 {Slug}", want);
        SendPanelState();
        interactor.Poke($"已新建风格「{name}」(slug: {want})，可在面板『美学系统』里查看/锁定；本次会话想用它就用 <LockAestheticStyle slug=\"{want}\"/>。");
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("把面板里当前所有卡片保存成自包含 HTML 文件（内联样式 + 内嵌 Lanxi/KaTeX 字体，可脱机打开）。卡片内容不会丢，面板重启后也能恢复。")]
    public async Task SaveHtmlCards([Description("文件名（可空，自动按日期生成）")] string? name = null, CancellationToken cancellationToken = default)
    {
        List<KeyValuePair<string, string>> history = LoadHistory(Configuration.RestoreCount < 0 ? 0 : Configuration.RestoreCount);
        if (history.Count == 0)
        {
            interactor.Poke("面板里还没有卡片，没有可保存的产物。");
            return;
        }

        string fileName = SafeFileName(name, Configuration.LockedStyle, ".html");
        string path = Path.Combine(SaveDir, fileName);
        try
        {
            Directory.CreateDirectory(SaveDir);
            string body = string.Join("\n\n", history.Select(h => h.Value));
            File.WriteAllText(path, BuildStandaloneHtml(body), new System.Text.UTF8Encoding(false));
            logger.LogInformation("[RawHtml] 已保存 {Count} 张卡片：{Path}", history.Count, path);
            interactor.Poke($"已保存 {history.Count} 张卡片到：{path}");
        }
        catch (Exception e)
        {
            logger.LogError(e, "[RawHtml] 保存卡片失败");
            interactor.Poke("保存卡片失败：" + e.Message);
        }
    }

    /// <summary>把当前卡片写进历史（面板渲染成功后上报）。</summary>
    public Task AddCardToHistory(string ts, string style, string html)
    {
        AppendHistory(ts, style, html);
        return Task.CompletedTask;
    }

    // ================= 配置 / 面板联动 =================

    private void PersistConfig()
    {
        try { configurationSystem.SetConfiguration(GetType(), Configuration, Character.StorageKey); }
        catch (Exception ex) { logger.LogWarning(ex, "保存 RawHtml 配置失败"); }
        RememberConfigStamp();   // 自己写的改动不算「外部改动」，避免 OnUpdate 里的监听重复触发
    }

    // ---- 监听模块设置页里的配置改动，改完就地生效（不必重载插件）----

    private DateTime configStamp = DateTime.MinValue;
    private long lastConfigCheck;

    private void RememberConfigStamp()
    {
        try
        {
            string? path = configurationSystem.GetConfigurationFilePath(GetType(), Character.StorageKey);
            configStamp = path != null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch { /* 忽略 */ }
    }

    protected override Task OnUpdate()
    {
        long now = Environment.TickCount64;
        if (now - lastConfigCheck < 2000) return Task.CompletedTask;
        lastConfigCheck = now;
        try
        {
            string? path = configurationSystem.GetConfigurationFilePath(GetType(), Character.StorageKey);
            if (path == null || File.Exists(path) == false) return Task.CompletedTask;
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            if (configStamp == DateTime.MinValue) { configStamp = stamp; return Task.CompletedTask; }
            if (stamp == configStamp) return Task.CompletedTask;
            configStamp = stamp;

            if (configurationSystem.GetConfiguration(GetType(), Character.StorageKey) is not RawHtmlConfig reloaded) return Task.CompletedTask;
            bool changed =
                reloaded.RenderHtml != Configuration.RenderHtml ||
                reloaded.AestheticInjection != Configuration.AestheticInjection ||
                reloaded.LockedStyle != Configuration.LockedStyle ||
                reloaded.InjectDocPaths != Configuration.InjectDocPaths ||
                reloaded.ExtraStyleDirs != Configuration.ExtraStyleDirs ||
                reloaded.CustomStyleDirs != Configuration.CustomStyleDirs;
            if (changed == false) return Task.CompletedTask;

            logger.LogInformation("[RawHtml] 检测到模块设置页的配置改动，就地生效");
            Configuration = reloaded;
            ApplyConfigToHandler();
            SendPanelState();
        }
        catch (Exception ex) { logger.LogWarning(ex, "监听 RawHtml 配置改动失败"); }
        return Task.CompletedTask;
    }

    private void SetRender(bool on)
    {
        if (Configuration.RenderHtml == on) return;
        Configuration.RenderHtml = on;
        if (on == false) Configuration.AestheticInjection = false;   // 渲染关闭 → 美学强制关闭（与 DSH 一致）
        PersistConfig();
        ApplyConfigToHandler();
        SendPanelState();
    }

    private void SetAesthetic(bool on)
    {
        bool want = Configuration.RenderHtml && on;
        if (Configuration.AestheticInjection == want) return;
        Configuration.AestheticInjection = want;
        PersistConfig();
        ApplyConfigToHandler();
        SendPanelState();
    }

    private void SetLockedStyle(string slug, bool fromAi = false)
    {
        slug = AestheticLibrary.NormalizeSlug(slug);
        if (Configuration.LockedStyle == slug) { SendPanelState(); return; }
        Configuration.LockedStyle = slug;
        PersistConfig();
        ApplyConfigToHandler();
        SendPanelState();
        if (!fromAi)
        {
            StyleInfo? info = slug.Length == 0 ? null : library?.FindStyle(slug);
            logger.LogInformation("[RawHtml] 锁定风格 → {Slug}", slug.Length == 0 ? "(自动)" : slug);
            interactor.Poke(slug.Length == 0
                ? "已解除美学风格锁定：AI 会按主题自选风格。"
                : $"已锁定美学风格「{info?.Name ?? slug}」：接下来的卡片都会按这个风格输出（下一次回复生效）。");
        }
    }

    private void SendPanelState()
    {
        if (bridge == null) return;
        try
        {
            // 历史记录面板展示本地全部留存（文件已在落盘时按上限裁剪）；重启后不再自动重放
            List<KeyValuePair<string, string>> history = LoadHistory(int.MaxValue);
            string json = library?.BuildStateJson(Configuration.RenderHtml, Configuration.AestheticInjection,
                Configuration.LockedStyle, history, SaveDir, Configuration.AutoSaveOnRender) ?? "{}";
            bridge.SendMessage("state", new { json = json });
        }
        catch (Exception ex) { logger.LogWarning(ex, "推送面板状态失败"); }
    }

    // ---- 导出：把卡片原文包成自包含 HTML ----

    /// <summary>把一张卡片的 HTML 包成可脱机查看的独立文档（内联 KaTeX 样式 + 内嵌所用 Lanxi 字体）。</summary>
    private string BuildStandaloneHtml(string cardHtml)
    {
        string body = NormalizeFontUrls(cardHtml);
        string style = library?.FontFaceCss(AestheticLibrary.ExtractFontAliases(body)) ?? "";
        System.Text.StringBuilder sb = new();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\" />");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\" />");
        sb.AppendLine("<title>VCP 卡片</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(KatexCss());
        sb.AppendLine(style);
        sb.AppendLine("html,body{margin:0;padding:0;background:#0f1115;color:#e7e7e7;font-family:ui-sans-serif,system-ui,'Segoe UI','Microsoft YaHei',sans-serif}");
        sb.AppendLine("#cards{padding:14px;display:flex;flex-direction:column;gap:16px}.vcp-card-wrap{width:100%}");
        sb.AppendLine("</style></head><body><div id=\"cards\">");
        sb.AppendLine(body);
        sb.AppendLine("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>导出用的 KaTeX 样式：把 url() 指向的字体换成 data URI，脱机也能显示公式。</summary>
    private string KatexCss()
    {
        try
        {
            string file = Path.Combine(resourcesDir, "vendor", "katex.min.css");
            if (File.Exists(file) == false) return "";
            string css = File.ReadAllText(file);
            string fontsRoot = Path.Combine(resourcesDir, "vendor", "fonts");
            return Regex.Replace(css,
                @"url\(\s*(['""]?)(?:[^)'""]*/)?fonts/(?<n>[A-Za-z0-9_\-]+\.woff2?)",
                m =>
                {
                    string name = m.Groups["n"].Value;
                    string fontFile = Path.Combine(fontsRoot, name);
                    if (File.Exists(fontFile) == false) return m.Value;
                    string mime = name.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase) ? "font/woff2" : "font/woff";
                    return "url(" + DataUri(fontFile, mime) + ")";
                });
        }
        catch { return ""; }
    }

    private static string NormalizeFontUrls(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        return Regex.Replace(html, @"url\(\s*(['""]?)/(?:fonts-v2|fonts|vendor-v2|vendor)/", "url($1");
    }

    /// <summary>把文件读成 data URI（导出文件可脱机打开）。读不到时返回空串。</summary>
    private static string DataUri(string path, string mime)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
        }
        catch { return ""; }
    }

    /// <summary>给导出文件生成一个安全的文件名（去掉系统非法字符，必要时按风格 slug 区分）。</summary>
    private static string SafeFileName(string? name, string? style, string ext = ".html")
    {
        string baseName = (name ?? "").Trim();
        if (baseName.Length == 0)
            baseName = "RawHtml-卡片-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        baseName = string.Join("_", baseName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (style != null && style.Length > 0)
            baseName += "-" + AestheticLibrary.NormalizeSlug(style);
        if (baseName.Length == 0) baseName = "RawHtml-card";
        if (!ext.StartsWith(".")) ext = "." + ext;
        return baseName + ext;
    }

    // ================= 面板 → C# 消息 =================

    /// <summary>处理渲染进程回传（渲染回执 / 面板设置 / 卡片内按钮点击）。</summary>
    private void OnBridgeMessage(string type, JsonElement payload)
    {
        // 前端就绪信号：flush 排队卡片 + 推配置
        if (type == "init")
        {
            (int Seq, string Html)[] queued;
            lock (pendingCards)
            {
                queued = pendingCards.ToArray();
                pendingCards.Clear();
            }
            panelReady = true;
            SendPanelState();
            foreach ((int seq, string card) in queued)
            {
                try
                {
                    logger.LogInformation("[RawHtml] 补发排队卡片 #{Seq}（{Length} 字符）", seq, card.Length);
                    bridge?.SendMessage("renderHtml", new { html = card, seq = seq });
                }
                catch (Exception ex) { logger.LogWarning(ex, "RawHtml 补发排队卡片失败"); }
            }
            if (queued.Length > 0)
                interactor.Poke($"面板已就绪，补发了 {queued.Length} 张排队卡片。");
            return;
        }
        if (type == "renderResult")
        {
            // 前端渲染的真实回执：成功/失败都由它决定，不再由「已发送」假装成功
            bool ok = payload.TryGetProperty("ok", out JsonElement okEl) && okEl.ValueKind == JsonValueKind.True;
            int seq = payload.TryGetProperty("seq", out JsonElement seqEl) && seqEl.TryGetInt32(out int s) ? s : 0;
            if (ok)
            {
                int ms = payload.TryGetProperty("ms", out JsonElement msEl) && msEl.TryGetInt32(out int m) ? m : 0;
                logger.LogInformation("[RawHtml] 卡片 #{Seq} 渲染成功（{Ms} ms）", seq, ms);
                interactor.Poke($"卡片 #{seq} 已渲染（{ms} ms）。");
            }
            else
            {
                string err = payload.TryGetProperty("error", out JsonElement errEl) ? errEl.GetString() ?? "未知错误" : "未知错误";
                logger.LogError("[RawHtml] 卡片 #{Seq} 渲染失败：{Error}", seq, err);
                interactor.Poke($"卡片 #{seq} 渲染失败：{err}（面板里也显示了报错与原始 HTML）");
            }
            return;
        }
        if (type.StartsWith("panel", StringComparison.Ordinal))
        {
            _ = HandlePanelMessage(type, payload);   // 内部自带 try/catch，不冒泡到 IPC 线程
            return;
        }
        if (type == "host")
        {
            string? text = payload.TryGetProperty("text", out JsonElement t) ? t.GetString() : null;
            if (!string.IsNullOrEmpty(text)) interactor.Poke(text);
        }
        if (type == "log")
        {
            string level = payload.TryGetProperty("level", out JsonElement lv) ? lv.GetString() ?? "info" : "info";
            string text = payload.TryGetProperty("text", out JsonElement t) ? t.GetString() ?? "" : "";
            if (text.Length == 0) return;
            if (level is "error" or "unhandledrejection" or "resource-error") logger.LogWarning("[RawHtml前端/{Level}] {Text}", level, text);
            else logger.LogInformation("[RawHtml前端/{Level}] {Text}", level, text);
        }
        // 前端把「最近一张卡片」栅格化后的 PNG 回传（供 SendCardToQQ 使用）。
        // 注意：它不是 panel* 前缀，OnBridgeMessage 不会把它转给 HandlePanelMessage，
        // 必须在此直接处理，否则消息会被静默丢弃（之前就是漏了这条路由）。
        if (type == "lastCardImage")
        {
            string str(string k) => payload.TryGetProperty(k, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
            string req = str("req");
            string data = str("data");
            string err = str("error");
            if (!string.IsNullOrEmpty(err)) lastRasterizeError = err;
            logger.LogInformation("[RawHtml] lastCardImage 收到: req={Req} dataLen={DataLen} error={Err}", req, (data ?? "").Length, string.IsNullOrEmpty(err) ? "(none)" : err);
            TaskCompletionSource<string>? tcs = null;
            lock (qqSendWaiters) { if (qqSendWaiters.TryGetValue(req, out tcs)) qqSendWaiters.Remove(req); }
            tcs?.TrySetResult(data ?? "");
            return;
        }
    }

    private async Task HandlePanelMessage(string type, JsonElement payload)
    {
        string Str(string name) => payload.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
        bool Flag(string name) => payload.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.True;

        try
        {
            switch (type)
            {
                case "panelRequestState":
                    SendPanelState();
                    break;
                case "panelSetRender":
                    SetRender(Flag("value"));
                    break;
                case "panelSetAesthetic":
                    SetAesthetic(Flag("value"));
                    break;
                case "panelLockStyle":
                    SetLockedStyle(Str("slug"));
                    break;
                case "panelAddHistory":         // 面板渲染成功 → 落盘（用于重启后恢复）
                    AppendHistory(Str("ts"), Str("style"), Str("html"));
                    break;
                case "panelClearHistory":
                    ClearHistory();
                    break;
                case "panelDeleteHistoryItem":   // 历史记录面板：删除指定一条
                    DeleteHistoryItem(Str("ts"));
                    break;
                case "panelSaveHtml":           // 导出一张 / 全部卡片为独立 HTML
                {
                    string fileName = SafeFileName(Str("name"), Str("style"));
                    string path = Path.Combine(SaveDir, fileName);
                    try
                    {
                        Directory.CreateDirectory(SaveDir);
                        File.WriteAllText(path, BuildStandaloneHtml(Str("html")), new System.Text.UTF8Encoding(false));
                        logger.LogInformation("[RawHtml] 已保存卡片：{Path}", path);
                        bridge?.SendMessage("toast", new { text = "已保存：" + path });
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "[RawHtml] 保存卡片失败");
                        bridge?.SendMessage("toast", new { text = "保存失败：" + e.Message });
                    }
                    break;
                }
                case "panelSavePdf":           // 导出整页为 PDF（Electron 打印，保留背景）
                {
                    string fileName = SafeFileName(Str("name"), Str("style"));
                    string path = Path.Combine(SaveDir, fileName);
                    try
                    {
                        Directory.CreateDirectory(SaveDir);
                        if (window != null)
                        {
                            await window.Window.WebContents.PrintToPDFAsync(path, new ElectronNET.API.Entities.PrintToPDFOptions {
                                PrintBackground = true,
                                Landscape = false
                            });
                            logger.LogInformation("[RawHtml] 已保存 PDF：{Path}", path);
                            bridge?.SendMessage("toast", new { text = "已保存 PDF：" + path });
                        }
                        else bridge?.SendMessage("toast", new { text = "面板未启动，无法导出 PDF" });
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "[RawHtml] 保存 PDF 失败");
                        bridge?.SendMessage("toast", new { text = "PDF 保存失败：" + e.Message });
                    }
                    break;
                }
                case "panelSavePng":            // 前端用 SVG→canvas 栅格化后传来的 base64 PNG
                {
                    string fileName = SafeFileName(Str("name"), Str("style"), ".png");
                    string data = Str("data");
                    int comma = data.IndexOf(',');
                    if (comma >= 0) data = data.Substring(comma + 1);   // 去掉 data:image/png;base64, 前缀
                    if (data.Length == 0) { bridge?.SendMessage("toast", new { text = "PNG 数据为空" }); break; }
                    string path = Path.Combine(SaveDir, fileName);
                    try
                    {
                        Directory.CreateDirectory(SaveDir);
                        byte[] bytes = Convert.FromBase64String(data);
                        File.WriteAllBytes(path, bytes);
                        logger.LogInformation("[RawHtml] 已保存 PNG：{Path}", path);
                        bridge?.SendMessage("toast", new { text = "已保存 PNG：" + path });
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "[RawHtml] 保存 PNG 失败");
                        bridge?.SendMessage("toast", new { text = "PNG 保存失败：" + e.Message });
                    }
                    break;
                }
                case "panelPngCss":             // 生成 PNG 前：让宿主把 KaTeX / 用到的 Lanxi 字体内联成 data URI 返回
                {
                    string req = Str("req");
                    string css;
                    try { css = KatexCss() + "\n" + (library?.FontFaceCss(AestheticLibrary.ExtractFontAliases(Str("html"))) ?? ""); }
                    catch (Exception e) { logger.LogWarning(e, "[RawHtml] 生成 PNG 内联样式失败"); css = ""; }
                    bridge?.SendMessage("pngCss", new { req = req, css = css });
                    break;
                }
                case "panelReadStyle":
                {
                    string slug = AestheticLibrary.NormalizeSlug(Str("slug"));
                    string? doc = library?.ReadStyleDoc(slug);
                    StyleInfo? info = library?.FindStyle(slug);
                    bridge?.SendMessage("styleDoc", new {
                        slug,
                        name = info?.Name ?? slug,
                        builtin = info?.Builtin ?? false,
                        doc = doc ?? $"（找不到风格文档：{slug}）"
                    });
                    break;
                }
                case "panelCreateStyle":
                {
                    string name = Str("name");
                    if (string.IsNullOrWhiteSpace(name)) { bridge?.SendMessage("toast", new { text = "风格名不能为空" }); break; }
                    string want = AestheticLibrary.NormalizeSlug(Str("slug").Length > 0 ? Str("slug") : name);
                    bool exists = library?.FindStyle(want) != null;
                    string doc = AestheticLibrary.BuildStyleDoc(want, name, Str("scene"), Str("tags"), Str("colors"), Str("fonts"), Str("notes"));
                    string? error = library?.WriteStyleDoc(want, doc);
                    if (error != null) { bridge?.SendMessage("toast", new { text = "保存失败：" + error }); break; }
                    logger.LogInformation("[RawHtml] 面板新建/覆盖风格 {Slug}", want);
                    SendPanelState();
                    bridge?.SendMessage("toast", new { text = (exists ? "已覆盖风格 " : "已新建风格 ") + want });
                    break;
                }
                case "panelDeleteStyle":
                {
                    string slug = AestheticLibrary.NormalizeSlug(Str("slug"));
                    string? error = library?.DeleteStyle(slug);
                    if (error != null) { bridge?.SendMessage("toast", new { text = error }); break; }
                    if (AestheticLibrary.NormalizeSlug(Configuration.LockedStyle) == slug) SetLockedStyle("");
                    logger.LogInformation("[RawHtml] 面板删除自定义风格 {Slug}", slug);
                    SendPanelState();
                    bridge?.SendMessage("toast", new { text = "已删除 " + slug });
                    break;
                }
                case "panelOpenDocs":
                {
                    string docsDir = library?.DocsDir ?? "";
                    if (Directory.Exists(docsDir)) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", docsDir) { UseShellExecute = true }); } catch { /* 忽略 */ } }
                    break;
                }
                case "panelReloadPage":
                    bridge?.SendMessage("reload", new { });
                    break;
            }
        }
        catch (Exception ex) { logger.LogError(ex, "处理面板消息 {Type} 失败", type); }
    }

    protected override async Task OnDestroy()
    {
        // 先摘回调，再关窗口：避免在途消息打到已销毁的 BrowserWindow（主进程会抛 BrowserWindow not found）
        if (bridge != null)
        {
            try { bridge.OnMessage -= OnBridgeMessage; } catch { /* 忽略 */ }
            try { bridge.Dispose(); } catch { /* 忽略 */ }
        }
        bridge = null;
        RawHtmlWindow? w = window;
        window = null;
        if (w != null)
        {
            if (ReferenceEquals(leakedWindow, w)) leakedWindow = null;   // 正常释放：撤掉泄漏登记
            await Task.Delay(150);   // 给在途的窗口命令一点落地时间
            try { w.Dispose(); } catch { /* 忽略 */ }
        }
        panelReady = false;
        lock (pendingCards) pendingCards.Clear();
        await base.OnDestroy();
    }
}
