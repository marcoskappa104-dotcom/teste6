using System.Collections.Generic;
using UnityEngine;
using Mirror;

namespace RPG.Network
{
    /// <summary>
    /// Tracking de cooldowns server-side do jogador.
    ///
    /// Separa:
    /// - Cooldowns de skills (chave int = skill index)
    /// - Cooldowns de ataque básico por monstro (chave long = monsterNetId<<32|attackerNetId)
    ///
    /// Faz cleanup periódico de entradas expiradas.
    /// Extraído do NetworkPlayer para isolar a lógica.
    /// </summary>
    public class PlayerCooldownTracker : MonoBehaviour
    {
        private const float COOLDOWN_CLEANUP_INTERVAL = 60f;

        private readonly Dictionary<int, float>  _skillCooldowns       = new();
        private readonly Dictionary<long, float> _basicAttackCooldowns = new();

        private readonly List<int>  _cleanupBufferInt  = new(8);
        private readonly List<long> _cleanupBufferLong = new(8);

        private float _lastCleanupTime;

        [Server]
        public void ServerReset()
        {
            _skillCooldowns.Clear();
            _basicAttackCooldowns.Clear();
            _lastCleanupTime = Time.time;
        }

        [Server]
        public void ServerTick()
        {
            if (Time.time - _lastCleanupTime < COOLDOWN_CLEANUP_INTERVAL) return;
            _lastCleanupTime = Time.time;
            CleanupExpiredCooldowns();
        }

        [Server]
        public void ServerClearAll()
        {
            _skillCooldowns.Clear();
            _basicAttackCooldowns.Clear();
        }

        // ══════════════════════════════════════════════════════════════════
        // Skills
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public bool TryCheckAndSetSkill(int skillIndex, float cooldownDuration, float capSeconds)
        {
            if (cooldownDuration <= 0f) return true;
            cooldownDuration = Mathf.Min(cooldownDuration, capSeconds);

            if (_skillCooldowns.TryGetValue(skillIndex, out float endTime) && Time.time < endTime)
                return false;

            _skillCooldowns[skillIndex] = Time.time + cooldownDuration;
            return true;
        }

        [Server]
        public bool TryGetSkillEndTime(int skillIndex, out float endTime)
            => _skillCooldowns.TryGetValue(skillIndex, out endTime);

        // ══════════════════════════════════════════════════════════════════
        // Basic Attack
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public bool TryCheckAndSetBasicAttack(long cooldownKey, float cooldownDuration, float capSeconds)
        {
            if (cooldownDuration <= 0f) return true;
            cooldownDuration = Mathf.Min(cooldownDuration, capSeconds);

            if (_basicAttackCooldowns.TryGetValue(cooldownKey, out float endTime) && Time.time < endTime)
                return false;

            _basicAttackCooldowns[cooldownKey] = Time.time + cooldownDuration;
            return true;
        }

        // ══════════════════════════════════════════════════════════════════
        // Cleanup
        // ══════════════════════════════════════════════════════════════════

        [Server]
        private void CleanupExpiredCooldowns()
        {
            float now = Time.time;

            _cleanupBufferInt.Clear();
            foreach (var kv in _skillCooldowns)
                if (kv.Value <= now) _cleanupBufferInt.Add(kv.Key);
            foreach (var k in _cleanupBufferInt) _skillCooldowns.Remove(k);

            _cleanupBufferLong.Clear();
            foreach (var kv in _basicAttackCooldowns)
                if (kv.Value <= now) _cleanupBufferLong.Add(kv.Key);
            foreach (var k in _cleanupBufferLong) _basicAttackCooldowns.Remove(k);
        }
    }
}
