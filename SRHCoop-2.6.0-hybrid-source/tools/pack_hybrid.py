"""Repack the game APK with the hybrid mod: no BepInEx, no DEX patch, no native shim.

* keeps every ABI the game shipped (armeabi-v7a + x86); the mod is pure managed
  code so it works on both without the old arm32-only native shim
* adds assets/bin/Data/Managed/SRHCoop.dll so Unity loads it with the other
  script assemblies
* optionally patches a startup method in Assembly-CSharp.dll (see il_patch.py)
"""
import argparse
import os
import sys
import zipfile

SIGNATURE_SUFFIXES = (".SF", ".RSA", ".DSA", ".EC")


def is_signature(name):
    if name.upper() == "META-INF/MANIFEST.MF":
        return True
    if not name.upper().startswith("META-INF/"):
        return False
    leaf = name.upper()[len("META-INF/"):]
    return "/" not in leaf and leaf.endswith(SIGNATURE_SUFFIXES)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("input")
    ap.add_argument("output")
    ap.add_argument("--dll", required=True, help="SRHCoop.dll to add to Managed/")
    ap.add_argument("--patched-csharp", default=None,
                    help="Assembly-CSharp.dll that replaces the one inside the APK")
    ap.add_argument("--drop-abi", default="",
                    help="comma separated entry prefixes to drop (default: keep every ABI)")
    args = ap.parse_args()

    drops = tuple(p for p in args.drop_abi.split(",") if p)
    patched = None
    if args.patched_csharp:
        patched = open(args.patched_csharp, "rb").read()

    added = 0
    dropped = 0
    tmp = args.output + ".tmp"
    with zipfile.ZipFile(args.input) as zin, ZipFileWriter(tmp) as zout:
        for info in zin.infolist():
            name = info.filename
            if is_signature(name):
                continue
            if drops and name.startswith(drops):
                dropped += 1
                continue
            if name == "assets/bin/Data/Managed/Assembly-CSharp.dll" and patched is not None:
                zout.write_bytes(name, patched)
                added += 1
                continue
            zout.copy(zin, info)
        with open(args.dll, "rb") as f:
            zout.write_bytes("assets/bin/Data/Managed/SRHCoop.dll", f.read())
            added += 1
    os.replace(tmp, args.output)
    print("[pack] %s -> %s (dropped %d, injected %d)" % (args.input, args.output, dropped, added))


class ZipFileWriter(object):
    def __init__(self, path):
        self.z = zipfile.ZipFile(path, "w", allowZip64=True)

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.z.close()

    def copy(self, zin, info):
        zi = zipfile.ZipInfo(info.filename, info.date_time)
        zi.compress_type = info.compress_type
        zi.external_attr = info.external_attr
        zi.create_system = info.create_system
        with zin.open(info) as src, self.z.open(zi, "w") as dst:
            while True:
                chunk = src.read(1 << 20)
                if not chunk:
                    break
                dst.write(chunk)

    def write_bytes(self, name, data, compress=zipfile.ZIP_DEFLATED):
        zi = zipfile.ZipInfo(name)
        zi.compress_type = compress
        zi.external_attr = 0o644 << 16
        self.z.writestr(zi, data)


if __name__ == "__main__":
    main()
