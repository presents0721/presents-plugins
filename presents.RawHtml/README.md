# presents.RawHtml —— Alife 版「Raw HTML 卡片」插件

把 DeepSeek Harness 的 [`dsh-raw-html-v2`](https://github.com/plolpl789/dsh-raw-html-v2) 的**渲染能力**移植到 Alife 桌面框架。

## 它做什么

让 AI 直接输出 VCP 协议的裸 HTML（`<div id="vcp-root">…</div>`），在 Alife 的一个独立 **webview 面板**里渲染成富卡片：KaTeX 公式、Mermaid 图表、SVG、内置字体、JS 动画、交互控件。

## 为什么不是「直接移植」

原插件 99% 是 DSH **网页聊天客户端**代码：靠钩住 `conversation.chat.node` 的 `assistant-step` 槽位、用 `</>` 开关按钮与「可信模式」盾牌来做内联渲染。Alife 是 C# 桌面框架，**没有对话 DOM、没有 Slot API**，所以：

- ❌ `lib/client.js`（按钮/槽位/`input()` 桥/下载按钮）与 `cordis.patch.yml` 全部作废；
- ✅ **渲染内核 `vcp-engine-v1.js` 是纯浏览器 JS**，原样复用（已下载到 `Resources/RawHtml/vcp-engine-v1.js`），它加载后会挂出 `window.__vcpStable.render(html)`（返回真实 DOM 节点）与 `window.__vcpMath.renderMathInContent/renderMermaidInContent`；
- ✅ KaTeX / Mermaid / ColorEngine / 字体资产原样复用（`Resources/RawHtml/vendor/`）。

## Alife 侧的等价实现

| DSH 原版 | Alife 移植版 |
|---|---|
| AI 在回复正文输出 `<div id="vcp-root">` | AI 输出 `<RenderHtmlCard>……卡片 HTML……</RenderHtmlCard>` |
| 槽位渲染器接管 assistant-step | C# 把 html 经 `PetBridge.SendMessage("renderHtml", …)` 转发到 webview |
| webview 内 v1 引擎渲染（React 渲染 vdom） | `alife-bridge.js` 取 `window.__vcpStable.render(html)` 的 **vdom** → `window.__vcpMount()` 挂载（详见下一节） |

> 为什么不是 `<RenderHtmlCard html="..."/>`：卡片 HTML 里满是尖括号与引号，放进 XML 属性会被
> `XmlStreamParser` 当成嵌套标签拆掉。因此本插件采用框架自带的**纯文本区**方案
> （`functionService.AddPlainAreas(nameof(RenderHtmlCard))`，与官方 `Alife.Function.ProcessService`
> 的 `WriteProcess` 同款）：标签内的内容原样透传，函数以 `FunctionMode.Content` 在闭标签时
> 用 `context.FullContent` 一次性拿到完整 HTML。

## 文件结构

```
presents.RawHtml/
├── manifest.json
├── RawHtmlService.cs          # [Module] 宿主：注册 AI 函数 + 开 webview 面板 + 面板消息/配置联动
├── RawHtmlConfig.cs           # 配置（渲染 HTML / 美学注入 / 锁定风格 / 目录），框架自动挂到模块设置页
├── AestheticLibrary.cs        # 美学风格库（解析/读写风格文档）+ 提示词协议构建
├── Resources/RawHtml/
│   ├── index.html             # 面板壳：按顺序加载 vendor + shim + 引擎 + 桥 + 面板界面
│   ├── vcp-host-shim.js       # ★ 补上引擎需要的宿主三件套（f/vc/hp）+ vdom→DOM 挂载器
│   ├── alife-bridge.js        # 收消息 → 调 vcp 引擎渲染 → 挂载 → 回执/报错
│   ├── panel-settings.js      # ★ 「VCP 渲染设置」与「美学系统」界面（对标 DSH 插件同名 UI）
│   ├── panel.css              # 上述界面的样式
│   ├── vcp-engine-v1.js       # 原样复用的渲染内核（来自 dsh-raw-html-v2，MIT）
│   ├── styles/                # 12 个内置美学风格 + _INDEX/_BASELINE/_FONTS（与 DSH 风格包互通）
│   │   └── docs/              # DESIGN/EDITORIAL/FRAMING/VCP-INTERACTIONS（供 AI 按需去读）
│   ├── fonts.css              # 26 款 Lanxi 字体的 @font-face 清单（自动生成）
│   ├── fonts/                 # 26 款 Lanxi 字体（.woff2，约 64MB，来自 DSH 插件 assets/fonts）
│   └── vendor/                # katex / auto-render / mermaid / VCPColorEngine
│       └── fonts/*.woff2      # KaTeX 数学字体（缺了公式会退化成系统字体）
└── README.md
```

## 美学系统（移植自 dsh-raw-html-v2）

原插件由「渲染内核」+「美学系统」两部分组成，本插件把后者也搬过来了：

| DSH 原版 | Alife 移植版 |
|---|---|
| 聊天框旁「</>」按钮 → VCP 渲染设置（渲染 HTML / 美学注入 两个开关） | 面板工具栏「⚙ 渲染设置」（同一套开关）+ 模块设置页里的同名配置项 |
| 美学系统查看器（风格卡网格 / 色板 / 字体 / 点卡锁定） | 面板工具栏「🎨 美学系统」——搜索、色板、字体、锁定/解除、文档全文、新建自定义风格 |
| 风格库存 `styles/*.md`（12 个内置） | 内置风格放进 `Resources/RawHtml/styles/`；自定义风格写到 `Storage/<角色>/RawHtml/Styles/`（可互相拷贝） |
| Host 侧 `buildAestheticText()` 注入美学协议 + 风格锁定 | C# `AestheticLibrary.BuildAestheticProtocol()` 注入（锁定风格时内联该风格文档全文） |
| 字体由 `/fonts-v2/` 路由全局注册 @font-face | 26 款 Lanxi 字体随插件分发（`fonts.css`），卡片直接写 `font-family:'Lanxi-XXX'` |

**两个开关的语义**（与 DSH 一致）：

- **渲染 HTML**：关闭 → 不再注册任何 AI 函数（AI 输出裸 HTML 只当普通文本），面板显示「渲染已关闭」提示；
- **美学注入**：关闭 → 只注入渲染协议，不注入审美公约/风格库/字体规范，风格完全自由；渲染关闭时本项自动置灰并强制关闭。

**AI 侧函数**：

| 函数 | 作用 |
|---|---|
| `<RenderHtmlCard>…</RenderHtmlCard>` | 渲染一张卡片（内容模式 + 纯文本区，HTML 原样透传） |
| `<ListAestheticStyles/>` | 列出可用风格（slug — 名称 · 场景），自选风格前先看 |
| `<LockAestheticStyle slug="brutalism"/>` | 锁定/解除风格（留空=解除），锁定后写进配置 |
| `<ReadAestheticStyle slug="ink-letter"/>` | 读某个风格文档全文 |
| `<CreateAestheticStyle name="青灰档案" colors="#F0EFEB #1C1C1A #334EAC"/>` | 新建自定义风格（写进用户的风格库） |

面板里改开关/锁定会即时落盘（`ConfigurationSystem`）并重新注册函数文档，**下一次 AI 回复即生效**；
在 Alife 模块设置页里改配置也会被自动发现（2 秒轮询配置文件），无需重载插件。

## 保存所有产物（HTML / PNG / PDF + 重启恢复）

渲染出来的每一张卡片、每一张图都能落盘，且可脱机查看：

| 入口 | 产物 | 说明 |
|---|---|---|
| 每张卡片右上角（悬停出现）`⤓ HTML` | 单个自包含 `.html` | 内联 KaTeX 样式 + 内嵌所用 Lanxi 字体 + Mermaid/SVG，双击即可在浏览器打开 |
| 每张卡片右上角 `🖼 PNG` | 单个 `.png` | 用 SVG `<foreignObject>` 栅格化（尽力而为；含外部图片/跨域字体会受限，失败会提示改用 HTML/PDF） |
| 工具栏 `⤓ 保存全部` → 导出全部为单个 HTML | 合并 `.html` | 所有卡片拼进一个文件，版心统一 |
| 工具栏 `⤓ 保存全部` → 每张卡片单独保存 | 多个 `.html` | 第 N 张 = `card-N.html` |
| 工具栏 `⤓ 保存全部` → 导出整页为 PDF | `.pdf` | 走 Electron `PrintToPDF`，保留背景色 |

导出目录默认 `Storage/<角色>/RawHtml/Saved`，可在「模块设置页 → RawHtml → 保存目录」改（分号/换行分隔多个目录，第一个用于写入）。
配置项还有「自动保存每次渲染」（默认开：每次 AI 渲染都把卡片原文累加到 `Saved/cards.jsonl`）与「重启后恢复卡片数」（默认 20：面板重新打开时自动重放最近 N 张）。
AI 也可直接调用 `<SaveHtmlCards name="..."/>` 把当前所有卡片一次性导出。

> 数据流：前端渲染成功 → 上报 `panelAddHistory{ts,style,html}` → C# 写 `cards.jsonl`（按内容去重）；
> 导出时 C# 调 `AestheticLibrary` 把卡片包成自包含 HTML（`BuildStandaloneHtml` 内联 KaTeX 字体 + 用过哪些 Lanxi 字体就内嵌哪些）。


## 移植的关键坑（vcp-engine-v1.js 不是独立库）

引擎原文注明：*「本文件无 import/export，直接以文本形式注入到 bundle 的 vc()/Xu() 定义之前，
依赖 bundle 模块作用域内的 `vc` / `hp` / `f`（React）」*。也就是说它**离不开宿主**：

| 引擎依赖 | 原宿主（DSH 前端 bundle） | 本插件（`vcp-host-shim.js`） |
|---|---|---|
| `f` / `f.jsx` / `f.Fragment` | React | 最小元素工厂 `{type, props}` |
| `vc`（DOM 节点 → vdom 节点） | 压缩后为 `Xu` | `domToVdom()`：属性/class/style→对象、非可信模式丢弃 `on*` |
| `hp`（style 串 → style 对象） | 压缩后为 `jd`/`Sd` | `styleToObject()`：kebab→camel、保留 CSS 变量 |
| `window.__vcpStable.render()` 返回值 | **vdom（React 元素）** | 用 `window.__vcpMount(vdom, container)` 挂载 |

所以「面板一片空白」的两种典型成因：
1. 缺 `vcp-host-shim.js` → 引擎抛 `f is not defined`（或返回 undefined），卡片根本挂不上；
2. 把 `render()` 的返回值当 DOM 节点 `appendChild` → 抛异常（返回的是 vdom）；
3. `window.__vcpTrusted` 必须是**函数**（引擎判定 `typeof === 'function'` 才进可信模式），
   赋 `true` 是无效的 —— 表现为卡片内 `<script>`/`onclick` 全部失效；
4. **卡片里的 `<style>` 绝不能提到 `<head>`**：引擎的 `enforceFontChain()` / `collectTextSelectors()`
   只在卡片子树里找 `<style>`，据此判断某元素是否被「显式字体选择器」命中。提到 head 后查不到，
   每个文字元素都会被强制 `font-family:inherit !important` 回退系统无衬线 —— 表现就是
   「Lanxi 字体全不生效、卡片一律黑体」。

## 排障

- 渲染失败**不再静默**：面板里会显示红色报错块（含原始 HTML），同时把
  `[RawHtml前端/error] …` 写进 Alife 日志（前端 console.error / onerror / unhandledrejection 全部转发）。
- 每张卡片都有序号：C# 日志 `[RawHtml] 卡片 #N（M 字符）已下发渲染`，前端回执
  `renderResult` → 插件 Poke「卡片 #N 已渲染（X ms）」或「渲染失败：…」。
  同一张卡片被反复渲染时，看序号即可确认是模型重复调用（不是插件重发）。
- 面板窗口在插件加载时创建一次；若某次重载没有正常释放，下次加载会自动关掉残留窗口。


## 实现状态：已对齐真实 API（无需再猜）

`PetBridge` / `RawHtmlWindow` 直接移植自 **`presents.DeskPet3D`** 的 `Protocol/PetBridge.cs` 与 `Protocol/PetWindow.cs`（仅改命名空间与精简 3D 专属逻辑）：
- 主进程 → 渲染进程：`bridge.SendMessage(type, payload)` → `Electron.IpcMain.Send(window, channelId, json)`；
- 渲染进程 → 主进程：`ipcRenderer.send(channelId, json)` → `IpcMain.On` → `OnMessage` 事件；
- 频道号 `ChannelId` 由窗口生成并拼入 URL `?panelChannel=xxx`，前端 `alife-bridge.js` 从 `location.search` 读取后 `ipcRenderer.on(channelId, ...)` / `.send(channelId, ...)`。

`wwwRoot` 取自建档的 `pluginSystem.PluginContext.GetPluginDirectoryPath("presents.RawHtml") + "Resources","RawHtml"`；`PluginSystem`/`StorageSystem` 经构造函数注入。无需任何假设。

## 安装 / 使用

1. 把整个 `presents.RawHtml/` 放到 Alife 的 `Storage/Plugins/` 下（插件市场安装会自动完成）；
2. 重载 / 重启 Alife（或经 DeveloperTools MCP 触发 reload）；插件启动后会自动弹出「Raw HTML 卡片面板」窗口；
3. 让 AI 用 `<RenderHtmlCard><div id="vcp-root">…</div></RenderHtmlCard>` 输出卡片（函数文档以 `DocumentMode.Explicit` 注入，AI 开箱可见）；
4. 面板里点「渲染示例卡片」可脱离 AI 先验证引擎工作正常；面板未就绪时发来的卡片会自动排队，就绪后补发。

## 安全说明

`alife-bridge.js` 里 `window.__vcpTrusted = function () { return window.__vcpTrustedEnabled !== false; }`
（等价 DSH「可信模式」）：允许 AI 卡片内 `<script>`/`onclick` 执行。仅在信任模型输出时使用；
若要降级为纯静态渲染，在 `alife-bridge.js` 里置 `window.__vcpTrustedEnabled = false` 即可
（引擎只看 `typeof window.__vcpTrusted === 'function'`，赋 `true` 无效）。

## 字体

- **KaTeX 数学字体已内置**：`vendor/fonts/*.woff2`（20 个，来自 `katex@0.16.9`，与 `katex.min.css` 版本一致）。
  缺了它们公式仍会渲染，但会退化成系统字体（字形错位、不够美观），并会在控制台报 `fonts/KaTeX_*.woff2` 404。
- **26 款 Lanxi 字体已内置**（`fonts/*.woff2` + 自动生成的 `fonts.css`，约 64MB，来自 DSH 插件 `assets/fonts`）：
  卡片里直接写 `font-family:'Lanxi-XXX'`（如 `Lanxi-鱼尾行书` / `Lanxi-超粗黑` / `Lanxi-可宋`）即命中，无需自建 `@font-face`。
  子元素字体要写进 `#vcp-root .x{font-family:'Lanxi-XX'}` 类规则，别写进子元素行内 style。
- 增删字体后重新生成清单（`fonts.css` / `fonts.json`）：

  ```powershell
  # 扫描 fonts/ 目录，输出 @font-face 清单与字体元数据
  node -e "const fs=require('fs'),p='Resources/RawHtml/fonts';const fs2=require('path');const files=fs.readdirSync(p).filter(f=>f.endsWith('.woff2')).sort();const items=files.map(f=>{let b=f.replace(/\.woff2$/i,'');if(b.startsWith('Aa'))b=b.slice(2);return{alias:b.startsWith('Lanxi-')?b:'Lanxi-'+b,file:f}});fs.writeFileSync('Resources/RawHtml/fonts.css',items.map(i=>`@font-face{font-family:'${i.alias}';src:url('fonts/${encodeURIComponent(i.file)}');font-display:swap;}`).join('\n'),'utf8');fs.writeFileSync('Resources/RawHtml/fonts.json',JSON.stringify({count:items.length,fonts:items},null,1),'utf8')"
  ```


## 源码仓库说明

本目录是第一方源码镜像；`Resources/RawHtml/vendor`（KaTeX / Mermaid / ColorEngine）与 `Resources/RawHtml/fonts`（26 款 Lanxi 字体）属于第三方资源，未纳入 Git，完整可运行的包见 [Releases](https://github.com/presents0721/presents-plugins/releases)。

