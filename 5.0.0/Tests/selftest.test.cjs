/* 测试框架自带的示例用例，也是 DOM 桩能力的回归测试。
 * 运行方式：node Resources/dom-harness.cjs --test Tests/selftest.test.cjs
 */
test('DOM 桩能创建并查询元素', () => {
  const box = document.createElement('div');
  box.id = 'alife-selftest-box';
  document.body.appendChild(box);
  assert(document.getElementById('alife-selftest-box') === box, '应能按 id 找回元素');
  box.remove();
  assert(!document.getElementById('alife-selftest-box'), '清理后不应残留');
});

test('样式桩支持 setProperty / getPropertyValue', () => {
  const el = document.createElement('div');
  el.style.setProperty('--probe-alpha', '0.45');
  assertEqual(el.style.getPropertyValue('--probe-alpha'), '0.45');
  el.style.removeProperty('--probe-alpha');
  assertEqual(el.style.getPropertyValue('--probe-alpha'), '');
});

test('classList 可切换选中态', () => {
  const el = document.createElement('div');
  el.classList.add('on');
  assert(el.classList.contains('on'));
  el.classList.toggle('on', false);
  assert(!el.classList.contains('on'));
});

test('断言失败会被正确记为不通过', () => {
  assertThrows(() => assert(false, '预期失败'), 'assertThrows 应捕获异常');
});

test('清理契约：测试层不能留在页面上', () => {
  const layer = document.createElement('div');
  layer.id = '__wp_test_panel';
  layer.style.position = 'fixed';
  layer.style.zIndex = '9999';
  document.body.appendChild(layer);
  assert(document.getElementById('__wp_test_panel'), '测试层已创建');
  layer.parentNode.removeChild(layer);
  assert(!document.getElementById('__wp_test_panel'), '测试层必须在用例结束时移除');
});
