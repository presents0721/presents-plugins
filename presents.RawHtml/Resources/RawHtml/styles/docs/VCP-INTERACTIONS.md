# VCP 交互元素手册（P1 · 白名单交互 · P2 · 可信模式脚本）

> dsh-raw-html 渲染链路支持的交互元素，分两档：
> - **P1 零 JS 白名单交互**（默认，永远可用）：依赖浏览器原生控件行为与 CSS。
> - **P2 卡片脚本通道**（可信模式开启后可用）：`<script>` 由引擎提取并全局执行，
>   用于 addEventListener / 事件委托 / fetch / WebGL 等增强（机制见 §9）。

## 原则

1. **交互零 JS 优先**：折叠用 `<details>`、选项卡/手风琴用 radio/checkbox hack、轮播用 CSS 动画——都不需要脚本，下载快照后依然可用。
2. **onclick 属性两条通道**：① `onclick="input('...')"` 永远可用——渲染层桥接为 React onClick → `window.__dshInput`，点击把文本填入输入框并发送；② 自定义函数 onclick（如 `onclick="switchTab(1)"`）在**可信模式**下由 `vcpVcShim` 以 **ref 直写原生 attribute**（`setAttribute('onclick', code)`）→ 浏览器原生 inline handler 生效（v0.7.13 起放宽，2026-09-02 实测验收：第一版 React 透传点不动、ref 直写后 GUI 与下载快照一致生效），函数需由卡内 `<script>` 定义在全局作用域；不可信模式下仍剥除。
3. **流式注意**：流式输出过程中元素会被反复重建，**交互状态（展开/选中）可能重置**；交互请在流结束后使用。卡片结构（元素类型/文本/样式）在流式中稳定。
4. **默认安全边界**（不可信模式）：script/iframe/object/embed 会被过滤（script 代码不执行）；href/src 仅放行 http/https/mailto/data:image/相对路径；style 中 `position:fixed`、`z-index>=1000`、`content:` 会被剥离。**可信模式例外见原则 5**。
5. **可信模式边界**（localStorage `raw-html-v2.trusted==='1'` 或 `window.__DSH_TRUSTED__`）：`<script>` 放行（引擎 extract→DOM settle 后全局 eval，`__vcpTrustedRan` 幂等去重）；`on*` 属性**除 input 桥外，自定义函数经 ref 直写 attribute 放行**（见原则 2）；`javascript:` 协议的 href/src 依旧拒绝。

---

## 1. 折叠面板（details/summary）

浏览器原生折叠，`open` 属性控制默认展开。

> ⚠️ **示例样式前置说明**：下面 §1~§4 的示例为演示控件结构与写法的**片段**——在真实 VCP 卡片中使用时：选择器请加 `#vcp-root` 前缀（如 `#vcp-root .tab-label`，作用域化后 (1,1,0) 压皮肤）；`<style>` 写在 `#vcp-root` 开标签后；radio/checkbox 的 `id` 建议用卡内唯一值，多卡同屏避免 `label[for]` 串卡（或用 `label` 包裹 + `:has` 结构）。

```html
<details style="border:1px solid rgba(64,180,255,.3);border-radius:10px;padding:12px;background:rgba(64,180,255,.05);">
  <summary style="cursor:pointer;font-weight:600;color:#40dcff;">点击展开 · 实现细节</summary>
  <div style="padding:10px 4px;font-size:13px;line-height:1.8;">
    这里是折叠内容……可以放代码、表格、长文本。
  </div>
</details>
```

## 2. 选项卡（radio hack，CSS-only）

用 `input[type=radio]` + `label` + 兄弟选择器实现无 JS 切换。
要点：radio 隐藏、label 做按钮、`:checked + ... ~ 面板` 控制显示。

```html
<style>
  .tab-demo .tab-input{display:none}
  .tab-demo .tab-panel{display:none;padding:12px;border-radius:0 0 10px 10px;background:#0a1626;}
  .tab-demo .tab-label{display:inline-block;padding:8px 16px;cursor:pointer;border-radius:8px 8px 0 0;background:rgba(64,180,255,.08);color:#7fa8c9;margin-right:4px;}
  .tab-demo .tab-label:focus-visible{outline:2px solid #40dcff;outline-offset:2px;}
  .tab-demo input:checked + .tab-label{background:#40dcff;color:#06182e;font-weight:600;}
  .tab-demo input:checked + .tab-label + .tab-panel{display:block}
</style>
<div class="tab-demo" style="border:1px solid rgba(64,180,255,.3);border-radius:10px;overflow:hidden;">
  <input type="radio" name="tab1" id="t-a" class="tab-input" checked>
  <label for="t-a" class="tab-label">概览</label>
  <div class="tab-panel">概览内容……</div>
  <input type="radio" name="tab1" id="t-b" class="tab-input">
  <label for="t-b" class="tab-label">数据</label>
  <div class="tab-panel">数据内容……</div>
</div>
```

## 3. 手风琴（checkbox hack）

多个 checkbox 各自控制一个面板，可同时展开多个。

```html
<style>
  .acc-item{border:1px solid rgba(64,180,255,.25);border-radius:8px;margin-bottom:8px;overflow:hidden;}
  .acc-item input{display:none}
  .acc-item .acc-body{display:none;padding:10px 14px;background:#0a1626;font-size:13px;}
  .acc-item input:checked + label + .acc-body{display:block}
  .acc-item label{display:block;padding:10px 14px;cursor:pointer;font-weight:600;color:#cfe6f8;background:rgba(64,180,255,.06);}
  .acc-item label:focus-visible{outline:2px solid #40dcff;outline-offset:2px;}
  .acc-item input:checked + label{color:#40dcff}
</style>
<div>
  <div class="acc-item"><input type="checkbox" id="a1"><label for="a1">第一步 · 收集</label><div class="acc-body">……</div></div>
  <div class="acc-item"><input type="checkbox" id="a2"><label for="a2">第二步 · 分析</label><div class="acc-body">……</div></div>
</div>
```

## 4. 轮播（CSS 动画自动播放）

纯 CSS 平移循环；多张面板容器横向排布，父容器溢出隐藏，`@keyframes` 整体平移。
注意：这是「自动轮播」，无法手动切换（无 JS）。时长建议 4~8s 一屏。

```html
<style>
  @keyframes lx-carousel{0%,28%{transform:translateX(0)}33%,61%{transform:translateX(-100%)}66%,94%{transform:translateX(-200%)}100%{transform:translateX(0)}}
  .car{overflow:hidden;border-radius:10px;border:1px solid rgba(64,180,255,.3);}
  .car-track{display:flex;width:300%;animation:lx-carousel 9s infinite;}
  .car-slide{width:33.333%;padding:18px;box-sizing:border-box;background:#0a1626;}
</style>
<div class="car"><div class="car-track">
  <div class="car-slide">第一屏内容……</div>
  <div class="car-slide">第二屏内容……</div>
  <div class="car-slide">第三屏内容……</div>
</div></div>
```

## 5. 按钮交互（onclick → input 桥）

**恒可用的交互通道之一**（与 details/radio 零 JS、script+onclick 脚本通道并列，见原则 2/§9）。
点击后把文本填入输入框并发送。可做「一键追问 / 快捷指令 / 选项确认」。

```html
<button onclick="input('请展开讲讲第二步')" style="background:#40dcff;color:#06182e;border:none;border-radius:999px;padding:8px 18px;font-weight:600;cursor:pointer;font-size:13px;">展开讲讲第二步</button>
```

⚠️ 注意：input 桥按钮在**下载的 HTML 快照里会失活**（onClick 是 React 函数属性、不落 outerHTML，下载页无 `window.__dshInput`）——详见 §8。

## 6. 系统提示词注入（可选）

把下面这段加入模型系统提示词，模型就会主动使用交互元素：

```
## VCP 交互能力（三条通道 · 按需取用）
① 零 JS 原生：`<details><summary>` 折叠；radio/checkbox hack 选项卡与手风琴（样式在 <style> 中定义，选择器需 #vcp-root 前缀）；CSS 动画自动轮播——不需要脚本，任何模式可用。
② `onclick="input('...')"` 按钮：恒可用，点击把文本填入输入框并发送（下载快照后失活）。
③ 可信模式脚本：`<script>` 必须写在 vcp-root 闭合 </div> 之后（卡尾）；内联 `onclick="fn(this)"` 自定义函数可信模式下可用（函数由卡尾 script 定义在全局）；交互初始化务必幂等 + 用 closest('[id^="vcp-msg-"]') 定位本卡。
交互状态在流式输出中可能重置，请把交互元素放在卡片稳定结构内；不要在交互元素上依赖流式中的状态保持。
```

## 7. Mermaid 图表（VCP 卡片内 · 白底淡色主题）

> 模型在 vcp-root 内输出 `<pre class="language-mermaid">…</pre>`，渲染层自动：
> 白底框体 + 蓝灰线条 + 石墨文字 + 右上角工具栏（− / 100% / ＋ / 适应）。
> 渲染逻辑见 assets/vendor/vcp-engine-v1.js 的 mermaid 模块；测试 tests/engine-smoke.test.mjs。

### 7.1 基础用法（三种高频图型）

```html
<pre class="language-mermaid"><code class="language-mermaid">flowchart TD&#10;  A[开始] --&gt; B{判断}&#10;  B --&gt;|是| C[完成]&#10;  B --&gt;|否| D[重试]</code></pre>
<pre class="language-mermaid"><code class="language-mermaid">sequenceDiagram&#10;  蓝汐-&gt;&gt;用户: 输出卡片&#10;  用户--&gt;&gt;蓝汐: 反馈效果</code></pre>
<pre class="language-mermaid"><code class="language-mermaid">gantt&#10;  title 排期&#10;  dateFormat YYYY-MM-DD&#10;  section 阶段&#10;  任务A :done, a1, 2026-08-20, 1d</code></pre>
```
注意：mermaid 源码内的换行用 `&#10;`（HTML 实体），避免 vcp-root 出现空行；`>` 等符号用实体（`--&gt;`）。

### 7.2 分类型淡色染色（classDef，推荐）

给不同类型的节点/子图用低饱和淡色区分（主流程淡蓝、决策淡紫、终端淡绿），优雅且高级：

```html
<pre class="language-mermaid"><code class="language-mermaid">flowchart TD&#10;  classDef main fill:#e8f1fb,stroke:#7db3e8,color:#1e293b;&#10;  classDef decide fill:#f3eef9,stroke:#b39ddb,color:#1e293b;&#10;  classDef done fill:#eaf7ee,stroke:#8fd6a8,color:#1e293b;&#10;  A[发起请求]:::main --&gt; B{校验}:::decide&#10;  B --&gt;|通过| C[处理数据]:::main&#10;  B --&gt;|拒绝| D[返回错误]:::done</code></pre>
```

### 7.3 状态图着色陷阱（stateDiagram-v2 · 必读）

状态图染色**不要**用后缀绑定语法 `state "待支付" as pending :::main`——在部分 Mermaid 版本里 `:::` 类名不会落到状态节点上，classDef 颜色渲染失败（实测确认）。改用单独的 `class 状态名 类名` 语句绑定，且 classDef 名与状态名要错开（避免 `done` 这类重名冲突）：

```html
<pre class="language-mermaid"><code class="language-mermaid">stateDiagram-v2&#10;  [*] --&gt; pending&#10;  pending --&gt; done : 完成&#10;  done --&gt; [*]&#10;  state "待处理" as pending&#10;  state "已完成" as done&#10;  classDef primary fill:#e8f1fb,stroke:#7db3e8,color:#1e293b&#10;  classDef finish fill:#eaf7ee,stroke:#8fd6a8,color:#1e293b&#10;  class pending primary&#10;  class done finish</code></pre>
```

- 规则：`classDef 类名` 负责定义样式，`class 状态名 类名` 负责绑定，两者缺一不可；顺序上 classDef 在前、class 绑定在后。
- 流程图（flowchart）里的 `节点:::类名` 后缀语法正常生效，此坑**只针对 stateDiagram-v2**。

### 7.4 样式约定
- 框体由渲染层统一白底（模型无需写容器样式，写了也会被 div.mermaid 统一样式覆盖）
- 建议淡色系：淡蓝 `#e8f1fb` / 淡紫 `#f3eef9` / 淡绿 `#eaf7ee` / 淡黄 `#fdf6e3`，边框用同色系加深 30%，文字统一石墨 `#1e293b`
- 图表渲染失败自动回退为代码块（源码可读），不会闪错误
---

## 8. 卡片下载（浏览器半侧 · 非模型能力）

> 这是**渲染侧提供的能力**，模型无需在 HTML 里写任何下载按钮——用户把鼠标移到
> 任意已渲染的 VCP 卡片上，右上角会浮出「⤓ 下载 HTML」小按钮，点击即把该卡片
> 下载为**自包含 HTML 存档**（装帧小说 / 图表 / 卡片皆可）。

- **自包含**：下载文件会把卡片用到的**开源字体内嵌为 data URI**——
  内置精选 `/fonts-v2/Lanxi-*.woff2`（7 款 OFL）与 KaTeX `/vendor-v2/fonts-v2/*.woff2`（OFL）。
  任何电脑、任何时间、离线打开，装帧字体都完整还原。
- **全局字体自动随卡内嵌（v0.7.21+，F5）**：即使卡内没写 `@font-face`（Lanxi 字体由插件全局注入 head），下载时也会扫描卡内 `font-family` 引用的 `Lanxi-*` 家族名 → 从字体清单查路径 → 自动生成 `@font-face` + data URI 写进下载文件——**本地打开艺术字不黑体**。
- **不内嵌**：外置大库字体（`/fonts-v2/<子目录>/*.ttf`，可能商业授权）保留相对路径——
  本机打开仍正常，换机器则 fallback 系统字体（授权安全优先）。
- **已自包含**：Mermaid 渲染后是内联 SVG；表情包 `<img>` 是绝对 URL，本机仍可加载。
- ⚠️ **下载快照与脚本（v0.7.13 起已回注）**：下载文件 = `card.outerHTML` DOM 快照 + 引擎注册表回注的 `<script>`（引擎 `extractTrustedScripts` 按 uid 存源码、`downloadCard` 取回注入 body）——**卡内 `<script>` 定义的函数在下载文件里可继续被内联 onclick 调用**。仍不可用：① input 桥按钮（onClick 是 React 函数属性、不落 outerHTML，下载页无 `window.__dshInput`）；② 依赖宿主环境的能力（`window.__vcpTrusted`、fetch 跨域等）。纯 details/radio 折叠与链接下载后自然可用。
- 模型侧**无需任何动作**，正常输出 VCP 卡片即可；下载入口由插件自动附加。


---

## 9. 可信模式 · 卡片脚本通道（P2 · v7.2）

> 2026-09-02 定调：**脚本解禁为「非必要不写；能更好互动时允许写」**。
> 渲染器机制实录（排查「onclick 点不动」定位）：

### 9.1 脚本如何执行（机制）

- **开关**：`window.__vcpTrusted()` → client 层定义，`localStorage['raw-html-v2.trusted']==='1'` 或 `window.__DSH_TRUSTED__===true`。
- **提取**：引擎 `extractTrustedScripts(html)` 在解析前把 `<script>…</script>` 内容整体剥出进队（`<script src>` 外链不支持）。
- **执行**：v0.7.22 起 **extract 后立即 flush**（parseFrag 内）——流式与全量路径都会排程执行，不再依赖 settle 帧；`flushTrustedScripts` 用 `(0,eval)(decodeEntities(code))` **全局执行**（setTimeout(0) + rAF 双帧 + 500ms 兜底保证 DOM 挂载后至少执行一次）；`window.__vcpTrustedRan` 按脚本 id 幂等去重。
- **不可信模式**：script 代码被提取后**丢弃不执行**，与旧版一致。
- **⚠️放置铁律（0.7.27 实测）**：`<script>` 一律写在 **vcp-root 闭合 `</div>` 之后**（卡尾兄弟，渲染层吞并入卡）。**禁止放卡内**——流式中引擎面对未闭合 rawtext 会掉格式/字体错。

### 9.2 正确姿势（硬约束 · 违反即失效）

1. **事件绑定两种皆可（可信模式）**：内联 `onclick="fn()"`（v0.7.13 起由 `vcpVcShim` **ref 直写原生 attribute**——`setAttribute('onclick', code)`，绕开 React 属性管线 → 浏览器原生 inline handler，函数须由卡尾 `<script>` 全局定义）或 `<script>` 内 `addEventListener`/事件委托。**不可信模式**下内联 `on*` 一律剥除、script 不执行，只剩 details/radio/input 桥。
2. **定位卡片用 `closest('[id^="vcp-msg-"]')` 从元素向上找**（事件回调里最稳），或 `querySelectorAll('[id^="vcp-msg-"]')` 全局取——**禁止 `#vcp-root`**（`scopeVcp` 已把根 div 改写为 `vcp-msg-N`）。⚠️多卡同屏/同消息时 `querySelectorAll(...)` 取「最后一张」可能指错卡，务必从 `this`/`event.target` 向上 `closest`。
3. **幂等 + 隔离**——脚本会被多次执行（流式帧/历史重挂载），事件委托**必须全局去重**（如 `if(window.__myDemo)return; window.__myDemo=1`），否则一次点击多次响应；回调内用 `closest('[id^="vcp-msg-"]')` 限定只响应本卡元素。

### 9.3 最小可用模板（script 置卡尾 · 0.7.27 实测定稿）

```html
<div id="vcp-root" style="background:#F4F2EC;color:#1F1E1C;font-family:-apple-system,'PingFang SC',sans-serif;">
<style>#vcp-root{max-width:560px;box-sizing:border-box;padding:18px}#vcp-root .st{font-size:12px;color:#8A8780;border:1px dashed #C9C6BE;padding:8px 12px;border-radius:8px}#vcp-root .st.ok{color:#0f8f4f;border-color:#0f8f4f}#vcp-root .btn{padding:8px 16px;border:1px solid #1F1E1C;border-radius:8px;background:#fff;cursor:pointer;font-size:13px}</style>
<div class="st" data-st>脚本通道待激活…</div>
<button class="btn" onclick="demoTap(this)">点我（内联 onclick）</button>
</div>
<script>(function(){function last(){var a=document.querySelectorAll('[id^="vcp-msg-"]');return a[a.length-1]}var tr=0;function boot(){var c=last();if(!c&&tr<30){tr++;setTimeout(boot,100);return}if(!c)return;var s=c.querySelector('[data-st]');if(s){s.textContent='✓ 脚本已执行';s.classList.add('ok')}}boot()})();function demoTap(t){try{var c=t.closest('[id^="vcp-msg-"]');if(!c)return;var n=(parseInt(t.getAttribute('data-n')||'0',10)||0)+1;t.setAttribute('data-n',n);t.textContent='✓ 已点 '+n+' 次'}catch(e){}}</script>
```

- **要点**：`<script>` 在 vcp-root 闭合 `</div>` **之后**（卡尾兄弟）；函数声明为**全局**（内联 onclick 才能调到）；boot 轮询找卡（eval 可能早于 DOM 挂载，轮询兜底）；幂等去重看场景需要。

### 9.4 适用场景与克制

- **适合脚本**：选项卡局部刷新、图表联动、计时/时钟、需要读 DOM 的交互、fetch 数据、WebGL/Shader（解锁点）。
- **仍然优先零 JS**：纯展示折叠、静态选项卡、按钮发问——details/radio/input 桥足够时不要上脚本（下载快照保活、跨模式兼容）。
- 交互状态在流式中会重置：脚本初始化务必在 DOM settle 后（模板已含轮询兜底），不要在流式早期依赖时序。
- **一条消息一张卡**：同条消息多卡混排会触发切分器流式震动（渲染层已加 F8 自检自动兜底，但能免则免）——多卡请分多条消息发。
- **流式结束完整性由渲染层自愈**（style touch / processMath 补跑 / 健康自检，0.7.23~0.7.27）：KaTeX/Mermaid/字体在结束瞬间若未就绪会自动补跑，模型无需额外动作；console 出现 `F8/F8b 自检` 警告 = 自愈正常触发的信号。

---

*维护：本文件与 `lib/client.js`、`assets/vendor/vcp-engine-v1.js`、`tests/` 配套，改动渲染能力时同步更新。*
