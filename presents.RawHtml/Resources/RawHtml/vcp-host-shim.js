/* vcp-host-shim.js —— 给 vcp-engine-v1.js 补上「原宿主（DSH 前端 bundle）」的三件套。
 *
 * 为什么需要这个文件：
 *   vcp-engine-v1.js 原本是「以文本形式注入 dsh-web-frontend bundle 模块作用域」的：
 *   - 它直接引用宿主模块作用域里的 `vc`（DOM 节点 → vdom 节点）、`hp`（style 串 → style 对象）、
 *     `f`（React：f.jsx / f.Fragment）；
 *   - `window.__vcpStable.render(html)` 返回的是 **React 元素（vdom 对象）**，不是真实 DOM
 *     节点 —— 原宿主把它交给 React 渲染。
 *   Alife 的 Raw HTML 面板只是个普通 Electron 页面（没有 React、没有 bundle 作用域），
 *   所以缺了这三件套时 render() 会抛 `f is not defined` / 返回 undefined，
 *   表现就是「面板一片空白」。本文件提供等价实现：
 *     1) f / vc / hp / Xu / jd / Sd —— 让引擎按原样跑通（vdom 路径、自愈层全保留）；
 *     2) __vcpMount(vdom, container) —— 用一个小型 vdom→DOM 挂载器替代 ReactDOM
 *        （含 SVG 命名空间、ref 回调、on* 事件、style 对象/!important、style/link 提到 head）。
 *
 * 依赖加载顺序：vendor/* → 本文件 → vcp-engine-v1.js → alife-bridge.js
 */
(function () {
  'use strict';

  var SVG_NS = 'http://www.w3.org/2000/svg';
  // 引擎 guardChildren 里认定的 SVG 标签（名称已按 HTML 解析器的 SVG 大小写调整表还原）
  var SVG_TAGS = {
    svg: 1, g: 1, path: 1, rect: 1, circle: 1, ellipse: 1, line: 1, polyline: 1, polygon: 1,
    text: 1, tspan: 1, textPath: 1, defs: 1, use: 1, symbol: 1, marker: 1, pattern: 1,
    clipPath: 1, mask: 1, filter: 1, linearGradient: 1, radialGradient: 1, stop: 1,
    animate: 1, animateTransform: 1, animateMotion: 1, foreignObject: 1, switch: 1, view: 1
  };

  function reportError(where, e) {
    try { console.error('[vcp-host-shim] ' + where, e); } catch (_) {}
  }

  function isTrusted() {
    try { return typeof window.__vcpTrusted === 'function' && window.__vcpTrusted(); } catch (_) { return false; }
  }

  // ---------- 1) hp：style 字符串 → style 对象（与原宿主 hp/jd/Sd 等价） ----------
  function kebabToCamel(k) {
    if (k.indexOf('--') === 0) return k;                     // CSS 自定义属性原样保留
    return k.replace(/-([a-z])/g, function (m, c) { return c.toUpperCase(); });
  }
  function styleToObject(sv) {
    var out = {};
    if (sv == null) return out;
    if (typeof sv === 'object') return sv;
    var parts = String(sv).split(';');
    for (var i = 0; i < parts.length; i++) {
      var decl = parts[i];
      var j = decl.indexOf(':');
      if (j === -1) continue;
      var k = decl.slice(0, j).trim();
      var v = decl.slice(j + 1).trim();
      if (!k || !v) continue;
      out[kebabToCamel(k)] = v;
    }
    return out;
  }

  // ---------- 2) vc：DOM 节点 → vdom 节点（原宿主 vc/Xu 的等价物） ----------
  // 安全过滤与原宿主 parseOpen 一致：非可信模式下 on* 事件一律丢弃。
  function domToVdom(node) {
    if (!node) return null;
    if (node.nodeType === 3) return node.nodeValue && node.nodeValue.length ? node.nodeValue : null; // 文本
    if (node.nodeType === 8 || node.nodeType === 9 || node.nodeType === 10) return null;             // 注释/文档/DTD
    if (node.nodeType !== 1) return null;

    var props = {};
    var attrs = node.attributes;
    for (var i = 0; i < attrs.length; i++) {
      var a = attrs[i];
      if (/^on/i.test(a.name)) { if (isTrusted()) props[a.name] = a.value; continue; }
      if (a.name === 'style') { props.style = styleToObject(a.value); continue; }
      if (a.name === 'class') { props.className = a.value; continue; }
      props[a.name] = a.value;
    }

    var children = [];
    for (var c = 0; c < node.childNodes.length; c++) {
      var v = domToVdom(node.childNodes[c]);
      if (v !== null && v !== undefined && v !== '') children.push(v);
    }
    if (children.length === 1) props.children = children[0];
    else if (children.length > 1) props.children = children;

    return { type: node.localName, props: props };
  }

  // ---------- 3) f：React 元素工厂的最小等价物 ----------
  var FRAGMENT = (typeof Symbol === 'function') ? Symbol('vcp.Fragment') : '__vcp_fragment__';
  function jsx(type, props) { return { type: type, props: props || {} }; }
  var f = { Fragment: FRAGMENT, jsx: jsx, jsxs: jsx, createElement: jsx };

  window.f = f;
  window.vc = domToVdom;
  window.hp = styleToObject;
  // 引擎的「跨前端代际」别名探测分支（rc.8+ 压缩器改名后的名字）
  window.Xu = domToVdom;
  window.jd = styleToObject;
  window.Sd = styleToObject;

  // ---------- 4) vdom → DOM 挂载器（替代 ReactDOM） ----------
  function applyStyle(el, style) {
    if (style == null) return;
    if (typeof style === 'string') { el.setAttribute('style', style); return; }
    for (var k in style) {
      var v = style[k];
      if (v == null || v === '') continue;
      var sv = String(v);
      var imp = false;
      var m = /!\s*important\s*$/i.exec(sv);
      if (m) { imp = true; sv = sv.slice(0, m.index).trim(); }
      var prop = k.indexOf('--') === 0 ? k : k.replace(/[A-Z]/g, function (c) { return '-' + c.toLowerCase(); });
      try { el.style.setProperty(prop, sv, imp ? 'important' : ''); }
      catch (e) { try { el.style.setProperty(prop, sv); } catch (_) {} }
    }
  }

  function applyProps(el, props) {
    if (!props) return;
    for (var name in props) {
      if (name === 'children' || name === 'ref' || name === 'key') continue;
      var v = props[name];
      if (v == null || v === false) continue;

      if (name === 'className') { el.setAttribute('class', String(v)); continue; }
      if (name === 'style') { applyStyle(el, v); continue; }
      if (name === 'dangerouslySetInnerHTML') { if (v && v.__html != null) el.innerHTML = String(v.__html); continue; }
      if (/^on/i.test(name)) {
        // 引擎/宿主只在可信模式下把 on* 透传过来；字符串按内联事件设置，函数则直接监听
        if (typeof v === 'function') { el.addEventListener(name.slice(2).toLowerCase(), v); }
        else if (isTrusted()) { el.setAttribute(name, String(v)); }
        continue;
      }
      if (typeof v === 'function') continue;
      try {
        el.setAttribute(name, String(v));
        if ((name === 'value' || name === 'checked') && name in el && typeof el[name] !== 'boolean') el[name] = v;
      } catch (_) {}
    }
  }

  // 返回值：该子树挂载出的根元素（Fragment/纯文本返回父容器或 null）
  function build(vnode, parentEl, inSvg, refs) {
    if (vnode == null || vnode === false || vnode === true || vnode === '') return null;
    if (typeof vnode === 'string' || typeof vnode === 'number') {
      var text = document.createTextNode(String(vnode));
      parentEl.appendChild(text);
      return text;
    }
    if (Array.isArray(vnode)) {
      var last = null;
      for (var i = 0; i < vnode.length; i++) {
        var r = build(vnode[i], parentEl, inSvg, refs);
        if (r) last = r;
      }
      return last;
    }

    var type = vnode.type !== undefined ? vnode.type : vnode.tag;
    var props = vnode.props || {};

    if (type === f.Fragment) { build(props.children, parentEl, inSvg, refs); return null; }
    if (typeof type === 'function') {                                   // 极少数函数式组件
      try { return build(type(props), parentEl, inSvg, refs); }
      catch (e) { reportError('component', e); return null; }
    }
    if (typeof type !== 'string') return null;

    var svg = inSvg || type === 'svg' || SVG_TAGS[type] === 1;
    var el = svg ? document.createElementNS(SVG_NS, type) : document.createElement(type);
    applyProps(el, props);
    if (props.children != null) build(props.children, el, svg, refs);

    // 注意：<style> 必须留在卡片容器内，绝不能提到 head ——
    // 引擎的 enforceFontChain / collectTextSelectors 只在「卡片子树」里找 <style>，
    // 据此判断某元素是否被显式字体选择器命中；提到 head 后查不到 → 每个文字元素都会被
    // 强行继承系统无衬线（表现：Lanxi 字体全不生效、卡片一律黑体）。
    parentEl.appendChild(el);

    if (typeof props.ref === 'function') refs.push([props.ref, el]);     // ref 等整棵树入文档后再回调
    return el;
  }

  /**
   * 把引擎返回的 vdom 挂载到 container，并（在整棵树进入文档后）回调全部 ref。
   * 引擎的 ref 里带 KaTeX/Mermaid 渲染、色彩变量注入、字体链与 SVG 自愈 —— 必须回调。
   */
  window.__vcpMount = function (vnode, container) {
    var refs = [];
    var root = build(vnode, container, false, refs);
    for (var i = 0; i < refs.length; i++) {
      try { refs[i][0](refs[i][1]); } catch (e) { reportError('ref', e); }
    }
    return root;
  };

  window.__vcpHostShimReady = true;
})();
