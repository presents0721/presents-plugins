/* Alife 插件测试框架 · Node DOM 桩
 * 用法：
 *   node dom-harness.cjs <injected.js>        健康检查注入脚本
 *   node dom-harness.cjs --test <file.test.cjs> 运行自定义用例
 * stdout 只输出一行 JSON，脚本自身的 console 输出被隔离。
 */
'use strict';
const fs = require('fs');
const vm = require('vm');

const out = (o) => process.stdout.write(JSON.stringify(o) + '\n');

function makeEl(tag, id) {
  const children = [];
  const el = {
    tagName: String(tag || 'div').toUpperCase(), id: id || '', className: '',
    innerHTML: '', textContent: '', value: '50', src: '', dataset: {},
    style: {
      cssText: '', transition: '', display: '', background: '',
      setProperty(k, v) { this[k] = v; }, getPropertyValue(k) { return this[k] || ''; },
      removeProperty(k) { delete this[k]; }
    },
    classList: { _s: new Set(), add(x) { this._s.add(x); }, remove(x) { this._s.delete(x); },
      toggle(x, f) { if (f === undefined) f = !this._s.has(x); f ? this._s.add(x) : this._s.delete(x); },
      contains(x) { return this._s.has(x); } },
    clientWidth: 300, clientHeight: 200, isContentEditable: false,
    children, firstChild: null, parentNode: null, parentElement: null,
    appendChild(c) { children.push(c); c.parentNode = this; c.parentElement = this; this.firstChild = children[0]; return c; },
    insertBefore(c) { return this.appendChild(c); },
    removeChild(c) { const i = children.indexOf(c); if (i >= 0) children.splice(i, 1); c.parentNode = null; this.firstChild = children[0] || null; },
    remove() { if (this.parentNode) this.parentNode.removeChild(this); },
    setAttribute(k, v) { if (k === 'id') this.id = v; else if (k === 'style') this.style.cssText = v; else this[k] = v; },
    getAttribute(k) { return k === 'id' ? this.id : (this[k] === undefined ? null : String(this[k])); },
    removeAttribute(k) { delete this[k]; },
    querySelector(sel) { const a = this.querySelectorAll(sel); return a[0] || null; },
    querySelectorAll(sel) {
      const all = []; const walk = (n) => { for (const c of n.children) { all.push(c); walk(c); } }; walk(this);
      const s = String(sel || '').toLowerCase();
      return all.filter(c => c.matches ? c.matches(s) : true);
    },
    addEventListener() {}, removeEventListener() {}, dispatchEvent() { return true; },
    getBoundingClientRect() { return { left: 0, top: 0, right: 300, bottom: 200, width: 300, height: 200 }; },
    getContext() { return null; }, focus() {}, blur() {}, click() {}, load() {}, pause() {}, play() { return Promise.resolve(); },
    contains() { return false; }
  };
  el.matches = function (s) {
    s = s.toLowerCase();
    if (s.startsWith('#')) return this.id === s.slice(1);
    if (s.startsWith('.')) return String(this.className).split(/\s+/).includes(s.slice(1));
    return this.tagName.toLowerCase() === s;
  };
  return el;
}

function buildContext(collect) {
  const body = makeEl('body'), head = makeEl('head'), docEl = makeEl('html');
  const allOf = (root, arr) => { for (const c of root.children) { arr.push(c); allOf(c, arr); } return arr; };
  const document = {
    hidden: false, visibilityState: 'visible', hasFocus: () => true,
    body, head, documentElement: docEl,
    images: [], scripts: [], styleSheets: [], forms: [], links: [],
    getElementById(id) { return allOf(body, allOf(head, [])).find(x => x.id === id) || null; },
    createElement: (t) => makeEl(t), createTextNode: (t) => ({ textContent: t, nodeType: 3 }),
    querySelector(sel) { return allOf(body, []).find(x => x.matches && x.matches(sel)) || null; },
    querySelectorAll(sel) { return allOf(body, []).filter(x => x.matches && x.matches(sel)); },
    addEventListener(t, f, o) { collect.listeners.push({ type: t, signaled: !!(o && o.signal) }); },
    removeEventListener() {}, elementFromPoint() { return body; }
  };

  const win = {
    document, console: { log() {}, warn() {}, error() {}, info() {}, debug() {} },
    AbortController, Promise, URL, URLSearchParams, Date, Math, JSON, RegExp, Map, Set,
    setTimeout: (f) => { collect.timers++; return 0; }, clearTimeout() {},
    setInterval: () => { collect.timers++; return 0; }, clearInterval() {},
    requestAnimationFrame: () => 0, cancelAnimationFrame() {},
    MutationObserver: class { constructor(cb) { this.cb = cb; } observe() { collect.observers++; } disconnect() { collect.observers--; } },
    IntersectionObserver: class { observe() {} disconnect() {} },
    getComputedStyle: () => ({ backgroundColor: 'rgba(0, 0, 0, 0)', backgroundImage: 'none',
      pointerEvents: 'auto', position: 'static', zIndex: 'auto', opacity: '1', transition: '',
      getPropertyValue() { return ''; } }),
    devicePixelRatio: 1, innerWidth: 1920, innerHeight: 1080,
    location: { search: '', pathname: '/', href: 'http://localhost/' },
    navigator: { userAgent: 'node-harness' },
    performance: { now: () => Date.now(), memory: null,
      getEntriesByType: () => [] },
    fetch: () => Promise.resolve({ ok: true, json: () => Promise.resolve({ ok: true }) }),
    addEventListener(t, f, o) { collect.listeners.push({ type: t, signaled: !!(o && o.signal) }); },
    removeEventListener() {}, matchMedia: () => ({ matches: false, addListener() {} }),
    // 常见 Web API 桩：注入脚本里出现时不应该因为宿主是 Node 就崩
    DataTransfer: class { constructor() { this.items = []; this.files = []; this.types = []; }
      setData() {} getData() { return ''; } clearData() {} },
    Event: class { constructor(type) { this.type = type; } preventDefault() {} stopPropagation() {} },
    CustomEvent: class { constructor(type, init) { this.type = type; this.detail = init && init.detail; } },
    DOMParser: class { parseFromString() { return makeEl('html'); } },
    XMLHttpRequest: class { open() {} send() {} setRequestHeader() {} },
    Image: class { constructor() { this.style = makeEl('img').style; } },
    Blob: class {}, File: class {}, FileReader: class { readAsText() {} },
    ResizeObserver: class { observe() {} disconnect() {} unobserve() {} },
    requestIdleCallback: (f) => { setTimeout(f, 0); return 0; }, cancelIdleCallback() {},
    __alifeHarness: true
  };
  win.window = win; win.globalThis = win; win.self = win;
  return { win, document };
}

async function healthCheck(file) {
  const collect = { listeners: [], timers: 0, observers: 0 };
  // 不少插件的注入脚本是「模板」：C# 侧会在运行前把 __WANT__ / __TEXT__ 之类的
  // 占位符替换掉。直接执行模板必然报未定义，这里换成空字符串让脚本能跑完。
  const code = fs.readFileSync(file, 'utf8').replace(/__[A-Z][A-Z0-9_]*__/g, '""');
  const { win } = buildContext(collect);
  const before = new Set(Object.keys(win));
  const ctx = vm.createContext(win);

  let thrown = null;
  try { vm.runInContext(code, ctx, { filename: file, timeout: 5000 }); }
  catch (e) { thrown = (e && e.message) || String(e); }

  const added = Object.keys(win).filter(k => !before.has(k) && k !== '__alifeHarness');
  const unscoped = added.filter(k => !k.startsWith('__') && !k.startsWith('Alife'));
  const hasTeardown = added.some(k => /teardown|clear|dispose|destroy/i.test(k));
  const hasVersion = added.some(k => /ver/i.test(k));

  return {
    ok: !thrown,
    error: thrown,
    globalsAdded: added.slice(0, 60),
    unscopedGlobals: unscoped.slice(0, 20),
    listeners: collect.listeners.length,
    listenersWithoutSignal: collect.listeners.filter(x => !x.signaled).length,
    observers: Math.max(0, collect.observers),
    timers: collect.timers,
    hasTeardown, hasVersion
  };
}

async function runTests(file) {
  const results = [];
  const assertions = { total: 0, failed: 0 };
  const { win, document } = buildContext({ listeners: [], timers: 0, observers: 0 });

  const assert = (ok, msg) => {
    assertions.total++;
    if (!ok) { assertions.failed++; throw new Error(msg || '断言失败'); }
  };
  const api = {
    assert,
    assertEqual: (a, b, m) => assert(JSON.stringify(a) === JSON.stringify(b), m || `${a} !== ${b}`),
    assertThrows: (fn, m) => {
      const before = assertions.failed;
      let t = false;
      try { fn(); } catch (e) { t = true; }
      assertions.failed = before;   // 被主动捕获的断言失败不算测试失败
      assert(t, m || '期望抛异常');
    },
    document, window: win, console: win.console,
    setTimeout: win.setTimeout, clearTimeout: win.clearTimeout,
    Promise, JSON, Math, Date, RegExp, URL, URLSearchParams
  };
  api.globalThis = api;

  const tests = [];
  api.test = (name, fn) => tests.push({ name, fn });

  const ctx = vm.createContext(api);
  try {
    vm.runInContext(fs.readFileSync(file, 'utf8'), ctx, { filename: file, timeout: 10000 });
    for (const t of tests) {
      const started = Date.now();
      try { await t.fn(); results.push({ name: t.name, passed: true, ms: Date.now() - started }); }
      catch (e) { results.push({ name: t.name, passed: false, ms: Date.now() - started, message: (e && e.message) || String(e) }); }
    }
  } catch (e) {
    results.push({ name: '(加载)', passed: false, ms: 0, message: (e && e.message) || String(e) });
  }

  return { ok: results.every(x => x.passed), tests: results, assertions };
}

(async () => {
  const args = process.argv.slice(2);
  try {
    if (args[0] === '--test') out(await runTests(args[1]));
    else out(await healthCheck(args[0]));
  } catch (e) {
    out({ ok: false, error: (e && e.message) || String(e) });
  }
})();
