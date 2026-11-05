"""List textures in the Unity data files so we can find the splash logo."""
import os
import sys

import UnityPy

BASE = r"E:\deepseek\work\srhext\assets\bin\Data"


def main():
    targets = sys.argv[1:] or ["globalgamemanagers"]
    for name in targets:
        path = os.path.join(BASE, name)
        if not os.path.exists(path):
            print("missing", path)
            continue
        print("=== " + name)
        env = UnityPy.load(path)
        for obj in env.objects:
            if obj.type.name in ("Texture2D", "Sprite"):
                try:
                    data = obj.read()
                    w, h = getattr(data, "m_Width", None), getattr(data, "m_Height", None)
                    print("  %-12s %-28s %sx%s  fmt=%s" % (
                        obj.type.name, getattr(data, "m_Name", "?"), w, h,
                        getattr(getattr(data, "m_TextureFormat", None), "name", "?")))
                except Exception as e:
                    print("  err", obj.type.name, e)


if __name__ == "__main__":
    main()
