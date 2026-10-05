#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
build_ship_index.py —— 从项目舰船数据库生成 shipGlobalId -> 中文船名 的紧凑索引。

为什么需要：
  游戏内 mod 给出的是「全局船 id」（如 4180587728）与内部名（如 PZSC109_Sejong）。
  要显示成人看得懂、也方便 AI 引用的名字（"世宗"），需要一张 id -> 中文名 的表。
  项目已有的 wows_ships_data_*.json（945 艘，33MB）含 ship_id 与中文 name，
  但太大，不适合每次加载；这里抽成 ~40KB 的紧凑索引。

用法：
  python build_ship_index.py --db "C:\\path\\wows_ships_data_20260801_125351.json"
      [--out ship_names_zh.json]

输出格式：{"4180587728": "世宗|9|巡洋舰|pan_asia", ...}
  （minimap_annotate.py 的 --ships 直接吃这个文件，也吃原始大 JSON）

游戏更新出新船后重跑一次即可。
"""
import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--db', required=True, help='wows_ships_data_*.json（数组格式）')
    ap.add_argument('--out', default=os.path.join(HERE, 'ship_names_zh.json'))
    a = ap.parse_args()

    with open(a.db, encoding='utf-8') as f:
        data = json.load(f)
    if not isinstance(data, list):
        print('数据库应为数组格式', file=sys.stderr)
        return 2

    idx = {}
    no_id = no_name = 0
    for s in data:
        sid = s.get('ship_id')
        if not isinstance(sid, int):
            no_id += 1
            continue
        nm = s.get('name')
        if not nm:
            no_name += 1
            continue
        idx[str(sid)] = '%s|%s|%s|%s' % (nm, s.get('tier', ''), s.get('vtype', ''),
                                         s.get('nation', ''))

    with open(a.out, 'w', encoding='utf-8') as f:
        json.dump(idx, f, ensure_ascii=False, separators=(',', ':'))
    print('已写出 %s' % a.out)
    print('  条目 %d / 原始 %d（无 ship_id %d，无中文名 %d）'
          % (len(idx), len(data), no_id, no_name))
    print('  大小 %.1f KB' % (os.path.getsize(a.out) / 1024.0))
    for k in list(idx)[:3]:
        print('  样例 %s -> %s' % (k, idx[k]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
