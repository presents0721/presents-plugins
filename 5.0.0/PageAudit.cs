using System;
using System.Threading.Tasks;

namespace presents.OpsSuite;

/// <summary>
/// 页面注入层审计：通过 GlobalUI 的 IJSRuntime 在主页面跑一段只读脚本，
/// 列出遗留的 window.__* 全局、遮挡点击的 fixed 层、可疑测试 DOM 与样式残留。
/// 这正是「测试 div 留在页面上挡住侧边栏」那类问题的一眼可见工具。
/// </summary>
internal static class PageAudit
{
    public const string Script = """
        (function () {
          var out = { at: new Date().toLocaleTimeString(), globals: [], overlays: [],
                      clickBlockers: [], suspicious: [], initialResidue: [], markers: {}, domCount: 0 };

          try { for (var k in window) {
            if (k.indexOf('__') !== 0) continue;
            var v = window[k];
            out.globals.push({ name: k, type: typeof v,
                               size: (function(){ try { return JSON.stringify(v).length; } catch(e) { return -1; } })() });
          } } catch (e) { }

          try {
            ['__alifeWpObs','__alifeWpAbort','__alifeWpScene','__alifeWpCfg','__alifeWpVer',
             '__alifeWpTick','__tstatsMgr','__alifeWallpaperShow'].forEach(function (k) {
              out.markers[k] = k in window ? (window[k] ? 'set' : 'empty') : 'absent';
            });
          } catch (e) { }

          try {
            var all = document.body ? document.body.querySelectorAll('*') : [];
            out.domCount = all.length;
            var SUS = /test|probe|__wp_|debug|scratch/i;

            for (var i = 0; i < all.length; i++) {
              var el = all[i];
              var st = String(el.getAttribute('style') || '');

              if (st.indexOf('initial') >= 0 && out.initialResidue.length < 30)
                out.initialResidue.push({ tag: el.tagName, id: el.id, cls: String(el.className || '').slice(0, 60) });

              var id = String(el.id || '');
              if (id && SUS.test(id) && out.suspicious.length < 30)
                out.suspicious.push({ tag: el.tagName, id: id,
                  text: String(el.textContent || '').trim().slice(0, 40) });

              if (out.overlays.length >= 40) continue;
              var cs = getComputedStyle(el);
              if (cs.position !== 'fixed' && cs.position !== 'absolute') continue;
              var z = parseInt(cs.zIndex, 10);
              var area = el.clientWidth * el.clientHeight;
              // 阈值按视口比例算：写死 90000 在低分辨率下会漏掉一大片遮挡层
              if (area < Math.max(40000, (window.innerWidth * window.innerHeight) * 0.015)) continue;

              var blocks = (isNaN(z) ? 0 : z) >= 100 && cs.pointerEvents !== 'none';
              out.overlays.push({ tag: el.tagName, id: id,
                cls: String(el.className || '').slice(0, 60),
                w: el.clientWidth, h: el.clientHeight, z: isNaN(z) ? 0 : z,
                pe: cs.pointerEvents, bg: cs.backgroundColor,
                blocksClicks: blocks });

              // 「点了没反应」的直接证据：高层 + 不吃穿透。单独汇总一份，最值得先看
              if (blocks && out.clickBlockers.length < 20)
                out.clickBlockers.push({ tag: el.tagName, id: id || '(无 id)',
                  cls: String(el.className || '').slice(0, 60),
                  w: el.clientWidth, h: el.clientHeight, z: isNaN(z) ? 0 : z,
                  bg: cs.backgroundColor,
                  why: '固定/绝对定位 + z-index≥100 + pointer-events 非 none，覆盖区域内的点击会被它吃掉' });
            }
          } catch (e) { }

          return JSON.stringify(out);
        })()
        """;

    /// <summary>由 GlobalUI 提供的 JS 执行器；未挂载页面时为空。</summary>
    public static Func<string, Task<string>>? Runner { get; set; }

    static string lastResult = "{\"error\":\"尚未执行审计\"}";
    public static string LastResult => lastResult;

    public static async Task<string> RunAsync()
    {
        if (Runner == null) return "{\"error\":\"主页面未就绪（GlobalUI 尚未挂载）\"}";
        try
        {
            string result = await Runner(Script);
            if (!string.IsNullOrWhiteSpace(result)) lastResult = result;
            return lastResult;
        }
        catch (Exception ex)
        {
            return "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(ex.Message) + "}";
        }
    }
}
