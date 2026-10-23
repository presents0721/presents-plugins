/* panel-settings.js —— 面板自带的「VCP 渲染设置」与「美学系统」界面。
 *
 * 对标 dsh-raw-html-v2 的同名 UI：
 *   · 渲染设置：渲染 HTML 开关 / 美学注入开关（渲染关闭时置灰）/ 强制刷新页面 / 美学系统入口；
 *   · 美学系统：搜索 + 风格卡网格（色板 / 字体 / 场景 / 说明，点卡即锁定）+ 新建自定义风格 + 查看文档全文。
 *
 * 数据来自 C# 宿主（面板不能 fetch file:// 资源）：宿主收到 init 后推 state，
 * 之后每次开关/锁定/新建都会重新推一次；本文件只负责渲染与上报。
 */
(function () {
  'use strict';

  // 全局错误捕获：panel-settings.js 加载/运行期任何异常都回传到 C# 日志，避免「静默失败」导致 window.RawHtmlPanel 未挂载却无痕迹
  window.addEventListener('error', function (e) {
    try { send('log', { level: 'error', text: '[panel-settings.js] 错误: ' + (e && e.message ? e.message : e) + ' @' + (e && e.filename ? e.filename : '') + ':' + (e && e.lineno ? e.lineno : '') }); } catch (_) {}
  });

  var S = { render: true, aesthetic: true, locked: '', lockedName: '', styles: [], fonts: [], customDir: '', builtinDir: '', history: [], error: null };
  var dom = {};

  function send(type, payload) {
    if (window.RawHtmlBridge && window.RawHtmlBridge.send) window.RawHtmlBridge.send(type, payload);
  }
  function el(tag, cls, text) {
    var n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text != null) n.textContent = text;
    return n;
  }
  function esc(s) { return String(s == null ? '' : s); }

  // ---------- toast ----------
  var toastTimer = null;
  function toast(text) {
    if (!dom.toast) { dom.toast = el('div', 'rh-toast'); document.body.appendChild(dom.toast); }
    dom.toast.textContent = text;
    dom.toast.style.display = 'block';
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { dom.toast.style.display = 'none'; }, 2600);
  }

  // ---------- 渲染设置 popover ----------
  function closeSettings() { if (dom.popMask) { dom.popMask.remove(); dom.popMask = null; } }

  function openSettings() {
    closeSettings();
    var mask = el('div', 'rh-mask rh-bottom-right');
    var pop = el('div', 'rh-pop');
    pop.appendChild(el('h3', null, 'VCP 渲染设置'));
    pop.appendChild(el('div', 'rh-hint', '这里控制 AI 侧能力：关掉则不注入对应协议。「渲染 HTML」关闭时 AI 不再调用渲染函数，「美学注入」随之置灰。'));

    // 渲染 HTML
    var row1 = el('div', 'rh-row');
    var main1 = el('div', 'rh-row-main');
    main1.appendChild(el('div', 'rh-row-title', '渲染 HTML'));
    main1.appendChild(el('div', 'rh-row-desc', 'AI 输出 <RenderHtmlCard>…</RenderHtmlCard> 时渲染成真卡片（公式 / Mermaid / SVG / 字体 / 交互）。'));
    row1.appendChild(main1);
    var sw1 = el('div', 'rh-switch' + (S.render ? ' on' : ''));
    sw1.onclick = function () { send('panelSetRender', { value: !S.render }); };
    row1.appendChild(sw1);
    pop.appendChild(row1);

    // 美学注入
    var row2 = el('div', 'rh-row' + (S.render ? '' : ' rh-disabled'));
    var main2 = el('div', 'rh-row-main');
    main2.appendChild(el('div', 'rh-row-title', '美学注入'));
    var cur = S.locked ? ('当前锁定：' + (S.lockedName || S.locked)) : '当前：未锁定（AI 按主题自选风格）';
    main2.appendChild(el('div', 'rh-row-desc', '注入审美公约 + 风格库 + 26 款 Lanxi 字体规范，让卡片成体系地好看。' + cur));
    row2.appendChild(main2);
    var sw2 = el('div', 'rh-switch' + (S.aesthetic ? ' on' : '') + (S.render ? '' : ' disabled'));
    if (S.render) sw2.onclick = function () { send('panelSetAesthetic', { value: !S.aesthetic }); };
    row2.appendChild(sw2);
    pop.appendChild(row2);

    var actions = el('div', 'rh-actions');
    var reload = el('button', 'rh-btn rh-ghost', '⟳ 强制刷新页面');
    reload.title = '立即重新加载面板页面（调试样式/注入时用）';
    reload.onclick = function () { closeSettings(); send('panelReloadPage', {}); };
    actions.appendChild(reload);
    var open = el('button', 'rh-btn rh-primary', '美学系统 ▸');
    open.onclick = function () { closeSettings(); openViewer(); };
    actions.appendChild(open);
    pop.appendChild(actions);

    mask.appendChild(pop);
    mask.onclick = function (ev) { if (ev.target === mask) closeSettings(); };
    document.body.appendChild(mask);
    dom.popMask = mask;
  }

  // ---------- 美学系统 ----------
  function closeViewer() { if (dom.viewerMask) { dom.viewerMask.remove(); dom.viewerMask = null; } }

  function openViewer() {
    closeViewer();
    var mask = el('div', 'rh-mask rh-center');
    var panel = el('div', 'rh-panel');

    var head = el('div', 'rh-head');
    head.appendChild(el('h3', null, '美学系统'));
    head.appendChild(el('div', 'rh-sub', '点风格卡即锁定（绿勾），再点「解除锁定」恢复自选。'));
    var close = el('button', 'rh-btn', '✕ 关闭');
    close.onclick = closeViewer;
    head.appendChild(close);
    panel.appendChild(head);

    var body = el('div', 'rh-body');
    var tools = el('div', 'rh-tools');
    var search = el('input', 'rh-search');
    search.placeholder = '搜索风格：名称 / slug / 场景 / 标签…';
    search.oninput = function () { renderGrid(search.value); };
    tools.appendChild(search);
    var create = el('button', 'rh-btn rh-primary', '+ 新建风格');
    create.onclick = function () { openCreate(); };
    tools.appendChild(create);
    body.appendChild(tools);

    dom.grid = el('div', 'rh-grid');
    body.appendChild(dom.grid);
    panel.appendChild(body);

    var foot = el('div', 'rh-foot');
    foot.appendChild(el('div', 'rh-path', '自定义风格目录：' + (S.customDir || '(未配置)')));
    var openDir = el('button', 'rh-btn rh-ghost rh-icon', '打开目录');
    openDir.onclick = function () { send('panelOpenDocs', {}); };
    foot.appendChild(openDir);
    foot.appendChild(el('div', null, '字体 ' + S.fonts.length + ' 款'));
    panel.appendChild(foot);

    mask.appendChild(panel);
    mask.onclick = function (ev) { if (ev.target === mask) closeViewer(); };
    document.body.appendChild(mask);
    dom.viewerMask = mask;
    dom.search = search;
    renderGrid('');
  }

  function renderGrid(keyword) {
    if (!dom.grid) return;
    dom.grid.innerHTML = '';
    var kw = (keyword || '').trim().toLowerCase();
    var list = S.styles.filter(function (s) {
      if (!kw) return true;
      return (s.name + ' ' + s.slug + ' ' + (s.scene || '') + ' ' + (s.tags || '')).toLowerCase().indexOf(kw) !== -1;
    });

    list.forEach(function (s) {
      var card = el('div', 'rh-card' + (s.locked ? ' locked' : ''));
      var name = el('div', 'rh-name');
      name.appendChild(el('span', null, s.name || s.slug));
      name.appendChild(el('span', 'rh-slug', s.slug));
      if (s.locked) name.appendChild(el('span', 'rh-badge rh-ok', '当前'));
      name.appendChild(el('span', 'rh-badge' + (s.builtin ? '' : ' rh-custom'), s.builtin ? '内置' : '自定义'));
      card.appendChild(name);
      if (s.scene) card.appendChild(el('div', 'rh-scene', s.scene));
      if (s.desc) card.appendChild(el('div', 'rh-desc', s.desc));

      var sw = el('div', 'rh-swatches');
      (s.colors || []).slice(0, 6).forEach(function (c) {
        var b = el('span', 'rh-swatch');
        b.style.background = c;
        b.title = c;
        sw.appendChild(b);
      });
      card.appendChild(sw);
      if (s.fonts) card.appendChild(el('div', 'rh-fonts', '字体：' + s.fonts));

      var actions = el('div', 'rh-swatches');
      actions.style.marginTop = '10px';
      var view = el('button', 'rh-btn rh-ghost rh-icon', '文档');
      view.onclick = function (ev) { ev.stopPropagation(); openDoc(s.slug); };
      actions.appendChild(view);
      var lockBtn = el('button', 'rh-btn' + (s.locked ? '' : ' rh-primary'), s.locked ? '解除锁定' : '锁定此风格');
      lockBtn.onclick = function (ev) { ev.stopPropagation(); send('panelLockStyle', { slug: s.locked ? '' : s.slug }); };
      actions.appendChild(lockBtn);
      if (!s.builtin) {
        var del = el('button', 'rh-btn rh-ghost rh-icon', '删除');
        del.onclick = function (ev) {
          ev.stopPropagation();
          if (confirm('删除自定义风格「' + (s.name || s.slug) + '」？（内置风格不受影响）')) send('panelDeleteStyle', { slug: s.slug });
        };
        actions.appendChild(del);
      }
      card.appendChild(actions);
      card.onclick = function () { send('panelLockStyle', { slug: s.locked ? '' : s.slug }); };
      dom.grid.appendChild(card);
    });

    var add = el('div', 'rh-card rh-card-add');
    add.appendChild(el('div', null, '＋'));
    add.appendChild(el('div', null, '新建风格'));
    add.appendChild(el('div', 'rh-desc', '把一句想法变成可复用的风格文档（配色/骨架/字体/技法）'));
    add.onclick = function () { openCreate(); };
    dom.grid.appendChild(add);
  }

  // ---------- 历史记录 ----------
  function closeHistory() { if (dom.historyMask) { dom.historyMask.remove(); dom.historyMask = null; } }

  function _plainText(html) {
    return (html || '').replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim();
  }

  function openHistory() {
    closeHistory();
    if (!S.history) S.history = [];
    var mask = el('div', 'rh-mask rh-center');
    var panel = el('div', 'rh-panel');

    var head = el('div', 'rh-head');
    head.appendChild(el('h3', null, '历史记录'));
    head.appendChild(el('div', 'rh-sub', '重启后不自动展示，但都保存在本地；可在此恢复单张或删除。'));
    var close = el('button', 'rh-btn', '✕ 关闭');
    close.onclick = closeHistory;
    head.appendChild(close);
    panel.appendChild(head);

    var body = el('div', 'rh-body');
    var tools = el('div', 'rh-tools');
    var search = el('input', 'rh-search');
    search.placeholder = '搜索历史卡片内容…';
    search.oninput = function () { renderHistoryList(search.value); };
    tools.appendChild(search);
    var clearAll = el('button', 'rh-btn rh-ghost rh-icon', '清空历史');
    clearAll.onclick = function () {
      if (!S.history.length) { toast('历史记录已为空'); return; }
      if (confirm('清空全部历史记录？（此操作不可恢复）')) { send('panelClearHistory', {}); S.history = []; renderHistoryList(''); toast('已清空历史记录'); }
    };
    tools.appendChild(clearAll);
    body.appendChild(tools);

    dom.histGrid = el('div', 'rh-grid');
    body.appendChild(dom.histGrid);
    panel.appendChild(body);

    mask.appendChild(panel);
    mask.onclick = function (ev) { if (ev.target === mask) closeHistory(); };
    document.body.appendChild(mask);
    dom.historyMask = mask;
    dom.histSearch = search;
    renderHistoryList('');
  }

  function renderHistoryList(keyword) {
    if (!dom.histGrid) return;
    dom.histGrid.innerHTML = '';
    if (!S.history || !S.history.length) {
      dom.histGrid.appendChild(el('div', 'rh-hist-empty', '暂无历史记录。生成卡片后会自动留存到这里。'));
      return;
    }
    var kw = (keyword || '').trim().toLowerCase();
    var list = S.history.slice().reverse().filter(function (h) {
      if (!kw) return true;
      return _plainText(h.html).toLowerCase().indexOf(kw) !== -1;
    });
    if (!list.length) {
      dom.histGrid.appendChild(el('div', 'rh-hist-empty', '没有匹配「' + keyword + '」的历史卡片。'));
      return;
    }
    list.forEach(function (h) {
      var card = el('div', 'rh-card rh-hist');
      var t = (h.ts || '').replace('T', ' ').slice(0, 19);
      card.appendChild(el('div', 'rh-hist-time', t));
      var preview = _plainText(h.html).slice(0, 160);
      card.appendChild(el('div', 'rh-hist-preview', preview || '(空内容)'));

      var actions = el('div', 'rh-hist-actions');
      var restore = el('button', 'rh-btn rh-primary rh-icon', '恢复');
      restore.onclick = function (ev) {
        ev.stopPropagation();
        closeHistory();
        if (h && h.html) window.RawHtmlBridge.renderCard(h.html, { record: false, silent: true });
        toast('已从历史记录恢复');
      };
      actions.appendChild(restore);
      var del = el('button', 'rh-btn rh-ghost rh-icon', '删除');
      del.onclick = function (ev) {
        ev.stopPropagation();
        if (!confirm('从历史记录删除这张卡片？（不影响已在面板里的卡片）')) return;
        send('panelDeleteHistoryItem', { ts: h.ts });
        S.history = S.history.filter(function (x) { return x.ts !== h.ts; });
        renderHistoryList(dom.histSearch ? dom.histSearch.value : '');
        toast('已从历史记录删除');
      };
      actions.appendChild(del);
      card.appendChild(actions);

      dom.histGrid.appendChild(card);
    });
  }

  // 新建风格表单
  function openCreate() {
    var mask = el('div', 'rh-mask rh-center');
    var panel = el('div', 'rh-panel');
    panel.style.width = 'min(620px, 94vw)';
    var head = el('div', 'rh-head');
    head.appendChild(el('h3', null, '新建 / 覆盖美学风格'));
    var close = el('button', 'rh-btn', '✕ 关闭');
    close.onclick = function () { mask.remove(); };
    head.appendChild(close);
    panel.appendChild(head);

    var form = el('div', 'rh-form');
    function field(label, placeholder, value) {
      var wrap = el('div', 'rh-field');
      wrap.appendChild(el('label', null, label));
      var input = el('input');
      input.placeholder = placeholder || '';
      input.value = value || '';
      wrap.appendChild(input);
      form.appendChild(wrap);
      return input;
    }
    var iName = field('风格名 *', '如：青灰档案', '');
    var iSlug = field('slug（英文小写，留空自动生成）', '如 qinghui-archive', '');
    var iScene = field('适用场景', '如：数据 / 学术 / 理性', '');
    var iTags = field('标签', '如：极简 / 档案 / 冷调', '');
    var iColors = field('色板（空格分隔 3-5 个 hex，按重要性）', '#F0EFEB #1C1C1A #334EAC #D4A017', '');
    var iFonts = field('字体安排', '标题：Lanxi-鱼尾行书；正文：系统无衬线', '');
    var wrapNotes = el('div', 'rh-field');
    wrapNotes.appendChild(el('label', null, '风格说明（核心命题与差异化准则 2-4 句）'));
    var iNotes = el('textarea');
    iNotes.placeholder = '例如：把数据当成档案编号来排——网格是骨架、明度是数值，装饰只留一条 0.5px 发丝线。';
    wrapNotes.appendChild(iNotes);
    form.appendChild(wrapNotes);
    panel.appendChild(form);

    var actions = el('div', 'rh-actions');
    var save = el('button', 'rh-btn rh-primary', '保存到我的风格库');
    save.onclick = function () {
      if (!iName.value.trim()) { toast('风格名不能为空'); return; }
      send('panelCreateStyle', {
        name: iName.value.trim(), slug: iSlug.value.trim(), scene: iScene.value.trim(), tags: iTags.value.trim(),
        colors: iColors.value.trim(), fonts: iFonts.value.trim(), notes: iNotes.value.trim()
      });
      mask.remove();
    };
    actions.appendChild(save);
    var tip = el('div', 'rh-hint');
    tip.style.flex = '1';
    tip.textContent = '保存后会出现在风格网格里（同名 slug 会覆盖）。想让 AI 自己想风格，也可以对它说「新建一个 XX 风格」。';
    actions.appendChild(tip);
    panel.appendChild(actions);

    mask.appendChild(panel);
    mask.onclick = function (ev) { if (ev.target === mask) mask.remove(); };
    document.body.appendChild(mask);
  }

  // 查看风格文档
  function openDoc(slug) {
    send('panelReadStyle', { slug: slug });
  }
  function showDoc(info) {
    var mask = el('div', 'rh-mask rh-center');
    var panel = el('div', 'rh-panel');
    var head = el('div', 'rh-head');
    head.appendChild(el('h3', null, (info.name || info.slug) + ' · ' + info.slug));
    head.appendChild(el('div', 'rh-sub', info.builtin ? '内置风格文档' : '自定义风格文档'));
    var close = el('button', 'rh-btn', '✕ 关闭');
    close.onclick = function () { mask.remove(); };
    head.appendChild(close);
    panel.appendChild(head);
    var body = el('div', 'rh-body');
    var pre = el('pre', 'rh-preview', info.doc || '');
    body.appendChild(pre);
    panel.appendChild(body);
    var actions = el('div', 'rh-actions');
    var lock = el('button', 'rh-btn rh-primary', S.locked === info.slug ? '解除锁定' : '锁定此风格');
    lock.onclick = function () { send('panelLockStyle', { slug: S.locked === info.slug ? '' : info.slug }); mask.remove(); };
    actions.appendChild(lock);
    panel.appendChild(actions);
    mask.appendChild(panel);
    mask.onclick = function (ev) { if (ev.target === mask) mask.remove(); };
    document.body.appendChild(mask);
  }

  // ---------- 保存：把卡片产物导出成文件 ----------
  function lockedStyle() { return S.locked || ''; }
  function shouldRecord() { return S.autoSave !== false; }

  /** 卡片右上角的悬停操作条（保存 HTML / 导出 PNG）。 */
  function decorateCard(wrap, html) {
    if (!wrap) return;
    var bar = el('div', 'card-actions');
    var bHtml = el('button', 'card-act', '⤓ HTML');
    bHtml.onclick = function (ev) { ev.stopPropagation(); send('panelSaveHtml', { name: '', style: lockedStyle(), html: html }); };
    var bPng = el('button', 'card-act', '🖼 PNG');
    bPng.onclick = function (ev) { ev.stopPropagation(); saveCardPng(wrap, html); };
    bar.appendChild(bHtml);
    bar.appendChild(bPng);
    wrap.appendChild(bar);
  }

  // ---- PNG 导出：高保真栅格化（SVG foreignObject → canvas）----
  //  1) 克隆时抹掉渲染层的「贴合面板」transform / 绝对定位，否则放进 foreignObject 会错位被裁；
  //  2) 组合页面里同源样式（引擎注入 / 卡片自带 <style>）+ 宿主内联的 KaTeX/Lanxi 字体（data URI）；
  //  3) 以 ≥2× 分辨率绘制，避免模糊。
  function collectPlainCss() {
    var out = [];
    try {
      for (var i = 0; i < document.styleSheets.length; i++) {
        var rules;
        try { rules = document.styleSheets[i].cssRules; } catch (e) { continue; }   // 跨域样式表不可读，跳过
        if (!rules) continue;
        for (var j = 0; j < rules.length; j++) {
          var text = (rules[j] && rules[j].cssText) || '';
          if (!/^@font-face/i.test(text)) out.push(text);   // @font-face 由宿主用 data URI 提供，这里跳过
        }
      }
    } catch (e) { /* ignore */ }
    return out.join('\n');
  }

  // 嵌进 SVG 的 <style> 必须保持 XML 合法
  function xmlText(s) { return String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/</g, '&lt;'); }

  var pngCssWaiters = {};
  var pngSeq = 0;
  /** 请宿主把 KaTeX / 用到的 Lanxi 字体内联成 data URI（页面是 file://，前端自己 fetch 不到字体）。 */
  function requestExportCss(html) {
    return new Promise(function (resolve) {
      if (!window.RawHtmlBridge || !window.RawHtmlBridge.send) { resolve(''); return; }
      var req = 'png' + (++pngSeq) + '-' + Date.now();
      var done = false;
      function finish(css) { if (done) return; done = true; delete pngCssWaiters[req]; resolve(css || ''); }
      pngCssWaiters[req] = finish;
      send('panelPngCss', { req: req, html: html });
      setTimeout(function () { finish(''); }, 2500);   // 宿主没响应也不卡死（退化为系统字体）
    });
  }
  function onPngCss(req, css) { var w = pngCssWaiters[req]; if (w) w(css); }

  // 把卡片里 http(s) 外链 <img> / background-image 内联成 data URI，避免它们污染 canvas 导致 toDataURL 失败。
  // 内联失败（跨域无 CORS 头等）则移除该图，至少保证文字/样式能导出。非 http(s)（同域/文件/data）不动，也不会污染。
  function inlineImages(node) {
    if (!node || !node.querySelectorAll) return Promise.resolve();
    function fetchDataUrl(url) {
      return new Promise(function (resolve) {
        if (!/^https?:\/\//i.test(url)) { resolve(url); return; }
        var done = false;
        var timer = setTimeout(function () { if (!done) { done = true; resolve(null); } }, 6000);
        try {
          fetch(url, { mode: 'cors' }).then(function (r) {
            if (!r.ok) throw 0;
            return r.blob();
          }).then(function (b) {
            var fr = new FileReader();
            fr.onload = function () { if (!done) { done = true; clearTimeout(timer); resolve(fr.result); } };
            fr.onerror = function () { if (!done) { done = true; clearTimeout(timer); resolve(null); } };
            fr.readAsDataURL(b);
          }).catch(function () { if (!done) { done = true; clearTimeout(timer); resolve(null); } });
        } catch (e) { if (!done) { done = true; clearTimeout(timer); resolve(null); } }
      });
    }
    var tasks = [];
    var imgs = node.querySelectorAll('img');
    for (var i = 0; i < imgs.length; i++) {
      (function (img) {
        var src = img.getAttribute('src') || '';
        if (!/^https?:\/\//i.test(src)) return;
        tasks.push(fetchDataUrl(src).then(function (d) { if (d) img.setAttribute('src', d); else img.removeAttribute('src'); }));
      })(imgs[i]);
    }
    var all = node.querySelectorAll('*');
    for (var j = 0; j < all.length; j++) {
      (function (el) {
        var cs = null; try { cs = getComputedStyle(el); } catch (e) {}
        var bi = cs && cs.backgroundImage;
        if (!bi || bi === 'none' || !/^url\(/i.test(bi)) return;
        var m = /url\((["']?)(.*?)\1\)/i.exec(bi);
        if (!m) return;
        var url = m[2];
        if (!/^https?:\/\//i.test(url)) return;
        tasks.push(fetchDataUrl(url).then(function (d) { if (d) el.style.backgroundImage = 'url(' + d + ')'; }));
      })(all[j]);
    }
    return Promise.all(tasks);
  }

  /** 把一张卡片栅格化成高清 PNG（SVG foreignObject → canvas），结果通过 cb(dataUrlBase64) 回调；失败走 onError(msg)。
   *  opts: { width?: 目标渲染宽度(像素) } —— 发 QQ 时传更宽（如 1440）避免图又小又糊。 */
  function rasterizeCard(wrap, html, cb, onError, opts) {
    opts = opts || {};
    var node = wrap && wrap.firstElementChild;
    if (!node) { if (onError) onError('卡片为空（没有可处理的内容）'); else toast('没有可处理的内容'); return; }
    var naturalW = Math.max(1, Math.ceil(node.scrollWidth || node.offsetWidth));
    // 目标宽度：发 QQ 时传更宽（如 1440）避免图又小；上限防止 base64 过大经桥传输超时
    var maxW = 1600;
    var targetW = (opts.width && opts.width > naturalW) ? opts.width : naturalW;
    if (targetW > maxW) targetW = maxW;
    var bg = '#ffffff';
    try {
      var cs = getComputedStyle(node);
      var c = cs && cs.backgroundColor;
      if (c && c !== 'rgba(0, 0, 0, 0)' && c !== 'transparent') bg = c;
      else { var bd = getComputedStyle(document.body); if (bd && bd.backgroundColor) bg = bd.backgroundColor; }
    } catch (e) { /* ignore */ }

    var holder = document.createElement('div');
    holder.style.cssText = 'position:fixed;left:-99999px;top:0;width:' + targetW + 'px;height:0;overflow:hidden;';
    var clone = node.cloneNode(true);
    clone.style.position = 'relative';
    clone.style.top = ''; clone.style.left = ''; clone.style.right = ''; clone.style.bottom = '';
    clone.style.margin = '0';
    clone.style.transform = 'none';
    clone.style.transformOrigin = '';
    clone.style.width = targetW + 'px';
    clone.style.height = 'auto';
    holder.appendChild(clone);
    document.body.appendChild(holder);

    // 先把外链图内联成 data URI（消除 canvas 污染），再量高度、序列化、出图
    inlineImages(clone).then(function () {
      var h = Math.max(1, Math.ceil(clone.scrollHeight || clone.offsetHeight || node.scrollHeight));
      document.body.removeChild(holder);
      toast('正在生成图片…');
      var serialized = '';
      try { serialized = new XMLSerializer().serializeToString(clone); } catch (e) { serialized = clone.outerHTML; }
      requestExportCss(html).then(function (hostCss) {
        var css = xmlText(hostCss) + '\n' + xmlText(collectPlainCss());
        // 1:1 渲染：SVG 尺寸 == 内容真实尺寸（cw=targetW, ch=h）。浏览器按真实分辨率绘制文字，不发虚。
        // 关键点：不用 viewBox 放大（1×→放大插值会糊），也不在 foreignObject 内用 transform:scale
        //（该 transform 在「SVG 转图片」管线里常常不生效，导致空白/半图，正是「截图失败 / 调不了」的真凶）。
        var cw = targetW, ch = h;
        var svg =
          '<svg xmlns="http://www.w3.org/2000/svg" width="' + cw + '" height="' + ch + '">' +
            '<foreignObject x="0" y="0" width="' + cw + '" height="' + ch + '">' +
              '<div xmlns="http://www.w3.org/1999/xhtml" style="width:' + cw + 'px;height:' + ch + 'px;overflow:hidden;background:' + bg + ';">' +
                '<style>' + css + '</style>' + serialized +
              '</div>' +
            '</foreignObject>' +
          '</svg>';
        var b64;
        try { b64 = btoa(unescape(encodeURIComponent(svg))); }
        catch (e) { if (onError) onError('图片导出失败：内容过大或含非法字符，请改用 HTML / PDF'); else toast('图片导出失败（内容过大或含非法字符），请改用 HTML / PDF'); return; }
        var img = new Image();
        img.onload = function () {
          try {
            var canvas = document.createElement('canvas');
            canvas.width = cw; canvas.height = ch;
            var ctx = canvas.getContext('2d');
            ctx.fillStyle = bg;
            ctx.fillRect(0, 0, cw, ch);
            ctx.drawImage(img, 0, 0, cw, ch);
            var url = canvas.toDataURL('image/png');
            var data = url.split(',')[1] || '';
            if (!data) { if (onError) onError('图片导出失败：画布为空，请改用 HTML / PDF'); else toast('图片导出失败（画布为空），请改用 HTML / PDF'); return; }
            cb(data);
          } catch (e) {
            if (onError) onError('图片导出失败：' + ((e && e.message) || e) + '（含外部跨域图片被拦，请用 HTML / PDF）');
            else toast('图片导出失败：' + ((e && e.message) || e) + '（含外部图片/跨域可能被拦，请用 HTML / PDF）');
          }
        };
        img.onerror = function () { if (onError) onError('SVG 栅格化失败（foreignObject 解析受限），请改用 HTML / PDF 导出'); else toast('图片栅格化失败（SVG 解析受限），请改用 HTML / PDF 导出'); };
        img.src = 'data:image/svg+xml;base64,' + b64;
      }).catch(function (e) { if (onError) onError('生成内联样式失败：' + ((e && e.message) || e)); });
    }).catch(function (e) { if (onError) onError('图片资源内联失败：' + ((e && e.message) || e)); else toast('图片资源内联失败'); });
  }

  /** 导出卡片为 PNG 文件。 */
  function saveCardPng(wrap, html) {
    rasterizeCard(wrap, html, function (data, ratio) {
      send('panelSavePng', { name: 'RawHtml-卡片-' + Date.now(), style: lockedStyle(), data: data });
      toast('已请求保存 PNG（' + ratio + '× 高清）');
    });
  }

  /** AI 触发：把面板里最近一张卡片栅格化成 PNG 并回传给 C#（用于发 QQ）。带超时兜底：前端未回传则发空，C# 侧会判超时。
   *  失败原因通过 error 字段回传，C# 侧据此给主人精确提示，而不是笼统的「30 秒未回传」。 */
  function rasterizeLastCard(req) {
    send('log', { level: 'info', text: '[RawHtml] rasterizeLastCard 进入; req=' + req + '; bridge=' + (window.RawHtmlBridge ? 'ok' : 'MISSING') });
    try {
      var wraps = document.querySelectorAll('#cards .vcp-card-wrap');
      var wrap = wraps.length ? wraps[wraps.length - 1] : null;
      var html = '';
      try { var arr = window.RawHtmlBridge.getCards(); if (arr && arr.length) html = arr[arr.length - 1].html || ''; } catch (e) { }
      send('log', { level: 'info', text: '[RawHtml] rasterizeLastCard: wrap=' + (wrap ? 'found' : 'null') + '; htmlLen=' + html.length });
      if (!wrap) {
        send('log', { level: 'info', text: '[RawHtml] rasterizeLastCard: 无卡片，回传错误' });
        send('lastCardImage', { req: req, data: '', error: '面板里没有可发送的卡片（最近一张卡片为空）' });
        return;
      }
      var done = false;
      var timer = setTimeout(function () {
        if (!done) {
          done = true;
          send('log', { level: 'info', text: '[RawHtml] rasterizeLastCard: 12s 超时触发，准备回传' });
          try { send('lastCardImage', { req: req, data: '', error: '前端栅格化超时（12s）' }); }
          catch (e) { send('log', { level: 'error', text: '[RawHtml] 超时回传异常: ' + (e && e.message ? e.message : e) }); }
        }
      }, 12000);
      // 按卡片自身自然宽度出图（1:1 直出）。不再强制 1440px：卡片内容有自身宽度约束，
      // 强行把版式撑到 1440 会让内容只占左边一小块、右侧留一大片空白（见用户截图）。
      rasterizeCard(wrap, html, function (data) {
        if (done) return; done = true; clearTimeout(timer);
        send('log', { level: 'info', text: '[RawHtml] rasterizeLastCard: 成功回调 dataLen=' + (data ? data.length : 0) });
        try { send('lastCardImage', { req: req, data: data }); }
        catch (e) { send('log', { level: 'error', text: '[RawHtml] 成功回传异常: ' + (e && e.message ? e.message : e) }); }
      }, function (err) {
        if (done) return; done = true; clearTimeout(timer);
        send('log', { level: 'info', text: '[RawHtml] rasterizeLastCard: 失败回调 err=' + err });
        try { send('lastCardImage', { req: req, data: '', error: err || '栅格化失败' }); }
        catch (e) { send('log', { level: 'error', text: '[RawHtml] 失败回传异常: ' + (e && e.message ? e.message : e) }); }
      });
    } catch (e) {
      send('log', { level: 'error', text: '[RawHtml] rasterizeLastCard 同步异常: ' + (e && e.message ? e.message : e) });
      try { send('lastCardImage', { req: req, data: '', error: '栅格化异常：' + (e && e.message ? e.message : e) }); } catch (_) { }
    }
  }

  function stamp() { return new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19); }

  /** 「保存全部」下拉菜单。 */
  function openSaveMenu(anchor) {
    closeSaveMenu();
    var menu = el('div', 'rh-menu');
    function item(label, fn) {
      var it = el('button', 'rh-menu-item', label);
      it.onclick = function () { closeSaveMenu(); fn(); };
      menu.appendChild(it);
    }
    item('导出全部为单个 HTML（合并）', function () {
      var list = window.RawHtmlBridge.getCards();
      if (!list.length) { toast('面板里还没有卡片'); return; }
      send('panelSaveHtml', { name: 'RawHtml-全部卡片-' + stamp(), style: lockedStyle(), html: list.map(function (c) { return c.html; }).join('\n\n') });
    });
    item('每张卡片单独保存为 HTML', function () {
      var list = window.RawHtmlBridge.getCards();
      if (!list.length) { toast('面板里还没有卡片'); return; }
      list.forEach(function (c, i) { send('panelSaveHtml', { name: 'card-' + (i + 1), style: lockedStyle(), html: c.html }); });
      toast('已请求保存 ' + list.length + ' 张卡片');
    });
    item('导出整页为 PDF', function () {
      send('panelSavePdf', { name: 'RawHtml-全部卡片-' + stamp(), style: lockedStyle() });
    });
    document.body.appendChild(menu);
    var r = anchor.getBoundingClientRect();
    menu.style.right = '12px';
    menu.style.top = (r.bottom + 6) + 'px';
    dom.saveMenu = menu;
    setTimeout(function () {
      window.addEventListener('click', function handler(ev) { if (ev.target !== menu && !menu.contains(ev.target)) { closeSaveMenu(); window.removeEventListener('click', handler); } });
    }, 0);
  }
  function closeSaveMenu() { if (dom.saveMenu) { dom.saveMenu.remove(); dom.saveMenu = null; } }

  // ---------- 对外接口（由 alife-bridge.js 分发） ----------
  window.RawHtmlPanel = {
    applyState: function (state) {
      if (!state) return;
      S = Object.assign(S, state);
      var banner = document.getElementById('render-off-banner');
      if (banner) banner.className = 'rh-off-banner' + (S.render ? '' : ' show');
      // 面板开着时实时刷新
      if (dom.popMask) openSettings();
      if (dom.viewerMask) { var kw = dom.search ? dom.search.value : ''; renderGrid(kw); }

      // 历史记录：默认不在重启后自动重放；仅存入 S.history 供「历史记录」面板查阅 / 恢复 / 删除
      if (Array.isArray(state.history)) S.history = state.history.slice();
    },
    lockedStyle: lockedStyle,
    shouldRecord: shouldRecord,
    decorateCard: decorateCard,
    saveCardPng: saveCardPng,
    onPngCss: onPngCss,
    rasterizeLastCard: rasterizeLastCard,
    showDoc: showDoc,
    toast: toast
  };
  send('log', { level: 'info', text: '[panel-settings.js] 已加载; rasterizeLastCard=' + (window.RawHtmlPanel.rasterizeLastCard ? 'ok' : 'MISSING') });

  // ---------- 背景调色盘 ----------
  function initBgPicker() {
    var KEY = 'rawhtml-panel-bg';
    var btn = document.getElementById('btn-bg');
    var pop = document.getElementById('bg-pop');
    var swatches = document.getElementById('bg-swatches');
    var colorInput = document.getElementById('bg-color');
    var resetBtn = document.getElementById('bg-reset');
    var presets = ['#0f1115', '#11151c', '#1a1d24', '#2b3038', '#16241c', '#241318', '#f3efe7', '#e9eef5'];
    function apply(color) {
      document.documentElement.style.setProperty('--panel-bg', color);
      try { localStorage.setItem(KEY, color); } catch (e) { /* 忽略 */ }
      if (colorInput) colorInput.value = /^#[0-9a-fA-F]{6}$/.test(color) ? color : '#0f1115';
      if (swatches) Array.prototype.forEach.call(swatches.children, function (s) {
        s.classList.toggle('sel', s.dataset.color && s.dataset.color.toLowerCase() === color.toLowerCase());
      });
    }
    if (swatches) presets.forEach(function (c) {
      var b = document.createElement('button');
      b.className = 'bg-swatch';
      b.style.background = c;
      b.dataset.color = c;
      b.title = c;
      b.onclick = function (e) { e.stopPropagation(); apply(c); };
      swatches.appendChild(b);
    });
    if (btn && pop) {
      btn.onclick = function (e) { e.stopPropagation(); pop.classList.toggle('show'); };
      document.addEventListener('click', function (e) {
        if (pop.classList.contains('show') && !pop.contains(e.target) && e.target !== btn) pop.classList.remove('show');
      });
    }
    if (colorInput) colorInput.oninput = function () { apply(colorInput.value); };
    if (resetBtn) resetBtn.onclick = function (e) { e.stopPropagation(); apply('#0f1115'); };
    var saved = null;
    try { saved = localStorage.getItem(KEY); } catch (e) { /* 忽略 */ }
    apply(saved || '#0f1115');
  }

  // ---------- 工具栏接线 ----------
  function init() {
    var btnSettings = document.getElementById('btn-settings');
    var btnAesthetic = document.getElementById('btn-aesthetic');
    if (btnSettings) btnSettings.onclick = openSettings;
    if (btnAesthetic) btnAesthetic.onclick = function () { openViewer(); };
    var btnHistory = document.getElementById('btn-history');
    if (btnHistory) btnHistory.onclick = function () { openHistory(); };
    var quickLock = document.getElementById('btn-lock-style');
    if (quickLock) quickLock.onclick = function () { openViewer(); };
    var btnSave = document.getElementById('btn-save');
    if (btnSave) btnSave.onclick = function (ev) { ev.stopPropagation(); openSaveMenu(btnSave); };
    // 主动要一次状态（宿主 init 时也会推）
    send('panelRequestState', {});
    initBgPicker();
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();
