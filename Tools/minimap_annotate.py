#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
minimap_annotate.py —— 【已停用 / 留档备查】
把游戏小地图截图渲染成「AI 友好版」（叠加 A-J x 1-10 网格 + 边缘刻度 + 可选花名册侧栏）。

⚠️ 2026-09-27 决定：**此方案放弃，不再接入程序。**
   改用「游戏自带小地图截图直接喂 AI」。放弃原因：
     1) 叠加层盖住并劣化了游戏**原生**小地图 —— 原图本身就画好了网格、船只图标和舰名标签；
     2) 我/敌标记点本来就小，套上页边与方格后更看不清；
     3) **队伍规模不固定**（随机战 15v15、部分模式 7v7、PVE/行动 25v15 或不定人数），
        固定 10x10 + 固定侧栏的版式假设站不住；
     4) 文字叙述也有问题。结论是"还不如游戏原来那张小地图"。
   → 正确分工：**图用游戏原图负责空间位置；"谁是谁"用 players.json 的文本花名册给 AI。**
   保留本脚本是因为它的几何代码（正方形画布 / 像素正方格 / 边缘刻度 / 半透明网格）
   以后做「赛后复盘图」（回放解析出的精确坐标）时可以直接复用。

── 以下为原设计说明 ──
设计约束（全部来自实测反馈）：
  1. **展示战舰名**（中文船名 + 等级 + 舰种），不是用户名。
     船名来自 Tools\\ship_names_zh.json（由项目船库 wows_ships_data_*.json 生成，按 shipGlobalId 索引）。
  2. **比例正确**：输出画布严格**正方形**（这样最后压到 512×512 不会变形）；
     网格单元是**像素正方格**（以短边算格边，列/行按实际宽高比取整，整体居中，非方图只留边不拉伸）。
  3. **不动原图**：底图默认**几乎不改**（只做极轻锐化/对比）。
     教训：把对比推到 ×1.55 / 饱和 ×1.45 会把岸上绿色岛屿压成纯黑板块、把白色"自己"点洗掉，
     人眼和 AI 都看不清。**对比度要靠网格/标签/文字去挣，不能靠拉爆底图。**
  4. **网格要轻**：默认 `subtle` —— 半透明白色细线 + 边缘短刻度，地形能透出来；
     另有 `bold`（高权重，远看醒目）与 `ticks`（只画边缘刻度、地图上完全无线）两种风格。
  5. **字号按最终目标尺寸反推**（默认打到 512×512），压缩后字号仍 ≥ 14px 等效。

用法：
  python minimap_annotate.py --input 小地图.png
      [--region x,y,w,h] [--divisions 10] [--size 1280] [--target 512]
      [--grid-style subtle|bold|ticks] [--enhance none|mild|strong]
      [--players players.json] [--state state.json] [--ships ship_names_zh.json]
      [--map-name 25_sea_hope] [--time 300] [--title "第3局"]
      [--panel] [--show-player-name] [--out out.png]

输出：
  <out>           正方形标注版 PNG
  <out>_readme.txt 读图提示 + 纯文本花名册（中文舰名），建议拼进 AI 提示词
"""
import argparse
import json
import os
import sys

from PIL import Image, ImageDraw, ImageEnhance, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))

FONT_BOLD = [r'C:\Windows\Fonts\msyhbd.ttc', r'C:\Windows\Fonts\msyh.ttc',
             r'C:\Windows\Fonts\simhei.ttf', r'C:\Windows\Fonts\arialbd.ttf']
FONT_REG = [r'C:\Windows\Fonts\msyh.ttc', r'C:\Windows\Fonts\simhei.ttf',
            r'C:\Windows\Fonts\simsun.ttc']

COL_TEAM = (60, 255, 110)
COL_ENEMY = (255, 60, 60)
COL_SELF = (255, 255, 255)
COL_DEAD = (170, 170, 170)
COL_BG = (16, 17, 21)
COL_LABEL = (255, 232, 96)
COL_META = (240, 242, 246)

INK = (255, 255, 255, 100)       # 内部细网格（半透明白）
INK_EDGE = (255, 255, 255, 165)  # 外边框
INK_TICK = (255, 226, 80, 235)   # 边缘刻度（亮黄）


def font(paths, size):
    for p in paths:
        try:
            if os.path.exists(p):
                return ImageFont.truetype(p, size)
        except Exception:
            continue
    return ImageFont.load_default()


def col_name(i):
    s = ''
    i += 1
    while i > 0:
        i, r = divmod(i - 1, 26)
        s = chr(65 + r) + s
    return s


def read_json(p):
    try:
        with open(p, 'r', encoding='utf-8') as f:
            return json.load(f)
    except Exception:
        return None


def load_ships(path):
    """支持完整船库数组或紧凑索引 {id: '名|级|种|系'}。"""
    if not path or not os.path.exists(path):
        return {}
    try:
        d = json.load(open(path, encoding='utf-8'))
    except Exception:
        return {}
    out = {}
    if isinstance(d, list):
        for s in d:
            sid = s.get('ship_id')
            if isinstance(sid, int):
                out[str(sid)] = {'name': s.get('name'), 'tier': s.get('tier'),
                                 'vtype': s.get('vtype'), 'nation': s.get('nation')}
    elif isinstance(d, dict):
        for k, v in d.items():
            if isinstance(v, str):
                p = v.split('|')
                out[str(k)] = {'name': p[0] if p else v,
                               'tier': p[1] if len(p) > 1 else None,
                               'vtype': p[2] if len(p) > 2 else None,
                               'nation': p[3] if len(p) > 3 else None}
            elif isinstance(v, dict):
                out[str(k)] = v
    return out


def resolve_ship(p, ships):
    """返回 (显示名, 等级, 舰种, 是否命中)。优先全局船 id，其次内部名降级。"""
    sid = p.get('shipGlobalId')
    if sid is not None:
        e = ships.get(str(sid))
        if e and e.get('name'):
            return e['name'], e.get('tier'), e.get('vtype'), True
    internal = p.get('shipInternal') or ''
    if internal:
        return internal, p.get('tier'), None, False
    return '未知舰船', p.get('tier'), None, False


def build_roster(players, state, ships, show_player=False):
    """返回 (面板行, 纯文本行)"""
    lines, texts = [], []
    if players and players.get('players'):
        rows = players['players']
        team = [p for p in rows if p.get('teamId') == 0]
        enemy = [p for p in rows if p.get('teamId') == 1]
        if not enemy:
            enemy = [p for p in rows if p.get('teamId') not in (0, 1)]

        def push(coll_lines, coll_texts, p, col):
            nm, tier, vtype, _ok = resolve_ship(p, ships)
            flags = []
            if p.get('isOwn'):
                flags.append('我')
            elif p.get('isBot'):
                flags.append('AI')
            if not p.get('alive'):
                flags.append('沉')
            head = 'T%s %s' % (tier, nm) if tier else str(nm)
            fl = ''.join('[%s]' % f for f in flags)
            coll_lines.append((('    %s%s' % (head, fl)).rstrip(),
                               col if p.get('alive') else COL_DEAD, False))
            extra = vtype or ''
            if show_player and not p.get('isBot') and p.get('name'):
                extra = ('%s %s' % (extra, p['name'])).strip()
            coll_texts.append('    %s  %s%s' % (head, extra, (' ' + fl) if fl else ''))

        lines.append(('我方 %d' % len(team), COL_TEAM, True))
        texts.append('【我方】%d 条' % len(team))
        for p in team:
            push(lines, texts, p, COL_TEAM)
        lines.append(('敌方 %d' % len(enemy), COL_ENEMY, True))
        texts.append('【敌方】%d 条' % len(enemy))
        for p in enemy:
            push(lines, texts, p, COL_ENEMY)

    if state:
        if state.get('teamAlive') is not None or state.get('enemyAlive') is not None:
            s = '我方存活 %s/%s   敌方存活 %s/%s' % (
                state.get('teamAlive'), state.get('teamCount'),
                state.get('enemyAlive'), state.get('enemyCount'))
            lines.append((s, (235, 235, 235), True))
            texts.append(s)
        if state.get('damageDealt') is not None:
            s = '我的伤害 %s / 击杀 %s / 核心区 %s' % (
                state.get('damageDealt'), state.get('frags'), state.get('citadels'))
            lines.append((s, (235, 235, 235), True))
            texts.append(s)
    return lines, texts


def halo(d, xy, text, f, fill, anchor=None, halo=2):
    x, y = xy
    for dx in range(-halo, halo + 1):
        for dy in range(-halo, halo + 1):
            if dx or dy:
                d.text((x + dx, y + dy), text, font=f, fill=(0, 0, 0), anchor=anchor)
    d.text((x, y), text, font=f, fill=fill, anchor=anchor)


def enhance(img, mode):
    if mode == 'none':
        return img
    if mode == 'strong':
        img = ImageEnhance.Contrast(img).enhance(1.35)
        img = ImageEnhance.Color(img).enhance(1.20)
        return ImageEnhance.Sharpness(img).enhance(1.25)
    # mild（默认）：几乎保持原样，只提一点清晰度
    img = ImageEnhance.Contrast(img).enhance(1.10)
    return ImageEnhance.Sharpness(img).enhance(1.15)


def annotate(img, divisions, size, target, meta, grid_style='subtle',
             panel_lines=None, panel_w=0, enh='mild'):
    """img: 小地图（任意宽高比）→ (正方形画布, (cols, rows, cell))"""
    # 网格几何：单元像素正方，整体居中
    cell = min(img.width, img.height) / float(divisions)
    cols = max(1, int(round(img.width / cell)))
    rows = max(1, int(round(img.height / cell)))
    gw, gh = cols * cell, rows * cell
    gx, gy = (img.width - gw) / 2.0, (img.height - gh) / 2.0

    k = size / float(target)
    meta_h = int(round(size * 0.050)) if meta else 0
    label_band = int(round(size * 0.032))
    legend_h = int(round(size * 0.046))
    top_band = meta_h + label_band
    bottom_band = label_band + legend_h
    left_band = label_band
    right_band = label_band

    avail_w = size - left_band - right_band - panel_w
    avail_h = size - top_band - bottom_band
    m = max(64, int(min(avail_w, avail_h)))

    sc = min(m / float(img.width), m / float(img.height))
    mw = max(1, int(round(img.width * sc)))
    mh = max(1, int(round(img.height * sc)))
    ox = left_band + (m - mw) // 2
    oy = top_band + (avail_h - m) // 2 + (m - mh) // 2

    canvas = Image.new('RGB', (size, size), COL_BG)

    # ---- 底图：默认几乎不动（保真优先）----
    big = enhance(img.resize((mw, mh), Image.LANCZOS), enh)
    canvas.paste(big, (ox, oy))

    sx = mw / float(img.width)
    sy = mh / float(img.height)

    def P(x, y):
        return (ox + x * sx, oy + y * sy)

    # ---- 网格：画在独立 RGBA 层上半透明合成，地形能透出来 ----
    ov = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
    od = ImageDraw.Draw(ov)
    w_thin = max(1, int(round(0.9 * k)))
    w_tick = max(1, int(round(1.6 * k)))
    tick = max(4, int(round(6 * k)))

    if grid_style in ('subtle', 'bold'):
        ink = INK if grid_style == 'subtle' else (255, 240, 90, 150)
        wline = w_thin if grid_style == 'subtle' else max(2, int(round(1.6 * k)))
        if grid_style == 'bold':
            for c in range(cols + 1):
                x, _ = P(gx + c * cell, gy)
                od.line([(x, oy), (x, oy + mh)], fill=(0, 0, 0, 150), width=wline + 4)
            for r in range(rows + 1):
                _, y = P(gx, gy + r * cell)
                od.line([(ox, y), (ox + mw, y)], fill=(0, 0, 0, 150), width=wline + 4)
        for c in range(1, cols):
            x, _ = P(gx + c * cell, gy)
            od.line([(x, oy), (x, oy + mh)], fill=ink, width=wline)
        for r in range(1, rows):
            _, y = P(gx, gy + r * cell)
            od.line([(ox, y), (ox + mw, y)], fill=ink, width=wline)

    # 外边框
    od.rectangle([ox, oy, ox + mw - 1, oy + mh - 1], outline=INK_EDGE,
                 width=max(1, int(round(1.1 * k))))
    # 边缘刻度（每个格点在四边向外伸出短刻度）—— 定位全靠它，不影响地图
    for c in range(cols + 1):
        x, _ = P(gx + c * cell, gy)
        od.line([(x, oy - tick), (x, oy)], fill=INK_TICK, width=w_tick)
        od.line([(x, oy + mh), (x, oy + mh + tick)], fill=INK_TICK, width=w_tick)
    for r in range(rows + 1):
        _, y = P(gx, gy + r * cell)
        od.line([(ox - tick, y), (ox, y)], fill=INK_TICK, width=w_tick)
        od.line([(ox + mw, y), (ox + mw + tick, y)], fill=INK_TICK, width=w_tick)

    canvas = Image.alpha_composite(canvas.convert('RGBA'), ov).convert('RGB')
    d = ImageDraw.Draw(canvas)

    f_lbl = font(FONT_BOLD, max(15, int(round(15 * k))))
    f_head = font(FONT_BOLD, max(17, int(round(19 * k))))
    f_panel = font(FONT_BOLD, max(14, int(round(16 * k))))
    f_item = font(FONT_REG, max(16, int(round(17 * k))))
    f_tip = font(FONT_REG, max(13, int(round(14 * k))))

    # ---- 坐标标签：放在刻度带里，与 meta 不再重叠 ----
    for c in range(cols):
        px, _ = P(gx + (c + 0.5) * cell, gy)
        lbl = col_name(c)
        halo(d, (px, oy - tick - label_band * 0.34), lbl, f_lbl, COL_LABEL,
             anchor='mm', halo=2)
        halo(d, (px, oy + mh + tick + label_band * 0.34), lbl, f_lbl, COL_LABEL,
             anchor='mm', halo=2)
    for r in range(rows):
        _, py = P(gx, gy + (r + 0.5) * cell)
        lbl = str(r + 1)
        halo(d, (ox - tick - label_band * 0.34, py), lbl, f_lbl, COL_LABEL,
             anchor='mm', halo=2)
        halo(d, (ox + mw + tick + label_band * 0.34, py), lbl, f_lbl, COL_LABEL,
             anchor='mm', halo=2)

    # ---- 顶部 meta（独占一条带，自动缩放以适应宽度）----
    if meta:
        maxw = size - left_band - right_band - 8
        fm = f_head
        while d.textlength(meta, font=fm) > maxw and fm.size > 12:
            fm = font(FONT_BOLD, fm.size - 1)
        halo(d, (left_band * 0.45, meta_h * 0.52), meta, fm, COL_META,
             anchor='lm', halo=2)

    # ---- 图例（底部独立带）----
    lx = left_band * 0.45
    ly = size - legend_h * 0.52
    rr = max(6, int(round(5 * k)))
    for label, col in (('我方', COL_TEAM), ('敌方', COL_ENEMY),
                       ('自己', COL_SELF), ('已沉', COL_DEAD)):
        d.ellipse([lx - rr, ly - rr, lx + rr, ly + rr], fill=col,
                  outline=(0, 0, 0), width=max(2, rr // 3))
        halo(d, (lx + rr + 6, ly), label, f_item, (240, 240, 240), anchor='lm', halo=1)
        lx += rr * 2 + 14 + int(d.textlength(label, font=f_item)) + 16
    halo(d, (lx + 6, ly), '坐标：字母+数字，如 F6', f_tip, (200, 206, 216),
         anchor='lm', halo=1)

    # ---- 右侧花名册（可选）：整块放在坐标带下方，避免压住顶部 meta ----
    if panel_w and panel_lines:
        px = size - panel_w + 14
        top = top_band + int(round(4 * k))
        d.rectangle([px - 16, top, size - 5, size - 6], fill=(22, 24, 30))
        y = top + int(round(10 * k))
        for text, col, is_head in panel_lines:
            f = f_head if is_head else f_panel
            halo(d, (px, y), text, f, col, halo=2)
            y += f.size + int(round(6 * k))
            if y > size - 12:
                break
    return canvas, (cols, rows, cell)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--input', required=True)
    ap.add_argument('--out', default='')
    ap.add_argument('--region', default='', help='x,y,w,h')
    ap.add_argument('--divisions', type=int, default=10)
    ap.add_argument('--size', type=int, default=1280, help='输出画布边长（正方形）')
    ap.add_argument('--target', type=int, default=512, help='最终压缩边长，用于反推字号/线宽')
    ap.add_argument('--grid-style', default='subtle',
                    choices=['subtle', 'bold', 'ticks'],
                    help='subtle=半透明细线(默认) / bold=高权重 / ticks=地图上不画线，只留边缘刻度')
    ap.add_argument('--enhance', default='none', choices=['none', 'mild', 'strong'],
                    help='none=完全保留原图(默认) / mild=轻微提对比 / strong=强力(会压暗地形，慎用)')
    ap.add_argument('--players', default='')
    ap.add_argument('--state', default='')
    ap.add_argument('--ships', default=os.path.join(HERE, 'ship_names_zh.json'))
    ap.add_argument('--map-name', default='')
    ap.add_argument('--time', default='')
    ap.add_argument('--title', default='')
    ap.add_argument('--panel', action='store_true')
    ap.add_argument('--show-player-name', action='store_true')
    a = ap.parse_args()

    img = Image.open(a.input).convert('RGB')
    if a.region:
        try:
            x, y, w, h = [int(v) for v in a.region.split(',')]
            img = img.crop((x, y, x + w, y + h))
        except Exception as e:
            print('region 解析失败: %s' % e, file=sys.stderr)
            return 2

    ships = load_ships(a.ships)
    players = read_json(a.players) if a.players else None
    state = read_json(a.state) if a.state else None
    panel_lines, roster_text = build_roster(players, state, ships,
                                            show_player=a.show_player_name)

    bits = []
    if a.title:
        bits.append(a.title)
    if a.map_name:
        bits.append('地图 %s' % a.map_name)
    if a.time:
        bits.append('t=%s' % a.time)
    if state and state.get('my_ship_internal'):
        sid = None
        for p in (players or {}).get('players', []):
            if p.get('isOwn'):
                sid = p.get('shipGlobalId')
                break
        nm, tier, _vt, _ok = resolve_ship(
            {'shipGlobalId': sid, 'shipInternal': state['my_ship_internal'],
             'tier': state.get('my_tier')}, ships)
        bits.append('我的船 T%s %s' % (tier, nm) if tier else '我的船 %s' % nm)
    meta = '   |   '.join(bits)   # 避免用 '·'：部分粗体字体缺该字形会渲染成怪符号

    panel_w = int(a.size * 0.34) if a.panel else 0
    out_img, ginfo = annotate(img, a.divisions, a.size, a.target, meta,
                              grid_style=a.grid_style, panel_lines=panel_lines,
                              panel_w=panel_w, enh=a.enhance)

    out = a.out or (os.path.splitext(a.input)[0] + '_ai.png')
    out_img.save(out)
    cols, rows, cell = ginfo
    print('已输出: %s  (%dx%d, 网格 %d列×%d行, 格边 %.1fpx, 风格 %s, 增强 %s)'
          % (out, out_img.width, out_img.height, cols, rows, cell,
             a.grid_style, a.enhance))

    tip = os.path.splitext(out)[0] + '_readme.txt'
    with open(tip, 'w', encoding='utf-8') as f:
        f.write('=== 读图提示（可直接拼进 AI 提示词）===\n')
        f.write('1) 这是游戏小地图的 AI 友好版：%d 列（A..%s，左->右）x %d 行（1..%d，上->下）。\n'
                % (cols, col_name(cols - 1), rows, rows))
        f.write('2) 网格是**正方格**、均匀铺满地图；描位置请用「字母+数字」（如 F6），不要编造经纬度。\n')
        f.write('3) 坐标参考系：四边有亮黄短刻度，标签在刻度外侧，上下都有字母、左右都有数字。\n')
        f.write('4) 颜色语义（本图已统一）：绿=我方，红=敌方，白=自己，灰=已阵亡。\n')
        f.write('5) 底图基本保留游戏原貌（未做破坏性增强），岸线/岛屿细节可直接判读。\n')
        if a.map_name:
            f.write('6) 地图：%s。\n' % a.map_name)
        f.write('\n=== 本局双方舰船（来自游戏内实时权威数据）===\n')
        for line in roster_text:
            f.write(line + '\n')
        f.write('\n注：船名取自项目舰船数据库（按 shipGlobalId 精确匹配）；'
                '“AI”= 官方 isBot 判定的电脑玩家。\n')
    print('读图提示+文本花名册: %s' % tip)
    return 0


if __name__ == '__main__':
    sys.exit(main())
