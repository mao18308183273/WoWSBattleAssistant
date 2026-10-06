// hook_math.js —— 验证 Start.exe 的预瞄是「物理计算」还是「屏幕标定」
//
// 思路：
//   · 若它做物理弹道 → 会大量调 atan2/asin/sqrt/sin/cos（算方位角与提前量）
//   · 若它靠屏幕标定 → 数学函数调用很少，主要是像素坐标加减
// 另外 hook Qt 定时器，看它的刷新周期（决定它多快重算一次预瞄）。
'use strict';

const stat = {};
let tickCount = 0;
let timerHooks = 0;

function bump(k) {
  stat[k] = (stat[k] || 0) + 1;
}

function hookMath() {
  const m = Process.findModuleByName('ucrtbase.dll') ||
            Process.findModuleByName('msvcrt.dll') ||
            Process.findModuleByName('api-ms-win-crt-math-l1-1-0.dll');
  const mod = m || Process.mainModule;
  const names = ['atan2', 'asin', 'acos', 'sqrt', 'sin', 'cos', 'tan',
                 'atan', 'pow', 'fmod', 'log', 'exp', 'floor', 'ceil',
                 '__round', 'atan2f', 'sqrtf', 'hypot'];
  let ok = 0, fail = [];
  for (const n of names) {
    try {
      const a = mod.findExportByName ? mod.findExportByName(n) : null;
      if (!a) { fail.push(n); continue; }
      Interceptor.attach(a, { onEnter() { bump(n); } });
      ok++;
    } catch (e) { fail.push(n); }
  }
  send({ t: 'log', m: '[+] 数学函数 hook 成功 ' + ok + ' 个' +
                  (fail.length ? '，未找到: ' + fail.join(',') : '') });
}

function hookQt() {
  // Qt 的 QTimer / 事件循环在 qt5core_conda.dll
  const q = Process.findModuleByName('Qt5Core_conda.dll');
  if (!q) { send({ t: 'log', m: '[!] Qt5Core_conda.dll 未找到' }); return; }
  // QAbstractEventDispatcher::processEvents 的重载在 Qt 里是内联的，
  // 改抓 QCoreApplication 相关的虚表不现实。
  // 换个思路：统计时间差，观察它是否周期性重算。
  send({ t: 'log', m: '[i] Qt 已加载 @ ' + q.base + '（定时器改用时间差观察）' });
}

function hookScreen() {
  // 若它做屏幕坐标运算，会调 GetCursorPos / GetClientRect / GetWindowRect
  const u = Process.findModuleByName('user32.dll');
  if (!u) return;
  const names = ['GetCursorPos', 'GetClientRect', 'GetWindowRect',
                 'GetForegroundWindow', 'SetWindowPos', 'SetLayeredWindowAttributes'];
  let ok = [];
  for (const n of names) {
    try {
      const a = u.getExportByName(n);
      if (a) { Interceptor.attach(a, { onEnter() { bump('u32!' + n); } }); ok.push(n); }
    } catch (e) {}
  }
  send({ t: 'log', m: '[+] user32 hook: ' + ok.join(', ') });
}

function hookGdi() {
  // 它要画叠加层 → 必然大量调 GDI
  const g = Process.findModuleByName('gdi32.dll');
  if (!g) return;
  ['CreateSolidBrush', 'SetPixel', 'BitBlt', 'CreateFontIndirectW',
   'SelectObject', 'ExtTextOutW'].forEach(n => {
    try {
      const a = g.getExportByName(n);
      if (a) Interceptor.attach(a, { onEnter() { bump('gdi!' + n); } });
    } catch (e) {}
  });
  send({ t: 'log', m: '[+] gdi32 hook 已装（统计绘制调用频次）' });
}

setTimeout(() => {
  send({ t: 'log', m: '=== 开始统计（20 秒后出报告）===' });
  hookMath();
  hookScreen();
  hookGdi();
  setTimeout(() => {
    const keys = Object.keys(stat);
    send({ t: 'log', m: '=== 20 秒统计结果 ===' });
    keys.sort((a, b) => stat[b] - stat[a]).forEach(k => {
      send({ t: 'log', m: '  ' + k.padEnd(24) + stat[k] });
    });
    if (!keys.length) send({ t: 'log', m: '  （一次都没被调用）' });
  }, 20000);
}, 500);

rpc.exports = {
  stats() { return JSON.stringify(stat); }
};
