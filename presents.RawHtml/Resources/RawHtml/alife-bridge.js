/* Alife Raw HTML 插件 —— webview 桥（基于 Electron ipcRenderer）
 *
 * 职责：
 *   1) 收 C# 的 renderHtml → 调 vcp 引擎渲染 → 追加进面板；
 *   2) 把渲染结果/前端报错回传 C#（type=renderResult / log），面板永不「静默空白」；
 *   3) 面板设置与美学系统的消息通道（state / styleDoc / toast / reload → window.RawHtmlPanel）；
 *   4) 暴露 window.RawHtmlBridge.sendToHost(text) 给卡片内按钮回传对话。
 *
 * 关键点（原版移植时踩过的坑）：
 *   - window.__vcpTrusted 必须是**函数**（引擎用 typeof === 'function' 判定可信模式），
 *     赋 true 是无效的；
 *   - window.__vcpStable.render(html) 返回 **vdom**，必须交给 window.__vcpMount 挂载
 *     （引擎依赖 vcp-host-shim.js 提供的 f/vc/hp；直接 appendChild 会抛异常 → 空白面板）；
 *   - 卡片里的字体 URL：DSH 用 /fonts-v2/xxx，本插件字体在 fonts/，渲染前统一改写。
 */
(function () {
  'use strict';

  var container = document.getElementById('cards');

  // 从 URL query 取频道号（C# 端 RawHtmlWindow 拼入 ?panelChannel=xxx）
  function getChannel() {
    var m = /[?&]panelChannel=([^&]+)/.exec(location.search);
    return m ? decodeURIComponent(m[1]) : null;
  }
  var channel = getChannel();

  // Alife 5 宿主桥：window.alifeWindow（宿主注入；postMessage 上行 / onMessage 下行）
  var host = (window.alifeWindow && typeof window.alifeWindow.postMessage === 'function') ? window.alifeWindow : null;

  // 旧版 Electron 渲染进程 IPC（仅在宿主桥缺失时兜底；WebView2 下 window.require 不存在）
  var ipc = null;
  if (!host) {
    try {
      if (window.require) {
        var electron = window.require('electron');
        if (electron && electron.ipcRenderer) ipc = electron.ipcRenderer;
      }
    } catch (e) { ipc = null; }
  }

  // 等价 DSH「可信模式」盾牌：允许卡片内 <script>/onclick 执行。
  // 引擎判定方式：typeof window.__vcpTrusted === 'function' && window.__vcpTrusted()
  window.__vcpTrusted = function () { return window.__vcpTrustedEnabled !== false; };

  // Mermaid 10+ 需手动 initialize（关闭自动渲染，由引擎驱动）
  try {
    if (window.mermaid && typeof window.mermaid.initialize === 'function') {
      window.mermaid.initialize({ startOnLoad: false, securityLevel: 'loose' });
    }
  } catch (e) { /* ignore */ }

  // ---- 与 C# 的通道 ----
  function send(type, payload) {
    try {
      var msg = { type: type };
      if (payload) { for (var k in payload) msg[k] = payload[k]; }

      // 宿主桥：直接投递给本窗口的 C# 实例（每个面板一个窗口，不需要频道过滤）
      if (host) { host.postMessage(msg); return; }

      if (!ipc || !channel) return;
      ipc.send(channel, JSON.stringify(msg));
    } catch (e) { /* 通道不可用时仅留在控制台 */ }
  }
  function sendLog(level, text) { send('log', { level: level, text: String(text) }); }

  // ---- 自绘标题栏按钮：Alife 5 窗口没有系统标题栏，隐藏/关闭只能回传 C# 处理 ----
  (function bindWinControls() {
    var hide = document.getElementById('win-hide');
    var close = document.getElementById('win-close');
    if (hide) hide.addEventListener('click', function () { send('panelHide', null); });
    if (close) close.addEventListener('click', function () { send('panelClose', null); });
  })();

  // 前端异常/告警都回传宿主日志，避免「面板空白但插件日志毫无线索」
  window.addEventListener('error', function (ev) {
    sendLog('error', 'window.onerror: ' + (ev && ev.message) + ' @' + (ev && ev.filename) + ':' + (ev && ev.lineno));
  });
  window.addEventListener('unhandledrejection', function (ev) {
    sendLog('error', 'unhandledrejection: ' + (ev && ev.reason && (ev.reason.message || ev.reason)));
  });
  ['error', 'warn'].forEach(function (level) {
    var orig = console[level];
    if (typeof orig !== 'function') return;
    console[level] = function () {
      try {
        var parts = [];
        for (var i = 0; i < arguments.length; i++) {
          var a = arguments[i];
          parts.push(a instanceof Error ? (a.message + '\n' + a.stack) : (typeof a === 'object' ? JSON.stringify(a) : String(a)));
        }
        sendLog(level, parts.join(' '));
      } catch (e) { /* ignore */ }
      return orig.apply(console, arguments);
    };
  });

  // ---- 渲染 ----
  var renderSeq = 0;
  var cards = [];   // 已成功渲染的卡片原文（用于「保存全部」与重启后恢复）

  function currentLockedStyle() {
    return (window.RawHtmlPanel && typeof window.RawHtmlPanel.lockedStyle === 'function') ? window.RawHtmlPanel.lockedStyle() : '';
  }

  function scrollToEnd(el) {
    try { if (el && typeof el.scrollIntoView === 'function') el.scrollIntoView({ behavior: 'smooth', block: 'end' }); }
    catch (e) { /* ignore */ }
  }

  // 找到卡片根节点：渲染引擎会把 id="vcp-root" 作用域重命名为 vcp-msg-N，故优先按前缀匹配
  function findRoot(wrap) {
    if (!wrap || !wrap.querySelector) return null;
    return wrap.querySelector('[id^="vcp-msg-"]') || wrap.querySelector('#vcp-root');
  }

  // ---- 渲染层确定性清除「伪装饰件」----
  // AI 爱在卡片顶部加一行伪命令行标题（如 CNN.exe --intro）、右下角加伪状态角标（status: online）。
  // 协议规则只能约束新生成；这里在渲染时直接清掉，连重启后重放的历史卡也一并处理。
  // 保守起见：只认非常明确的模式，且不碰 <pre>/<code> 里的真终端内容。
  function _firstContentChild(el) {
    if (!el || !el.children) return null;
    for (var i = 0; i < el.children.length; i++) {
      var t = el.children[i].tagName;
      if (t === 'STYLE' || t === 'LINK' || t === 'SCRIPT') continue;
      return el.children[i];
    }
    return null;
  }
  function _looksLikeCliHeader(el) {
    if (!el || el.tagName === 'PRE' || el.tagName === 'CODE') return false;
    if (el.querySelector && el.querySelector('pre,code')) return false;
    var t = (el.textContent || '').replace(/\s+/g, ' ').trim();
    if (!t || t.length > 80) return false;
    if (/[\u4e00-\u9fff]/.test(t)) return false;                    // 含中文 → 视为正文标题，不删
    if (/\b[\w.-]+\.exe\b/i.test(t) && /--/.test(t)) return true;   // CNN.exe --intro / NOEL.exe --neon-mode-ON
    if (/^[\w.-]+@[\w.-]+[:~]/.test(t)) return true;                // user@host:~$ ./connect
    return false;
  }
  function sanitizeDecorations(root) {
    if (!root) return;
    // 1) 顶部伪命令行标题条：沿「首个内容子节点」向下最多 4 层，命中即删
    var node = root, depth = 0;
    while (node && depth < 4) {
      var el = _firstContentChild(node);
      if (!el) break;
      if (_looksLikeCliHeader(el)) { el.remove(); break; }
      node = el; depth++;
    }
    // 2) 伪状态角标：整节点文字恰好为 status: xxx
    var all = root.querySelectorAll('*');
    for (var i = all.length - 1; i >= 0; i--) {
      var e = all[i];
      if (e.children.length === 0 && /^status\s*[:：]\s*[a-z]+$/i.test((e.textContent || '').trim())) e.remove();
    }
  }

  // ---- 卡片等比缩放贴合面板 ----
  // 思路：AI 按固定基准宽度设计卡片；渲染层测出自然尺寸后，用 transform:scale 把整张卡片
  // 缩放到「面板宽度 − 左右留白」。面板变宽 → 放大；变窄 → 缩小；左右留白始终一致。
  var _fitRaf = 0;
  var _fitQueue = [];
  // 统一调度：rAF 回调里必须先复位 _fitRaf，否则标志位永久为真，
  // 后续 resize 与新卡片都会被 if (_fitRaf) 挡掉（曾导致「只有第一次缩放生效、新卡不受限」）。
  function _scheduleFit() {
    if (_fitRaf) return;
    _fitRaf = requestAnimationFrame(function () { _fitRaf = 0; _flushFit(); });
  }
  function _fitEnqueue(wrap) { if (!wrap) return; _fitQueue.push(wrap); _scheduleFit(); }
  function _flushFit() {
    var q = _fitQueue; _fitQueue = [];
    for (var i = 0; i < q.length; i++) _fitOne(q[i]);
  }
  function _fitOne(wrap) {
    var root = findRoot(wrap);
    if (!root) return;
    // 先复位到自然状态再测，避免读到被 transform 缩放后的视觉尺寸
    root.style.transform = 'none';
    root.style.position = '';
    root.style.width = '';
    var nw = root.getBoundingClientRect().width;
    if (!nw) return;
    var sideMargin = Math.max(16, container.clientWidth * 0.05);
    var avail = Math.max(240, container.clientWidth - sideMargin * 2);
    var scale = avail / nw;
    scale = Math.max(0.35, Math.min(2.6, scale));
    var nh = root.getBoundingClientRect().height;
    wrap.style.position = 'relative';
    wrap.style.height = (nh * scale) + 'px';
    root.style.boxSizing = 'border-box';
    root.style.position = 'absolute';
    root.style.top = '0';
    root.style.left = '0';
    root.style.right = '0';
    root.style.margin = '0 auto';
    root.style.width = nw + 'px';
    root.style.transformOrigin = 'top center';
    root.style.transform = 'scale(' + scale + ')';
  }
  function _fitAll() {
    var wraps = container.querySelectorAll('.vcp-card-wrap');
    for (var i = 0; i < wraps.length; i++) _fitQueue.push(wraps[i]);
    _scheduleFit();
  }
  window.addEventListener('resize', _fitAll);
  window.addEventListener('load', _fitAll);
  // 图片/字体加载后卡片高度会变，实时重算；transform 不改布局盒，不会触发观测回环
  var _ro = ('ResizeObserver' in window) ? new ResizeObserver(function (entries) {
    for (var i = 0; i < entries.length; i++) {
      var w = entries[i].target && entries[i].target.parentNode;
      if (w && w.classList && w.classList.contains('vcp-card-wrap')) _fitEnqueue(w);
    }
  }) : null;

  /** DSH 的字体路由是 /fonts-v2/，本插件字体在 fonts/ —— 改写后 AI 沿用旧写法也能命中真实字体。 */
  function normalizeFontUrls(html) {
    return String(html || '')
      .replace(/url\(\s*(['"]?)\/fonts-v2\//gi, "url($1fonts/")
      .replace(/url\(\s*(['"]?)\/vendor-v2\//gi, "url($1vendor/")
      .replace(/url\(\s*(['"]?)\/fonts\//gi, "url($1fonts/")
      .replace(/url\(\s*(['"]?)\/vendor\//gi, "url($1vendor/");
  }

  function showErrorBanner(seq, html, err) {
    var box = document.createElement('div');
    box.className = 'vcp-card-error';
    var msg = err && (err.message || err) ? (err.message || err) : String(err);
    box.textContent = '卡片 #' + seq + ' 渲染失败：' + msg;
    var pre = document.createElement('pre');
    pre.style.cssText = 'max-height:220px;overflow:auto;white-space:pre-wrap;opacity:.75;margin:8px 0 0';
    pre.textContent = String(html).slice(0, 2000);
    box.appendChild(pre);
    container.appendChild(box);
    scrollToEnd(box);
  }

  /** 渲染一张卡片。opts: { seq, record(默认true), silent(默认false) }。
   *  - record: 是否记入历史（用于「保存全部」与重启恢复）。示例卡/恢复重放应置 false。
   *  - silent: 是否静默（不回执 renderResult，避免恢复时刷屏）。 */
  function renderCard(html, opts) {
    opts = opts || {};
    var seq = (opts.seq != null) ? opts.seq : (++renderSeq);
    var record = opts.record !== false;
    var silent = opts.silent === true;
    var t0 = (window.performance && performance.now) ? performance.now() : Date.now();
    try {
      if (!window.__vcpStable || typeof window.__vcpStable.render !== 'function') {
        throw new Error('渲染引擎未加载（vcp-engine-v1.js 缺失或加载失败）');
      }
      if (!window.__vcpMount) {
        throw new Error('宿主 shim 未加载（vcp-host-shim.js 缺失或加载失败）');
      }

      var source = normalizeFontUrls(html);
      // 引擎返回 vdom（React 元素），不是 DOM 节点
      var vdom = window.__vcpStable.render(source);
      if (!vdom) throw new Error('引擎未返回渲染节点（内容为空？）');

      var wrap = document.createElement('div');
      wrap.className = 'vcp-card-wrap';
      container.appendChild(wrap);           // 先入文档：ref 里的 processMath 需要 isConnected
      window.__vcpMount(vdom, wrap);

      // 兜底：ref 未覆盖到的自愈（字体链 / SVG 类动画中心），幂等
      try { if (window.__vcpStable._test) window.__vcpStable._test.enforceFontChain(wrap); } catch (e) {}
      try { if (window.__vcpStable._test) window.__vcpStable._test.healSvgAnimation(wrap); } catch (e) {}
      // 兜底：KaTeX / Mermaid（ref 已调一次，这里幂等再保障一次，防止 KaTeX 未就绪的时序）
      try { if (window.__vcpMath && window.__vcpMath.processMath) window.__vcpMath.processMath(wrap); } catch (e) {}
      try { if (window.__vcpMath && window.__vcpMath.applyColorVars) window.__vcpMath.applyColorVars(wrap); } catch (e) {}

      // 先清掉 AI 惯加的伪命令行标题条 / status 角标（历史卡重放同样生效），再测尺寸
      try { var _r0 = findRoot(wrap); if (_r0) sanitizeDecorations(_r0); } catch (e) {}
      scrollToEnd(wrap);
      // 等比缩放贴合面板：挂载后再测尺寸，并在内容（图片/字体）加载变化时重算
      if (_ro) { var _r = findRoot(wrap); if (_r) _ro.observe(_r); }
      _fitEnqueue(wrap);
      // 卡片装饰（悬停「保存」按钮等）
      try { if (window.RawHtmlPanel && window.RawHtmlPanel.decorateCard) window.RawHtmlPanel.decorateCard(wrap, html); } catch (e) {}

      if (record) {
        cards.push({ seq: seq, html: html });
        var auto = (window.RawHtmlPanel && typeof window.RawHtmlPanel.shouldRecord === 'function') ? window.RawHtmlPanel.shouldRecord() : true;
        if (auto) send('panelAddHistory', { ts: new Date().toISOString(), style: currentLockedStyle(), html: html });
      }
      var ms = ((window.performance && performance.now) ? performance.now() : Date.now()) - t0;
      if (!silent) send('renderResult', { seq: seq, ok: true, ms: Math.round(ms), length: String(source).length });
      return true;
    } catch (e) {
      try { console.error('[RawHtml] 渲染失败', e); } catch (_) {}
      showErrorBanner(seq, html, e);
      send('renderResult', { seq: seq, ok: false, error: (e && e.message) || String(e) });
      return false;
    }
  }

  // ---- 收 C# 消息 ----
  function onHostMessage(msg) {
    if (!msg || !msg.type) return;
    if (msg.type === 'renderHtml' && msg.html) {
      renderCard(msg.html, { seq: msg.seq, record: true, silent: false });
      return;
    }
    if (msg.type === 'clear') { container.innerHTML = ''; cards = []; return; }
    if (msg.type === 'reload') { location.reload(true); return; }   // true=绕过磁盘缓存，确保拉到最新 JS（file:// 下 ?v= 查询无法破缓存）
    if (msg.type === 'toast') {
      if (window.RawHtmlPanel) window.RawHtmlPanel.toast(msg.text || '');
      return;
    }
    if (msg.type === 'state') {
      var state = null;
      try { state = typeof msg.json === 'string' ? JSON.parse(msg.json) : msg.json; } catch (e) { console.warn('[RawHtml] state 解析失败', e); }
      if (state && window.RawHtmlPanel) window.RawHtmlPanel.applyState(state);
      return;
    }
    if (msg.type === 'styleDoc') {
      if (window.RawHtmlPanel) window.RawHtmlPanel.showDoc(msg);
      return;
    }
    if (msg.type === 'pngCss') {
      if (window.RawHtmlPanel && window.RawHtmlPanel.onPngCss) window.RawHtmlPanel.onPngCss(msg.req, msg.css || '');
      return;
    }
    if (msg.type === 'rasterizeLast') {
      var pOk = !!(window.RawHtmlPanel && window.RawHtmlPanel.rasterizeLastCard);
      send('log', { level: 'info', text: '[RawHtml] 收到 rasterizeLast; panel=' + (window.RawHtmlPanel ? 'ok' : 'MISSING') + '; fn=' + (pOk ? 'ok' : 'MISSING') });
      if (pOk) window.RawHtmlPanel.rasterizeLastCard(msg.req);
      else send('log', { level: 'error', text: '[RawHtml] rasterizeLast 被丢弃：window.RawHtmlPanel / rasterizeLastCard 不存在（panel-settings.js 未加载或加载报错）' });
      return;
    }
  }

  if (host) {
    // 宿主下行消息：可能是 JSON 字符串，也可能已是对象
    host.onMessage(function (data) {
      try {
        var msg = (typeof data === 'string') ? JSON.parse(data) : data;
        onHostMessage(msg);
      } catch (e) { console.warn('[RawHtml] bad msg', e); }
    });
    // 通知宿主：前端已就绪（C# 收到后会推配置 + 补发排队卡片）
    send('init', null);
  } else if (ipc && channel) {
    ipc.on(channel, function (ev, json) {
      try { onHostMessage(JSON.parse(json)); } catch (e) { console.warn('[RawHtml] bad msg', e); }
    });
    // 通知主进程：前端已就绪（C# 收到后会推配置 + 补发排队卡片）
    ipc.send(channel, JSON.stringify({ type: 'init' }));
  } else {
    // 兜底：postMessage 通道（个别环境 ipcRenderer 不可用）
    window.addEventListener('message', function (ev) { onHostMessage(ev.data); });
    console.warn('[RawHtml] 宿主桥与 ipcRenderer 都不可用，回退 postMessage');
  }

  // 暴露给宿主/面板的回传与手动渲染入口
  window.RawHtmlBridge = {
    send: send,
    renderCard: function (html, opts) { return renderCard(html, opts || {}); },
    getCards: function () { return cards.slice(); },
    clearCards: function () { cards = []; },
    sendToHost: function (text) { send('host', { text: text }); }
  };

  // 示例卡片（用于不依赖 AI 的手动验证）
  var SAMPLE = [
    '<div id="vcp-root" style="width:760px;margin:0 auto;">',
    '  <div class="paper" style="font-family:\'Lanxi-WenKai\',serif;max-width:680px;margin:0 auto;padding:28px 32px;background:#fbf7ef;color:#222;border-radius:10px;">',
    '    <h2 style="margin:0 0 8px">示例 · VCP 卡片</h2>',
    '    <p>行内公式 $E=mc^2$，块级公式：</p>',
    '    <p>$$ \\int_0^1 x^2\\,dx = \\frac{1}{3} $$</p>',
    '    <pre class="language-mermaid"><code>graph TD; A[Alife] --> B[RawHtml 面板]; B --> C[VCP 引擎];</code></pre>',
    '    <button onclick="window.RawHtmlBridge && window.RawHtmlBridge.sendToHost(\'卡片按钮被点击\')">点我回传宿主</button>',
    '  </div>',
    '</div>'
  ].join('\n');

  document.getElementById('btn-sample').addEventListener('click', function () { window.RawHtmlBridge.renderCard(SAMPLE, { record: false }); });
  document.getElementById('btn-clear').addEventListener('click', function () {
    container.innerHTML = ''; cards = []; send('panelClearHistory', {});
  });

  // 延迟一拍再报：panel-settings.js 在本文件之后「异步」加载（index.html 用时间戳动态注入），0ms 可能早于它，故延到 800ms
  setTimeout(function () {
    var diag = '[RawHtml] 启动自检: engine=' + (window.__vcpStable ? 'ok' : 'MISSING') +
      '; shim=' + (window.__vcpHostShimReady ? 'ok' : 'MISSING') + '; panel=' + (window.RawHtmlPanel ? 'ok' : 'MISSING');
    console.log(diag);
    send('log', { level: 'info', text: diag });
  }, 800);
})();
