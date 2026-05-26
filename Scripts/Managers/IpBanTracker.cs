using System.Collections.Generic;
using UnityEngine;
using RPG.Data;

namespace RPG.Network
{
    /// <summary>
    /// Tracking de tentativas de login por IP com ban temporário.
    ///
    /// Extraído do ServerAuthManager para isolar:
    /// - Contagem de falhas por IP
    /// - Aplicação de ban temporário ao atingir threshold
    /// - Eviction LRU em duas fases (inativos primeiro, depois bans antigos)
    /// - Cap defensivo contra DoS de memória
    ///
    /// Não toca em sessões ou em conexões — apenas registra/consulta IPs.
    /// </summary>
    public class IpBanTracker
    {
        private const int MAX_TRACKED_IPS = 10_000;

        private class IpData
        {
            public int   FailedAttempts;
            public float BanUntil;
            public float LastAttemptTime;
        }

        private readonly Dictionary<string, IpData> _ipBans = new Dictionary<string, IpData>();

        public int TrackedCount => _ipBans.Count;

        // ══════════════════════════════════════════════════════════════════
        // API pública
        // ══════════════════════════════════════════════════════════════════

        public bool IsBanned(string ip)
        {
            if (string.IsNullOrEmpty(ip) || ip == "unknown") return false;
            if (!_ipBans.TryGetValue(ip, out var data)) return false;
            return Time.time < data.BanUntil;
        }

        public void RecordFailedAttempt(string ip)
        {
            if (string.IsNullOrEmpty(ip) || ip == "unknown") return;

            if (!_ipBans.ContainsKey(ip) && _ipBans.Count >= MAX_TRACKED_IPS)
            {
                EvictLeastRecent(targetSize: MAX_TRACKED_IPS - (MAX_TRACKED_IPS / 10));

                if (_ipBans.Count >= MAX_TRACKED_IPS)
                {
                    Debug.LogError("[IpBanTracker] SECURITY: _ipBans cheio mesmo após eviction. " +
                                   "IP novo NÃO trackado. Sob ataque massivo?");
                    return;
                }
            }

            if (!_ipBans.TryGetValue(ip, out var data))
            {
                data = new IpData();
                _ipBans[ip] = data;
            }

            data.FailedAttempts++;
            data.LastAttemptTime = Time.time;

            if (data.FailedAttempts >= GameConstants.Auth.LOGIN_MAX_PER_IP)
            {
                data.BanUntil = Time.time + GameConstants.Auth.IP_BAN_DURATION_SECONDS;
                Debug.LogWarning($"[IpBanTracker] SECURITY [{System.DateTime.UtcNow:o}]: " +
                                 $"IP banido por brute-force: {ip} " +
                                 $"({data.FailedAttempts} falhas, ban por " +
                                 $"{GameConstants.Auth.IP_BAN_DURATION_SECONDS}s)");
            }
        }

        public void ClearFailures(string ip)
        {
            if (string.IsNullOrEmpty(ip) || ip == "unknown") return;
            _ipBans.Remove(ip);
        }

        /// <summary>
        /// Limpa entradas expiradas. Chame periodicamente.
        /// Aplica também o cap de tamanho.
        /// </summary>
        public void RunPeriodicCleanup()
        {
            var expired = new List<string>();
            foreach (var kv in _ipBans)
            {
                var data = kv.Value;
                if (Time.time >= data.BanUntil
                    && Time.time - data.LastAttemptTime > GameConstants.Auth.IP_BAN_DURATION_SECONDS)
                    expired.Add(kv.Key);
            }
            foreach (var ip in expired)
                _ipBans.Remove(ip);

            if (_ipBans.Count > MAX_TRACKED_IPS)
                EvictLeastRecent(targetSize: MAX_TRACKED_IPS - (MAX_TRACKED_IPS / 10));
        }

        // ══════════════════════════════════════════════════════════════════
        // Eviction LRU em duas fases
        // ══════════════════════════════════════════════════════════════════

        private void EvictLeastRecent(int targetSize)
        {
            if (_ipBans.Count <= targetSize) return;

            float now      = Time.time;
            int toRemove   = _ipBans.Count - targetSize;

            // Fase 1: remove IPs inativos (ban já expirou)
            var inactiveCandidates = new List<KeyValuePair<string, float>>(_ipBans.Count);
            foreach (var kv in _ipBans)
            {
                if (now >= kv.Value.BanUntil)
                    inactiveCandidates.Add(new KeyValuePair<string, float>(kv.Key, kv.Value.LastAttemptTime));
            }
            inactiveCandidates.Sort((a, b) => a.Value.CompareTo(b.Value));

            int removed = 0;
            foreach (var c in inactiveCandidates)
            {
                if (removed >= toRemove) break;
                _ipBans.Remove(c.Key);
                removed++;
            }

            if (removed > 0)
                Debug.Log($"[IpBanTracker] Eviction LRU fase 1: removeu {removed} IPs inativos.");

            int stillToRemove = toRemove - removed;
            if (stillToRemove <= 0) return;

            // Fase 2: remove bans ativos antigos (proteção contra DoS de memória)
            var activeBans = new List<KeyValuePair<string, float>>(_ipBans.Count);
            foreach (var kv in _ipBans)
            {
                if (now < kv.Value.BanUntil)
                    activeBans.Add(new KeyValuePair<string, float>(kv.Key, kv.Value.LastAttemptTime));
            }
            activeBans.Sort((a, b) => a.Value.CompareTo(b.Value));

            int activeRemoved = 0;
            foreach (var c in activeBans)
            {
                if (activeRemoved >= stillToRemove) break;
                _ipBans.Remove(c.Key);
                activeRemoved++;
            }

            if (activeRemoved > 0)
                Debug.LogWarning($"[IpBanTracker] SECURITY: eviction LRU fase 2 removeu " +
                                 $"{activeRemoved} bans ATIVOS antigos (proteção contra DoS de memória). " +
                                 "IPs evictados serão re-banidos se continuarem atacando.");
        }
    }
}
