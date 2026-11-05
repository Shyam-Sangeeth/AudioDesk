"""Presses AudioDesk's global shortcuts and prints what changed after each, through the local API and Sonar's web
server. This CHANGES REAL AUDIO: volumes, mute, output devices, Sonar routing and the profile. Run it only when that's
wanted. It saves Sonar's outputs, presets and sliders first and puts them back at the end; AudioDesk's own settings
are not restored, so back up %APPDATA%\\AudioDesk\\mixer.json first and restore it afterwards (config-diff.py).

It uses F13-F20, which no other app uses. Set them up once, with AudioDesk closed:
    python -I tools/shortcut-keys-test.py --setup
then start AudioDesk (with Knob support on, for the local API) and run:
    python -I tools/shortcut-keys-test.py"""
import ctypes
import json
import os
import ssl
import sys
import time
import urllib.parse
import urllib.request

SHORTCUTS = {
    "game.volume_down": "F13", "game.volume_up": "F14", "game.mute": "F15", "media.next_device": "F16",
    "aux.sonar": "F17", "headset": "F18", "home_theater": "F19", "sonar": "F20",
}
CHANNELS = ("game", "media", "aux")

if sys.argv[1:] == ["--setup"]:
    path = os.path.join(os.environ["APPDATA"], "AudioDesk", "mixer.json")
    config = json.load(open(path, encoding="utf-8-sig"))
    config["Shortcuts"] = SHORTCUTS
    json.dump(config, open(path, "w", encoding="utf-8"), indent=2)
    print("F13-F20 written to", path)
    sys.exit(0)

props = json.load(open(os.path.join(os.environ["ProgramData"], "SteelSeries", "GG", "coreProps.json"), encoding="utf-8"))
ctx = ssl.create_default_context()
ctx.check_hostname = False
ctx.verify_mode = ssl.CERT_NONE
apps = json.load(urllib.request.urlopen(f"https://{props['ggEncryptedAddress']}/subApps", context=ctx, timeout=3))
base = apps["subApps"]["sonar"]["metadata"]["webServerAddress"].rstrip("/")


def sget(path):
    return json.load(urllib.request.urlopen(base + path, timeout=3))


def sput(path):
    urllib.request.urlopen(urllib.request.Request(base + path, method="PUT"), timeout=3).read()


def sonar():
    return {
        "outputs": {r["id"]: r["deviceId"] for r in sget("/classicRedirections") if r["id"] in CHANNELS},
        "presets": {c["virtualAudioDevice"]: (c["id"], c["name"]) for c in sget("/configs/selected")
                    if c["virtualAudioDevice"] in CHANNELS},
        "levels": {ch: s["classic"] for ch, s in sget("/volumeSettings/classic")["devices"].items() if ch in CHANNELS},
    }


def api():
    with open(r"\\.\pipe\AudioDesk.Api", "r+b", buffering=0) as f:
        line = b""
        while not line.endswith(b"\n"):
            line += f.read(1)
    return json.loads(line)


def show(label):
    s, sn = api(), sonar()
    names = {d["id"]: d["name"].split(" (")[0].replace("SteelSeries Sonar - ", "Sonar ")
             for c in s["channels"] for d in c["devices"]}
    chans = " | ".join(f"{c['id']}: {round(c['volume'] * 100)}%{' muted' if c['muted'] else ''}"
                       f"{' sonar' if c['through_sonar'] else ''} {names.get(c['device'], c['device'])}" for c in s["channels"])
    out = ", ".join(f"{k}={names.get(v, v or '-')}" for k, v in sn["outputs"].items())
    lv = ", ".join(f"{k}={round(v['volume'] * 100)}{'M' if v['muted'] else ''}" for k, v in sn["levels"].items())
    pr = ", ".join(f"{k}={v[1]}" for k, v in sn["presets"].items())
    print(f"{label}\n    profile={s['profile']} sonar={s['sonar']} | {chans}\n"
          f"    Sonar out: {out} | sliders: {lv} | presets: {pr}", flush=True)


def press(n, wait=2.5):
    vk = 0x7C + n - 13  # VK_F13 onwards
    u = ctypes.windll.user32
    u.keybd_event(vk, 0, 0, 0)
    time.sleep(0.05)
    u.keybd_event(vk, 0, 2, 0)
    time.sleep(wait)


before = sonar()
try:
    show("0. start")
    press(13); press(13); show("1. F13 x2 Game volume down (Game down two steps, and Sonar's game slider with it)")
    press(14); press(14); show("2. F14 x2 Game volume up (back where it was)")
    press(15); show("3. F15 Game mute (muted)")
    press(15); show("4. F15 again (unmuted)")
    press(16, 4); show("5. F16 Media next device (Media's output moves to the next device)")
    press(16, 4); show("6. F16 again (the next one again)")
    press(17, 4); show("7. F17 Aux through Sonar (Aux leaves Sonar, back on its own device)")
    press(17, 4); show("8. F17 again (Aux through Sonar again)")
    press(19, 5); show("9. F19 Home Theater profile")
    press(18, 5); show("10. F18 Headset profile")
    press(20, 6); show("11. F20 Sonar (every channel off Sonar)")
    press(20, 6); show("12. F20 again (every channel through Sonar)")
finally:
    now = sonar()
    for ch, dev in before["outputs"].items():
        if dev and now["outputs"].get(ch) != dev:
            sput(f"/classicRedirections/{ch}/deviceId/{urllib.parse.quote(dev, safe='')}")
    for ch, (pid, _) in before["presets"].items():
        if now["presets"].get(ch, ("",))[0] != pid:
            sput(f"/configs/{urllib.parse.quote(pid, safe='')}/select")
    for ch, lv in before["levels"].items():
        if abs(now["levels"][ch]["volume"] - lv["volume"]) > 0.0005:
            sput(f"/volumeSettings/classic/{ch}/Volume/{lv['volume']:.3g}")
        if now["levels"][ch]["muted"] != lv["muted"]:
            sput(f"/volumeSettings/classic/{ch}/Mute/{'true' if lv['muted'] else 'false'}")
    time.sleep(2)
    after = sonar()
    print("Sonar put back: outputs", after["outputs"] == before["outputs"],
          "presets", {k: v[0] for k, v in after["presets"].items()} == {k: v[0] for k, v in before["presets"].items()},
          "sliders", after["levels"] == before["levels"])
