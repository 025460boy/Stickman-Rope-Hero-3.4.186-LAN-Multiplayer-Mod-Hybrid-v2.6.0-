using System;
using System.Collections.Generic;
using System.Net;
using Game.Character.CharacterController;
using Game.Enemy;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SRHCoop
{
    /// <summary>
    /// LAN co-op with a host-authoritative combat model:
    ///   * every peer broadcasts its own transform / animation / health (15 Hz)
    ///   * attack *intents* are sent to the host, who is the only one deciding
    ///     whether they connect (range, cone, cooldown, damage clamp)
    ///   * the host broadcasts a single DAMAGE verdict, the victim applies it
    /// This removes the "I hit you but your client disagreed" divergence the old
    /// peer-to-peer build had. Host role is elected by lowest peer id, so it
    /// migrates automatically when the host leaves.
    /// </summary>
    public class CoopBehaviour : MonoBehaviour
    {
        public const string Version = "2.0.0";

        private CoopConfig _cfg;
        private Player _player;
        private AnimationController _anim;
        private HitEntity _hit;
        private int _playerInstanceId;

        private readonly Dictionary<string, RemoteAvatar> _avatars = new Dictionary<string, RemoteAvatar>();
        private readonly Dictionary<string, PeerState> _peers = new Dictionary<string, PeerState>();
        private readonly Dictionary<string, float> _lastAttackBy = new Dictionary<string, float>();
        private readonly Dictionary<string, int> _appliedDamage = new Dictionary<string, int>();
        private readonly Dictionary<string, float> _pingSent = new Dictionary<string, float>();

        private LanTransport _net;
        private NpcSync _npc;
        private readonly List<NpcSync.ProxyHit> _hitBuffer = new List<NpcSync.ProxyHit>();
        private readonly Dictionary<string, float> _lastNpcHit = new Dictionary<string, float>();
        private string _myId;
        private string _myName;
        private bool _lanOn;
        private float _sendTimer;
        private float _pingTimer;
        private int _stateSeq;
        private int _atkSeq;
        private int _pingSeq;

        private bool _showPanel = true;
        private bool _lastDoMelee;
        private bool _lastShoot;
        private int _lastMelee;
        private float _attackCooldown;

        private Texture2D _white;
        private GUIStyle _label, _small, _title, _button, _center, _field;
        private float _bootStart;
        private bool _frameStarted;
        private bool _privacyHandled;
        private bool _bootLogged;
        private int _rejectedVerdicts;
        private System.Net.IPEndPoint _directTarget;
        private string _manualIp = "";

        private Vector2 _floatPos;
        private Vector2 _panelPos;
        private Vector2 _dragOffset;
        private float _dragDist;
        private bool _dragFloat;
        private bool _dragPanel;
        private bool _posInit;
        private int _lastScreenH;
        private Font _font;
        private bool _fontTried, _cjk;

        // local-only test bot (never a host candidate)
        private PeerState _bot;
        private float _botTime;

        private float _rttMs = -1f;

        private void Awake()
        {
            _cfg = CoopConfig.Load();
            _manualIp = _cfg.ConnectIp ?? "";
            _myId = System.Guid.NewGuid().ToString("N").Substring(0, 8);
            _myName = "P-" + _myId.Substring(0, 4);

            _white = new Texture2D(1, 1);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();

            CoopLog.Info("SRHCoop " + Version + " ready, id=" + _myId + " platform=" + Application.platform);
            CoopLog.Info("log: " + CoopLog.Path);
        }

        private void OnDestroy()
        {
            StopLan();
        }

        /// <summary>Some emulators never deliver the touch to the privacy panel's
        /// ACCEPT button (reproducible on the unmodified APK too). Store the same
        /// keys the button would and continue to the menu.</summary>
        private System.Collections.IEnumerator SkipPrivacyGate()
        {
            yield return new WaitForSeconds(Mathf.Max(0.6f, _cfg.BootSplashSeconds));
            string scene = SceneManager.GetActiveScene().name ?? "";
            if (!scene.Contains("Privacy")) yield break;
            try
            {
                PlayerPrefs.SetInt("PrivacyPolicyAcceptedKey", 1);
                PlayerPrefs.SetInt("PPConfirmed", 1);
                PlayerPrefs.Save();
                CoopLog.Info("privacy gate skipped -> Menu");
                SceneManager.LoadScene("Menu");
            }
            catch (Exception e)
            {
                CoopLog.Error("skip privacy failed: " + e.Message);
            }
        }

        // ------------------------------------------------------------- main loop

        private void Update()
        {
            HandlePrivacyGate();
            TrackLocalPlayer();

            if (!_lanOn && _cfg.AutoStart && _player != null) StartLan();

            if (_lanOn && _net != null)
            {
                HandleIncoming();

                _sendTimer -= Time.deltaTime;
                if (_sendTimer <= 0f)
                {
                    _sendTimer = 1f / Mathf.Max(5, _cfg.SendRateHz);
                    SendLocalState();
                }

                _pingTimer -= Time.deltaTime;
                if (_pingTimer <= 0f)
                {
                    _pingTimer = 1f;
                    SendPing();
                }

                if (_npc != null)
                {
                    if (_player != null)
                    {
                        List<NpcEntry> npcs = _npc.Collect(_player, 0.1f);
                        if (npcs != null && npcs.Count > 0)
                            _net.Broadcast(NpcSync.Build(_myId, npcs));
                    }
                    _npc.Tick();
                }

                ExpirePeers();
            }

            DetectLocalAttack();
        }

        private void TrackLocalPlayer()
        {
            Player found = null;
            try
            {
                var pm = PlayerManager.Instance;
                if (pm != null) found = pm.Player;
            }
            catch { }
            if (found == null)
            {
                try { found = UnityEngine.Object.FindObjectOfType<Player>(); }
                catch { }
            }

            if (found == null)
            {
                if (_player != null && _player.gameObject == null) ClearLocal();
                return;
            }

            int id = found.gameObject.GetInstanceID();
            if (_player == null || id != _playerInstanceId)
            {
                ClearAvatars();
                _player = found;
                _playerInstanceId = id;
                _anim = found.GetComponent<AnimationController>();
                _hit = found;
                CoopLog.Info("local player bound: " + found.name + " hp=" + found.Health.Current +
                             " scene=" + SceneManager.GetActiveScene().name);
            }
        }

        private void ClearLocal()
        {
            _player = null;
            _anim = null;
            _hit = null;
            _playerInstanceId = 0;
        }

        private void ClearAvatars()
        {
            foreach (var a in _avatars.Values) if (a != null) Destroy(a.gameObject);
            _avatars.Clear();
        }

        // --------------------------------------------------------------- network

        public void StartLan()
        {
            if (_lanOn) return;
            _net = new LanTransport();
            _net.Start(_cfg.Port);
            _lanOn = _net.Running;
            if (_lanOn) CoopLog.Info("LAN started on port " + _cfg.Port);
            if (_lanOn && _cfg.SyncNpcs) _npc = new NpcSync();
            if (_lanOn && !string.IsNullOrEmpty(_manualIp)) ConnectDirect();
        }

        public void StopLan()
        {
            if (_net != null)
            {
                try { _net.Broadcast(Wire.BuildSimple(MsgType.Bye, _myId, 0, 0)); } catch { }
                _net.Stop();
                _net = null;
            }
            _lanOn = false;
            _peers.Clear();
            if (_npc != null) { _npc.Clear(); _npc = null; }
            ClearAvatars();
        }

        /// <summary>Owner side of the NPC damage relay.</summary>
        private void OnNpcDamage(byte[] data)
        {
            string attacker; int npcId; float amount; Vector3 origin, dir;
            NpcSync.ReadDamage(data, out attacker, out npcId, out amount, out origin, out dir);
            if (attacker == _myId) return;

            float last;
            if (_lastNpcHit.TryGetValue(attacker, out last) &&
                Time.realtimeSinceStartup - last < 0.12f) return;
            _lastNpcHit[attacker] = Time.realtimeSinceStartup;

            if (_npc != null) _npc.ApplyDamage(npcId, Mathf.Clamp(amount, 0f, 60f), origin, dir);
        }

        /// <summary>Host = peer with the lexicographically smallest id (self included).
        /// Two peers that cannot see each other may both claim host; their verdicts are
        /// tagged with the sender id so receivers only accept the rightful one.</summary>
        public bool IsHost
        {
            get
            {
                foreach (var kv in _peers)
                    if (string.CompareOrdinal(kv.Key, _myId) < 0) return false;
                return true;
            }
        }

        private PeerState HostPeer
        {
            get
            {
                PeerState best = null;
                foreach (var kv in _peers)
                    if (string.CompareOrdinal(kv.Key, _myId) < 0)
                        if (best == null || string.CompareOrdinal(kv.Key, best.Id) < 0) best = kv.Value;
                return best;
            }
        }

        private void SendLocalState()
        {
            if (_player == null || _net == null) return;
            try
            {
                byte[] payload = BuildLocalState();
                _net.Broadcast(payload);
                // Direct link: keeps working when the router drops broadcast traffic.
                if (_directTarget != null) _net.SendTo(payload, _directTarget);
            }
            catch (Exception e) { CoopLog.Error("SendLocalState: " + e.Message); }
        }

        private byte[] BuildLocalState()
        {
            PeerFlags flags = PeerFlags.None;
            if (_hit != null && _hit.IsDead) flags |= PeerFlags.Dead;
            if (_player.IsSprinting) flags |= PeerFlags.Sprinting;
            if (_player.IsFlying) flags |= PeerFlags.Flying;
            if (IsHost) flags |= PeerFlags.Host;

            return Wire.BuildState(_myId, _myName, ++_stateSeq,
                _player.transform.position,
                _player.transform.eulerAngles.y,
                0f,
                _hit != null ? _hit.Health.Current : 100f,
                _hit != null ? _hit.Health.Max : 100f,
                flags, ReadLocalAnim());
        }

        /// <summary>Snapshot of the animator parameters that drive locomotion.</summary>
        private AnimSnapshot ReadLocalAnim()
        {
            var a = new AnimSnapshot();
            Animator an = _anim != null ? _anim.MainAnimator : null;
            if (an == null) return a;

            a.Forward = an.GetFloat("Forward");
            a.Turn = an.GetFloat("Turn");
            a.StrafeDir = an.GetFloat("StrafeDir");
            a.Jump = an.GetFloat("Jump");
            a.MeleeSpeed = an.GetFloat("MeleeWeaponAttackSpeed");

            ushort f = 0;
            if (an.GetBool("Sprint")) f |= AnimBits.Sprint;
            if (an.GetBool("Strafe")) f |= AnimBits.Strafe;
            if (an.GetBool("Crouch")) f |= AnimBits.Crouch;
            if (an.GetBool("OnGround")) f |= AnimBits.OnGround;
            if (an.GetBool("IsMoving")) f |= AnimBits.IsMoving;
            if (an.GetBool("IsFlying")) f |= AnimBits.IsFlying;
            if (an.GetBool("Gliding")) f |= AnimBits.Gliding;
            if (an.GetBool("NearWall")) f |= AnimBits.NearWall;
            if (an.GetBool("IsOnWater")) f |= AnimBits.IsOnWater;
            if (an.GetBool("DoMelee")) f |= AnimBits.DoMelee;
            if (an.GetBool("RangedWeaponShoot")) f |= AnimBits.RangedWeaponShoot;
            if (an.GetBool("RangedWeaponRecharge")) f |= AnimBits.RangedWeaponRecharge;
            a.Flags2 = f;

            a.Melee = (byte)Mathf.Clamp(an.GetInteger("Melee"), 0, 255);
            a.MeleeWeaponType = (byte)Mathf.Clamp(an.GetInteger("MeleeWeaponType"), 0, 255);
            a.RangedWeaponType = (byte)Mathf.Clamp(an.GetInteger("RangedWeaponType"), 0, 255);

            try
            {
                var wc = _player.WeaponController;
                if (wc != null && wc.CurrentWeapon != null)
                    a.Weapon = wc.CurrentWeapon.gameObject.name;
            }
            catch { }

            return a;
        }

        /// <summary>Fallback discovery: type the peer's IP and talk to it directly.</summary>
        private void ConnectDirect()
        {
            string ip = (_manualIp ?? "").Trim();
            System.Net.IPAddress addr;
            if (!System.Net.IPAddress.TryParse(ip, out addr))
            {
                CoopLog.Warn("direct connect: invalid ip '" + ip + "'");
                return;
            }
            _directTarget = new System.Net.IPEndPoint(addr, _cfg.Port);
            _cfg.ConnectIp = ip;
            _cfg.Save();
            CoopLog.Info("direct connect -> " + _directTarget);
            SendLocalStateTo(_directTarget);
        }

        /// <summary>Sweep the local /24: peers answer by unicast, so this works even
        /// when the router drops broadcast entirely.</summary>
        private void ScanLan()
        {
            if (_net == null || _player == null) return;
            int sent = _net.ScanSubnet(BuildLocalState());
            CoopLog.Info("scan: probed " + sent + " hosts on " + LanTransport.LocalAddress());
        }

        private void SendPing()
        {
            if (_net == null) return;
            _net.Broadcast(Wire.BuildSimple(MsgType.Ping, _myId, ++_pingSeq,
                (long)(Time.realtimeSinceStartup * 1000f)));
        }

        private void HandleIncoming()
        {
            List<LanTransport.Datagram> packets = _net.Drain();
            for (int i = 0; i < packets.Count; i++)
            {
                byte[] data = packets[i].Data;
                IPEndPoint from = packets[i].From;
                byte type; string id;
                if (!Wire.ReadHeader(data, out type, out id)) continue;
                if (id == _myId) continue;

                try
                {
                    switch (type)
                    {
                        case MsgType.State: OnState(Wire.ReadState(data, from)); break;
                        case MsgType.Attack: OnAttackIntent(Wire.ReadAttack(data)); break;
                        case MsgType.Damage: OnDamageVerdict(Wire.ReadDamage(data)); break;
                        case MsgType.Bye: RemovePeer(id); break;
                        case MsgType.NpcState:
                            if (_npc != null) _npc.Apply(id, NpcSync.Read(data));
                            break;
                        case MsgType.NpcDamage: OnNpcDamage(data); break;
                        case MsgType.Ping: OnPing(data, from); break;
                        case MsgType.Pong: OnPong(data); break;
                    }
                }
                catch (Exception e) { CoopLog.Error("packet " + type + ": " + e.Message); }
            }
        }

        private void OnPing(byte[] data, IPEndPoint from)
        {
            int seq; long ms;
            Wire.ReadSimple(data, out seq, out ms);
            if (_net != null)
                _net.SendTo(Wire.BuildSimple(MsgType.Pong, _myId, seq, ms), from);
        }

        private void OnPong(byte[] data)
        {
            int seq; long ms;
            Wire.ReadSimple(data, out seq, out ms);
            float now = Time.realtimeSinceStartup * 1000f;
            _rttMs = Mathf.Max(0f, now - ms);
        }

        private void OnState(PeerState state)
        {
            bool isNew = !_peers.ContainsKey(state.Id);
            state.LastSeen = Time.realtimeSinceStartup;
            _peers[state.Id] = state;

            RemoteAvatar avatar;
            if (!_avatars.TryGetValue(state.Id, out avatar) || avatar == null)
            {
                if (_player == null) return;
                avatar = RemoteAvatar.Spawn(_player, state.Id, state.Name, state.Position, state.Yaw);
                if (avatar == null) return;
                _avatars[state.Id] = avatar;
            }
            avatar.ApplyState(state);

            if (isNew)
            {
                CoopLog.Info("peer joined: " + state.Name + " (" + state.Id + ") at " + state.EndPoint);
                // answer directly so discovery also works when broadcast is one-way
                if (_player != null) SendLocalStateTo(state.EndPoint);
            }
        }

        private void SendLocalStateTo(IPEndPoint target)
        {
            if (_player == null || _net == null || target == null) return;
            try
            {
                PeerFlags flags = PeerFlags.None;
                if (_hit != null && _hit.IsDead) flags |= PeerFlags.Dead;
                if (IsHost) flags |= PeerFlags.Host;
                _net.SendTo(Wire.BuildState(_myId, _myName, ++_stateSeq,
                    _player.transform.position, _player.transform.eulerAngles.y, 0f,
                    _hit != null ? _hit.Health.Current : 100f,
                    _hit != null ? _hit.Health.Max : 100f,
                    flags, ReadLocalAnim()), target);
            }
            catch { }
        }

        private void ExpirePeers()
        {
            List<string> gone = null;
            foreach (var kv in _peers)
            {
                if (Time.realtimeSinceStartup - kv.Value.LastSeen > LanTransport.PeerTimeoutSeconds)
                {
                    if (gone == null) gone = new List<string>();
                    gone.Add(kv.Key);
                }
            }
            if (gone != null) foreach (string id in gone) RemovePeer(id);
        }

        private void RemovePeer(string id)
        {
            if (_peers.Remove(id)) CoopLog.Info("peer left: " + id);
            RemoteAvatar avatar;
            if (_avatars.TryGetValue(id, out avatar))
            {
                _avatars.Remove(id);
                if (avatar != null) Destroy(avatar.gameObject);
            }
        }

        // ---------------------------------------------------------------- combat

        private void DetectLocalAttack()
        {
            if (_player == null || _anim == null || _anim.MainAnimator == null) return;
            if (!_lanOn || Time.time < _attackCooldown) return;

            bool doMelee = false, shoot = false;
            int melee = 0;
            try
            {
                doMelee = _anim.MainAnimator.GetBool("DoMelee");
                shoot = _anim.MainAnimator.GetBool("RangedWeaponShoot");
                melee = _anim.MainAnimator.GetInteger("Melee");
            }
            catch { return; }

            bool meleeStarted = (doMelee && !_lastDoMelee) || (melee != 0 && melee != _lastMelee);
            bool shootStarted = shoot && !_lastShoot;
            _lastDoMelee = doMelee;
            _lastShoot = shoot;
            _lastMelee = melee;
            if (!meleeStarted && !shootStarted) return;

            _attackCooldown = Time.time + 0.3f;
            Vector3 dir = _player.transform.forward;
            Vector3 origin = _player.transform.position + Vector3.up * 1.2f;

            if (shootStarted) SubmitAttack(1, origin, dir, _cfg.RangedRange, _cfg.RangedDamage);
            if (meleeStarted) SubmitAttack(0, origin, dir, _cfg.MeleeRange, _cfg.MeleeDamage);
        }

        private void SubmitAttack(byte kind, Vector3 origin, Vector3 dir, float range, float damage)
        {
            var attack = new AttackMsg
            {
                AttackerId = _myId,
                AtkSeq = ++_atkSeq,
                Kind = kind,
                Origin = origin,
                Direction = dir,
                Range = range,
                Damage = damage,
            };

            if (IsHost)
            {
                ResolveAttack(attack);
            }
            else
            {
                PeerState host = HostPeer;
                if (host != null && _net != null)
                    _net.SendTo(Wire.BuildAttack(attack), host.EndPoint);
                else
                    _net.Broadcast(Wire.BuildAttack(attack));   // host not known yet
            }

            RelayNpcHits(kind, origin, dir, range, damage);
        }

        /// <summary>Proxies are inert puppets: send the hit to whoever owns the real NPC.</summary>
        private void RelayNpcHits(byte kind, Vector3 origin, Vector3 dir, float range, float damage)
        {
            if (_npc == null || _net == null) return;
            try
            {
                _hitBuffer.Clear();
                _npc.CollectHits(origin, dir, range, kind == 0 ? 70f : 25f, _hitBuffer);
                for (int i = 0; i < _hitBuffer.Count; i++)
                {
                    NpcSync.ProxyHit hit = _hitBuffer[i];
                    byte[] payload = NpcSync.BuildDamage(hit.Id, damage, _myId, origin, dir);
                    PeerState owner;
                    if (_peers.TryGetValue(hit.Owner, out owner) && owner.EndPoint != null)
                        _net.SendTo(payload, owner.EndPoint);
                    else
                        _net.Broadcast(payload);
                    CoopLog.Info("npc hit -> relayed to " + hit.Owner + " (id " + hit.Id + ")");
                }
            }
            catch (Exception e) { CoopLog.Error("RelayNpcHits: " + e.Message); }
        }

        private void OnAttackIntent(AttackMsg attack)
        {
            if (!IsHost) return;                  // only the host decides
            if (attack.AttackerId == _myId) return;
            ResolveAttack(attack);
        }

        /// <summary>Host side: validate an attack intent and broadcast the verdict.</summary>
        private void ResolveAttack(AttackMsg attack)
        {
            float now = Time.realtimeSinceStartup;
            float last;
            if (_lastAttackBy.TryGetValue(attack.AttackerId, out last) && now - last < 0.18f) return;
            _lastAttackBy[attack.AttackerId] = now;

            float damage = Mathf.Clamp(attack.Damage, 0f, 60f);
            float range = Mathf.Clamp(attack.Range, 0f, 80f);
            Vector3 dir = attack.Direction.normalized;

            foreach (var kv in _peers)
            {
                PeerState p = kv.Value;
                if (p.Id == attack.AttackerId) continue;
                TryResolveHit(attack, p.Id, p.Position, p.Dead, damage, range, dir);
            }

            // The host is a victim too - without this a client could never damage it.
            if (_player != null && _hit != null && !_hit.IsDead && attack.AttackerId != _myId)
                TryResolveHit(attack, _myId, _player.transform.position, false, damage, range, dir);
        }

        private void TryResolveHit(AttackMsg attack, string victimId, Vector3 victimPos, bool victimDead,
            float damage, float range, Vector3 dir)
        {
            if (victimDead) return;

            Vector3 target = victimPos + Vector3.up * 1.0f;
            Vector3 toTarget = target - attack.Origin;
            float dist = toTarget.magnitude;
            if (dist > range) return;

            float angle = Vector3.Angle(dir, toTarget);
            float limit = attack.Kind == 0 ? 70f : 25f;
            if (dist > 1.2f && angle > limit) return;

            var verdict = new DamageMsg
            {
                AttackerId = attack.AttackerId,
                SenderId = _myId,
                VictimId = victimId,
                AtkSeq = attack.AtkSeq,
                Amount = damage,
                Kind = attack.Kind,
                Origin = attack.Origin,
                Direction = dir,
            };
            _net.Broadcast(Wire.BuildDamage(verdict));
            ApplyDamageLocally(verdict);
        }

        private void OnDamageVerdict(DamageMsg dmg)
        {
            // Clients trust the host; the host already applied its own verdict.
            if (IsHost) return;

            // Only the elected host may judge damage. This stops a desynced second
            // host (both sides unable to see each other) from double-ruling.
            string host = AuthoritativeHostId();
            if (dmg.SenderId != host)
            {
                if (_rejectedVerdicts++ < 3)
                    CoopLog.Warn("ignored verdict from " + dmg.SenderId + " (host is " + host + ")");
                return;
            }
            ApplyDamageLocally(dmg);
        }

        /// <summary>Lowest peer id owns combat arbitration.</summary>
        private string AuthoritativeHostId()
        {
            string best = _myId;
            foreach (var kv in _peers)
                if (string.CompareOrdinal(kv.Key, best) < 0) best = kv.Key;
            return best;
        }

        private void ApplyDamageLocally(DamageMsg dmg)
        {
            // optimistic update of the remote health bar; corrected by the next state packet
            RemoteAvatar avatar;
            if (_avatars.TryGetValue(dmg.VictimId, out avatar) && avatar != null)
                avatar.Health = Mathf.Max(0f, avatar.Health - dmg.Amount);

            if (dmg.VictimId != _myId) return;
            if (_hit == null || _hit.IsDead) return;

            string key = dmg.AttackerId + ":" + dmg.AtkSeq;
            int seen;
            if (_appliedDamage.TryGetValue(dmg.AttackerId, out seen) && seen >= dmg.AtkSeq) return;
            _appliedDamage[dmg.AttackerId] = dmg.AtkSeq;

            try
            {
                _hit.OnHit(DamageType.Instant, null, dmg.Amount, dmg.Origin, dmg.Direction);
                CoopLog.Info("hit by " + dmg.AttackerId + " for " + dmg.Amount +
                             " (hp " + _hit.Health.Current + ")");
            }
            catch (Exception e) { CoopLog.Error("apply damage: " + e.Message); }
        }

        // ------------------------------------------------------------------- gui

        private static readonly string[] CjkFonts =
        {
            "Noto Sans CJK SC", "Noto Sans SC", "Noto Sans CJK", "Source Han Sans CN",
            "Noto Sans CJK JP", "Droid Sans Fallback", "sans-serif"
        };

        private void EnsureFont(int size)
        {
            if (_font != null || _fontTried) return;
            _fontTried = true;
            for (int i = 0; i < CjkFonts.Length && _font == null; i++)
            {
                try
                {
                    Font f = Font.CreateDynamicFontFromOSFont(CjkFonts[i], size);
                    if (f == null) continue;
                    f.RequestCharactersInTexture("联机玩家", size, FontStyle.Normal);
                    CharacterInfo ci;
                    if (f.GetCharacterInfo('联', out ci, size, FontStyle.Normal) && ci.glyphWidth > 0)
                    {
                        _font = f;
                        _cjk = true;
                        CoopLog.Info("CJK font: " + CjkFonts[i]);
                    }
                }
                catch { }
            }
            if (_font == null) CoopLog.Warn("no CJK font, English UI");
        }

        private string L(string cn, string en) { return _cjk ? cn : en; }

        private void EnsureStyles()
        {
            if (_label != null && _lastScreenH == Screen.height) return;
            _lastScreenH = Screen.height;
            int baseSize = Mathf.Max(20, Mathf.RoundToInt(Screen.height / 34f * _cfg.UiScale));
            EnsureFont(baseSize);
            UiKit.Init();

            _label = Label(baseSize, FontStyle.Bold, UiKit.Text);
            _small = Label(Mathf.Max(17, baseSize - 5), FontStyle.Normal, UiKit.Muted);
            _title = Label(baseSize + 3, FontStyle.Bold, new Color(1f, 0.93f, 0.72f));
            _center = Label(baseSize, FontStyle.Bold, UiKit.Text);
            _center.alignment = TextAnchor.MiddleCenter;
            _button = _center;

            _field = new GUIStyle(GUI.skin.textField)
            {
                fontSize = Mathf.Max(16, baseSize - 6),
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(6, 6, 4, 4),
            };
            _field.normal.textColor = UiKit.Text;
            _field.normal.background = null;
            _field.hover.background = null;
            _field.active.background = null;
            _field.focused.background = null;
            if (_font != null) _field.font = _font;
        }

        private GUIStyle Label(int size, FontStyle style, Color color)
        {
            var s = new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                fontStyle = style,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false,
            };
            s.normal.textColor = color;
            if (_font != null) s.font = _font;
            return s;
        }

        /// <summary>The launch scene only shows the Naxeex logo and its ACCEPT button
        /// cannot be pressed on some emulators, so we set the same prefs it would and
        /// continue to the menu after the boot screen has been shown.</summary>
        private void HandlePrivacyGate()
        {
            if (_privacyHandled || !_cfg.SkipPrivacyScreen) return;
            // Wait for the first rendered frame: Unity shows the native splash while
            // the first scene loads, and skipping before that means the launch screen
            // (logo + mascot) never gets drawn at all.
            if (!_frameStarted) return;
            string scene;
            try { scene = SceneManager.GetActiveScene().name ?? ""; }
            catch { return; }
            if (!scene.Contains("Privacy")) return;

            _privacyHandled = true;
            CoopLog.Info("privacy scene detected: " + scene);
            StartCoroutine(SkipPrivacyGate());
        }

        private void OnGUI()
        {
            if (_white == null) return;
            if (!_frameStarted)
            {
                // Start the launch-screen timer from the first rendered frame: on slow
                // devices the scene can take >10 s to appear after Awake.
                _frameStarted = true;
                _bootStart = Time.realtimeSinceStartup;
            }
            EnsureStyles();
            float s = Mathf.Max(0.5f, Screen.height / 1080f);
            if (_cfg.ShowHealthBars) DrawHealthBars(s);
            DrawPanel();
            DrawBootSplash();
        }

        /// <summary>
        /// The launch screen is the level0 privacy scene: only the Naxeex logo is
        /// visible (the panel is faded out), so the mascot is drawn small, centred
        /// below the logo, on the scene's black background.
        /// </summary>
        private void DrawBootSplash()
        {
            if (UiKit.Chibi == null) return;
            float t = Time.realtimeSinceStartup - _bootStart;
            float hold = Mathf.Max(0.8f, _cfg.BootSplashSeconds);
            if (t > hold + 0.8f) return;
            if (SceneManager.GetActiveScene().name == "Game") return;

            float fade = t < 0.4f ? t / 0.4f : (t > hold ? Mathf.Clamp01((hold + 0.8f - t) / 0.8f) : 1f);
            if (fade <= 0.01f) return;

            if (!_bootLogged)
            {
                _bootLogged = true;
                CoopLog.Info("[BOOT] t=" + t.ToString("F1") + " hold=" + hold.ToString("F1") +
                             " chibi=" + (UiKit.Chibi != null) + " scene=" + SceneManager.GetActiveScene().name);
            }

            string scene = "";
            try { scene = SceneManager.GetActiveScene().name ?? ""; } catch { }
            // Only the launch scene has the Naxeex logo, so the mascot belongs there.
            // (Returning players skip that scene, and drawing over the menu looked
            // like a stray sticker on top of the HUD.)
            if (!scene.Contains("Privacy")) return;

            float h = Screen.height * 0.135f;
            float w = h * ((float)UiKit.Chibi.width / UiKit.Chibi.height);
            float cx = Screen.width * 0.5f;
            float top = Screen.height * 0.775f;

            // soft dark plate so the mascot keeps its black backdrop everywhere
            float pp = h * 0.10f;
            UiKit.DrawRounded(new Rect(cx - w * 0.5f - pp, top - pp, w + pp * 2, h + pp * 2),
                new Color(0f, 0f, 0f, 0.55f * fade), 18f);

            GUI.color = new Color(1f, 1f, 1f, fade);
            GUI.DrawTexture(new Rect(cx - w * 0.5f, top, w, h), UiKit.Chibi, ScaleMode.ScaleToFit, true);
            GUI.color = Color.white;

            GUIStyle s = _small;
            if (s != null)
            {
                string cap = L("局域网联机已加载", "LAN co-op loaded");
                Vector2 size = s.CalcSize(new GUIContent(cap));
                GUI.color = new Color(1f, 1f, 1f, 0.55f * fade);
                GUI.Label(new Rect(cx - size.x * 0.5f, top + h + h * 0.16f, size.x, size.y), cap, s);
                GUI.color = Color.white;
            }
        }

        private void DrawHealthBars(float s)
        {
            Camera cam = null;
            try { cam = Camera.main; } catch { }
            if (cam == null) return;

            float u = s * _cfg.UiScale;
            foreach (var kv in _avatars)
            {
                RemoteAvatar avatar = kv.Value;
                if (avatar == null || avatar.IsNpc || !avatar.gameObject.activeInHierarchy) continue;

                Vector3 sp = cam.WorldToScreenPoint(avatar.BarAnchor);
                if (sp.z <= 0.1f) continue;

                float w = 132f * u, h = 12f * u;
                float x = sp.x - w * 0.5f;
                float y = Screen.height - sp.y;
                float frac = avatar.MaxHealth <= 0f ? 0f : Mathf.Clamp01(avatar.Health / avatar.MaxHealth);

                GUI.color = new Color(0f, 0f, 0f, 0.7f);
                GUI.DrawTexture(new Rect(x - 2f * u, y - 2f * u, w + 4f * u, h + 4f * u), _white);
                GUI.color = new Color(0.15f, 0.15f, 0.15f, 0.9f);
                GUI.DrawTexture(new Rect(x, y, w, h), _white);
                GUI.color = frac > 0.5f
                    ? new Color(0.25f, 0.9f, 0.3f, 1f)
                    : (frac > 0.22f ? new Color(0.95f, 0.75f, 0.15f, 1f) : new Color(0.9f, 0.2f, 0.15f, 1f));
                GUI.DrawTexture(new Rect(x, y, w * frac, h), _white);

                GUI.color = Color.white;
                string caption = avatar.DisplayName;
                if (avatar.IsHost) caption += L("（主机）", " [HOST]");
                if (avatar.Dead) caption += L("（阵亡）", " [DEAD]");
                Vector2 size = _small.CalcSize(new GUIContent(caption));
                GUI.Label(new Rect(sp.x - size.x * 0.5f, y - size.y - 3f * u, size.x, size.y),
                          caption, _small);
            }
            GUI.color = Color.white;
        }

        // ------------------------------------------------------------ dragging

        private void InitPositions()
        {
            if (_posInit) return;
            _posInit = true;
            float w = Screen.width, h = Screen.height;
            float s = FloatSize();
            _floatPos = _cfg.FloatX >= 0f
                ? new Vector2(_cfg.FloatX * w, _cfg.FloatY * h)
                : new Vector2(w * 0.02f, h * 0.22f);
            _panelPos = _cfg.PanelX >= 0f
                ? new Vector2(_cfg.PanelX * w, _cfg.PanelY * h)
                : new Vector2(w * 0.02f, h * 0.05f);
            ClampPos(ref _floatPos, s, s);
            ClampPos(ref _panelPos, w * 0.45f, h * 0.45f);
        }

        private static float FloatSize()
        {
            return Mathf.Min(Screen.width, Screen.height) * 0.17f;
        }

        private static void ClampPos(ref Vector2 p, float w, float h)
        {
            float maxX = Mathf.Max(0f, Screen.width - w);
            float maxY = Mathf.Max(0f, Screen.height - h);
            p.x = Mathf.Clamp(p.x, 0f, maxX);
            p.y = Mathf.Clamp(p.y, 0f, maxY);
        }

        private void SavePositions()
        {
            try
            {
                _cfg.FloatX = _floatPos.x / Mathf.Max(1f, Screen.width);
                _cfg.FloatY = _floatPos.y / Mathf.Max(1f, Screen.height);
                _cfg.PanelX = _panelPos.x / Mathf.Max(1f, Screen.width);
                _cfg.PanelY = _panelPos.y / Mathf.Max(1f, Screen.height);
                _cfg.Save();
            }
            catch { }
        }

        /// <summary>Touch/mouse drag with click detection (a short press is a click).</summary>
        private void HandleDrag(Rect r, ref Vector2 pos, ref bool dragging, out bool clicked, float slop)
        {
            clicked = false;
            Event e = Event.current;
            if (e == null) return;

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (r.Contains(e.mousePosition))
                    {
                        dragging = true;
                        _dragOffset = e.mousePosition - pos;
                        _dragDist = 0f;
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (dragging)
                    {
                        pos = e.mousePosition - _dragOffset;
                        _dragDist += e.delta.magnitude;
                        ClampPos(ref pos, r.width, r.height);
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (dragging)
                    {
                        dragging = false;
                        if (_dragDist <= slop) clicked = true;
                        e.Use();
                        SavePositions();
                    }
                    break;
            }
        }

        /// <summary>Collapsed state: just the mascot, draggable; tap to open the panel.</summary>
        private void DrawFloatButton()
        {
            float s = FloatSize();
            Rect r = new Rect(_floatPos.x, _floatPos.y, s, s);
            bool clicked;
            HandleDrag(r, ref _floatPos, ref _dragFloat, out clicked, s * 0.10f);
            if (clicked)
            {
                _showPanel = true;
                return;
            }

            // soft plate so the character stays readable on bright scenes
            UiKit.DrawRounded(new Rect(r.x + s * 0.06f, r.y + s * 0.06f, s * 0.88f, s * 0.88f),
                new Color(0f, 0f, 0f, 0.28f), s * 0.44f);

            if (UiKit.Mascot != null)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.98f);
                GUI.DrawTexture(r, UiKit.Mascot, ScaleMode.ScaleToFit, true);
                GUI.color = Color.white;
            }
            else if (UiKit.Button(r, L("联机", "LAN"), _center, true))
            {
                _showPanel = true;
            }

            // tiny status dot
            float d = s * 0.16f;
            Rect dot = new Rect(r.xMax - d * 1.1f, r.yMax - d * 1.1f, d, d);
            UiKit.DrawRounded(dot, _lanOn ? new Color(0.35f, 0.88f, 0.45f, 0.95f)
                                          : new Color(0.55f, 0.55f, 0.6f, 0.85f), d * 0.5f);
        }

        private void DrawPanel()
        {
            // keep the launch screen clean: only logo + mascot
            string sceneNow = "";
            try { sceneNow = SceneManager.GetActiveScene().name ?? ""; } catch { }
            if (sceneNow.Contains("Privacy") && Time.realtimeSinceStartup - _bootStart < Mathf.Max(0.6f, _cfg.BootSplashSeconds))
                return;

            float fs = _label.fontSize;
            float pad = fs * 0.62f;
            float bh = fs * 1.85f;
            float gap = fs * 0.34f;
            float w = fs * 16.2f;
            float headerH = fs * 2.35f;
            float y;

            InitPositions();

            if (!_showPanel)
            {
                DrawFloatButton();
                return;
            }

            // drag the panel by its header (leave the close button clickable)
            Rect handle = new Rect(_panelPos.x + 5f, _panelPos.y + 5f,
                w - 10f - fs * 2.2f, headerH);
            bool clickedPanel;
            HandleDrag(handle, ref _panelPos, ref _dragPanel, out clickedPanel, fs * 2.2f);

            float x0 = _panelPos.x;
            float y0 = _panelPos.y;

            int shown = Mathf.Min(_peers.Count + (_bot != null ? 1 : 0), 4);
            float cardH = fs * 2.45f;
            float chipH = fs * 1.45f;
            float infoH = fs * 1.25f;
            float directH = _lanOn ? bh * 0.85f + gap * 0.5f : 0f;
            // keep this in sync with the layout below, otherwise the last row spills out
            float totalH = 5f + headerH + gap
                           + chipH + gap * 0.6f
                           + bh + gap * 0.8f
                           + infoH + gap * 0.5f
                           + directH
                           + shown * (cardH + gap * 0.6f)
                           + gap * 0.4f
                           + bh + gap + bh
                           + pad;

            Rect panel = new Rect(x0, y0, w, totalH);
            // drop shadow
            UiKit.DrawRounded(new Rect(panel.x + 4f, panel.y + 5f, panel.width, panel.height),
                new Color(0f, 0f, 0f, 0.35f));
            UiKit.DrawRoundedOutline(panel, UiKit.Panel, UiKit.PanelEdge, 1.2f);

            // header strip
            Rect header = new Rect(panel.x + 5f, panel.y + 5f, panel.width - 10f, headerH);
            UiKit.DrawRounded(header, UiKit.Header, 14f);
            UiKit.Fill(new Rect(header.x + 14f, header.yMax - 2.5f, header.width - 28f, 2.5f),
                new Color(UiKit.Accent.r, UiKit.Accent.g, UiKit.Accent.b, 0.8f));

            y = header.y + headerH * 0.5f - fs * 0.50f;
            GUI.Label(new Rect(header.x + fs * 0.7f, y, header.width - fs * 3.4f, fs * 1.3f),
                L("火柴人绳索英雄 · 联机", "SRH Co-op"), _title);

            Rect close = new Rect(header.xMax - fs * 1.55f, header.y + (headerH - fs * 1.55f) * 0.5f,
                fs * 1.55f, fs * 1.55f);
            if (UiKit.Button(close, "×", _center, false, 0.85f))
            {
                _showPanel = false;
                return;
            }

            y = header.yMax + gap;

            // role chip + id
            string role = !_lanOn ? L("未联机", "offline") : (IsHost ? L("主机", "HOST") : L("客户端", "CLIENT"));
            Color chipColor = !_lanOn ? new Color(0.35f, 0.38f, 0.45f, 1f) : (IsHost ? UiKit.Accent : UiKit.Good);
            float chipW = fs * 3.6f;
            Rect chip = new Rect(x0 + pad * 0.7f, y, chipW, chipH);
            UiKit.DrawRounded(chip, new Color(chipColor.r, chipColor.g, chipColor.b, 0.9f), 8f);
            GUI.Label(chip, role, _center);
            GUI.Label(new Rect(chip.xMax + fs * 0.45f, y, w - chipW - fs * 2f, chipH), _myId, _small);
            y += chipH + gap * 0.6f;

            float innerX = x0 + pad * 0.7f;
            float innerW = w - pad * 1.4f;
            float halfW = (innerW - gap) * 0.5f;

            if (UiKit.Button(new Rect(innerX, y, halfW, bh),
                    _lanOn ? L("关闭联机", "Stop LAN") : L("开始联机", "Start LAN"), _center, true))
            {
                if (_lanOn) StopLan(); else StartLan();
            }
            if (UiKit.Button(new Rect(innerX + halfW + gap, y, halfW, bh),
                    _cfg.ShowHealthBars ? L("血条：显示", "Bars: ON") : L("血条：隐藏", "Bars: OFF"), _center, false))
                _cfg.ShowHealthBars = !_cfg.ShowHealthBars;
            y += bh + gap * 0.8f;

            string info = _lanOn
                ? L("在线 ", "peers ") + _peers.Count
                  + (_rttMs >= 0f ? L("　延迟 ", "  rtt ") + Mathf.RoundToInt(_rttMs) + "ms" : "")
                  + (_net != null ? L("　收 ", "  rx ") + _net.PacketsReceived : "")
                : L("与好友连同一个 Wi-Fi 即可自动发现", "Same Wi-Fi required");
            GUI.Label(new Rect(innerX, y, innerW, fs * 1.25f), info, _small);
            y += fs * 1.25f + gap * 0.5f;

            if (_lanOn)
            {
                float fieldW = innerW * 0.52f;
                float smallW = (innerW - fieldW - gap * 2f) * 0.5f;
                Rect field = new Rect(innerX, y, fieldW, bh * 0.85f);
                UiKit.DrawRounded(field, new Color(0.07f, 0.09f, 0.13f, 1f), 10f);
                GUI.SetNextControlName("ipfield");
                string before = _manualIp;
                _manualIp = GUI.TextField(new Rect(field.x + fs * 0.4f, field.y + fs * 0.1f,
                        field.width - fs * 0.8f, field.height - fs * 0.2f),
                    _manualIp ?? "", 16, _field);
                if (string.IsNullOrEmpty(_manualIp) && before == _manualIp)
                    GUI.Label(new Rect(field.x + fs * 0.55f, field.y, field.width, field.height),
                        L("对方 IP", "peer ip"), _small);

                if (UiKit.Button(new Rect(innerX + fieldW + gap, y, smallW, bh * 0.85f),
                        L("直连", "Link"), _center, false))
                    ConnectDirect();
                if (UiKit.Button(new Rect(innerX + fieldW + gap * 2f + smallW, y, smallW, bh * 0.85f),
                        L("扫描", "Scan"), _center, true))
                    ScanLan();
                y += bh * 0.85f + gap * 0.5f;
            }

            // peer cards
            foreach (var kv in _peers)
            {
                DrawPeerCard(new Rect(innerX, y, innerW, cardH), kv.Value.Name, kv.Value.Health,
                    kv.Value.MaxHealth, kv.Value.IsHost, kv.Value.Dead, kv.Value.RttMs);
                y += cardH + gap * 0.6f;
                if (y > Screen.height - bh * 3f) break;
            }
            if (_bot != null && y <= Screen.height - bh * 3f)
            {
                DrawPeerCard(new Rect(innerX, y, innerW, cardH), _bot.Name, _bot.Health, _bot.MaxHealth,
                    false, false, -1);
                y += cardH + gap * 0.6f;
            }
            y += gap * 0.4f;

            if (UiKit.Button(new Rect(innerX, y, halfW, bh), L("测试机器人", "Test bot"), _center, false))
                SpawnTestBot();
            if (UiKit.Button(new Rect(innerX + halfW + gap, y, halfW, bh), L("测试受击", "Test hit"), _center, false))
                SimulateRemoteHit();
            y += bh + gap;

            if (UiKit.Button(new Rect(innerX, y, halfW, bh), L("回环测试", "Loop test"), _center, false, 0.82f))
                StartCoroutine(LoopbackTest());
            if (UiKit.Button(new Rect(innerX + halfW + gap, y, halfW, bh),
                    _cfg.SyncNpcs ? L("NPC：同步", "NPC: ON") : L("NPC：关闭", "NPC: OFF"),
                    _center, false, 0.82f))
            {
                _cfg.SyncNpcs = !_cfg.SyncNpcs;
                _cfg.Save();
                if (_lanOn)
                {
                    if (_npc != null) { _npc.Clear(); _npc = null; }
                    if (_cfg.SyncNpcs) _npc = new NpcSync();
                }
            }
        }

        private void DrawPeerCard(Rect r, string name, float hp, float maxHp, bool host, bool dead, int rtt)
        {
            UiKit.DrawRounded(r, new Color(0.10f, 0.13f, 0.19f, 0.95f), 12f);
            float fs = _label.fontSize;
            float pad = fs * 0.42f;

            GUI.Label(new Rect(r.x + pad, r.y + pad * 0.6f, r.width * 0.62f, fs * 1.15f),
                dead ? name + L("（阵亡）", " [DEAD]") : name, _label);

            if (host)
            {
                float bw = fs * 2.4f;
                Rect badge = new Rect(r.xMax - pad - bw, r.y + pad * 0.55f, bw, fs * 1.05f);
                UiKit.DrawRounded(badge, new Color(UiKit.Accent.r, UiKit.Accent.g, UiKit.Accent.b, 0.9f), 6f);
                GUI.Label(badge, L("主机", "HOST"), _small);
            }

            float barY = r.yMax - pad * 0.7f - fs * 0.6f;
            float barW = r.width - pad * 2f - fs * 3.2f;
            float frac = maxHp <= 0f ? 0f : Mathf.Clamp01(hp / maxHp);
            Color c = frac > 0.5f ? UiKit.Good
                : (frac > 0.22f ? new Color(0.95f, 0.75f, 0.15f, 1f) : new Color(0.90f, 0.28f, 0.22f, 1f));
            UiKit.Bar(new Rect(r.x + pad, barY, barW, fs * 0.55f), frac, c,
                new Color(0.06f, 0.07f, 0.10f, 1f));

            GUI.Label(new Rect(r.xMax - pad - fs * 3.1f, barY - fs * 0.14f, fs * 3.1f, fs * 0.9f),
                string.Format("{0:F0}/{1:F0}", hp, maxHp), _small);
        }

        /// <summary>
        /// Opens a second UDP endpoint on this device that impersonates a real peer:
        /// it sends a state packet, then an attack intent. This exercises the whole
        /// wire path (serialise, socket, host validation, damage verdict, victim
        /// application) without needing a second phone.
        /// </summary>
        private System.Collections.IEnumerator LoopbackTest()
        {
            if (_player == null || _net == null)
            {
                CoopLog.Warn("[LOOP] no player / LAN off");
                yield break;
            }
            string fakeId = "zzzz" + _myId.Substring(0, 4);
            int fakePort = _cfg.Port + 1;
            var t = new LanTransport();
            t.Start(fakePort);
            CoopLog.Info("[LOOP] fake peer " + fakeId + " on udp " + fakePort);

            Vector3 pos = _player.transform.position + _player.transform.forward * 2f;
            var fakeAnim = new AnimSnapshot { Flags2 = AnimBits.OnGround };
            t.SendTo(Wire.BuildState(fakeId, L("回环玩家", "LoopPeer"), 1, pos,
                    _player.transform.eulerAngles.y + 180f, 0f, 100f, 100f, PeerFlags.None, fakeAnim),
                new IPEndPoint(IPAddress.Loopback, _cfg.Port));

            yield return new WaitForSeconds(1.2f);
            CoopLog.Info("[LOOP] peers now=" + _peers.Count + " host=" + IsHost);

            var attack = new AttackMsg
            {
                AttackerId = fakeId,
                AtkSeq = 1,
                Kind = 0,
                Origin = _player.transform.position + Vector3.up * 1.2f + _player.transform.forward * 2f,
                Direction = -_player.transform.forward,
                Range = 3.2f,
                Damage = 10f,
            };
            CoopLog.Info("[LOOP] sending attack intent, hp=" + _hit.Health.Current);
            t.SendTo(Wire.BuildAttack(attack), new IPEndPoint(IPAddress.Loopback, _cfg.Port));

            yield return new WaitForSeconds(1.5f);
            CoopLog.Info("[LOOP] hp after=" + (_hit == null ? -1f : _hit.Health.Current));
            t.Stop();

            NpcSelfTest();
        }

        /// <summary>Smoke test for NPC replication: feed one of the local NPCs back into
        /// the receive path so the proxy/template code runs without a second device.</summary>
        private void NpcSelfTest()
        {
            if (_npc == null)
            {
                CoopLog.Info("[NPC] sync disabled");
                return;
            }
            BaseStatusNPC[] all = UnityEngine.Object.FindObjectsOfType<BaseStatusNPC>();
            if (all == null || all.Length == 0)
            {
                CoopLog.Info("[NPC] no npc nearby, cannot self test");
                return;
            }
            BaseStatusNPC src = null;
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].gameObject.activeInHierarchy) { src = all[i]; break; }
            if (src == null) { CoopLog.Info("[NPC] no active npc"); return; }

            StartCoroutine(NpcSelfTestRoutine(src));
        }

        private System.Collections.IEnumerator NpcSelfTestRoutine(BaseStatusNPC src)
        {
            var e = new NpcEntry
            {
                Id = src.GetInstanceID(),
                Type = src.gameObject.name.Replace("(Clone)", "").Trim(),
                Position = _player.transform.position + _player.transform.right * 2.5f,
                Yaw = _player.transform.eulerAngles.y + 180f,
                Health = 100f,
                MaxHealth = 100f,
                Flags = 1 | 2,
            };
            CoopLog.Info("[NPC] self test with type " + e.Type + " id=" + e.Id);
            for (int i = 0; i < 60; i++)
            {
                e.Position = _player.transform.position + _player.transform.right * 2.5f;
                _npc.Apply("selftest", new List<NpcEntry> { e });
                if (i == 0)
                    CoopLog.Info("[NPC] proxies=" + _npc.VisibleProxies + " templates=" + _npc.KnownTemplates);
                if (i == 10)
                {
                    float before = src != null ? src.Health.Current : -1f;
                    CoopLog.Info("[NPC] relay test, real npc hp before=" + before);
                    _npc.ApplyDamage(e.Id, 12f, e.Position, Vector3.up);
                    CoopLog.Info("[NPC] real npc hp after=" + (src != null ? src.Health.Current : -1f));
                }
                yield return new WaitForSeconds(0.1f);
            }
        }

        // ------------------------------------------------------------- debug bot

        public void SpawnTestBot()
        {
            if (_player == null)
            {
                CoopLog.Warn("no local player to clone yet");
                return;
            }
            const string botId = "bot-1";
            RemoteAvatar old;
            if (_avatars.TryGetValue(botId, out old) && old != null) Destroy(old.gameObject);
            _avatars.Remove(botId);

            string name = L("测试机器人", "TestBot");
            Vector3 pos = _player.transform.position + _player.transform.forward * 2.2f;
            float yaw = _player.transform.eulerAngles.y + 180f;
            RemoteAvatar avatar = RemoteAvatar.Spawn(_player, botId, name, pos, yaw);
            if (avatar == null) return;
            _avatars[botId] = avatar;

            _bot = new PeerState
            {
                Id = botId,
                Name = name,
                Position = pos,
                Yaw = yaw,
                Health = 100f,
                MaxHealth = 100f,
            };
            avatar.ApplyState(_bot);
            _botTime = 0f;
            CoopLog.Info("test bot spawned");
        }

        public void SimulateRemoteHit()
        {
            if (_player == null || _hit == null)
            {
                CoopLog.Warn("no local player for hit test");
                return;
            }
            // Goes through the real authority path: host resolves, victim applies.
            var attack = new AttackMsg
            {
                AttackerId = "bot-1",
                AtkSeq = ++_atkSeq,
                Kind = 0,
                Origin = _player.transform.position + Vector3.up * 1.2f + _player.transform.forward * 2.2f,
                Direction = -_player.transform.forward,
                Range = 3.2f,
                Damage = 15f,
            };
            CoopLog.Info("[TEST] remote hit, hp before=" + _hit.Health.Current +
                         " host=" + IsHost);
            if (IsHost) ResolveAttack(attack);
            else
            {
                PeerState host = HostPeer;
                if (host != null && _net != null) _net.SendTo(Wire.BuildAttack(attack), host.EndPoint);
            }
            CoopLog.Info("[TEST] hp after=" + (_hit == null ? -1f : _hit.Health.Current));
        }

        private void LateUpdate()
        {
            if (_bot == null || _player == null) return;
            RemoteAvatar avatar;
            if (!_avatars.TryGetValue(_bot.Id, out avatar) || avatar == null) return;

            _botTime += Time.deltaTime;
            _bot.Position = _player.transform.position + _player.transform.forward * 2.2f;
            _bot.Yaw = _player.transform.eulerAngles.y + 180f;
            _bot.Health = 60f + Mathf.Sin(_botTime * 2f) * 40f;
            // mirror the local parameters so the avatar animates like the player
            _bot.Anim = ReadLocalAnim();
            avatar.ApplyState(_bot);
        }
    }
}

