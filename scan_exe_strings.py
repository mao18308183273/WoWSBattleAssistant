import os, sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
t = r"J:\Games\World_of_Warships_CN360\bin\13243917\bin64\WorldOfWarships64.exe"
data = open(t, "rb").read()
print("size:", len(data))
patterns = [b"PnFModsLoader", b"ModsShell", b"res_mods", b"customPorts",
            b"BattleStatistics", b"dataHub", b"webGate", b"replaysGate", b"combatlog",
            b"python.log", b"scripts.zip", b"PyImport_", b"PyRun_", b"BigWorld",
            b"scripts_config", b"battleGate", b"callbacks", b"events.pyc", b"flashGate",
            b"mods", b"PnFMods", b"wows_replays"]
for pat in patterns:
    idx = data.find(pat)
    if idx >= 0:
        start = max(0, idx - 30)
        end = min(len(data), idx + len(pat) + 40)
        ctx = bytes([ch if 32 <= ch < 127 else 46 for ch in data[start:end]])
        print(f"  HIT {pat!r} @ {idx:#x}: {ctx.decode('ascii', 'replace')}")
    else:
        print(f"  --  {pat!r} NOT FOUND")
