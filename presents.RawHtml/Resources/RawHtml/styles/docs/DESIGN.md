# 蓝汐 · 视觉设计系统（DESIGN.md）

> 定位：**纯技术参考手册**——只给「会崩 / 会错 / 踩过的坑」的技术约束与资产，不教审美。
> 审美见 [EDITORIAL.md](./EDITORIAL.md)；灵魂见 [BREATH.md](./BREATH.md)。

## 文档地图（省 Token 分层 · 2026-09-02 定调 A+B 重组）

- **◈ CORE（置顶）**：写卡必守的浓缩段——系统提示注入首选，模型每次输出前已内化，**常驻成本 ≈ 20 行**。
- **§0~§6（正文）**：资产速查（字体/色板）与权威细节（排版/铁律）——**按需读取**：`grep "章节标题" DESIGN.md` 拿行号 → `read` 用 offset/limit 只取该段。
- **§7~§8（附录）**：皮肤战役档案 / 引擎边界——**开发与排查专用，模型不读**。
- 交互细则唯一权威 [VCP-INTERACTIONS.md](./VCP-INTERACTIONS.md)；装帧见 [FRAMING.md](./FRAMING.md)。

---

## ◈ CORE · 写卡必守（注入首选 · 每次输出前已内化）

**C0 输出纪律**：视觉 = 正文裸 HTML（GUI 自动渲染）；禁止写 .html 文件丢链接（用户明确要求存档除外）。

**C1 空行律**：卡前必空行、卡内绝无 `\n\n`；`<style>` 内每条规则独立一行、行间无空行、**禁超长单行**（几千字符单行 style = 流式溢出源码）。

**C2 根容器与样式**：根 = `<div id="vcp-root" style="背景/字色/字族/字号（短内联）">`，长样式全部进 `<style>`；**类选择器一律 `#vcp-root` 前缀**（作用域化后 (1,1,0) 稳压皮肤）；style 写在开标签后、内容之前；根与设宽容器 `box-sizing:border-box`。

**C3 字体三律**：① Lanxi-* 已全局注册，直接写 `font-family:'Lanxi-XX'`，**勿自建 @font-face**；② 子元素字体写进 `#vcp-root .x{font-family:'Lanxi-XX'}` class 规则，**禁止子元素行内 font-family**（被字体链兜底压成黑体）；③ 关键文字属性（字族/字号/字重/行高/字距）尽量显式声明（boost 保护稳赢皮肤）。正文永远系统无衬线，数字用 `tabular-nums`。

**C4 交互三通道（按需取用）**：
- ① 零 JS（`<details>`/radio hack）——默认首选，下载快照保活；
- ② `onclick="input('…')"` 按钮——恒可用，点击真发送（下载快照失活）；
- ③ 脚本（可信模式）：**`<script>` 一律写在 vcp-root 闭合 `</div>` 之后（卡尾兄弟）**——放卡内会让引擎流式中掉格式；函数声明为全局；定位本卡用 `closest('[id^="vcp-msg-"]')`；初始化幂等去重。自定义函数 onclick 需可信模式。
- **一条消息一张卡**（多卡分多条消息发）；script 是增强不是默认。

**C5 排版简则**：正文 14-15px、行高 1.6-1.8、两端对齐、首行缩进 2em（标题/诗歌除外）；全角中文标点（「」『』优先）；中西文间加空格；**字级阶梯：主标题 > 副标题 > 正文 > 后记 > 装饰**，逐级递减；标题 1-2 款艺术字即可，正文永不艺术字。

**C6 禁项速记**：`backdrop-filter` / `flex-wrap:wrap` / `margin:0 auto` / 入场 transform 位移与 `animation-delay` 错峰（入场动画只用 opacity 淡入）/ Emoji（装饰图标用内联 SVG）/ CSS > 约 200 行 / 类选择器 > 12；图片用 `<img>` 不用 `![]()`；SVG 必带 width/height+viewBox、transform 动画配 `transform-box:fill-box`；深色容器内无白块亮块；`position:fixed`、`z-index>=1000`、`content:` 会被渲染层剥离。

**C7 动笔三问**：主角清晰吗？文字可读吗？删掉装饰还成立吗？——答「是」即落笔；落盘一次勾选 C1~C6，不为自检延长思考。

---

## 0. 字体资产（按需查询 · 资产表不常驻）

> `/fonts-v2/` 双源：① 插件内置精选（assets/fonts，随包分发即用）；② 外置大库（`raw-html-v2.fontsRoot` 可配置，默认 `I:\字体`）。
> 写卡引用直接用 `font-family:'Lanxi-XXX'`（C3），下表仅供查路径/确认可用款。

### 0.0 内置精选（26 款 · 随插件分发 · 全开源自带）

> 由 `tools/subset_fonts.py` + `tools/subset-style-fonts.py` 生成（GB2312 一级 + 标点；鱼尾行书繁全 BMP）。改动内置字体源后重跑 tools 脚本。

| 领域 | font-family 名 | /fonts-v2/ 路径 | 体积 |
|---|---|---|---|
| 楷书 | `Lanxi-WenKai` | Lanxi-WenKai.woff2 | 883KB |
| 楷书·细 | `Lanxi-WenKaiLight` | Lanxi-WenKaiLight.woff2 | 921KB |
| 手写楷 | `Lanxi-MaShanZheng` | Lanxi-MaShanZheng.woff2 | 1.7MB |
| 黑体 | `Lanxi-HeiTi` | Lanxi-HeiTi.woff2 | 1.3MB |
| 黑体·细 | `Lanxi-HeiTiLight` | Lanxi-HeiTiLight.woff2 | 1.3MB |
| 黑体·粗 | `Lanxi-HeiTiBold` | Lanxi-HeiTiBold.woff2 | 1.4MB |
| 英文花体 | `Lanxi-GreatVibes` | Lanxi-GreatVibes.woff2 | 44KB |
| 宋体·正文（warm-minimal） | `Lanxi-可宋` | Lanxi-可宋.woff2 | 2.79MB |
| 宋体·标题 | `Lanxi-朗宋` | Lanxi-朗宋.woff2 | 2.05MB |
| 宋体·副题 | `Lanxi-品宋` | Lanxi-品宋.woff2 | 2.53MB |
| 宋体·颜韵 | `Lanxi-颜宋` | Lanxi-颜宋.woff2 | 2.37MB |
| 极细·空灵 | `Lanxi-静黑超细` | Lanxi-静黑超细.woff2 | 1.39MB |
| 黑体·几何 | `Lanxi-点黑` | Lanxi-点黑.woff2 | 1.92MB |
| 冲击·超粗 | `Lanxi-超粗黑` | Lanxi-超粗黑.woff2 | 0.73MB |
| 可爱·卡通 | `Lanxi-卡通` | Lanxi-卡通.woff2 | 1.17MB |
| 可爱·手绘 | `Lanxi-叮叮` | Lanxi-叮叮.woff2 | 4.21MB |
| 可爱·萌系 | `Lanxi-喵喵` | Lanxi-喵喵.woff2 | 2.88MB |
| 手写·文人 | `Lanxi-春兰茅坤` | Lanxi-春兰茅坤.woff2 | 1.73MB |
| 书法·行书（刊头首选） | `Lanxi-鱼尾行书` | Lanxi-鱼尾行书.woff2 | 3.70MB |
| 书法·江湖 | `Lanxi-狂侠体` | Lanxi-狂侠体.woff2 | 3.57MB |
| 少女·手写日记（仅手账场景） | `Lanxi-暗恋初夏` | Lanxi-暗恋初夏.woff2 | 3.25MB |
| 少女·标题俏皮（仅手账场景） | `Lanxi-秀英体` | Lanxi-秀英体.woff2 | 1.82MB |
| 少女·软萌（仅手账场景） | `Lanxi-初心少女` | Lanxi-初心少女.woff2 | 1.46MB |
| 少女·童趣（仅手账场景） | `Lanxi-丫丫体` | Lanxi-丫丫体.woff2 | 1.65MB |
| 繁体·行书（须配繁体内容） | `Lanxi-鱼尾行书繁` | Lanxi-鱼尾行书繁.woff2 | 7.64MB |

### 0.1 外置全量（可选配置后可用 · 参考清单 · 商业授权用户自担）

> 12 风格核心已全部子集化内置（§0.0），下表仅剩未入库扩展款——写卡用到需用户本机装有该字体根（`/fonts-v2/<子目录>/…`）。

| 风格 | font-family 名 | 相对路径（/fonts-v2/ 之后） |
|---|---|---|
| 书法·皇家 | `Lanxi-瘦金书` | `方正所有字体/02/FZfonts/FZSZJW.TTF` |
| 书法·石刻 | `Lanxi-魏碑` | `方正所有字体/02/FZfonts/FZWBJW.TTF` |
| 书法·隶意 | `Lanxi-隶书` | `方正所有字体/02/FZfonts/FZLSJW.TTF` |
| 书法·狂草 | `Lanxi-行草` | `方正所有字体/02/FZfonts/FZXBSJW.TTF` |
| 书法·简牍 | `Lanxi-汉简` | `方正所有字体/02/FZfonts/FZHCJW.TTF` |
| 书法·铁筋 | `Lanxi-铁筋隶书` | `方正所有字体/02/FZfonts/FZTJLSJW.TTF` |
| 书法·手写 | `Lanxi-叶根友` | `bb1213/bb1213/叶根友特色简体升级版.ttf` |
| 书法·舒体 | `Lanxi-舒体` | `方正所有字体/02/FZfonts/FZSTJW.TTF` |
| 艺术·装饰 | `Lanxi-黄金时代` | `造字工房最全字体-2016-01-06更新/艺术字体/MFTheGoldenEra_Noncommercial-Bold.ttf` |
| 艺术·优雅 | `Lanxi-尚雅` | `造字工房最全字体-2016-01-06更新/艺术字体/MFShangYa_Noncommercial-Regular.ttf` |
| 艺术·几何 | `Lanxi-菱心体` | `常用字体/汉仪菱心体简.ttf` |
| 艺术·海报 | `Lanxi-海报体` | `常用字体/华康海报体W12(P).ttf` |
| 艺术·综艺 | `Lanxi-综艺` | `方正所有字体/02/FZfonts/FZZHYJW.TTF` |
| 极细·秀美 | `Lanxi-浪倩` | `造字工房最全字体-2016-01-06更新/艺术字体/MFLangQian_Noncommercial-Regular.ttf` |
| 书法·行书·繁 | `Lanxi-鱼尾行书繁` | `新增-2026-08-28/鱼尾书法行书-繁体.ttf`（繁体字库 · 须配繁体内容） |
| 书法·狂草 | `Lanxi-狂派手书` | `新增-2026-08-28/Aa狂派手书.woff2`（子集 · GB2312 一级口径，10.2MB） |
| 篆书·游龙 | `Lanxi-游龙篆书` | `新增-2026-08-28/字魂游龙篆书(商用需授权).ttf` |
| 篆书·山北 | `Lanxi-山北篆体` | `新增-2026-08-28/依山北篆体.ttf` |
| 手写·自由 | `Lanxi-自由浪漫` | `新增-2026-08-28/自由浪漫体.ttf` |
| 手写·胶带 | `Lanxi-修正带灵感` | `新增-2026-08-28/香蕉修正带灵感体.ttf` |

---

## 1. 色板参考（色纸卡 · 只查值）

> **默认基调（定调）**：浅纸底 + 墨色 + 明度即层级；深色系黑底 + 白字 + 对比撞色。
> 四色系与选色逻辑见 [EDITORIAL.md §1](./EDITORIAL.md)。

| 色板 | 基底 | 正文 | accent | 描边 |
|---|---|---|---|---|
| 暖纸书房 | `#faf6ef` | `#2b2b28` | `#8a6d3b` | `#e4dccb` |
| 终端绿幕（仅终端） | `#0d1117` | `#c9d1d9` | `#3fb950` | — |
| 胶片黄昏 | `linear-gradient(165deg,#1a0f0a,#3a1f14 55%,#5c2e1c)` | `#f4e9d8` | `#e0a458` | `rgba(224,164,88,.18)` |
| 青瓷素雅 | `linear-gradient(165deg,#eef4f1,#e3ede8 60%,#d7e4dd)` | `#2a3a33` | `#4e8a6e` | `#c8d8cf` |

### 1.5 声明式配色（可选 · 免写 hex · 渲染层色彩引擎代劳）

根容器声明 `data-vcp-preset`，渲染层自动生成整套 `--vcp-*` 变量 + 卡片基座（对比度由引擎闭环保证 ≥4.5:1）；子元素取 `var(--vcp-base)` / `--vcp-surface` / `--vcp-border` / `--vcp-text-primary` / `--vcp-text-muted` / `--vcp-accent-primary` / `--vcp-code-bg` / `--vcp-danger`。

```html
<div id="vcp-root" data-vcp-preset="editorial"><!-- 内容 --></div>
```

- 预设：`editorial` / `chiaroscuro` / `fauvism` / `cyberpunk` / `wabi_sabi`；人格 `cyber-hacker` / `jiangnan-scholar` / `void-prophet`。
- 自定义：`data-vcp-mode="dark|light"`、`data-vcp-soul="色温,愉悦,激惹,熵"`、`data-vcp-accent="#hex|角度"`。
- 依赖色彩引擎经 /vendor 加载；未就绪自动降级（根容器需自带 style 兜底）。

---

## 2. 中文排版

- **首行缩进**：正文自然段 `text-indent: 2em`；段间靠缩进不靠空行。
- **字号阶梯**：正文 14-15px（五号）；小标题 18-20px；大标题 22-24px。
- **字级区分度**：主标题 > 副标题 > 正文 > 后记 > 装饰 > 其他，逐级递减；主/副/正文三大类**必须有字体区分**（不只靠字号）：标题用楷书/衬线/艺术字体，正文系统无衬线，数字衬线 `tabular-nums`。
- **行距**：正文 1.6-1.8；标题 1.2-1.35。
- **对齐**：正文两端对齐；中文正文不居中（标题/诗歌除外）。
- **标点**：全角中文标点；弯引号「」『』优先。
- **字距**：正文不加字距；标题可略加 0.5-2px。
- **背题孤行**：小标题与后段同块；正文避免孤行。
- **中西混排**：英文/数字半角，中西文间加空格。

---

## 3. 动笔三问 · 自检后置

审美与灵魂在动笔前已内化（灵魂见 BREATH.md §4；数据契约见 EDITORIAL.md；装帧见 FRAMING.md）。动笔时只问三句，答「是」即落笔：

1. **主角清晰吗？**
2. **文字可读吗？**
3. **删掉装饰还成立吗？**

落盘后一次勾选 CORE C1~C6（协议正文已注入同款精简版），不为自检延长思考。

---

## 4. 安全铁律（细节与勘误全集 · 排查/教学时按需查阅）

> CORE 已浓缩必守项；本节保留完整细节、勘误史与渲染器机制——模型排查 bug 或需要证据链时回来查，不要求常驻记忆。

0. **输出纪律（最高优先级）**：视觉直接输出 HTML 到正文——唯一交付方式；禁止写 .html 文件丢路径（用户明确要求存档除外，正文仍须同步呈现核心内容）。
1. **vcp-root 内禁空行**（`\n\n`）——md HTML 块遇空行即拆。⚠️勘误（2026-08-28 · 雨柬卡）：卡前文字后单换行（无空行）会把「文字+开标签」并进同一段落整段当文本吐出——**卡前必有空行、卡内绝无空行**。
2. **禁 backdrop-filter**——子背景用实色多层渐变；根 `display:block;width:100%;box-sizing:border-box;overflow:hidden`。
3. **交互通道**：零 JS（details/radio/input 桥）→ 脚本（可信模式 v7.2+）。脚本机制与放置铁律详见 [VCP-INTERACTIONS.md §9](./VCP-INTERACTIONS.md)；script 一律卡尾。
4. **流式三规则**：① 开标签后紧贴首元素；② style 在开标签后、内容前；③ 子块少换行。
5. **code 成对设色**：深容器「更深底 + 亮字」/ 浅容器「浅灰底 + 深色字」；禁只设一面（v6.32 已自动修字色，写对更稳）。
6. **根容器 id 契约 + 开标签短小**：根必须 `id="vcp-root"`（class 不被作用域化）；开标签只放短关键值（背景/字色/字族/字号），**开标签长度 = 流式空窗期**。⚠️勘误（2026-08-28 · 背景渐变三轮定位）：开标签平色 background 会永远压住 style 渐变——「平色内联+渐变 style」禁止；渐变要么全内联、要么全进 style。style 书写：每条规则独立一行、行间无空行、禁几千字符超长单行（实测流式溢出/闪动病根）。
7. **字号被皮肤压制**（已兜底 v6.19：自动 important + ref 锁）——极端情况：内联 !important 或 `transform:scale`+`transform-origin:left center`；书法细体配 `text-shadow` 增重。
8. **容器内元素同明度域**：深容器内 code/徽章/按钮也深色，禁白块抢戏。
9. **入场动画只用 opacity 淡入**：禁 transform 位移与 delay 错峰（流式重挂载重播）；流式卡片尽量无动画（2026-08-28 实测动画本身稳定，opacity 仍是流式期最稳）。
10. **CSS 规模上限**：≤ 约 200 行、类选择器 ≤ 12；用过的类必定义。
11. **HTML 语境图片用 `<img>`**，不用 Markdown `![]()`。
12. **流式健壮性自愈（0.7.23~0.7.27 · 模型无需动作）**：结束完整性自动补跑——style touch（CSSOM 重收录）→ `processMath`（KaTeX/Mermaid 补渲染）→ 字体链/SVG 自愈 → F8/F8b 健康自检（异常自动清缓存全量重渲一次；console `F8` 警告 = 自愈信号）。
13. **写卡三律（0.7.27 实测）**：script 卡尾；一条消息一张卡；卡前空行。

---

## 5. 交互元素

> 折叠 / 选项卡 / 手风琴 / 轮播 / 按钮与三通道细则的唯一权威见 [VCP-INTERACTIONS.md](./VCP-INTERACTIONS.md)。

## 6. 故事装帧 · SVG 顶栏封面

> 叙事类回复可在卡片顶部加内联 SVG 小顶栏封面。完整手册见 [FRAMING.md](./FRAMING.md)。

---

## 7. 附录 A · 皮肤覆盖历史档案（开发排查 · 模型不读）

> 渲染器 0.7.x 已内建自愈（boost 文字声明 !important + parseOpen/守卫 ref 锁根字体 + 自愈层按属性锁 inherit），模型只需遵守 CORE C3 粗体结论。以下为历史战役细节，排查皮肤/字号问题时查阅。

maid-atelier 皮肤注入带 `!important` 的全局字体覆盖（特异性 (0,2,0)）。对策：`font-family` 声明**必须加 `!important`** 且选择器以 `#vcp-root` 前缀（(1,1,0) 压过）；或 `data-skin-chrome` 属性绕开。字号同理。

⚠️ 实测补充（2026-08-28 · 雨柬卡历史卡字号被压）：boost 只给 style 文字声明加 `!important`，不改选择器特异性——裸类（`.num{font-size:86px!important}`）特异性 (0,1,0) 被皮肤 (0,2,0)!important 反压（!important 间比特异性）。所有类选择器带 `#vcp-root` 前缀；终极保险 = 内联元素 style（parseOpen ref 锁 → 内联 !important，最高优先级）。

⚠️⚠️ 实测补充（2026-08-25 · 晚报卡四连问 · 自愈 v6.33d 落网）：「强调词显小」根因 = font-size 被皮肤 textRule 压制（同时注入 font-size/font-family 均 !important）。类规则 boost 后 (1,1,0)!important 保住显式声明字号的元素；「没写 font-size 的强调词 span」只有继承值——**继承输给直接作用在元素上的皮肤规则**。对策（已内建）：自愈层把 font-family/font-size/font-weight/font-style/line-height/letter-spacing 按属性锁 `inherit`（仅覆盖未显式声明属性；color/text-align 不锁——颜色是 AI 设计、对齐是布局）。

---

## 8. 附录 B · 引擎/渲染层已知边界（开发排查 · 模型不读）

- 引擎文件（`assets/vendor/vcp-engine-v1.js`）由前端运行时 XHR 加载——改动后**刷新页面即生效**；`lib/client.js` 打包进 web bundle——改动需重启 dsh web。
- settle 复用缓存（`vcpSettleMemo`）保证「无感固化」；F8/F8b 自检检出异常时自动清缓存强制全量重渲（console 警告为正常自愈信号）。
- 同屏多条消息各自作用域化（`vcp-msg-N`）；同名全局函数以 `closest('[id^="vcp-msg-"]')` 隔离可安全同名。

*—— 蓝汐 · 深蓝深海频道 · 美学是理解，不是模板 ——*
