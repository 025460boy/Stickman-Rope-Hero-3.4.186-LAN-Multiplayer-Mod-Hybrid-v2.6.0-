using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace SRHCoop
{
    public static class MsgType
    {
        public const byte State = 1;
        public const byte Attack = 2;    // client -> host (or host local)
        public const byte Damage = 3;    // host -> everyone
        public const byte Bye = 4;
        public const byte Ping = 5;
        public const byte Pong = 6;
        public const byte NpcState = 7;
        public const byte NpcDamage = 8;
    }

    [Flags]
    public enum PeerFlags
    {
        None = 0,
        Dead = 1,
        Sprinting = 2,
        Flying = 4,
        Host = 8,
    }

    public class PeerState
    {
        public string Id;
        public string Name;
        public IPEndPoint EndPoint;
        public float LastSeen;
        public int Seq;

        public Vector3 Position;
        public float Yaw;
        public float Speed;
        public float Health;
        public float MaxHealth;
        public PeerFlags Flags;
        public int RttMs = -1;
        public AnimSnapshot Anim;

        public bool Dead { get { return (Flags & PeerFlags.Dead) != 0; } }
        public bool IsHost { get { return (Flags & PeerFlags.Host) != 0; } }
    }

    /// <summary>Animator parameters that the game's locomotion blend tree consumes.</summary>
    public struct AnimSnapshot
    {
        public string Weapon;
        public float Forward;
        public float Turn;
        public float StrafeDir;
        public float Jump;
        public float MeleeSpeed;
        public ushort Flags2;
        public byte Melee;
        public byte MeleeWeaponType;
        public byte RangedWeaponType;
    }

    /// <summary>Bit layout of AnimSnapshot.Flags2.</summary>
    public static class AnimBits
    {
        public const int Sprint = 1 << 0;
        public const int Strafe = 1 << 1;
        public const int Crouch = 1 << 2;
        public const int OnGround = 1 << 3;
        public const int IsMoving = 1 << 4;
        public const int IsFlying = 1 << 5;
        public const int Gliding = 1 << 6;
        public const int NearWall = 1 << 7;
        public const int IsOnWater = 1 << 8;
        public const int DoMelee = 1 << 9;
        public const int RangedWeaponShoot = 1 << 10;
        public const int RangedWeaponRecharge = 1 << 11;
        public const int SuperheroLanding = 1 << 12;
    }

    public class AttackMsg
    {
        public string AttackerId;
        public int AtkSeq;
        public byte Kind;              // 0 melee, 1 ranged
        public Vector3 Origin;
        public Vector3 Direction;
        public float Range;
        public float Damage;
    }

    public class DamageMsg
    {
        public string AttackerId;
        public string SenderId;        // host that judged it - receivers validate this
        public string VictimId;
        public int AtkSeq;
        public float Amount;
        public byte Kind;
        public Vector3 Origin;
        public Vector3 Direction;
    }

    /// <summary>
    /// UDP LAN transport. Every peer broadcasts on the same port, so no host,
    /// discovery phase or manual IP entry is required.
    /// </summary>
    public class LanTransport : IDisposable
    {
        public const int DefaultPort = 27811;
        private const float PeerTimeout = 5f;

        private UdpClient _udp;
        private Thread _rxThread;
        private volatile bool _running;
        private readonly Queue<Datagram> _inbox = new Queue<Datagram>();
        private readonly object _lock = new object();

        public struct Datagram
        {
            public byte[] Data;
            public IPEndPoint From;
        }

        public int Port = DefaultPort;
        public bool Running { get { return _running; } }
        public int PacketsSent;
        public int PacketsReceived;
        public string LastError = "";

        public void Start(int port)
        {
            if (_running) return;
            Port = port;
            try
            {
                _udp = new UdpClient();
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.EnableBroadcast = true;
                try { _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true); }
                catch { }
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                _udp.Client.ReceiveTimeout = 1000;
                _running = true;
                _rxThread = new Thread(RxLoop) { IsBackground = true, Name = "SRHCoop-RX" };
                _rxThread.Start();
                CoopLog.Info("LAN listening on UDP " + port);
            }
            catch (Exception e)
            {
                LastError = e.Message;
                CoopLog.Error("LAN start failed: " + e);
                _running = false;
            }
        }

        public void Stop()
        {
            _running = false;
            try { if (_udp != null) _udp.Close(); } catch { }
            _udp = null;
        }

        private void RxLoop()
        {
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try
                {
                    byte[] data = _udp.Receive(ref remote);
                    if (data == null || data.Length < 6) continue;
                    Interlocked.Increment(ref PacketsReceived);
                    var dg = new Datagram { Data = data, From = new IPEndPoint(remote.Address, remote.Port) };
                    lock (_lock) { _inbox.Enqueue(dg); }
                }
                catch (SocketException)
                {
                    if (!_running) break;
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception e)
                {
                    LastError = e.Message;
                    Thread.Sleep(50);
                }
            }
        }

        public List<Datagram> Drain()
        {
            var list = new List<Datagram>();
            lock (_lock)
            {
                while (_inbox.Count > 0) list.Add(_inbox.Dequeue());
            }
            return list;
        }

        public void Broadcast(byte[] payload)
        {
            if (!_running || _udp == null) return;
            try
            {
                _udp.Send(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, Port));
                Interlocked.Increment(ref PacketsSent);
            }
            catch (Exception e) { LastError = e.Message; }

            foreach (IPEndPoint ep in SubnetBroadcasts())
            {
                try
                {
                    _udp.Send(payload, payload.Length, ep);
                    Interlocked.Increment(ref PacketsSent);
                }
                catch { }
            }
        }

        public void SendTo(byte[] payload, IPEndPoint target)
        {
            if (!_running || _udp == null || target == null) return;
            try
            {
                _udp.Send(payload, payload.Length, target);
                Interlocked.Increment(ref PacketsSent);
            }
            catch (Exception e) { LastError = e.Message; }
        }

        private static List<IPEndPoint> _subnets;
        private static float _subnetStamp;
        private static IPAddress _localAddr;

        /// <summary>Egress interface address, discovered without DNS.</summary>
        public static IPAddress LocalAddress()
        {
            if (_localAddr != null) return _localAddr;
            try
            {
                using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    probe.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 65530));
                    _localAddr = ((IPEndPoint)probe.LocalEndPoint).Address;
                }
            }
            catch { }
            return _localAddr;
        }

        /// <summary>Poke every host on the local /24 with a small packet. Peers answer
        /// by unicast, so discovery still works when broadcast is filtered.</summary>
        public int ScanSubnet(byte[] payload)
        {
            IPAddress local = LocalAddress();
            if (local == null || _udp == null) return 0;
            byte[] b = local.GetAddressBytes();
            int sent = 0;
            for (int host = 1; host <= 254; host++)
            {
                b[3] = (byte)host;
                try
                {
                    _udp.Send(payload, payload.Length, new IPEndPoint(new IPAddress(b), Port));
                    sent++;
                }
                catch { }
            }
            Interlocked.Add(ref PacketsSent, sent);
            return sent;
        }

        /// <summary>Never use DNS here: resolving the host name can block for seconds
        /// on Android and stall the Unity main thread.</summary>
        private List<IPEndPoint> SubnetBroadcasts()
        {
            float now = Time.realtimeSinceStartup;
            if (_subnets != null && now - _subnetStamp < 30f) return _subnets;
            var list = new List<IPEndPoint>();
            IPAddress local = LocalAddress();
            if (local != null)
            {
                byte[] b = local.GetAddressBytes();
                if (b[0] != 127 && b[0] != 0)
                {
                    b[3] = 255;
                    list.Add(new IPEndPoint(new IPAddress(b), Port));
                }
            }
            _subnets = list;
            _subnetStamp = now;
            return list;
        }

        public void Dispose() { Stop(); }

        public static float PeerTimeoutSeconds { get { return PeerTimeout; } }
    }

    /// <summary>Binary wire format shared by all peers.</summary>
    public static class Wire
    {
        private const byte M0 = (byte)'S', M1 = (byte)'C', M2 = (byte)'P';
        public const byte Version = 4;
        private const byte Ver = Version;

        private static BinaryWriter Begin(MemoryStream ms, byte type)
        {
            var w = new BinaryWriter(ms);
            w.Write(M0); w.Write(M1); w.Write(M2); w.Write(Ver); w.Write(type);
            return w;
        }

        public static byte[] BuildState(string id, string name, int seq, Vector3 pos, float yaw,
            float speed, float hp, float maxHp, PeerFlags flags, AnimSnapshot anim)
        {
            using (var ms = new MemoryStream(128))
            using (var w = Begin(ms, MsgType.State))
            {
                w.Write(id ?? "");
                w.Write(name ?? "");
                w.Write(seq);
                w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
                w.Write(yaw);
                w.Write(speed);
                w.Write(hp);
                w.Write(maxHp);
                w.Write((byte)flags);

                // animator parameters: the game drives locomotion with a blend tree,
                // so the state hash alone is useless for walking / running
                w.Write(anim.Weapon ?? "");
                w.Write(anim.Forward);
                w.Write(anim.Turn);
                w.Write(anim.StrafeDir);
                w.Write(anim.Jump);
                w.Write(anim.MeleeSpeed);
                w.Write(anim.Flags2);
                w.Write(anim.Melee);
                w.Write(anim.MeleeWeaponType);
                w.Write(anim.RangedWeaponType);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static byte[] BuildAttack(AttackMsg a)
        {
            using (var ms = new MemoryStream(64))
            using (var w = Begin(ms, MsgType.Attack))
            {
                w.Write(a.AttackerId ?? "");
                w.Write(a.AtkSeq);
                w.Write(a.Kind);
                w.Write(a.Origin.x); w.Write(a.Origin.y); w.Write(a.Origin.z);
                w.Write(a.Direction.x); w.Write(a.Direction.y); w.Write(a.Direction.z);
                w.Write(a.Range);
                w.Write(a.Damage);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static byte[] BuildDamage(DamageMsg d)
        {
            using (var ms = new MemoryStream(80))
            using (var w = Begin(ms, MsgType.Damage))
            {
                w.Write(d.AttackerId ?? "");
                w.Write(d.SenderId ?? "");
                w.Write(d.VictimId ?? "");
                w.Write(d.AtkSeq);
                w.Write(d.Amount);
                w.Write(d.Kind);
                w.Write(d.Origin.x); w.Write(d.Origin.y); w.Write(d.Origin.z);
                w.Write(d.Direction.x); w.Write(d.Direction.y); w.Write(d.Direction.z);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static byte[] BuildSimple(byte type, string id, int seq, long ms)
        {
            using (var stream = new MemoryStream(32))
            using (var w = Begin(stream, type))
            {
                w.Write(id ?? "");
                w.Write(seq);
                w.Write(ms);
                w.Flush();
                return stream.ToArray();
            }
        }

        public static bool ReadHeader(byte[] data, out byte type, out string id)
        {
            type = 0; id = null;
            if (data == null || data.Length < 7) return false;
            if (data[0] != M0 || data[1] != M1 || data[2] != M2) return false;
            if (data[3] != Ver) return false;
            type = data[4];
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms))
                {
                    r.ReadBytes(5);
                    id = r.ReadString();
                }
            }
            catch { return false; }
            return true;
        }

        public static PeerState ReadState(byte[] data, IPEndPoint from)
        {
            using (var ms = new MemoryStream(data))
            using (var r = new BinaryReader(ms))
            {
                r.ReadBytes(5);
                var s = new PeerState();
                s.Id = r.ReadString();
                s.Name = r.ReadString();
                s.Seq = r.ReadInt32();
                s.Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                s.Yaw = r.ReadSingle();
                s.Speed = r.ReadSingle();
                s.Health = r.ReadSingle();
                s.MaxHealth = r.ReadSingle();
                s.Flags = (PeerFlags)r.ReadByte();

                var anim = new AnimSnapshot();
                anim.Weapon = r.ReadString();
                anim.Forward = r.ReadSingle();
                anim.Turn = r.ReadSingle();
                anim.StrafeDir = r.ReadSingle();
                anim.Jump = r.ReadSingle();
                anim.MeleeSpeed = r.ReadSingle();
                anim.Flags2 = r.ReadUInt16();
                anim.Melee = r.ReadByte();
                anim.MeleeWeaponType = r.ReadByte();
                anim.RangedWeaponType = r.ReadByte();
                s.Anim = anim;

                s.EndPoint = from;
                return s;
            }
        }

        public static AttackMsg ReadAttack(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var r = new BinaryReader(ms))
            {
                r.ReadBytes(5);
                var a = new AttackMsg();
                a.AttackerId = r.ReadString();
                a.AtkSeq = r.ReadInt32();
                a.Kind = r.ReadByte();
                a.Origin = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                a.Direction = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                a.Range = r.ReadSingle();
                a.Damage = r.ReadSingle();
                return a;
            }
        }

        public static DamageMsg ReadDamage(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var r = new BinaryReader(ms))
            {
                r.ReadBytes(5);
                var d = new DamageMsg();
                d.AttackerId = r.ReadString();
                d.SenderId = r.ReadString();
                d.VictimId = r.ReadString();
                d.AtkSeq = r.ReadInt32();
                d.Amount = r.ReadSingle();
                d.Kind = r.ReadByte();
                d.Origin = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                d.Direction = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                return d;
            }
        }

        public static void ReadSimple(byte[] data, out int seq, out long ms)
        {
            using (var stream = new MemoryStream(data))
            using (var r = new BinaryReader(stream))
            {
                r.ReadBytes(5);
                r.ReadString();
                seq = r.ReadInt32();
                ms = r.ReadInt64();
            }
        }
    }
}
