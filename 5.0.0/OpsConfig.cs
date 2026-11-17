using System.ComponentModel;

namespace presents.OpsSuite;

/// <summary>运维套件的全部可调参数。所有采集默认只读，不会主动改动别的插件。</summary>
public sealed class OpsConfig
{
    [DisplayName("采样间隔（秒）")]
    [Description("进程 / 显存 / 端口的采样周期。设为 0 可关闭自动采样，只保留手动快照。")]
    public int SampleIntervalSeconds { get; set; } = 5;

    [DisplayName("保留历史快照数")]
    [Description("内存中的滚动历史长度，用于趋势图与热重载 diff。")]
    public int HistoryLength { get; set; } = 120;

    [DisplayName("自动记录热重载前后快照")]
    [Description("监听插件加载 / 卸载事件，自动拍前后两张快照并计算差值。")]
    public bool AutoReloadDiff { get; set; } = true;

    [DisplayName("配置变更自动留档")]
    [Description("扫描 Storage 下所有 Configuration/*.json，发现变化就存一份历史版本。")]
    public bool WatchConfigChanges { get; set; } = true;

    [DisplayName("配置历史保留份数（每个文件）")]
    public int ConfigHistoryPerFile { get; set; } = 20;

    [DisplayName("显存告警阈值（MB）")]
    [Description("任一进程专用显存超过该值即触发资源事件；0 = 不告警。")]
    public int GpuAlertMB { get; set; } = 1200;

    [DisplayName("工作集告警阈值（MB）")]
    public int WorkingSetAlertMB { get; set; } = 1500;

    [DisplayName("进程数异常增长阈值")]
    [Description("两次采样之间，同名进程增加超过该数量即记为可疑残留。")]
    public int ProcessGrowthAlert { get; set; } = 4;

    [DisplayName("自动降级通知")]
    [Description("超阈值时向角色推一条提示，并发布可被 EventFlow 订阅的资源事件。")]
    public bool NotifyOnBudgetExceeded { get; set; } = true;

    [DisplayName("告警冷却（秒）")]
    [Description("同一条告警在该时间内只记一次，避免每轮采样重复刷屏；0 = 不去重。恢复正常后冷却自动清零。")]
    public int AlertCooldownSeconds { get; set; } = 300;

    [DisplayName("面板端口（0 = 随机）")]
    [Description("本地只读面板地址。0 表示随机端口，端口会写入 %TEMP%/alife-ops.port。")]
    public int PanelPort { get; set; } = 0;

    [DisplayName("启用页面注入层审计")]
    [Description("通过 GlobalUI 扫描主页面 window.__* 全局、遗留 DOM 与 fixed 遮挡层。")]
    public bool EnablePageAudit { get; set; } = true;

    [DisplayName("启用 AI 工具集")]
    public bool EnableAiFunctions { get; set; } = true;
}
