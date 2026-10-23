using System;
using System.ComponentModel;

namespace presents.RawHtml;

/// <summary>
/// RawHtml 插件配置（实现 IConfigurable 后由框架自动挂到模块设置页，改动即时落盘）。
/// 面板里的「渲染设置」与这里读写同一份配置。
/// </summary>
public class RawHtmlConfig
{
    [DisplayName("渲染 HTML")]
    [Description("作用：AI 调用 RenderHtmlCard 时是否真的渲染成卡片。关闭=不注册该函数，AI 输出裸 HTML 只会当作普通文本（等价 DSH 的「渲染 HTML」开关）。")]
    public bool RenderHtml { get; set; } = true;

    [DisplayName("美学注入")]
    [Description("作用：是否向 AI 注入「美学系统」协议（审美公约 + 风格库 + 字体规范 + 锁定风格文档）。关闭=只注入渲染协议，风格完全自由。渲染关闭时本项无效。")]
    public bool AestheticInjection { get; set; } = true;

    [DisplayName("锁定的美学风格")]
    [Description("作用：锁定后，本会话所有视觉卡片一律按该风格输出。填风格 slug（如 porcelain-data / brutalism / ink-letter），留空=由 AI 按主题自选。面板「美学系统」里点风格卡即可锁定/解锁。")]
    public string LockedStyle { get; set; } = string.Empty;

    [DisplayName("自定义风格目录")]
    [Description("作用：存放你自己新建/编辑的风格文档（.md）。留空=默认 Storage/<角色>/RawHtml/Styles。多个目录用分号或换行分隔。")]
    public string CustomStyleDirs { get; set; } = string.Empty;

    [DisplayName("外置风格目录")]
    [Description("作用：挂载外部美学风格库（如别人分享的风格包目录），只读参与检索与列表。多个目录用分号或换行分隔。")]
    public string ExtraStyleDirs { get; set; } = string.Empty;

    [DisplayName("注入设计手册路径")]
    [Description("作用：把插件内置的设计手册（DESIGN.md / EDITORIAL.md / FRAMING.md / VCP-INTERACTIONS.md）路径写进提示词，AI 需要细节时可以自己去读。关闭可省几十 token。")]
    public bool InjectDocPaths { get; set; } = true;

    [DisplayName("保存目录")]
    [Description("作用：导出卡片文件放哪。留空=默认 Storage/<角色>/RawHtml/Saved。支持分号/换行分隔多个目录（第一个用于写入）。")]
    public string SaveDirectory { get; set; } = string.Empty;

    [DisplayName("自动保存每次渲染")]
    [Description("作用：AI 每次渲染卡片时，顺手把卡片原文存成 .html 落盘（内容去重）。关掉则只在点「保存」时写文件。")]
    public bool AutoSaveOnRender { get; set; } = true;

    [DisplayName("历史留存上限")]
    [Description("作用：本地卡片历史（cards.jsonl）最多保留多少张，超出只留最近 N 张（0=不限制）。重启后默认不再自动重放，可在面板「🕘 历史记录」里查看、恢复或删除。")]
    public int RestoreCount { get; set; } = 100;

    [DisplayName("QQ 群号（发图用）")]
    [Description("作用：AI 在 QQ 群里把卡片发给你指定的群（SendCardToQQ 的默认目标；AI 也可直接传群号覆盖）。需已安装并连接「QQ聊天」(QChat 双向) 或「QQ单向发送」(QChatSender)。留 0 = 未设默认群，必须由 AI 显式传群号。")]
    public long QQGroupId { get; set; } = 0;
    public static string[] SplitDirs(string raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
