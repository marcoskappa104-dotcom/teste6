using System;
using System.Collections;
using UnityEngine;
using Mirror;
using RPG.Data;

namespace RPG.Network
{
    /// <summary>
    /// Coroutine de regen de HP/MP do jogador server-side.
    ///
    /// - Tick a cada REGEN_INTERVAL segundos
    /// - Suprime regen por REGEN_COMBAT_SUPPRESSION segundos após receber dano
    /// - Dispara callback OnRegenTick(hpRestored, mpRestored) quando há valor relevante
    ///
    /// Extraído do NetworkPlayer para isolar a lógica de tick.
    /// </summary>
    public class PlayerRegenLoop : NetworkBehaviour
    {
        private const float REGEN_INTERVAL              = 5f;
        private const float REGEN_COMBAT_SUPPRESSION    = 8f;
        private const float REGEN_DISPLAY_THRESHOLD     = 1f;

        public const float CombatSuppressionSeconds = REGEN_COMBAT_SUPPRESSION;

        private Coroutine _coroutine;
        private float     _lastDamageTime = -999f;

        /// <summary>Callback disparado em cada tick com (hpRestored, mpRestored).</summary>
        public event Action<float, float> OnRegenTick;

        /// <summary>Fornecedor de estado autoritativo do servidor.</summary>
        public Func<RegenSnapshot> SnapshotProvider;

        /// <summary>Aplica os novos valores de HP/MP no jogador.</summary>
        public Action<float, float> ApplyRegen;

        public struct RegenSnapshot
        {
            public bool         IsDead;
            public float        CurrentHP, MaxHP;
            public float        CurrentMP, MaxMP;
            public DerivedStats Stats;
        }

        [Server]
        public void ServerStart()
        {
            Stop();
            _coroutine = StartCoroutine(RegenLoop());
        }

        [Server]
        public void Stop()
        {
            if (_coroutine != null)
            {
                StopCoroutine(_coroutine);
                _coroutine = null;
            }
        }

        [Server]
        public void NotifyDamageTaken()
        {
            _lastDamageTime = Time.time;
        }

        [Server]
        public void ResetCombatSuppression()
        {
            _lastDamageTime = -999f;
        }

        [Server]
        private IEnumerator RegenLoop()
        {
            var wait = new WaitForSeconds(REGEN_INTERVAL);
            while (true)
            {
                yield return wait;
                if (this == null) yield break;

                if (SnapshotProvider == null || ApplyRegen == null) continue;

                var snap = SnapshotProvider();
                if (snap.IsDead) continue;
                if (snap.Stats == null) continue;

                // Otimização: já está cheio → não faz nada
                if (snap.CurrentHP >= snap.MaxHP - 0.01f
                    && snap.CurrentMP >= snap.MaxMP - 0.01f) continue;

                bool inCombat = (Time.time - _lastDamageTime) < REGEN_COMBAT_SUPPRESSION;
                if (inCombat) continue;

                float newHP = snap.CurrentHP;
                float newMP = snap.CurrentMP;
                float hpRestored = 0f;
                float mpRestored = 0f;

                if (snap.CurrentHP < snap.MaxHP && snap.Stats.HPRegen > 0f)
                {
                    newHP = Mathf.Min(snap.MaxHP, snap.CurrentHP + snap.Stats.HPRegen);
                    hpRestored = newHP - snap.CurrentHP;
                }

                if (snap.CurrentMP < snap.MaxMP && snap.Stats.MPRegen > 0f)
                {
                    newMP = Mathf.Min(snap.MaxMP, snap.CurrentMP + snap.Stats.MPRegen);
                    mpRestored = newMP - snap.CurrentMP;
                }

                ApplyRegen(newHP, newMP);

                if (hpRestored >= REGEN_DISPLAY_THRESHOLD || mpRestored >= REGEN_DISPLAY_THRESHOLD)
                    OnRegenTick?.Invoke(hpRestored, mpRestored);
            }
        }
    }
}
