using System;
using Game.Character.CharacterController;
using Game.Character.Extras;
using UnityEngine;

namespace SRHCoop
{
    /// <summary>
    /// Remote player puppet: a stripped clone of the local player prefab driven
    /// purely by network state (transform + animator state).
    /// </summary>
    public class RemoteAvatar : MonoBehaviour
    {
        public string PeerId = "";
        public string DisplayName = "";
        public float Health = 100f;
        public float MaxHealth = 100f;
        public bool Dead;
        public bool IsHost;
        /// <summary>True for replicated NPC puppets (no name plate / health bar).</summary>
        public bool IsNpc;

        public Transform HeadAnchor;
        public Animator Animator;
        public Renderer[] Renderers;

        public Vector3 TargetPosition;
        public float TargetYaw;

        private float _spawnTime;
        private float _fadeIn;

        public static RemoteAvatar Spawn(Player source, string id, string displayName, Vector3 position, float yaw)
        {
            if (source == null) return null;

            bool wasActive = source.gameObject.activeSelf;
            if (wasActive) source.gameObject.SetActive(false);
            GameObject clone;
            try
            {
                clone = Instantiate(source.gameObject, position, Quaternion.Euler(0f, yaw, 0f));
            }
            finally
            {
                if (wasActive) source.gameObject.SetActive(true);
            }

            // The clone is still inactive, so none of its Awake() ran yet.
            foreach (MonoBehaviour mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                if (mb is StuffHelper) continue;          // keeps clothes / skins
                DestroyImmediate(mb);
            }
            foreach (Rigidbody rb in clone.GetComponentsInChildren<Rigidbody>(true))
                DestroyImmediate(rb);
            foreach (Collider col in clone.GetComponentsInChildren<Collider>(true))
                DestroyImmediate(col);
            foreach (CharacterJoint joint in clone.GetComponentsInChildren<CharacterJoint>(true))
                DestroyImmediate(joint);
            foreach (AudioSource audio in clone.GetComponentsInChildren<AudioSource>(true))
                DestroyImmediate(audio);

            clone.name = "RemotePlayer_" + displayName;
            RemoteAvatar avatar = clone.AddComponent<RemoteAvatar>();
            avatar.PeerId = id;
            avatar.DisplayName = string.IsNullOrEmpty(displayName) ? id : displayName;
            avatar.TargetPosition = position;
            avatar.TargetYaw = yaw;
            avatar.Animator = clone.GetComponentInChildren<Animator>();
            avatar.Renderers = clone.GetComponentsInChildren<Renderer>(true);
            avatar._spawnTime = Time.time;

            if (avatar.Animator != null)
            {
                avatar.Animator.applyRootMotion = false;
                avatar.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }

            Transform head = FindDeep(clone.transform, "Head");
            if (head == null) head = FindDeep(clone.transform, "head");
            avatar.HeadAnchor = head;
            if (avatar.HeadAnchor == null)
            {
                var anchor = new GameObject("HeadAnchor");
                anchor.transform.SetParent(clone.transform, false);
                anchor.transform.localPosition = new Vector3(0f, 2.1f, 0f);
                avatar.HeadAnchor = anchor.transform;
            }

            clone.SetActive(true);
            CoopLog.Info("avatar spawned for " + displayName +
                         " animator=" + (avatar.Animator == null ? "none" : avatar.Animator.name));
            return avatar;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeep(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        // animator parameter hashes (same controller as the local player, so the same names)
        private static readonly int HForward = Animator.StringToHash("Forward");
        private static readonly int HTurn = Animator.StringToHash("Turn");
        private static readonly int HStrafeDir = Animator.StringToHash("StrafeDir");
        private static readonly int HJump = Animator.StringToHash("Jump");
        private static readonly int HMeleeSpeed = Animator.StringToHash("MeleeWeaponAttackSpeed");
        private static readonly int HSprint = Animator.StringToHash("Sprint");
        private static readonly int HStrafe = Animator.StringToHash("Strafe");
        private static readonly int HCrouch = Animator.StringToHash("Crouch");
        private static readonly int HOnGround = Animator.StringToHash("OnGround");
        private static readonly int HIsMoving = Animator.StringToHash("IsMoving");
        private static readonly int HIsFlying = Animator.StringToHash("IsFlying");
        private static readonly int HGliding = Animator.StringToHash("Gliding");
        private static readonly int HNearWall = Animator.StringToHash("NearWall");
        private static readonly int HOnWater = Animator.StringToHash("IsOnWater");
        private static readonly int HDoMelee = Animator.StringToHash("DoMelee");
        private static readonly int HRangedShoot = Animator.StringToHash("RangedWeaponShoot");
        private static readonly int HRangedRecharge = Animator.StringToHash("RangedWeaponRecharge");
        private static readonly int HMelee = Animator.StringToHash("Melee");
        private static readonly int HMeleeType = Animator.StringToHash("MeleeWeaponType");
        private static readonly int HRangedType = Animator.StringToHash("RangedWeaponType");
        private static readonly int HDie = Animator.StringToHash("Die");

        private AnimSnapshot _anim;
        private bool _hasAnim;
        private bool _wasDead;
        private string _weapon;
        private Transform[] _children;

        private void Update()
        {
            transform.position = Vector3.Lerp(transform.position, TargetPosition,
                1f - Mathf.Exp(-14f * Time.deltaTime));
            transform.rotation = Quaternion.Slerp(transform.rotation,
                Quaternion.Euler(0f, TargetYaw, 0f), 1f - Mathf.Exp(-12f * Time.deltaTime));

            if (_fadeIn < 1f) _fadeIn = Mathf.Clamp01((Time.time - _spawnTime) / 0.4f);
            if (!_hasAnim || Animator == null || !Animator.isActiveAndEnabled) return;

            // locomotion lives in blend-tree parameters, not in the state hash
            Animator.SetFloat(HForward, _anim.Forward, 0.1f, Time.deltaTime);
            Animator.SetFloat(HTurn, _anim.Turn, 0.1f, Time.deltaTime);
            Animator.SetFloat(HStrafeDir, _anim.StrafeDir);
            Animator.SetFloat(HJump, _anim.Jump);
            Animator.SetFloat(HMeleeSpeed, _anim.MeleeSpeed);
            Animator.SetInteger(HMelee, _anim.Melee);
            Animator.SetInteger(HMeleeType, _anim.MeleeWeaponType);
            Animator.SetInteger(HRangedType, _anim.RangedWeaponType);

            ushort f = _anim.Flags2;
            Animator.SetBool(HSprint, (f & AnimBits.Sprint) != 0);
            Animator.SetBool(HStrafe, (f & AnimBits.Strafe) != 0);
            Animator.SetBool(HCrouch, (f & AnimBits.Crouch) != 0);
            Animator.SetBool(HOnGround, (f & AnimBits.OnGround) != 0);
            Animator.SetBool(HIsMoving, (f & AnimBits.IsMoving) != 0);
            Animator.SetBool(HIsFlying, (f & AnimBits.IsFlying) != 0);
            Animator.SetBool(HGliding, (f & AnimBits.Gliding) != 0);
            Animator.SetBool(HNearWall, (f & AnimBits.NearWall) != 0);
            Animator.SetBool(HOnWater, (f & AnimBits.IsOnWater) != 0);
            Animator.SetBool(HDoMelee, (f & AnimBits.DoMelee) != 0);
            Animator.SetBool(HRangedShoot, (f & AnimBits.RangedWeaponShoot) != 0);
            Animator.SetBool(HRangedRecharge, (f & AnimBits.RangedWeaponRecharge) != 0);
        }

        public void ApplyState(PeerState state)
        {
            TargetPosition = state.Position;
            TargetYaw = state.Yaw;
            Health = state.Health;
            MaxHealth = state.MaxHealth > 0f ? state.MaxHealth : 100f;
            IsHost = state.IsHost;
            if (!string.IsNullOrEmpty(state.Name)) DisplayName = state.Name;

            _anim = state.Anim;
            _hasAnim = true;

            if (state.Dead && !_wasDead)
            {
                _wasDead = true;
                if (Animator != null) Animator.SetTrigger(HDie);
            }
            else if (!state.Dead)
            {
                _wasDead = false;
            }
            Dead = state.Dead;

            SetWeapon(_anim.Weapon);
        }

        /// <summary>Weapon meshes are separate children; only one is active at a time.</summary>
        private void SetWeapon(string name)
        {
            if (string.IsNullOrEmpty(name) || name == _weapon) return;
            _weapon = name;
            if (_children == null) _children = GetComponentsInChildren<Transform>(true);

            if (_weaponObj != null) _weaponObj.gameObject.SetActive(false);

            bool found = false;
            for (int i = 0; i < _children.Length; i++)
            {
                Transform t = _children[i];
                if (t == null || t.name != name) continue;
                t.gameObject.SetActive(true);
                _weaponObj = t;
                found = true;
                break;
            }
            if (!found)
                CoopLog.Warn("weapon object not found on clone: " + name);
            else
                CoopLog.Info("remote weapon -> " + name);
        }

        private Transform _weaponObj;

        public Vector3 BarAnchor
        {
            get
            {
                if (HeadAnchor != null) return HeadAnchor.position + Vector3.up * 0.45f;
                return transform.position + Vector3.up * 2.4f;
            }
        }
    }
}
