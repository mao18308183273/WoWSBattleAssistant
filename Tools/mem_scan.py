#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
mem_scan.py —— 直接读目标进程内存，搜已解密的敏感字符串。

用途：Start.exe 的字符串在磁盘上全加密（静态搜不到任何 URL/域名/路径），
但运行时会解密到堆上。这个脚本用 OpenProcess + ReadProcessMemory 扫
目标进程的所有可读区域，找出：
  - URL / 域名
  - 文件路径
  - 疑似预瞄相关的配置键（aim/lead/target/lock 之类）
  - 授权/机器码相关

只读内存，不写入、不注入。
"""
import argparse
import ctypes
import ctypes.wintypes as w
import re
import sys

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

k32 = ctypes.WinDLL('kernel32', use_last_error=True)

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
MEM_COMMIT = 0x1000
PAGE_GUARD = 0x100
PAGE_NOACCESS = 0x20


def open_proc(pid):
    h = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    if not h:
        raise ctypes.WinError(ctypes.get_last_error())
    return h


def read_mem(h, addr, size):
    buf = ctypes.create_string_buffer(size)
    n = w.SIZE_T()
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, size, ctypes.byref(n)):
        return b''
    return buf.raw[:n.value]


def enum_regions(h):
    """枚举所有可读（含 MEM_COMMIT 且非保护页）的区域。"""
    regions = []
    addr = 0
    while addr < 0x7FFFFFFF0000:
        mbi = w.MEMORY_BASIC_INFORMATION()
        n = w.SIZE_T()
        if not k32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi),
                                   ctypes.sizeof(mbi)):
            break
        base, size = mbi.BaseAddress, mbi.RegionSize
        if mbi.State == MEM_COMMIT and not (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)):
            regions.append((base, size))
        nxt = base + size
        if nxt <= addr:
            break
        addr = nxt
    return regions


PATTERNS = {
    'URL': rb'https?://[\x20-\x7e]{6,300}',
    '域名候选': rb'[\w\-\.]{2,40}\.(?:com|net|cn|org|io|cc|top|xyz|ru|tk)\b',
    'Windows 路径': rb'[A-Za-z]:\\[\x20-\x7e]{4,200}',
    '注册表': rb'(?:HKEY_|SOFTWARE\\|CurrentVersion)[\x20-\x7e]{4,200}',
    '预瞄相关': rb'(?:autoaim|aimbot|lead|target|lockon|torpedo|shotPoint|'
                rb'distDes|aimHeight|WorldOfWarships|wows)[\x20-\x7e]{0,60}',
    '授权相关': rb'(?:license|licence|activation|verify|serial|machinecode|'
                rb'hwid|token|auth)[\x20-\x7e]{0,60}',
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--pid', type=int, required=True)
    ap.add_argument('--max-mb', type=int, default=2048, help='单区读取上限 MB')
    ap.add_argument('--full', action='store_true', help='整段扫（慢但全）')
    a = ap.parse_args()

    h = open_proc(a.pid)
    regions = enum_regions(h)
    total = sum(s for _, s in regions)
    print('PID %d：%d 个可读区域，共 %.1f MB' % (a.pid, len(regions), total / 1048576))

    hits = {k: [] for k in PATTERNS}
    seen = {k: set() for k in PATTERNS}
    scanned = 0
    cap = a.max_mb * 1048576
    for base, size in regions:
        if size > cap:
            size = cap
        data = read_mem(h, base, size)
        scanned += len(data)
        for label, pat in PATTERNS.items():
            for m in re.finditer(pat, data):
                try:
                    s = m.group(0).decode('utf-8', 'replace')
                except Exception:
                    continue
                if s in seen[label]:
                    continue
                seen[label].add(s)
                hits[label].append((base + m.start(), s))
        if not a.full and scanned > 300 * 1048576:
            break

    print('已扫描 %.1f MB\n' % (scanned / 1048576))
    for label in PATTERNS:
        lst = hits[label]
        print('=== %s：%d 条 ===' % (label, len(lst)))
        for addr, s in lst[:40]:
            print('  0x%x  %s' % (addr, s[:150]))
        if not lst:
            print('  （无）')
        print()

    k32.CloseHandle(h)


if __name__ == '__main__':
    main()