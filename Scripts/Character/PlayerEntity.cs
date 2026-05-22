using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using RPG.Data;

namespace RPG.Character
{

    [RequireComponent(typeof(NavMeshAgent))]
    public class PlayerEntity : MonoBehaviour
    {
        public static readonly HashSet<PlayerEntity> All = new HashSet<PlayerEntity>();

        // Configuração de NavMeshAgent — manter em sync com NetworkPlayer
        private const float AGENT_ACCELERATION   = 60f;
        private const float AGENT_ANGULAR_SPEED  = 720f;
        private const float AGENT_STOPPING_DIST  = 0.15f;
        private const float AGENT_MIN_SPEED      = 2f;
        private const float AGENT_MAX_SPEED      = 10f;

        // ── Estado autoritativo ────────────────────────────────────────────
        public CharacterData Data  { get; private set; }
        public DerivedStats  Stats { get; private set; }

        public float CurrentHP { get; private set; }
        public float CurrentMP { get; private set; }

        public bool IsInitialized => Data != null && Stats != null;
        public bool IsDead        => CurrentHP <= 0f;

        // ── Eventos para a UI ──────────────────────────────────────────────
        public event Action<float, float> OnHPChanged;
        public event Action<float, float> OnMPChanged;
        public event Action<bool>         OnDeathChanged;
        public event Action               OnStatsChanged;
        public event Action               OnInitialized;
        public event Action<ITargetable>  OnTargetChanged;

        // ── Componentes ────────────────────────────────────────────────────
        private NavMeshAgent _agent;
        public  NavMeshAgent Agent => _agent;

        private Camera _cachedCamera;
        public Camera MainCamera
        {
            get
            {
                if (_cachedCamera == null)
                    _cachedCamera = Camera.main;
                return _cachedCamera;
            }
        }

        public ITargetable CurrentTarget { get; private set; }

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            _agent        = GetComponent<NavMeshAgent>();
            _cachedCamera = Camera.main;
        }

        private void OnEnable()
        {
            All.Add(this);
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDisable()
        {
            All.Remove(this);
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _cachedCamera = null;
        }

        // ── Inicialização ──────────────────────────────────────────────────

        public void InitializeFromServer(CharacterData data)
        {
            if (data == null)
            {
                Debug.LogError("[PlayerEntity] InitializeFromServer: data nulo.");
                return;
            }

            Data  = data;
            Stats = data.GetDerivedStats();

            if (Stats == null)
            {
                Debug.LogError("[PlayerEntity] InitializeFromServer: GetDerivedStats retornou null.");
                return;
            }

            CurrentHP = Mathf.Clamp(data.CurrentHP, 0f, Stats.MaxHP);
            CurrentMP = Mathf.Clamp(data.CurrentMP, 0f, Stats.MaxMP);

            ConfigureAgent();

            OnInitialized?.Invoke();
            OnHPChanged?.Invoke(CurrentHP, Stats.MaxHP);
            OnMPChanged?.Invoke(CurrentMP, Stats.MaxMP);
        }

        // ── Atualizações vindas do servidor ────────────────────────────────

        public void SetHPFromServer(float hp, float maxHp)
        {
            if (!IsInitialized) return;
            if (Stats == null) return;

            bool wasDead = IsDead;

            // Stats.MaxHP é mantido em sync com o servidor. Se o servidor
            // mandou um MaxHP diferente do cacheado, clonamos e atualizamos.
            if (!Mathf.Approximately(Stats.MaxHP, maxHp))
            {
                var updated = Stats.Clone();
                updated.MaxHP = maxHp;
                Stats = updated;
            }

            CurrentHP = Mathf.Clamp(hp, 0f, maxHp);
            OnHPChanged?.Invoke(CurrentHP, maxHp);

            bool nowDead = IsDead;
            if (nowDead != wasDead)
            {
                if (nowDead && _agent != null && _agent.isOnNavMesh)
                    _agent.ResetPath();
                OnDeathChanged?.Invoke(nowDead);
            }
        }

        public void SetMPFromServer(float mp, float maxMp)
        {
            if (!IsInitialized) return;
            if (Stats == null) return;

            if (!Mathf.Approximately(Stats.MaxMP, maxMp))
            {
                var updated = Stats.Clone();
                updated.MaxMP = maxMp;
                Stats = updated;
            }

            CurrentMP = Mathf.Clamp(mp, 0f, maxMp);
            OnMPChanged?.Invoke(CurrentMP, maxMp);
        }

        public void RefreshStatsFromServer(float maxHp, float maxMp)
        {
            if (!IsInitialized) return;
            if (Stats == null) return;

            var updated = Stats.Clone();
            updated.MaxHP = maxHp;
            updated.MaxMP = maxMp;
            Stats = updated;

            CurrentHP = Mathf.Min(CurrentHP, maxHp);
            CurrentMP = Mathf.Min(CurrentMP, maxMp);

            OnStatsChanged?.Invoke();
            OnHPChanged?.Invoke(CurrentHP, maxHp);
            OnMPChanged?.Invoke(CurrentMP, maxMp);
        }

        public void FullRefreshStatsFromData()
        {
            if (!IsInitialized || Data == null) return;

            var newStats = Data.GetDerivedStats();
            if (newStats == null) return;

            Stats = newStats;
            ConfigureAgent();

            CurrentHP = Mathf.Min(CurrentHP, Stats.MaxHP);
            CurrentMP = Mathf.Min(CurrentMP, Stats.MaxMP);

            OnStatsChanged?.Invoke();
            OnHPChanged?.Invoke(CurrentHP, Stats.MaxHP);
            OnMPChanged?.Invoke(CurrentMP, Stats.MaxMP);
        }

        public void UpdateDataFromServer(int level, long exp, long expToNext,
                                         int freePoints,
                                         int allocSTR, int allocAGI, int allocVIT,
                                         int allocDEX, int allocINT, int allocLUK)
        {
            if (Data == null) return;
            Data.Level                 = level;
            Data.Experience            = exp;
            Data.ExperienceToNextLevel = expToNext;
            Data.FreeAttributePoints   = freePoints;
            Data.AllocatedSTR          = allocSTR;
            Data.AllocatedAGI          = allocAGI;
            Data.AllocatedVIT          = allocVIT;
            Data.AllocatedDEX          = allocDEX;
            Data.AllocatedINT          = allocINT;
            Data.AllocatedLUK          = allocLUK;
        }

        // ── Morte e Respawn ────────────────────────────────────────────────

        public void OnServerDeath()
        {
            CurrentHP = 0f;
            if (_agent != null && _agent.isOnNavMesh)
                _agent.ResetPath();

            if (CurrentTarget != null)
                ClearTarget();

            OnHPChanged?.Invoke(0f, Stats?.MaxHP ?? 1f);
            OnDeathChanged?.Invoke(true);
        }

        // === FIX (Lote 2): ClearTarget ANTES de mover ===
        // Ordem reorganizada: limpar target → mover → restaurar HP/MP →
        // notificar. Reduz chance de UI tentar renderizar target panel
        // com referência stale durante a transição de respawn.
        public void OnServerRespawn(Vector3 position, float hp, float maxHp, float mp, float maxMp)
        {
            if (!IsInitialized) return;
            if (Stats == null) return;

            // 1. Limpa target ANTES de qualquer outra coisa visual.
            if (CurrentTarget != null)
                ClearTarget();

            // 2. Move o player para a nova posição.
            transform.position = position;
            if (_agent != null && _agent.isOnNavMesh)
                _agent.Warp(position);

            // 3. Atualiza stats (MaxHP/MaxMP podem ter sido recalculados no servidor).
            var updated = Stats.Clone();
            updated.MaxHP = maxHp;
            updated.MaxMP = maxMp;
            Stats = updated;

            CurrentHP = hp;
            CurrentMP = mp;

            // 4. Notifica UI (death=false dispara remoção da death screen, etc).
            OnDeathChanged?.Invoke(false);
            OnHPChanged?.Invoke(CurrentHP, maxHp);
            OnMPChanged?.Invoke(CurrentMP, maxMp);
        }

        // ── Movimento ──────────────────────────────────────────────────────

        public void MoveToConfirmed(Vector3 destination)
        {
            if (IsDead || _agent == null || !_agent.isOnNavMesh) return;

            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
            else
                _agent.SetDestination(destination);
        }

        public void StopMovement()
        {
            if (_agent != null && _agent.isOnNavMesh)
                _agent.ResetPath();
        }

        public bool HasReachedDestination()
        {
            if (_agent == null) return true;
            return !_agent.pathPending
                && _agent.remainingDistance <= _agent.stoppingDistance
                && (!_agent.hasPath || _agent.velocity.sqrMagnitude < 0.01f);
        }

        // ══════════════════════════════════════════════════════════════════
        // Target — proteção contra alvos destruídos pelo Unity
        // ══════════════════════════════════════════════════════════════════

        private static bool IsTargetUnityDestroyed(ITargetable t)
        {
            if (t == null) return false;
            if (t is UnityEngine.Object obj) return obj == null;
            return false;
        }

        public void SetTarget(ITargetable target)
        {
            bool currentIsDead = IsTargetUnityDestroyed(CurrentTarget);

            if (!currentIsDead && CurrentTarget == target) return;

            if (!currentIsDead)
            {
                try { CurrentTarget?.OnDeselected(); }
                catch (MissingReferenceException) { /* destruído entre check e call */ }
            }

            CurrentTarget = target;

            try { CurrentTarget?.OnSelected(); }
            catch (MissingReferenceException)
            {
                CurrentTarget = null;
                OnTargetChanged?.Invoke(null);
                return;
            }

            OnTargetChanged?.Invoke(target);
        }

        public void ClearTarget()
        {
            bool hadTarget = !ReferenceEquals(CurrentTarget, null);
            if (!hadTarget) return;

            try { CurrentTarget?.OnDeselected(); }
            catch (MissingReferenceException) { /* alvo destruído — ok */ }

            CurrentTarget = null;
            OnTargetChanged?.Invoke(null);
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private void ConfigureAgent()
        {
            if (_agent == null || Stats == null) return;

            _agent.speed            = Mathf.Clamp(Stats.MoveSpeed, AGENT_MIN_SPEED, AGENT_MAX_SPEED);
            _agent.acceleration     = AGENT_ACCELERATION;
            _agent.angularSpeed     = AGENT_ANGULAR_SPEED;
            _agent.autoBraking      = false;
            _agent.stoppingDistance = AGENT_STOPPING_DIST;
        }
    }
}