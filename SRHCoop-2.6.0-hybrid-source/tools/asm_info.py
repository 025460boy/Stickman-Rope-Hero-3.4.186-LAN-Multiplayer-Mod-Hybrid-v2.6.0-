import sys
import dnfile

for path in sys.argv[1:]:
    pe = dnfile.dnPE(path)
    m = pe.net.metadata
    st = m.struct
    print(path.split("\\")[-1])
    print("   metadata v%d.%d runtime %d.%d" % (st.MajorVersion, st.MinorVersion,
                                                st.MajorRuntimeVersion, st.MinorRuntimeVersion))
    for r in (pe.net.mdtables.Assembly or []):
        print("   assembly: %s %s" % (r.Name, r.Version))
    for r in (pe.net.mdtables.CustomAttribute or []):
        try:
            t = r.Type
            if t is None:
                continue
            name = str(t.row.TypeName)
            if name in ("TargetFrameworkAttribute", "AssemblyConfigurationAttribute"):
                v = r.Value
                raw = bytes(v) if v else b""
                txt = "".join(chr(b) if 32 <= b < 127 else "." for b in raw)
                print("   %s: %s" % (name, txt[-60:]))
        except Exception:
            pass
