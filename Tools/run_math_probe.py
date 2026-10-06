import frida, time, sys
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
pid = int(sys.argv[1])
s = frida.attach(pid)
sc = s.create_script(open('hook_math.js', encoding='utf-8').read())
sc.on('message', lambda m, d: print(
    m['payload'].get('m', '') if m['type'] == 'send' else str(m)[:200], flush=True))
sc.load()
try:
    time.sleep(26)
except KeyboardInterrupt:
    pass
s.detach()
