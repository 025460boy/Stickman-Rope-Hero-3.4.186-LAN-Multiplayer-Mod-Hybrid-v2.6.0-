using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace SRHCoop
{
    /// <summary>File + logcat logger. Replaces the BepInEx log source.</summary>
    public static class CoopLog
    {
        private static string _path;
        private static readonly object _lock = new object();
        private static readonly List<string> _recent = new List<string>();
        private static bool _initFailed;

        public static string Path
        {
            get
            {
                if (_path == null && !_initFailed)
                {
                    try
                    {
                        _path = System.IO.Path.Combine(Application.persistentDataPath, "SRHCoop.log");
                    }
                    catch
                    {
                        _initFailed = true;
                    }
                }
                return _path;
            }
        }

        public static void Info(string msg) { Write("INFO", msg); }
        public static void Warn(string msg) { Write("WARN", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }

        public static List<string> Recent { get { return _recent; } }

        private static void Write(string level, string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + msg;
            try { Debug.Log("[SRHCoop] " + msg); } catch { }
            lock (_lock)
            {
                _recent.Add(line);
                if (_recent.Count > 200) _recent.RemoveAt(0);
                try
                {
                    string p = Path;
                    if (p != null) File.AppendAllText(p, line + Environment.NewLine);
                }
                catch { }
            }
        }
    }

    /// <summary>Tiny ini style config, written next to the log file.</summary>
    public class CoopConfig
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>();
        private string _path;

        public int Port = 27811;
        public bool AutoStart = true;
        public float MeleeDamage = 18f;
        public float RangedDamage = 12f;
        public float MeleeRange = 3.2f;
        public float RangedRange = 60f;
        public int SendRateHz = 15;
        public bool ShowHealthBars = true;
        public float UiScale = 1.2f;
        /// <summary>The privacy gate cannot be clicked in some emulators, so the
        /// hybrid build jumps straight to the menu. Turn off to keep the gate.</summary>
        public bool SkipPrivacyScreen = true;
        /// <summary>How long the launch screen (logo + mascot) stays before the menu.</summary>
        public float BootSplashSeconds = 3.0f;
        /// <summary>Fallback when the router blocks broadcast: peer IP to talk to directly.</summary>
        public string ConnectIp = "";
        /// <summary>Replicate nearby humanoid NPCs so both players see the same crowd.</summary>
        public bool SyncNpcs = true;
        /// <summary>Normalised screen positions of the floating button and the panel
        /// (-1 = never moved, use defaults).</summary>
        public float FloatX = -1f, FloatY = -1f, PanelX = -1f, PanelY = -1f;

        public static CoopConfig Load()
        {
            var cfg = new CoopConfig();
            try
            {
                cfg._path = System.IO.Path.Combine(Application.persistentDataPath, "SRHCoop.cfg");
                if (!File.Exists(cfg._path))
                {
                    cfg.Save();
                    return cfg;
                }
                foreach (string raw in File.ReadAllLines(cfg._path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    cfg._values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                cfg.Port = cfg.GetInt("Port", cfg.Port);
                cfg.AutoStart = cfg.GetBool("AutoStart", cfg.AutoStart);
                cfg.MeleeDamage = cfg.GetFloat("MeleeDamage", cfg.MeleeDamage);
                cfg.RangedDamage = cfg.GetFloat("RangedDamage", cfg.RangedDamage);
                cfg.MeleeRange = cfg.GetFloat("MeleeRange", cfg.MeleeRange);
                cfg.RangedRange = cfg.GetFloat("RangedRange", cfg.RangedRange);
                cfg.SendRateHz = Mathf.Clamp(cfg.GetInt("SendRateHz", cfg.SendRateHz), 5, 30);
                cfg.ShowHealthBars = cfg.GetBool("ShowHealthBars", cfg.ShowHealthBars);
                cfg.UiScale = Mathf.Clamp(cfg.GetFloat("UiScale", cfg.UiScale), 0.7f, 2.5f);
                cfg.SkipPrivacyScreen = cfg.GetBool("SkipPrivacyScreen", cfg.SkipPrivacyScreen);
                string ip;
                if (cfg._values.TryGetValue("ConnectIp", out ip)) cfg.ConnectIp = ip.Trim();
                cfg.SyncNpcs = cfg.GetBool("SyncNpcs", cfg.SyncNpcs);
                cfg.FloatX = cfg.GetFloat("FloatX", cfg.FloatX);
                cfg.FloatY = cfg.GetFloat("FloatY", cfg.FloatY);
                cfg.PanelX = cfg.GetFloat("PanelX", cfg.PanelX);
                cfg.PanelY = cfg.GetFloat("PanelY", cfg.PanelY);
                cfg.BootSplashSeconds = Mathf.Clamp(cfg.GetFloat("BootSplashSeconds", cfg.BootSplashSeconds), 0.5f, 12f);
            }
            catch (Exception e)
            {
                CoopLog.Warn("config load failed: " + e.Message);
            }
            return cfg;
        }

        public void Save()
        {
            try
            {
                if (_path == null) _path = System.IO.Path.Combine(Application.persistentDataPath, "SRHCoop.cfg");
                var sb = new StringBuilder();
                sb.AppendLine("# SRHCoop - Stickman Rope Hero LAN co-op");
                sb.AppendLine("# 淇敼鍚庨噸杩涙父鎴忕敓鏁?/ restart the game after editing");
                sb.AppendLine("Port=" + Port);
                sb.AppendLine("AutoStart=" + (AutoStart ? "true" : "false"));
                sb.AppendLine("MeleeDamage=" + MeleeDamage);
                sb.AppendLine("RangedDamage=" + RangedDamage);
                sb.AppendLine("MeleeRange=" + MeleeRange);
                sb.AppendLine("RangedRange=" + RangedRange);
                sb.AppendLine("SendRateHz=" + SendRateHz);
                sb.AppendLine("ShowHealthBars=" + (ShowHealthBars ? "true" : "false"));
                sb.AppendLine("UiScale=" + UiScale);
                sb.AppendLine("# 璺宠繃闅愮鏀跨瓥椤碉紙閮ㄥ垎妯℃嫙鍣ㄤ笂璇ラ〉鎸夐挳鐐逛笉鍔級/ skip the privacy gate");
                sb.AppendLine("SkipPrivacyScreen=" + (SkipPrivacyScreen ? "true" : "false"));
                sb.AppendLine("# 寮€鏈虹敾闈㈠仠鐣欐椂闀匡紙绉掞級/ launch screen duration");
                sb.AppendLine("BootSplashSeconds=" + BootSplashSeconds);
                sb.AppendLine("# 广播被路由器拦截时，填对方 IP 直连 / peer IP for direct connect");
                sb.AppendLine("ConnectIp=" + (ConnectIp ?? ""));
                sb.AppendLine("# 同步附近 NPC（双方看到同一批路人）/ replicate nearby NPCs");
                sb.AppendLine("SyncNpcs=" + (SyncNpcs ? "true" : "false"));
                sb.AppendLine("# 悬浮窗/面板位置（-1 表示未拖动过）/ normalised UI positions");
                sb.AppendLine("FloatX=" + FloatX);
                sb.AppendLine("FloatY=" + FloatY);
                sb.AppendLine("PanelX=" + PanelX);
                sb.AppendLine("PanelY=" + PanelY);
                File.WriteAllText(_path, sb.ToString());
            }
            catch (Exception e)
            {
                CoopLog.Warn("config save failed: " + e.Message);
            }
        }

        private int GetInt(string key, int def)
        {
            string v; int r;
            return _values.TryGetValue(key, out v) && int.TryParse(v, out r) ? r : def;
        }

        private float GetFloat(string key, float def)
        {
            string v; float r;
            return _values.TryGetValue(key, out v) && float.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out r) ? r : def;
        }

        private bool GetBool(string key, bool def)
        {
            string v;
            if (!_values.TryGetValue(key, out v)) return def;
            return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
        }
    }
}

