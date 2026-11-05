using System;
using System.Collections.Generic;
using Game.Character.CharacterController;
using Game.Enemy;
using UnityEngine;

namespace SRHCoop
{
    public class NpcEntry
    {
        public int Id;
        public string Type;
        public Vector3 Position;
        public float Yaw;
        public float Health;
        public float MaxHealth;
        public bool Dead;
        public ushort Flags;
        public float Forward;
        public float Turn;
    }

    /// <summary>
    /// Replicates the humanoid NPCs around each player.
    ///
    /// The game spawns pedestrians/enemies locally and randomly, so two devices never
    /// agree on them. We cannot make the simulation deterministic, so instead every
    /// peer publishes its own nearby NPCs and renders everyone else's as inert
    /// puppets ("union" model): both players end up seeing the same set of NPCs.
    /// </summary>
    public class NpcSync
    {
        private const int MaxPerPacket = 10;
        private const float Radius = 55f;
        private const float ProxyTimeout = 1.2f;

        private class Proxy
        {
            public RemoteAvatar Avatar;
            public float LastSeen;
            public string Owner;
            public int Id;
        }

        private readonly Dictionary<string, Proxy> _proxies = new Dictionary<string, Proxy>();
        private readonly Dictionary<string, GameObject> _templates = new Dictionary<string, GameObject>();
        private readonly Dictionary<string, bool> _missingTemplate = new Dictionary<string, bool>();
        private readonly List<BaseStatusNPC> _scanBuffer = new List<BaseStatusNPC>();
        private float _scanTimer;

        public int VisibleProxies { get { return _proxies.Count; } }
        public int KnownTemplates { get { return _templates.Count; } }

        // ------------------------------------------------------------------ send

        /// <summary>Collect the NPCs near <paramref name="player"/>; throttled internally.</summary>
        public List<NpcEntry> Collect(Player player, float interval)
        {
            if (player == null) return null;
            _scanTimer -= Time.deltaTime;
            if (_scanTimer > 0f) return null;
            _scanTimer = interval;

            List<NpcEntry> result = null;
            try
            {
                BaseStatusNPC[] all = UnityEngine.Object.FindObjectsOfType<BaseStatusNPC>();
                if (all == null || all.Length == 0) return null;

                Vector3 center = player.transform.position;
                for (int i = 0; i < all.Length; i++)
                {
                    BaseStatusNPC npc = all[i];
                    if (npc == null || !npc.gameObject.activeInHierarchy) continue;
                    if (npc.IsDead) continue;

                    float dist = (npc.transform.position - center).sqrMagnitude;
                    if (dist > Radius * Radius) continue;

                    if (result == null) result = new List<NpcEntry>(MaxPerPacket);
                    if (result.Count >= MaxPerPacket) break;

                    var e = new NpcEntry();
                    e.Id = npc.GetInstanceID();
                    e.Type = TypeName(npc.gameObject.name);
                    e.Position = npc.transform.position;
                    e.Yaw = npc.transform.eulerAngles.y;
                    e.Health = npc.Health.Current;
                    e.MaxHealth = npc.Health.Max;
                    e.Dead = npc.IsDead;

                    Animator an = npc.GetComponentInChildren<Animator>();
                    if (an != null)
                    {
                        if (an.GetBool("IsMoving")) e.Flags |= 1;
                        if (an.GetBool("OnGround")) e.Flags |= 2;
                        if (an.GetBool("DoMelee")) e.Flags |= 4;
                        if (an.GetBool("Strafe")) e.Flags |= 8;
                        e.Forward = an.GetFloat("Forward");
                        e.Turn = an.GetFloat("Turn");
                    }
                    result.Add(e);
                }
            }
            catch (Exception ex)
            {
                CoopLog.Error("npc scan: " + ex.Message);
            }
            return result;
        }

        private static string TypeName(string goName)
        {
            if (string.IsNullOrEmpty(goName)) return "?";
            return goName.Replace("(Clone)", "").Trim();
        }

        // -------------------------------------------------------------- receive

        public void Apply(string ownerId, List<NpcEntry> entries)
        {
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < entries.Count; i++)
            {
                NpcEntry e = entries[i];
                string key = ownerId + "#" + e.Id;
                Proxy p;
                if (!_proxies.TryGetValue(key, out p))
                {
                    RemoteAvatar avatar = SpawnProxy(e);
                    if (avatar == null) continue;
                    p = new Proxy { Avatar = avatar, Owner = ownerId, Id = e.Id };
                    _proxies[key] = p;
                    CoopLog.Info("npc proxy + " + e.Type);
                }
                p.LastSeen = now;

                RemoteAvatar a = p.Avatar;
                if (a == null) { _proxies.Remove(key); continue; }

                var st = new PeerState
                {
                    Position = e.Position,
                    Yaw = e.Yaw,
                    Health = e.Health,
                    MaxHealth = e.MaxHealth,
                    Flags = e.Dead ? PeerFlags.Dead : PeerFlags.None,
                    Anim = new AnimSnapshot
                    {
                        Flags2 = (ushort)((e.Flags & 1) != 0 ? AnimBits.IsMoving : 0),
                    },
                };
                a.ApplyState(st);
                a.IsNpc = true;
            }
        }

        private RemoteAvatar SpawnProxy(NpcEntry e)
        {
            string type = e.Type;
            GameObject template;
            if (!_templates.TryGetValue(type, out template))
            {
                template = CaptureTemplate(e);
                if (template == null)
                {
                    if (!_missingTemplate.ContainsKey(type))
                    {
                        _missingTemplate[type] = true;
                        CoopLog.Warn("npc type not seen locally yet: " + type + " (will skip)");
                    }
                    return null;
                }
                _templates[type] = template;
                CoopLog.Info("npc template cached: " + type);
            }

            GameObject clone = UnityEngine.Object.Instantiate(template, e.Position, Quaternion.Euler(0f, e.Yaw, 0f));
            clone.name = "NpcProxy_" + type;
            RemoteAvatar avatar = clone.AddComponent<RemoteAvatar>();
            avatar.Animator = clone.GetComponentInChildren<Animator>(true);
            avatar.Renderers = clone.GetComponentsInChildren<Renderer>(true);
            avatar.DisplayName = type;
            avatar.IsNpc = true;
            avatar.HeadAnchor = null;
            clone.SetActive(true);
            return avatar;
        }

        /// <summary>First sighting of a type: clone it once, strip every behaviour and
        /// keep it inactive as a reusable template.</summary>
        private GameObject CaptureTemplate(NpcEntry e)
        {
            try
            {
                BaseStatusNPC[] all = UnityEngine.Object.FindObjectsOfType<BaseStatusNPC>();
                BaseStatusNPC source = null;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && TypeName(all[i].gameObject.name) == e.Type)
                    {
                        source = all[i];
                        break;
                    }
                }
                if (source == null) return null;

                GameObject go = source.gameObject;
                bool wasActive = go.activeSelf;
                if (wasActive) go.SetActive(false);
                GameObject clone;
                try { clone = UnityEngine.Object.Instantiate(go); }
                finally { if (wasActive) go.SetActive(true); }

                foreach (MonoBehaviour mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb != null) UnityEngine.Object.DestroyImmediate(mb);
                foreach (Rigidbody rb in clone.GetComponentsInChildren<Rigidbody>(true))
                    UnityEngine.Object.DestroyImmediate(rb);
                foreach (Collider col in clone.GetComponentsInChildren<Collider>(true))
                    UnityEngine.Object.DestroyImmediate(col);
                foreach (CharacterJoint j in clone.GetComponentsInChildren<CharacterJoint>(true))
                    UnityEngine.Object.DestroyImmediate(j);

                clone.SetActive(false);
                return clone;
            }
            catch (Exception ex)
            {
                CoopLog.Error("template capture failed: " + ex.Message);
                return null;
            }
        }

        // --------------------------------------------------------------- upkeep

        public void Tick()
        {
            float now = Time.realtimeSinceStartup;
            List<string> gone = null;
            foreach (var kv in _proxies)
            {
                if (now - kv.Value.LastSeen > ProxyTimeout)
                {
                    if (gone == null) gone = new List<string>();
                    gone.Add(kv.Key);
                }
            }
            if (gone == null) return;
            for (int i = 0; i < gone.Count; i++)
            {
                Proxy p = _proxies[gone[i]];
                _proxies.Remove(gone[i]);
                if (p.Avatar != null) UnityEngine.Object.Destroy(p.Avatar.gameObject);
            }
        }

        public void Clear()
        {
            foreach (var kv in _proxies)
                if (kv.Value.Avatar != null) UnityEngine.Object.Destroy(kv.Value.Avatar.gameObject);
            _proxies.Clear();
            foreach (var kv in _templates)
                if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            _templates.Clear();
            _missingTemplate.Clear();
        }

        // ------------------------------------------------------------- combat

        public struct ProxyHit
        {
            public string Owner;
            public int Id;
            public Vector3 Position;
        }

        /// <summary>Proxies inside an attack cone - these are the ones we may relay
        /// damage for (the real NPC lives on another device).</summary>
        public void CollectHits(Vector3 origin, Vector3 dir, float range, float coneDeg,
            List<ProxyHit> outList)
        {
            if (outList == null) return;
            Vector3 d = dir.normalized;
            foreach (var kv in _proxies)
            {
                Proxy p = kv.Value;
                if (p.Avatar == null) continue;
                Vector3 to = p.Avatar.transform.position + Vector3.up * 1.0f - origin;
                float dist = to.magnitude;
                if (dist > range) continue;
                if (dist > 1.2f && Vector3.Angle(d, to) > coneDeg) continue;
                outList.Add(new ProxyHit
                {
                    Owner = p.Owner,
                    Id = p.Id,
                    Position = p.Avatar.transform.position,
                });
            }
        }

        /// <summary>Owner side: apply relayed damage to the real NPC (identified by the
        /// instance id we published).</summary>
        public bool ApplyDamage(int npcId, float amount, Vector3 hitPos, Vector3 hitDir)
        {
            try
            {
                BaseStatusNPC[] all = UnityEngine.Object.FindObjectsOfType<BaseStatusNPC>();
                for (int i = 0; i < all.Length; i++)
                {
                    BaseStatusNPC npc = all[i];
                    if (npc == null || npc.GetInstanceID() != npcId) continue;
                    if (npc.IsDead) return false;
                    float before = npc.Health.Current;
                    npc.OnHit(DamageType.Instant, null, amount, hitPos, hitDir);
                    CoopLog.Info("npc damage relayed: " + npc.name + " " + before.ToString("F0") +
                                 " -> " + npc.Health.Current.ToString("F0"));
                    return true;
                }
            }
            catch (Exception e)
            {
                CoopLog.Error("npc damage: " + e.Message);
            }
            return false;
        }

        public static byte[] BuildDamage(int npcId, float amount, string attackerId,
            Vector3 origin, Vector3 dir)
        {
            using (var ms = new System.IO.MemoryStream(56))
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write((byte)'S'); w.Write((byte)'C'); w.Write((byte)'P');
                w.Write(Wire.Version);
                w.Write(MsgType.NpcDamage);
                w.Write(attackerId ?? "");
                w.Write(npcId);
                w.Write(amount);
                w.Write(origin.x); w.Write(origin.y); w.Write(origin.z);
                w.Write(dir.x); w.Write(dir.y); w.Write(dir.z);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static void ReadDamage(byte[] data, out string attackerId, out int npcId,
            out float amount, out Vector3 origin, out Vector3 dir)
        {
            using (var ms = new System.IO.MemoryStream(data))
            using (var r = new System.IO.BinaryReader(ms))
            {
                r.ReadBytes(5);
                attackerId = r.ReadString();
                npcId = r.ReadInt32();
                amount = r.ReadSingle();
                origin = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                dir = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            }
        }

        // ------------------------------------------------------------ wire form

        public static byte[] Build(string senderId, List<NpcEntry> list)
        {
            using (var ms = new System.IO.MemoryStream(64 + list.Count * 44))
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write((byte)'S'); w.Write((byte)'C'); w.Write((byte)'P');
                w.Write(Wire.Version);
                w.Write(MsgType.NpcState);
                w.Write(senderId ?? "");
                w.Write((byte)list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    NpcEntry e = list[i];
                    w.Write(e.Id);
                    w.Write(e.Type ?? "");
                    w.Write(e.Position.x); w.Write(e.Position.y); w.Write(e.Position.z);
                    w.Write(e.Yaw);
                    w.Write(e.Health);
                    w.Write(e.MaxHealth);
                    w.Write(e.Flags);
                    w.Write(e.Forward);
                    w.Write(e.Turn);
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        public static List<NpcEntry> Read(byte[] data)
        {
            using (var ms = new System.IO.MemoryStream(data))
            using (var r = new System.IO.BinaryReader(ms))
            {
                r.ReadBytes(5);
                r.ReadString();                      // sender
                int count = r.ReadByte();
                var list = new List<NpcEntry>(count);
                for (int i = 0; i < count; i++)
                {
                    var e = new NpcEntry();
                    e.Id = r.ReadInt32();
                    e.Type = r.ReadString();
                    e.Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    e.Yaw = r.ReadSingle();
                    e.Health = r.ReadSingle();
                    e.MaxHealth = r.ReadSingle();
                    e.Flags = r.ReadUInt16();
                    e.Forward = r.ReadSingle();
                    e.Turn = r.ReadSingle();
                    list.Add(e);
                }
                return list;
            }
        }
    }
}
