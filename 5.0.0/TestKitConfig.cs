using System.ComponentModel;

namespace presents.PluginTestKit;

public sealed class TestKitConfig
{
    [DisplayName("启用静态结构检查")]
    [Description("manifest 字段、重复类型名、依赖环、目录卫生、JS 裸标识符与未纳入生命周期的事件监听。")]
    public bool EnableStaticChecks { get; set; } = true;

    [DisplayName("启用 JS 注入脚本测试")]
    [Description("把 C# raw string 里的注入脚本抽出来，在 node + 内置 DOM 桩里执行，检查语法与运行期异常。")]
    public bool EnableJsTests { get; set; } = true;

    [DisplayName("启用面板 UI 冒烟")]
    [Description("用 agent-browser 真打开每个本地面板，抓 JavaScript 运行时错误。会弹浏览器窗口、每个面板约 5 秒，默认关闭；建议用 testui 工具按需触发。")]
    public bool EnableUiSmokeChecks { get; set; } = false;

    [DisplayName("浏览器命令")]
    [Description("抓面板错误用的 CLI，默认 agent-browser（npm i -g agent-browser）。留空则跳过 UI 冒烟。")]
    public string UiBrowserCommand { get; set; } = "agent-browser";

    [DisplayName("启用真编译检查")]
    [Description("用宿主同一套引用集把插件源码重新编译一遍。Alife 用 Roslyn 逐文件直编、不开 ImplicitUsings，本地若开了隐式 using，缺 using 的代码本地全绿、到客户端会炸成一片 CS0246 且模块直接消失。这类问题只有真编译能抓，默认开。")]
    public bool EnableCompileChecks { get; set; } = true;

    [DisplayName("启用契约检查")]
    [Description("检查编译产物是否比源码新（改了没重编译）、[Module] 是否声明 editorUI、配置类是否用 public 字段/集合未初始化、事件订阅是否在 OnDestroy 退订。")]
    public bool EnableContractChecks { get; set; } = true;

    [DisplayName("启用 XmlFunction 契约检查")]
    [Description("解析源码里的 [XmlFunction]，检查工具名重复/非法、参数类型 AI 能否生成、参数与说明是否缺失。这类问题编译和运行都不报，只在 AI 真调用时暴露。")]
    public bool EnableXmlFunctionChecks { get; set; } = true;

    [DisplayName("启用配置迁移防覆盖检查")]
    [Description("对比配置历史版本，检测「同名条目整组被替换」这类会弄丢用户参数的问题。")]
    public bool EnableMigrationChecks { get; set; } = true;

    [DisplayName("启用自定义用例 Tests/*.test.cjs")]
    public bool EnableCustomTests { get; set; } = true;

    [DisplayName("热重载回归（次数，0 = 关闭）")]
    [Description("会真实重载指定插件并比对进程数/显存。默认关闭，避免在你工作时打断界面。需要显式通过 AI 或面板触发。")]
    public int ReloadIterations { get; set; } = 0;

    [DisplayName("热重载回归目标插件")]
    public string ReloadTarget { get; set; } = "presents.Wallpaper";

    [DisplayName("Node 路径（空 = 自动探测）")]
    public string NodePath { get; set; } = "";

    [DisplayName("面板端口（0 = 随机）")]
    public int PanelPort { get; set; } = 0;

    [DisplayName("报告保留份数")]
    public int ReportHistory { get; set; } = 30;

    [DisplayName("启用 AI 工具集")]
    public bool EnableAiFunctions { get; set; } = true;
}
