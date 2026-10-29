"""Prints AudioDesk's state from its local API (the named pipe AudioDesk.Api, see LocalApi.cs). Read only: it sends
nothing. Needs AudioDesk running with Knob support on.  python -I tools/local-api-state.py"""
import json

with open(r"\\.\pipe\AudioDesk.Api", "r+b", buffering=0) as f:
    line = b""
    while not line.endswith(b"\n"):
        line += f.read(1)
s = json.loads(line)
print("sonar_available", s["sonar_available"], "sonar", s["sonar"], "profile", s["profile"])
for c in s["channels"]:
    dev = next((d["name"] for d in c["devices"] if d["id"] == c["device"]), None)
    print(c["id"], "vol", c["volume"], "muted", c["muted"], "through_sonar", c["through_sonar"], "device", dev, "|", len(c["devices"]), "devices")
