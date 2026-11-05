using System;
using UnityEngine;

namespace SRHCoop
{
    /// <summary>
    /// Entry point for the hybrid build: no BepInEx, no native loader. The DLL sits
    /// in assets/bin/Data/Managed next to Assembly-CSharp and is started either by
    /// Unity's runtime initialisation callback or by a one-line IL call injected
    /// into the game (see tools/IlPatch).
    /// </summary>
    public static class CoopBootstrap
    {
        private static bool _started;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        public static void AutoStart()
        {
            Init();
        }

        public static void Init()
        {
            if (_started) return;
            _started = true;
            try
            {
                var go = new GameObject("SRHCoop");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<CoopBehaviour>();
                CoopLog.Info("bootstrap ok (hybrid, no BepInEx)");
            }
            catch (Exception e)
            {
                CoopLog.Error("bootstrap failed: " + e);
            }
        }
    }
}
