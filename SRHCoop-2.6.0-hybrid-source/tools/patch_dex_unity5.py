"""Patch Unity 5.x UnityPlayer.<clinit> to load libbe5hook.so after libmain.so.

Unity 5.6 has no UnityPlayer.loadNative; libmain.so is loaded from the
UnityPlayer static initializer via loadLibraryStatic("main").  We append the
shim load right after that succeeds, wrapped in a try/catch so a shim failure
never prevents the game from starting.
"""
import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile

PLAYER = os.path.join("com", "unity3d", "player", "UnityPlayer.smali")

INJECT = """
    :try_start_be5
    const-string v0, "be5hook"

    invoke-static {v0}, Ljava/lang/System;->loadLibrary(Ljava/lang/String;)V
    :try_end_be5
    .catch Ljava/lang/Throwable; {:try_start_be5 .. :try_end_be5} :catch_be5

    goto :goto_be5

    :catch_be5
    move-exception v0

    :goto_be5
"""

CLINIT_RE = re.compile(r"^\.method static constructor <clinit>\(\)V[ \t]*\r?\n", re.MULTILINE)
MAIN_LOAD_RE = re.compile(
    r'^[ \t]*sput-boolean[ \t]+v\d+,[ \t]*Lcom/unity3d/player/UnityPlayer;->n:Z[ \t]*\r?\n',
    re.MULTILINE)


def run(cmd):
    print("[dex] " + " ".join(cmd))
    subprocess.run(cmd, check=True)


def patch(text):
    if "be5hook" in text:
        print("[dex] already patched")
        return text
    m = CLINIT_RE.search(text)
    if not m:
        raise SystemExit("<clinit> not found in UnityPlayer.smali")
    end = text.find("\n.end method", m.end())
    if end < 0:
        raise SystemExit("unterminated <clinit>")
    body = text[m.end():end]
    matches = list(MAIN_LOAD_RE.finditer(body))
    if not matches:
        raise SystemExit("'sput-boolean ...UnityPlayer->n:Z' not found (unexpected Unity version)")
    at = m.end() + matches[-1].end()
    print("[dex] injecting be5hook after main library load at offset %d" % at)
    return text[:at] + INJECT + text[at:]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--input", required=True)
    ap.add_argument("--output", required=True)
    ap.add_argument("--baksmali", required=True)
    ap.add_argument("--smali", required=True)
    ap.add_argument("--java", default="java")
    args = ap.parse_args()

    tmp = tempfile.mkdtemp(prefix="be5dex56_")
    try:
        smali_dir = os.path.join(tmp, "smali")
        run([args.java, "-jar", args.baksmali, "d", args.input, "-o", smali_dir])
        path = os.path.join(smali_dir, PLAYER)
        if not os.path.isfile(path):
            raise SystemExit("missing " + path)
        with open(path, "r", encoding="utf-8") as f:
            src = f.read()
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(patch(src))
        run([args.java, "-jar", args.smali, "a", smali_dir, "-o", args.output])
        print("[dex] wrote %s (%d bytes)" % (args.output, os.path.getsize(args.output)))
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    main()
