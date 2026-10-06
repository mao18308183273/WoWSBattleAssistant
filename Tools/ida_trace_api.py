# -*- coding: utf-8 -*-
"""
IDA 动态分析脚本 —— F:\\预瞄工具\\Start.exe

★ 用法（在 IDA 里手动操作两步，本脚本只负责第 1 步）:
  第 1 步：Alt+F7 选本文件 → 自动在关键 WinHTTP / 文件 API 上打断点
  第 2 步：Debugger → Start process (F9)，让它跑起来

  断点每次命中会自动把运行时参数（解密后的服务器名、打开的文件路径）
  写到  C:\\Users\\mao_z\\Downloads\\ida_trace.log

★ 为什么必须动态看：
  静态分析里 URL / 域名 / 文件路径 / 配置名**一个都搜不到**（全加密），
  只有运行时解密后的值才暴露。

【关键目标】
  WinHttpConnect  第2参 pswzServerName = 云端域名    ★最想拿到的
  WinHttpOpenRequest 第3参 pwszObjectName = 请求路径
  CreateFileW     第1参 = 它打开的文件
  RegOpenKeyExW   它读的注册表项
  GetProcAddress  它动态取了哪些函数

【断点策略】
  每个 API 只在**前 2~3 个调用点**下断点。Qt 程序命中太频繁，全下会把
  进程钉死、根本跑不动。
"""

import traceback
import os

import idaapi
import ida_bytes
import ida_funcs
import ida_kernwin
import ida_name
import ida_nalt
import idautils
import idc

LOG_PATH = r"C:\Users\mao_z\Downloads\ida_trace.log"

_seen = set()
_installed = False


def log(msg):
    line = "[TRACE] %s" % msg
    try:
        idc.echo(line)
    except Exception:
        pass
    try:
        with open(LOG_PATH, "a", encoding="utf-8", errors="replace") as f:
            f.write(line + "\n")
    except Exception:
        pass


def read_wstr(addr, max_chars=300):
    """读 UTF-16 宽字符串（绝大多数 Win32 API 用这个）。"""
    try:
        addr = int(addr)
    except Exception:
        return None
    if addr <= 0 or addr > 0x7FFFFFFFFFFF:
        return None
    out = []
    for i in range(max_chars):
        try:
            ch = ida_bytes.get_wide_dword(addr + i * 2)
        except Exception:
            break
        if ch == 0:
            break
        if 9 <= ch < 0xFFFD:
            out.append(chr(ch))
        else:
            out.append("\\u%04x" % ch)
    s = "".join(out)
    return s if s else None


def read_ansi(addr, max_chars=300):
    try:
        addr = int(addr)
    except Exception:
        return None
    if addr <= 0 or addr > 0x7FFFFFFFFFFF:
        return None
    out = []
    for i in range(max_chars):
        try:
            ch = ida_bytes.get_byte(addr + i)
        except Exception:
            break
        if ch == 0:
            break
        if 32 <= ch < 0x7F:
            out.append(chr(ch))
        else:
            out.append(".")
    s = "".join(out)
    return s if out else None


def _get_call_args(n=4):
    """取当前暂停点的调用参数。
    IDA 在调试事件里可通过 idc.regs['RCX'] 之类读 x64 寄存器；
    读不到就退回用栈（ESP+8 起是返回地址，再往后是参数）。
    """
    out = [0, 0, 0, 0]
    try:
        regs = idc.regs
        for i, rn in enumerate(("RCX", "RDX", "R8", "R9")):
            v = regs[rn]
            if v:
                out[i] = v
        if any(out):
            return out
    except Exception:
        pass
    try:
        sp = idc.regs["ESP"]
        for i in range(n):
            out[i] = ida_bytes.get_dword(sp + 8 + i * 8)   # +8 跳过返回地址
    except Exception:
        pass
    return out


# ------------------------------------------------------------ 断点回调
def make_cb(tag, kind):
    def _cb(ea, ctx):
        try:
            a = _get_call_args(4)
            detail = ""
            if kind == "host":
                detail = "SERVER=%s" % (read_wstr(a[1]) or "?")
            elif kind == "path":
                detail = "VERB=%s  PATH=%s" % (read_wstr(a[1]) or "?",
                                               read_wstr(a[2]) or "?")
            elif kind == "file":
                detail = "FILE=%s" % (read_wstr(a[0]) or read_ansi(a[0]) or "?")
            elif kind == "reg":
                detail = "REGKEY=%s" % (read_wstr(a[1]) or "?")
            elif kind == "proc":
                detail = "PROC=%s" % (read_wstr(a[1]) or "?")
            key = (tag, str(detail)[:200])
            if key in _seen:
                return
            _seen.add(key)
            log("0x%x %-24s %s" % (ea, tag, detail))
        except Exception:
            log("回调异常:\n%s" % traceback.format_exc())
    return _cb


def iter_text_calls(name_frag):
    """在 .text 段里找出所有 call 到指定 API 的指令地址。"""
    hits = []
    try:
        for seg in idautils.Segments():
            sname = (idc.get_segm_name(seg) or "").lower()
            if not (sname.endswith(".text") or sname.endswith("code")):
                continue
            end = idc.get_segm_end(seg)
            ea = seg
            while ea < end:
                try:
                    if idc.print_insn_mnem(ea) and \
                       idc.print_insn_mnem(ea).lower() == "call":
                        for xref in idautils.CodeRefsFrom(ea, 0):
                            tgt = xref
                            tname = idc.get_name(tgt) or ""
                            if not tname:
                                try:
                                    tname = idc.get_name(ida_bytes.get_qword(tgt)) or ""
                                except Exception:
                                    pass
                            # call [rip+disp] 时 tgt 指向 IAT 槽
                            if not tname:
                                try:
                                    tname = ida_name.get_name(
                                        ida_bytes.get_qword(tgt)) or ""
                                except Exception:
                                    pass
                            if name_frag.lower() in tname.lower():
                                hits.append((ea, tname))
                except Exception:
                    pass
                ea = idc.next_head(ea, end)
    except Exception:
        log("扫描 %s 出错:\n%s" % (name_frag, traceback.format_exc()))
    return hits


# (导入名子串, 断点上限, 参数类型)
PLAN = [
    ("WinHttpConnect",     2, "host"),
    ("WinHttpOpenRequest", 2, "path"),
    ("CreateFileW",        3, "file"),
    ("RegOpenKeyExW",      2, "reg"),
    ("GetProcAddress",     1, "proc"),
]


def setup():
    global _installed
    if _installed:
        log("重复执行，已忽略")
        return
    _installed = True

    try:
        if os.path.exists(LOG_PATH):
            os.remove(LOG_PATH)
    except Exception:
        pass

    log("=" * 62)
    log("输入文件: %s" % ida_nalt.get_input_file_path())
    log("日志输出: %s" % LOG_PATH)
    log("=" * 62)

    total = 0
    for frag, limit, kind in PLAN:
        try:
            hits = iter_text_calls(frag)
        except Exception:
            hits = []
        if not hits:
            log("[跳过] %-22s 未找到直接 call（可能经 __imp 间接调用）" % frag)
            continue
        for ea, tname in hits[:limit]:
            try:
                if not ida_bytes.add_bpt(ea, 0, idc.BPT_BREAKPOINT):
                    # 已经打过则更新回调
                    b = ida_bytes.get_bpt(ea)
                    if b:
                        b.flags = idc.BPT_BREAKPOINT
                        ida_bytes.update_bpt(b)
                if not ida_bytes.get_bpt(ea):
                    continue
                idx = ida_bytes.get_bpt(ea)  # 可能不是同一结构
                ida_bytes.attach_bpt(ea, make_cb(frag, kind), 0) \
                    if hasattr(ida_bytes, "attach_bpt") else None
                ida_name.set_name(ea, "hook_%s_%X" % (frag, ea), ida_name.SN_NOWARN)
                total += 1
                log("[断点] 0x%x  <- %s  (%s)" % (ea, tname[:48], frag))
            except Exception as e:
                log("  下断点失败 @0x%x: %s" % (ea, e))
        log("[完成] %-22s 找到 %d 处，打 %d 个断点" % (frag, len(hits), limit))

    log("=" * 62)
    log("共 %d 个断点。现在按 F9 启动程序，断点命中会自动记录。" % total)
    log("提示：程序若停在断点，按 F2 继续；右键 → 继续执行(F2)可一直跑。")
    log("=" * 62)


try:
    setup()
except Exception:
    log("脚本异常:\n%s" % traceback.format_exc())