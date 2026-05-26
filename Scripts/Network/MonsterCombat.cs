using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.UI;
using RPG.Combat;
using RPG.Managers;

namespace RPG.Network
{
    /// <summary>
    /// Combate server-side do monstro:
    /// - Recebe Cmds (BasicAttack, RequestSkill, ProjectileDamage)
    /// - Aplica dano com validações server-authoritative
    /// - Mantém damageLog para distribuição de XP
    /// - Sorteia drops na morte
    ///
    /// Extraído do NetworkMonsterEntity. Funciona pareado a MonsterAI
    /// (para reações de aggro) e MonsterDeathHandler (para morte).
    /// </summary>
    [RequireComponent(typeof(NetworkMonsterEntity))]
    public class MonsterCombat : NetworkBehaviour
    {
        [Header("Drops")]
        [SerializeField] private LootTable _lootTable;
        [SerializeField] private List<LootEntry> _dropTable         = new List<LootEntry>();
        [SerializeField] private List<string>   _guaranteedDropIds = new List<string>();

        [Header("Recompensa")]
        [SerializeField] private long _expReward = 50;

        // ── Constantes ─────────────────────────────────────────────────────
        private const float ATTACK_RANGE_TOLERANCE         = 1.15f;
        private const float SERVER_MAX_PLAYER_ATTACK_RANGE = 30f;
        private const float DAMAGE_LOG_CLEANUP_INTERVAL    = 60f;
        private const int   MAX_DAMAGE_LOG_ENTRIES         = 64;

        // ── Componentes ────────────────────────────────────────────────────
        private NetworkMonsterEntity _entity;
        private MonsterAI            _ai;

        // ── Damage tracking ───────────────────────────────────────────────
        private readonly Dictionary<uint, float> _damageLog              = new();
        private readonly List<uint>              _damageLogCleanupBuffer = new(8);
        private uint                             _lastAttackerNetId; // Fallback para distribuição de XP

        private Coroutine _damageLogCleanupCoroutine;
        private bool      _hasMonsterId;
        private string    _monsterId;

        private WaitForSeconds _damageLogCleanupWait;

        private void Awake()
        {
            _entity = GetComponent<NetworkMonsterEntity>();
            _ai     = GetComponent<MonsterAI>();
            _damageLogCleanupWait = new WaitForSeconds(DAMAGE_LOG_CLEANUP_INTERVAL);
        }

        [Server]
        public void ServerSetup()
        {
            _monsterId    = _entity.MonsterId;
            _hasMonsterId = !string.IsNullOrEmpty(_monsterId);

            _damageLog.Clear();

            if (_damageLogCleanupCoroutine != null)
                StopCoroutine(_damageLogCleanupCoroutine);
            _damageLogCleanupCoroutine = StartCoroutine(DamageLogCleanupLoop());
        }

        [Server]
        public void ServerStopAndClear()
        {
            if (_damageLogCleanupCoroutine != null)
            {
                StopCoroutine(_damageLogCleanupCoroutine);
                _damageLogCleanupCoroutine = null;
            }
            _damageLog.Clear();
        }

        // ══════════════════════════════════════════════════════════════════
        // Damage log
        // ══════════════════════════════════════════════════════════════════

        [Server]
        private IEnumerator DamageLogCleanupLoop()
        {
            while (true)
            {
                yield return _damageLogCleanupWait;
                if (this == null || !isServer) yield break;
                if (_entity.IsDead) continue;
                CleanupOrphanedDamageEntries();
            }
        }

        [Server]
        private void CleanupOrphanedDamageEntries()
        {
            if (_damageLog.Count == 0) return;

            _damageLogCleanupBuffer.Clear();
            float maxDist = _ai != null ? _ai.LeashRange * 3f : 90f;

            foreach (var kv in _damageLog)
            {
                bool orphaned = false;

                if (!NetworkServer.spawned.TryGetValue(kv.Key, out var identity) || identity == null)
                {
                    orphaned = true;
                }
                else
                {
                    var np = identity.GetComponent<NetworkPlayer>();
                    if (np == null) orphaned = true;
                    else if (np.Dead) orphaned = true;
                    else if (Vector3.Distance(np.transform.position, transform.position) > maxDist)
                        orphaned = true;
                }

                if (orphaned) _damageLogCleanupBuffer.Add(kv.Key);
            }

            for (int i = 0; i < _damageLogCleanupBuffer.Count; i++)
                _damageLog.Remove(_damageLogCleanupBuffer[i]);
        }

        [Server]
        private void CreditDamageToShooter(uint shooterNetId, float dmg)
        {
            if (dmg <= 0f || shooterNetId == 0) return;

            var attacker = FindPlayerByNetId(shooterNetId);
            if (attacker == null) return;

            _lastAttackerNetId = shooterNetId; // Atualiza o último atacante

            if (_damageLog.TryGetValue(shooterNetId, out float existing))
            {
                _damageLog[shooterNetId] = existing + dmg;
                return;
            }

            if (_damageLog.Count >= MAX_DAMAGE_LOG_ENTRIES)
            {
                EvictLowestDamageContributor(dmg);
                if (_damageLog.Count >= MAX_DAMAGE_LOG_ENTRIES) return;
            }

            _damageLog[shooterNetId] = dmg;
        }

        [Server]
        private void EvictLowestDamageContributor(float newDamage)
        {
            if (_damageLog.Count == 0) return;

            uint  lowestKey   = 0;
            float lowestValue = float.MaxValue;

            foreach (var kv in _damageLog)
            {
                if (kv.Value < lowestValue)
                {
                    lowestValue = kv.Value;
                    lowestKey   = kv.Key;
                }
            }

            if (newDamage > lowestValue && lowestKey != 0)
                _damageLog.Remove(lowestKey);
        }

        // ══════════════════════════════════════════════════════════════════
        // Recebimento de dano
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public void ServerTakeProjectileDamage(uint shooterNetId, float dmg, bool crit)
        {
            if (_entity.IsDead || _entity.DeathProcessed) return;

            dmg = SanitizeDamage(dmg);
            dmg = Mathf.Max(1f, dmg);

            CreditDamageToShooter(shooterNetId, dmg);

            var attacker = FindPlayerByNetId(shooterNetId);
            if (attacker != null && !attacker.Dead)
                _ai?.ApplyAggroReaction(attacker);

            _entity.RpcShowDamageFloating(dmg, crit, _entity.ImpactPoint);
            _entity.ApplyDamageInternal(dmg);
        }

        [Command(requiresAuthority = false)]
        public void CmdRequestSkill(uint attackerNetId, int skillIndex, bool isPhysical)
        {
            if (_entity.IsDead || _entity.DeathProcessed) return;
            if (skillIndex < 0 || skillIndex >= NetworkInventory.GEM_SLOT_COUNT) return;

            var attacker = FindPlayerByNetId(attackerNetId);
            if (attacker == null || attacker.Dead) return;
            if (attacker.connectionToClient == null)
            {
                Debug.LogWarning($"[Security] CmdRequestSkill com netId sem conexão: {attackerNetId}");
                return;
            }

            var atkStats = attacker.ServerStats;
            if (atkStats == null) return;

            var inventory = attacker.GetComponent<NetworkInventory>();
            var skill     = inventory?.GetEquippedSkill(skillIndex);
            if (skill == null) { attacker.RpcSkillRejected(skillIndex, "Skill inválida."); return; }

            float dist            = Vector3.Distance(attacker.transform.position, transform.position);
            float maxAllowedRange = skill.Range * ATTACK_RANGE_TOLERANCE;
            if (dist > maxAllowedRange)
            {
                Debug.LogWarning($"[Security] {attacker.CharacterName} usou skill fora de range: " +
                                 $"dist={dist:0.2f} max={maxAllowedRange:0.2f}");
                return;
            }

            if (!attacker.ServerCheckAndSetCooldown(skillIndex, skill.Cooldown))
            {
                attacker.RpcSkillRejected(skillIndex, $"{skill.Name}: ainda em cooldown.");
                return;
            }
            if (attacker.CurrentMP < skill.ManaCost)
            {
                attacker.RpcSkillRejected(skillIndex, "MP insuficiente!");
                return;
            }

            attacker.ServerConsumeMP(skill.ManaCost);

            // --- AoE Support ---
            if (skill.AoERadius > 0.1f)
            {
                // Encontra todos os monstros no raio
                Collider[] hitColliders = Physics.OverlapSphere(transform.position, skill.AoERadius);
                foreach (var hit in hitColliders)
                {
                    var otherMonster = hit.GetComponent<NetworkMonsterEntity>();
                    if (otherMonster != null && !otherMonster.IsDead)
                    {
                        var otherCombat = otherMonster.GetComponent<MonsterCombat>();
                        if (otherCombat != null)
                        {
                            otherCombat.ServerTakeDamageFromPlayer(attacker, atkStats, isPhysical, skill);
                        }
                    }
                }
            }
            else
            {
                // Single Target
                ServerTakeDamageFromPlayer(attacker, atkStats, isPhysical, skill);
            }

            attacker.RpcSkillConfirmed(skillIndex, skill.Cooldown);

            if (!string.IsNullOrEmpty(skill.AnimTrigger))
                attacker.RpcPlayAnimation(skill.AnimTrigger);
        }

        [Command(requiresAuthority = false)]
        public void CmdBasicAttack(uint attackerNetId, float clientAttackRange)
        {
            if (_entity.IsDead || _entity.DeathProcessed) return;

            var attacker = FindPlayerByNetId(attackerNetId);
            if (attacker == null || attacker.Dead) return;
            if (attacker.connectionToClient == null)
            {
                Debug.LogWarning($"[Security] CmdBasicAttack com netId sem conexão: {attackerNetId}");
                return;
            }

            var atkStats = attacker.ServerStats;
            if (atkStats == null) return;

            var inventory = attacker.GetComponent<NetworkInventory>();
            WeaponAttackProfile profile = ResolveServerWeaponProfile(inventory);

            float serverRange    = profile.Range;
            float effectiveRange = Mathf.Min(
                Mathf.Clamp(clientAttackRange, 0.5f, SERVER_MAX_PLAYER_ATTACK_RANGE),
                serverRange);

            float dist            = Vector3.Distance(attacker.transform.position, transform.position);
            float maxAllowedRange = effectiveRange * ATTACK_RANGE_TOLERANCE;

            if (dist > maxAllowedRange)
            {
                Debug.LogWarning($"[Security] {attacker.CharacterName} atacou fora de range: " +
                                 $"dist={dist:0.2f} max={maxAllowedRange:0.2f} (perfil={profile.Type})");
                return;
            }

            long cooldownKey = BuildBasicAttackCooldownKey(attacker.netId, netId);
            float baseInterval = atkStats.ASPD > 0f ? (1f / atkStats.ASPD) : 1.2f;
            float attackInterval = Mathf.Clamp(
                baseInterval * profile.AttackIntervalMultiplier, 0.2f, 3f);

            if (!attacker.ServerCheckAndSetCooldownLong(cooldownKey, attackInterval)) return;

            if (profile.ManaCost > 0f)
            {
                if (attacker.CurrentMP < profile.ManaCost)
                {
                    attacker.RpcShowMessageToOwner("MP insuficiente para atacar!");
                    return;
                }
                attacker.ServerConsumeMP(profile.ManaCost);
            }

            bool hit = StatsCalculator.RollHit(atkStats.HIT, _entity.Stats.FLEE);
            if (!hit)
            {
                _entity.RpcShowMiss(transform.position);
                return;
            }

            bool  crit = StatsCalculator.RollCrit(atkStats.CRIT);
            float dmg;

            if (profile.IsPhysical)
            {
                dmg = StatsCalculator.CalculatePhysicalDamage(
                    atkStats.ATK * profile.DamageMultiplier,
                    _entity.Stats.DEF, crit, atkStats.CritDMG,
                    atkStats.Penetration, _entity.Stats.DamageReduction);
            }
            else
            {
                dmg = StatsCalculator.CalculateMagicDamage(
                    atkStats.MATK * profile.DamageMultiplier,
                    _entity.Stats.MDEF, crit, atkStats.CritDMG,
                    atkStats.MagicPenetration, _entity.Stats.DamageReduction);
            }

            dmg = SanitizeDamage(dmg);
            dmg = Mathf.Max(1f, dmg);

            if (!string.IsNullOrEmpty(profile.AnimTrigger))
                attacker.RpcPlayAnimation(profile.AnimTrigger);

            if (profile.UsesProjectile)
            {
                SpawnAttackProjectile(attacker, profile, dmg, crit);
            }
            else
            {
                CreditDamageToShooter(attacker.netId, dmg);
                _ai?.ApplyAggroReaction(attacker);
                _entity.RpcShowDamageFloating(dmg, crit, _entity.ImpactPoint);
                _entity.ApplyDamageInternal(dmg);
            }
        }

        [Server]
        private WeaponAttackProfile ResolveServerWeaponProfile(NetworkInventory inventory)
        {
            if (inventory == null) return WeaponAttackProfile.Default(WeaponType.Unarmed);

            string weaponId = inventory.ServerGetEquipped(EquipmentSlot.Weapon);
            if (string.IsNullOrEmpty(weaponId)) return WeaponAttackProfile.Default(WeaponType.Unarmed);

            var item = ItemDatabase.Instance?.GetItem(weaponId);
            if (item == null || !item.IsWeapon) return WeaponAttackProfile.Default(WeaponType.Unarmed);

            return item.GetEffectiveAttackProfile();
        }

        [Server]
        private void SpawnAttackProjectile(NetworkPlayer attacker, WeaponAttackProfile profile,
                                           float damage, bool crit)
        {
            var prefab = RPGNetworkManager.singleton?.GetProjectilePrefab(profile.Type);
            if (prefab == null)
            {
                Debug.LogWarning($"[Combat] Sem prefab de projétil para {profile.Type}. " +
                                 "Aplicando dano instantâneo como fallback.");
                CreditDamageToShooter(attacker.netId, damage);
                _ai?.ApplyAggroReaction(attacker);
                _entity.RpcShowDamageFloating(damage, crit, _entity.ImpactPoint);
                _entity.ApplyDamageInternal(damage);
                return;
            }

            Vector3 spawnPos = attacker.transform.position
                             + attacker.transform.forward * 0.5f
                             + Vector3.up * 1.2f;
            Quaternion spawnRot = Quaternion.LookRotation(
                (_entity.ImpactPoint - spawnPos).normalized);

            var go   = Instantiate(prefab, spawnPos, spawnRot);
            var proj = go.GetComponent<Projectile>();
            if (proj == null)
            {
                Debug.LogError("[Combat] Projétil prefab não tem componente Projectile!");
                Destroy(go);
                CreditDamageToShooter(attacker.netId, damage);
                _ai?.ApplyAggroReaction(attacker);
                _entity.RpcShowDamageFloating(damage, crit, _entity.ImpactPoint);
                _entity.ApplyDamageInternal(damage);
                return;
            }

            NetworkServer.Spawn(go);
            proj.ServerInitialize(_entity, attacker.netId, profile.ProjectileSpeed, damage, crit, _entity.SpawnGeneration);
        }

        [Server]
        public void ServerTakeDamageFromPlayer(
            NetworkPlayer attacker, DerivedStats atkStats,
            bool isPhysical, SkillData skill)
        {
            bool hit = StatsCalculator.RollHit(atkStats.HIT, _entity.Stats.FLEE);
            if (!hit) { _entity.RpcShowMiss(transform.position); return; }

            bool crit = StatsCalculator.RollCrit(atkStats.CRIT);
            float dmg;

            if (isPhysical)
            {
                dmg = StatsCalculator.CalculatePhysicalDamage(
                    atkStats.ATK * skill.AtkMultiplier,
                    _entity.Stats.DEF, crit, atkStats.CritDMG,
                    atkStats.Penetration, _entity.Stats.DamageReduction);
            }
            else
            {
                dmg = StatsCalculator.CalculateMagicDamage(
                    atkStats.MATK * skill.AtkMultiplier,
                    _entity.Stats.MDEF, crit, atkStats.CritDMG,
                    atkStats.MagicPenetration, _entity.Stats.DamageReduction);
            }

            dmg = SanitizeDamage(dmg);
            dmg = Mathf.Max(1f, dmg);

            CreditDamageToShooter(attacker.netId, dmg);

            _entity.RpcShowDamageFloating(dmg, crit, transform.position);
            _ai?.ApplyAggroReaction(attacker);
            _entity.ApplyDamageInternal(dmg);
        }

        // ══════════════════════════════════════════════════════════════════
        // Ataque do monstro (chamado por MonsterAI)
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public void ServerAttack(NetworkPlayer target)
        {
            if (target == null || target.Dead) return;

            var targetStats = target.ServerStats;

            bool hit = StatsCalculator.RollHit(_entity.Stats.HIT, targetStats?.FLEE ?? 20f);
            if (!hit)
            {
                _entity.RpcShowMiss(target.transform.position);
                return;
            }

            bool  crit = StatsCalculator.RollCrit(_entity.Stats.CRIT);
            float dmg  = StatsCalculator.CalculatePhysicalDamage(
                _entity.Stats.ATK, targetStats?.DEF ?? 10f, crit,
                _entity.Stats.CritDMG, _entity.Stats.Penetration,
                targetStats?.DamageReduction ?? 0f);

            dmg = SanitizeDamage(dmg);

            if (!target.Dead)
            {
                _entity.RpcShowDamageTakenOnPlayer(dmg, crit, target.transform.position);
                target.ServerApplyDamage(dmg);
            }

            _entity.RpcPlayAnim("Attack");
        }

        // ══════════════════════════════════════════════════════════════════
        // Distribuição na morte
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public void ServerDistributeRewardsAndDrops(Vector3 deathPos)
        {
            DistributeExp();

            if (_lootTable != null)
            {
                RPG.Managers.ItemDropManager.Instance?.ServerSpawnFromTable(
                    deathPos + Vector3.up * 0.5f, _lootTable);
            }
            else
            {
                RPG.Managers.ItemDropManager.Instance?.ServerSpawnDrop(
                    deathPos + Vector3.up * 0.5f,
                    _dropTable.Count > 0 ? _dropTable : null,
                    _guaranteedDropIds.Count > 0 ? _guaranteedDropIds : null);
            }
        }

        [Server]
        private void DistributeExp()
        {
            if (_damageLog.Count == 0)
            {
                if (_lastAttackerNetId != 0)
                {
                    var fallbackPlayer = FindPlayerByNetId(_lastAttackerNetId);
                    if (fallbackPlayer != null)
                    {
                        GrantExpShared(fallbackPlayer, _expReward);
                        if (_hasMonsterId)
                        {
                            var qm = fallbackPlayer.GetComponent<RPG.Quest.QuestManager>();
                            qm?.NotifyEvent(RPG.Quest.QuestObjectiveType.KillMonster, _monsterId, 1);
                        }
                    }
                }
                return;
            }

            float total = 0f;
            foreach (var kv in _damageLog) total += kv.Value;
            if (total <= 0f) return;

            foreach (var kv in _damageLog)
            {
                long xp = (long)Mathf.Max(1f, _expReward * (kv.Value / total));
                var  np = FindPlayerByNetId(kv.Key);
                if (np == null) continue;

                GrantExpShared(np, xp);

                if (_hasMonsterId)
                {
                    var qm = np.GetComponent<RPG.Quest.QuestManager>();
                    qm?.NotifyEvent(RPG.Quest.QuestObjectiveType.KillMonster, _monsterId, 1);
                }
            }
            _damageLog.Clear();
        }

        [Server]
        private void GrantExpShared(NetworkPlayer player, long amount)
        {
            if (player.PartyId == 0)
            {
                player.ServerGrantExp(amount);
                return;
            }

            // Lógica de Grupo
            var party = PartyManager.Instance?.GetParty(player.PartyId);
            if (party == null)
            {
                player.ServerGrantExp(amount);
                return;
            }

            // Encontra membros próximos
            var nearbyMembers = new List<NetworkPlayer>();
            foreach (var mId in party.MemberNetIds)
            {
                var member = FindPlayerByNetId(mId);
                if (member != null && !member.Dead && Vector3.Distance(member.transform.position, transform.position) < 40f)
                {
                    nearbyMembers.Add(member);
                }
            }

            if (nearbyMembers.Count <= 1)
            {
                player.ServerGrantExp(amount);
                return;
            }

            // Aplica bônus de grupo (ex: +10% por membro extra)
            float bonusFactor = 1.0f + (nearbyMembers.Count - 1) * 0.1f;
            long totalXpWithBonus = (long)(amount * bonusFactor);
            long share = totalXpWithBonus / nearbyMembers.Count;

            foreach (var member in nearbyMembers)
            {
                member.ServerGrantExp(share);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Helpers
        // ══════════════════════════════════════════════════════════════════

        private static NetworkPlayer FindPlayerByNetId(uint netId)
        {
            if (NetworkServer.spawned.TryGetValue(netId, out var identity))
                return identity?.GetComponent<NetworkPlayer>();
            return null;
        }

        private static float SanitizeDamage(float dmg)
        {
            if (float.IsNaN(dmg) || float.IsInfinity(dmg)) return 1f;
            return Mathf.Max(0f, dmg);
        }

        private static long BuildBasicAttackCooldownKey(uint attackerNetId, uint monsterNetId)
            => ((long)monsterNetId << 32) | attackerNetId;
    }
}
