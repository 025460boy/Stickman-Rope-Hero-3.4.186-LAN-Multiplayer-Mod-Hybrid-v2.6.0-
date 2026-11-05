using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace SRHCoop
{
    /// <summary>Small IMGUI toolkit: procedurally generated rounded shapes plus the
    /// embedded chibi mascot, so the panel looks native without shipping assets.</summary>
    public static class UiKit
    {
        public const float RoundBorder = 20f;
        private const int RoundSize = 64;

        public static Texture2D White;
        public static Texture2D Round;
        public static Texture2D Chibi;
        /// <summary>Floating-button artwork.</summary>
        public static Texture2D Mascot;

        public static readonly Color Panel = new Color(0.063f, 0.078f, 0.110f, 0.94f);
        public static readonly Color PanelEdge = new Color(0.29f, 0.45f, 0.72f, 0.85f);
        public static readonly Color Header = new Color(0.10f, 0.16f, 0.26f, 0.98f);
        public static readonly Color Accent = new Color(0.31f, 0.56f, 1.00f, 1f);
        public static readonly Color Text = new Color(0.92f, 0.95f, 1f, 1f);
        public static readonly Color Muted = new Color(0.60f, 0.68f, 0.80f, 1f);
        public static readonly Color Btn = new Color(0.148f, 0.190f, 0.280f, 0.96f);
        public static readonly Color BtnHot = new Color(0.220f, 0.290f, 0.420f, 0.98f);
        public static readonly Color Good = new Color(0.35f, 0.88f, 0.45f, 1f);

        public static void Init()
        {
            if (White != null) return;
            White = Solid(Color.white);
            Round = MakeRounded(RoundSize, 20f);
            Chibi = LoadEmbedded("SRHCoop.chibi.png");
            Mascot = LoadEmbedded("SRHCoop.mascot.png");
        }

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private static Texture2D MakeRounded(int size, float radius)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px0 = x + 0.5f, py0 = y + 0.5f;
                    float cx = Mathf.Clamp(px0, radius, size - radius);
                    float cy = Mathf.Clamp(py0, radius, size - radius);
                    float dx = px0 - cx, dy = py0 - cy;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(radius - d + 0.5f);
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        public static Texture2D LoadEmbedded(string name)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream(name))
                {
                    if (s == null)
                    {
                        CoopLog.Warn("embedded resource missing: " + name);
                        return null;
                    }
                    var buf = new byte[s.Length];
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = s.Read(buf, read, buf.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!tex.LoadImage(buf))
                    {
                        CoopLog.Warn("chibi decode failed");
                        return null;
                    }
                    tex.wrapMode = TextureWrapMode.Clamp;
                    CoopLog.Info("chibi loaded " + tex.width + "x" + tex.height);
                    return tex;
                }
            }
            catch (Exception e)
            {
                CoopLog.Warn("chibi load failed: " + e.Message);
                return null;
            }
        }

        // ------------------------------------------------------------- drawing

        public static void DrawRounded(Rect r, Color color, float radius = RoundBorder)
        {
            if (Round == null) return;
            float b = Mathf.Min(radius, Mathf.Min(r.width, r.height) * 0.5f);
            if (b <= 0.5f)
            {
                GUI.color = color;
                GUI.DrawTexture(r, White);
                GUI.color = Color.white;
                return;
            }
            float ts = 1f / RoundSize;
            float uvB = radius * ts;

            GUI.color = color;
            // middle
            Tex(new Rect(r.x + b, r.y + b, r.width - 2 * b, r.height - 2 * b),
                new Rect(uvB, uvB, 1f - 2 * uvB, 1f - 2 * uvB));
            // edges
            Tex(new Rect(r.x + b, r.y, r.width - 2 * b, b), new Rect(uvB, 0f, 1f - 2 * uvB, uvB));
            Tex(new Rect(r.x + b, r.yMax - b, r.width - 2 * b, b), new Rect(uvB, 1f - uvB, 1f - 2 * uvB, uvB));
            Tex(new Rect(r.x, r.y + b, b, r.height - 2 * b), new Rect(0f, uvB, uvB, 1f - 2 * uvB));
            Tex(new Rect(r.xMax - b, r.y + b, b, r.height - 2 * b), new Rect(1f - uvB, uvB, uvB, 1f - 2 * uvB));
            // corners
            Tex(new Rect(r.x, r.y, b, b), new Rect(0f, 0f, uvB, uvB));
            Tex(new Rect(r.xMax - b, r.y, b, b), new Rect(1f - uvB, 0f, uvB, uvB));
            Tex(new Rect(r.x, r.yMax - b, b, b), new Rect(0f, 1f - uvB, uvB, uvB));
            Tex(new Rect(r.xMax - b, r.yMax - b, b, b), new Rect(1f - uvB, 1f - uvB, uvB, uvB));
            GUI.color = Color.white;
        }

        private static void Tex(Rect dst, Rect uv)
        {
            GUI.DrawTextureWithTexCoords(dst, Round, uv);
        }

        /// <summary>Rounded outline: draws a slightly larger rounded rect then the fill.</summary>
        public static void DrawRoundedOutline(Rect r, Color fill, Color edge, float thickness = 1.5f)
        {
            DrawRounded(new Rect(r.x - thickness, r.y - thickness, r.width + thickness * 2, r.height + thickness * 2), edge);
            DrawRounded(r, fill);
        }

        public static void Fill(Rect r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(r, White);
            GUI.color = Color.white;
        }

        public static void Bar(Rect r, float frac, Color color, Color back)
        {
            DrawRounded(r, back, Mathf.Min(6f, r.height * 0.5f));
            float w = Mathf.Clamp01(frac) * r.width;
            if (w > 1f)
                DrawRounded(new Rect(r.x, r.y, w, r.height), color, Mathf.Min(6f, r.height * 0.5f));
        }

        /// <summary>Rounded button with hover tint; returns true on click.</summary>
        public static bool Button(Rect r, string text, GUIStyle style, bool primary, float alpha = 1f)
        {
            bool hover = r.Contains(Event.current.mousePosition);
            Color bg = primary ? Accent : Btn;
            if (hover) bg = Color.Lerp(bg, primary ? Color.white : BtnHot, primary ? 0.18f : 1f);
            bg.a *= alpha;

            DrawRounded(r, bg, 12f);
            if (!primary)
                DrawRoundedOutline(new Rect(r.x, r.y, r.width, r.height),
                    new Color(1f, 1f, 1f, 0.02f), new Color(1f, 1f, 1f, 0.10f * alpha), 1f);

            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Label(r, text, style);
            GUI.color = Color.white;

            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }
    }
}
