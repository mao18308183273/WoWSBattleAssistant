import zipfile, re, sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
z = zipfile.ZipFile(r"J:\Games\World_of_Warships_CN360\bin\13243917\res\scripts.zip")
targets = [
    "scripts/ReplayPlayer/Managers/BattleManagerUtils/EventHandlers/VehicleVisibility.pyc",
    "scripts/ReplayPlayer/Managers/BattleManagerUtils/EventHandlers/ShipHP/ShipDamage.pyc",
    "scripts/ReplayPlayer/Managers/BattleManagerUtils/EventHandlers/ShipKill.pyc",
    "scripts/ReplayPlayer/Managers/BattleManagerUtils/EventHandlers/ShootGuns.pyc",
    "scripts/ReplayPlayer/Managers/BattleManagerUtils/EventHandlers/ConsumableUsage.pyc",
    "scripts/ReplayPlayer/Managers/BattleManagerUtils/EventHandlers/BuildingCaught.pyc",
]
def strings(data, minlen=5):
    out = []
    cur = []
    for b in data:
        if 32 <= b < 127 or b in (9, 10, 13):
            cur.append(chr(b))
        else:
            if len(cur) >= minlen:
                out.append("".join(cur))
            cur = []
    if len(cur) >= minlen:
        out.append("".join(cur))
    return out
for t in targets:
    print("=" * 22, t)
    data = z.read(t)
    seen = set()
    shown = 0
    for s in strings(data, 6):
        if s in seen:
            continue
        seen.add(s)
        if re.search(r'[a-zA-Z_]{4,}', s) and not re.search(r'[\x00-\x1f]', s) and "Wargaming" not in s and "locals" not in s:
            print("  ", s)
            shown += 1
        if shown >= 80:
            break
z.close()
