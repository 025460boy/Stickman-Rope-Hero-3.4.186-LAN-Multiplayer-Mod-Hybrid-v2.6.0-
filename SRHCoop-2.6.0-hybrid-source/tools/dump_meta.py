import sys
import dnfile

path = sys.argv[1]
pe = dnfile.dnPE(path)
md = pe.net.mdtables


def s(v):
    if v is None:
        return ""
    return str(v)


def base_name(row):
    try:
        if row.Extends and row.Extends.row:
            er = row.Extends.row
            ns = s(er.TypeNamespace)
            return (ns + "." if ns else "") + s(er.TypeName)
    except Exception:
        pass
    return ""


lines = []
for row in md.TypeDef:
    ns = s(row.TypeNamespace)
    name = s(row.TypeName)
    full = (ns + "." + name) if ns else name
    lines.append("T %s :: %s" % (full, base_name(row)))
    try:
        methods = [s(m.row.Name) for m in (row.MethodList or [])]
    except Exception:
        methods = []
    try:
        fields = [s(f.row.Name) for f in (row.FieldList or [])]
    except Exception:
        fields = []
    for m in methods:
        lines.append("    M " + m)
    for f in fields:
        lines.append("    F " + f)

sys.stdout.reconfigure(encoding="utf-8")
sys.stdout.write("\n".join(lines))
