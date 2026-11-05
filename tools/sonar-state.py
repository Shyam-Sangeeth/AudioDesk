"""Read only: what Sonar reports through SteelSeries GG's local web server (Classic mode): each channel's output device,
selected preset and slider. Devices are named through AudioDesk's local API when it's on (Knob support), otherwise
shown by id.

    python -I tools/sonar-state.py              the state as JSON
    python -I tools/sonar-state.py --watch 60   one line a second for 60 s: each channel's output device, and (running)
                                                while something plays on it
    python -I tools/sonar-state.py --raw /classicRedirections   one response as it comes"""
import datetime
import json
import os
import ssl
import sys
import time
import urllib.request

props = json.load(open(os.path.join(os.environ["ProgramData"], "SteelSeries", "GG", "coreProps.json"), encoding="utf-8"))
ctx = ssl.create_default_context()
ctx.check_hostname = False
ctx.verify_mode = ssl.CERT_NONE
apps = json.load(urllib.request.urlopen(f"https://{props['ggEncryptedAddress']}/subApps", context=ctx, timeout=3))
base = apps["subApps"]["sonar"]["metadata"]["webServerAddress"].rstrip("/")
CHANNELS = ("game", "media", "aux")


def get(path):
    return json.load(urllib.request.urlopen(base + path, timeout=3))


def device_names():
    """Device id -> name, from AudioDesk's local API (one state line); empty when AudioDesk or the API is off."""
    try:
        with open(r"\\.\pipe\AudioDesk.Api", "r+b", buffering=0) as pipe:
            line = b""
            while not line.endswith(b"\n"):
                line += pipe.read(1)
        state = json.loads(line)
        return {d["id"]: d["name"] for c in state["channels"] for d in c["devices"]}
    except (OSError, ValueError, KeyError):
        return {}


args = sys.argv[1:]
if args[:1] == ["--raw"]:
    print(json.dumps(get(args[1] if len(args) > 1 else "/classicRedirections"), indent=1))
elif args[:1] == ["--watch"]:
    names = device_names()
    for _ in range(int(args[1]) if len(args) > 1 else 60):
        r = get("/classicRedirections")
        print(f"{datetime.datetime.now():%H:%M:%S} " + ", ".join(
            f"{x['id']}={names.get(x['deviceId'], x['deviceId'] or '-')}{' (running)' if x['isRunning'] else ''}"
            for x in r if x["id"] in CHANNELS), flush=True)
        time.sleep(1)
else:
    names = device_names()
    print(json.dumps({
        "mode": get("/mode"),
        "outputs": {r["id"]: names.get(r.get("deviceId"), r.get("deviceId")) for r in get("/classicRedirections")},
        "presets": {c["virtualAudioDevice"]: c["name"] for c in get("/configs/selected")},
        "levels": {ch: s["classic"] for ch, s in get("/volumeSettings/classic")["devices"].items()},
    }, indent=1))
