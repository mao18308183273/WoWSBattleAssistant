// probe.js —— 探测 Frida 17 里查导出函数的正确 API（只做一次，只读）
'use strict';

rpc.exports = {
  probe() {
    const r = [];
    try {
      const m = Process.findModuleByName('winhttp.dll');
      r.push('winhttp 模块: ' + (m ? m.base : 'null'));
      if (m) {
        r.push('模块实例方法: ' + Object.getOwnPropertyNames(Object.getPrototypeOf(m)).join(','));
        r.push('getExportByName 类型: ' + (typeof m.getExportByName));
        if (typeof m.getExportByName === 'function') {
          const e = m.getExportByName('WinHttpConnect');
          r.push('WinHttpConnect = ' + e);
          if (e) r.push('  →绝对地址 ' + e.toString());
        }
        const ex = m.enumerateExports ? m.enumerateExports() : [];
        r.push('导出总数: ' + ex.length);
        const hit = ex.filter(x => /WinHttp(Connect|OpenRequest|SendRequest|SetOption)/.test(x.name));
        hit.forEach(h => r.push('  导出: ' + h.name + ' @ ' + h.address));
      }
    } catch (e) {
      r.push('模块查询异常: ' + e);
    }
    try {
      r.push('Module.getExportByName: ' + (typeof Module.getExportByName));
      r.push('Module.findGlobalExportByName: ' + (typeof Module.findGlobalExportByName));
      r.push('Module.enumerateGlobalExports: ' + (typeof Module.enumerateGlobalExports));
      const all = Module.enumerateGlobalExports ? Module.enumerateGlobalExports() : [];
      const h2 = all.filter(x => /WinHttpConnect$/.test(x.name));
      h2.forEach(x => r.push('全局导出: ' + x.name + ' @ ' + x.address));
    } catch (e) {
      r.push('Module 静态查询异常: ' + e);
    }
    return r.join('\n');
  }
};
