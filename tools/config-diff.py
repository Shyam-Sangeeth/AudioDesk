"""Prints the settings that differ between two mixer.json files, nested keys as paths. Use it before restoring a
backup after a test, to keep any change made meanwhile.

    python -I tools/config-diff.py <backup.json> %APPDATA%\\AudioDesk\\mixer.json"""
import json
import sys


def flat(node, path=""):
    if isinstance(node, dict):
        for k, v in node.items():
            yield from flat(v, f"{path}.{k}" if path else k)
    else:
        yield path, node


a = dict(flat(json.load(open(sys.argv[1], encoding="utf-8-sig"))))
b = dict(flat(json.load(open(sys.argv[2], encoding="utf-8-sig"))))
changed = [key for key in sorted(a.keys() | b.keys()) if a.get(key, "<missing>") != b.get(key, "<missing>")]
for key in changed:
    print(f"{key}: {a.get(key, '<missing>')!r} -> {b.get(key, '<missing>')!r}")
print(f"{len(changed)} differ")
