import sys, zipfile, shutil, os

src, dst = sys.argv[1], sys.argv[2]
drop_prefixes = tuple(sys.argv[3].split(",")) if len(sys.argv) > 3 else ("lib/x86/",)
dropped = 0
with zipfile.ZipFile(src) as zin, zipfile.ZipFile(dst, "w", allowZip64=True) as zout:
    for info in zin.infolist():
        if info.filename.startswith(drop_prefixes):
            dropped += 1
            continue
        zi = zipfile.ZipInfo(info.filename, info.date_time)
        zi.compress_type = info.compress_type
        zi.external_attr = info.external_attr
        zi.create_system = info.create_system
        with zin.open(info) as fsrc, zout.open(zi, "w") as fdst:
            shutil.copyfileobj(fsrc, fdst, 1 << 20)
print("dropped %d entries -> %s (%d bytes)" % (dropped, dst, os.path.getsize(dst)))
