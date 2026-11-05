"""Rewrite NUL-terminated strings inside an ELF .so, padding with zeros.

Caveat: only safe for strings that are used as standalone C strings and whose
bytes are not shared with adjacent data.
"""
import sys


def patch(path_in, path_out, pairs):
    data = bytearray(open(path_in, "rb").read())
    for old, new in pairs:
        o = old.encode()
        n = new.encode()
        if len(n) > len(o):
            raise SystemExit("replacement longer than original: %r" % old)
        idx = 0
        count = 0
        while True:
            i = data.find(o, idx)
            if i < 0:
                break
            data[i:i + len(o)] = n + b"\x00" * (len(o) - len(n))
            idx = i + len(o)
            count += 1
        print("patched %r -> %r (%d occurrence(s))" % (old, new, count))
    open(path_out, "wb").write(data)


if __name__ == "__main__":
    args = sys.argv[1:]
    pairs = []
    for a in args[2:]:
        old, new = a.split("=>")
        pairs.append((old, new))
    patch(args[0], args[1], pairs)
