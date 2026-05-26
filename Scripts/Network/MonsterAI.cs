using System.Collections;
using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Data;

namespace RPG.Network
{
    public enum MonsterAIState { Idle, Patrol, Chase, Combat, Flee, ReturnHome, Dead }

    /// <summary>
    /// Máquina de estados de IA do monstro, server-side.
    /// Estados: Idle, Patrol, Chase, Combat, Flee, ReturnHome, Dead.
    ///
    /// Extraído do NetworkMonsterEntity. Comunica-se com:
    /// - NetworkMonsterEntity (HP, stats, IsMoving SyncVar)
    /// - MonsterCombat (ServerAttack)
    /// </summary>
    [RequireComponent(typeof(NetworkMonsterEntity))]
    public class MonsterAI : MonoBehaviour
    {
        [Header("Comportamento")]
        [SerializeField] private MonsterDisposition _disposition = MonsterDisposition.Aggressive;

        [Header("Ranges de IA")]
        [SerializeField] private float _aggroRange  = 10f;
        [SerializeField] private float _attackRange = 2.5f;
        [SerializeField] private float _leashRange  = 30f;

        [Header("Kite")]
        [SerializeField] private float _kiteDistanceFraction = 0.50f;

        [Header("Performance")]
        [SerializeField] private float _aggroScanInterval = 0.5f;
        [SerializeField] private float _pathUpdateRate    = 0.15f;
        [SerializeField] private float _hibernationRadius = 50f; // Se nenhum player estiver perto, a IA "hiberna"

        [Header("Patrulha")]
        [SerializeField] private bool        _usePatrolPoints = false;
        [SerializeField] private Transform[] _patrolPoints;
        [SerializeField] private float       _patrolWaitTime  = 2f;

        [Header("Fuga (Passive)")]
        [SerializeField] private float _fleeDuration  = 6f;
        [SerializeField] private float _fleeSpeedMult = 1.3f;

        [Header("Regen em ReturnHome")]
        [SerializeField] private float _regenInterval = 5f;
        [SerializeField] private float _regenPercent  = 0.05f;

        // ── Constantes ─────────────────────────────────────────────────────
        private const int   AGGRO_OVERLAP_BUFFER_SIZE = 32;
        private const float CHASE_DEST_FRACTION       = 0.82f;

        // ── Componentes ────────────────────────────────────────────────────
        private NetworkMonsterEntity _entity;
        private MonsterCombat        _combat;
        private NavMeshAgent         _agent;

        // ── Estado runtime ────────────────────────────────────────────────
        private MonsterAIState _state = MonsterAIState.Idle;
        private NetworkPlayer  _aggroTarget;
        private bool           _wasAttacked;
        private float          _attackAccumulator;
        private float          _fleeTimer;
        private int            _patrolIndex;
        private bool           _patrolWaiting;
        private bool           _patrolTargetSet;
        private Vector3        _homePosition;
        private float          _patrolRadius;
        private float          _kiteDistance;

        private int       _targetableLayerMask;
        private Collider[] _aggroOverlapBuffer;

        private Coroutine _aggroScanCoroutine;
        private Coroutine _pathUpdateCoroutine;
        private Coroutine _patrolWaitCoroutine;
        private Coroutine _regenCoroutine;

        private WaitForSeconds _aggroScanWait;
        private WaitForSeconds _pathUpdateWait;
        private WaitForSeconds _regenWait;

        public float            LeashRange => _leashRange;
        public MonsterAIState   State      => _state;
        public NetworkPlayer    AggroTarget => _aggroTarget;
        public MonsterDisposition Disposition => _disposition;

        // ══════════════════════════════════════════════════════════════════
        // Lifecycle
        // ══════════════════════════════════════════════════════════════════

        private void Awake()
        {
            _entity = GetComponent<NetworkMonsterEntity>();
            _combat = GetComponent<MonsterCombat>();
            _agent  = GetComponent<NavMeshAgent>();

            _kiteDistance = _attackRange * _kiteDistanceFraction;

            int layer = LayerMask.NameToLayer("Targetable");
            _targetableLayerMask = layer >= 0 ? (1 << layer) : 0;
            if (_targetableLayerMask == 0)
                Debug.LogWarning("[MonsterAI] Layer 'Targetable' não encontrado.");

            _aggroScanWait      = new WaitForSeconds(_aggroScanInterval);
            _pathUpdateWait     = new WaitForSeconds(_pathUpdateRate);
            _regenWait          = new WaitForSeconds(_regenInterval);
            _aggroOverlapBuffer = new Collider[AGGRO_OVERLAP_BUFFER_SIZE];
        }

        [Server]
        public void ServerSetupAndStart(Vector3 homePosition, float patrolRadius)
        {
            _homePosition = homePosition;
            _patrolRadius = Mathf.Max(0f, patrolRadius);

            _state             = MonsterAIState.Patrol;
            _aggroTarget       = null;
            _wasAttacked       = false;
            _attackAccumulator = 0f;
            _fleeTimer         = 0f;
            _patrolIndex       = 0;
            _patrolWaiting     = false;
            _patrolTargetSet   = false;

            CancelAllCoroutines();

            _aggroScanCoroutine  = StartCoroutine(AggroScanLoop());
            _pathUpdateCoroutine = StartCoroutine(PathUpdateLoop());
        }

        [Server]
        public void ServerStop()
        {
            _state = MonsterAIState.Dead;
            CancelAllCoroutines();
        }

        [Server]
        private void CancelAllCoroutines()
        {
            if (_aggroScanCoroutine  != null) { StopCoroutine(_aggroScanCoroutine);  _aggroScanCoroutine  = null; }
            if (_pathUpdateCoroutine != null) { StopCoroutine(_pathUpdateCoroutine); _pathUpdateCoroutine = null; }
            if (_patrolWaitCoroutine != null) { StopCoroutine(_patrolWaitCoroutine); _patrolWaitCoroutine = null; }
            if (_regenCoroutine      != null) { StopCoroutine(_regenCoroutine);      _regenCoroutine      = null; }
        }

        // ══════════════════════════════════════════════════════════════════
        // Tick (chamado por NetworkMonsterEntity.Update no servidor)
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public void ServerTick(float deltaTime)
        {
            if (_entity.IsDead) return;

            // --- IA Hibernation Optimization ---
            // Se o monstro não estiver em combate/chase, verifica se há players por perto
            if (_state == MonsterAIState.Idle || _state == MonsterAIState.Patrol || _state == MonsterAIState.ReturnHome)
            {
                bool anyPlayerNearby = false;
                foreach (var conn in NetworkServer.connections.Values)
                {
                    if (conn.identity != null && Vector3.Distance(transform.position, conn.identity.transform.position) < _hibernationRadius)
                    {
                        anyPlayerNearby = true;
                        break;
                    }
                }

                // Se não há players perto, pula o tick para economizar CPU
                if (!anyPlayerNearby) 
                {
                    if (_agent != null && _agent.hasPath) _agent.ResetPath();
                    return;
                }
            }

            _attackAccumulator += deltaTime;

            switch (_state)
            {
                case MonsterAIState.Idle: break;
                case MonsterAIState.Patrol:
                    if (_usePatrolPoints) ServerPatrolWaypoints();
                    break;
                case MonsterAIState.Chase:      ServerChaseCheck();      break;
                case MonsterAIState.Combat:     ServerCombat();          break;
                case MonsterAIState.Flee:       ServerFleeCheck();       break;
                case MonsterAIState.ReturnHome: ServerReturnHomeCheck(); break;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Aggro scan
        // ══════════════════════════════════════════════════════════════════

        [Server]
        private IEnumerator AggroScanLoop()
        {
            while (true)
            {
                if (this == null) yield break;

                if (!_entity.IsDead
                    && (_state == MonsterAIState.Idle || _state == MonsterAIState.Patrol))
                {
                    if (_disposition == MonsterDisposition.Aggressive)
                        TryAggro();
                    else if (_disposition == MonsterDisposition.Neutral && _wasAttacked)
                        TryAggro();
                }

                yield return _aggroScanWait;
            }
        }

        [Server]
        private void TryAggro()
        {
            if (_targetableLayerMask == 0) return;

            int count = Physics.OverlapSphereNonAlloc(
                transform.position, _aggroRange, _aggroOverlapBuffer, _targetableLayerMask);

            NetworkPlayer found   = null;
            float         closest = _aggroRange;

            for (int i = 0; i < count; i++)
            {
                var col = _aggroOverlapBuffer[i];
                if (col == null) continue;
                var np = col.GetComponent<NetworkPlayer>();
                if (np == null || np.Dead) continue;
                float d = Vector3.Distance(transform.position, np.transform.position);
                if (d < closest) { closest = d; found = np; }
            }

            if (found != null)
            {
                _aggroTarget       = found;
                _state             = MonsterAIState.Chase;
                float ai           = _entity.Stats.ASPD > 0f ? (1f / _entity.Stats.ASPD) : 1f;
                _attackAccumulator = ai * 0.3f;
                CancelPatrolWait();
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Path update
        // ══════════════════════════════════════════════════════════════════

        [Server]
        private IEnumerator PathUpdateLoop()
        {
            yield return null;
            while (true)
            {
                if (this == null) yield break;

                if (!_entity.IsDead)
                {
                    switch (_state)
                    {
                        case MonsterAIState.Chase:      UpdateChasePath();      break;
                        case MonsterAIState.ReturnHome: UpdateReturnHomePath(); break;
                        case MonsterAIState.Flee:       UpdateFleePath();       break;
                        case MonsterAIState.Patrol:
                            if (!_usePatrolPoints && _patrolRadius > 0.1f)
                                UpdatePatrolAreaPath();
                            break;
                    }
                }
                yield return _pathUpdateWait;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Estados
        // ══════════════════════════════════════════════════════════════════

        [Server]
        private void ServerPatrolWaypoints()
        {
            if (_patrolPoints == null || _patrolPoints.Length == 0) return;
            if (_agent == null || !_agent.isOnNavMesh || _patrolWaiting) return;

            if (!_agent.pathPending && _agent.remainingDistance < 0.5f)
            {
                _patrolIndex = (_patrolIndex + 1) % _patrolPoints.Length;
                if (_patrolPoints[_patrolIndex] == null) return;
                _agent.SetDestination(_patrolPoints[_patrolIndex].position);
                _patrolWaiting       = true;
                _patrolWaitCoroutine = StartCoroutine(PatrolWaitCoroutine());
            }
        }

        [Server]
        private IEnumerator PatrolWaitCoroutine()
        {
            _patrolWaiting = true;
            yield return new WaitForSeconds(_patrolWaitTime);
            _patrolWaiting       = false;
            _patrolTargetSet     = false;
            _patrolWaitCoroutine = null;
        }

        [Server]
        private void ServerChaseCheck()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) { ResetAggro(); return; }
            if (Vector3.Distance(transform.position, _homePosition) > _leashRange)
            { ResetAggro(); EnterReturnHome(); return; }

            float dist = Vector3.Distance(transform.position, _aggroTarget.transform.position);
            if (dist > _aggroRange * 2.5f) { ResetAggro(); return; }

            if (dist <= _attackRange)
            {
                float ai = _entity.Stats.ASPD > 0f ? (1f / _entity.Stats.ASPD) : 1f;
                _attackAccumulator = ai * 0.5f;
                _state             = MonsterAIState.Combat;

                if (_agent != null && _agent.isOnNavMesh)
                {
                    _agent.ResetPath();
                    _agent.stoppingDistance = 0.5f;
                    _agent.velocity         = Vector3.zero;
                }
            }
        }

        [Server]
        private void ServerCombat()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) { ResetAggro(); return; }
            if (Vector3.Distance(transform.position, _homePosition) > _leashRange)
            { ResetAggro(); EnterReturnHome(); return; }

            float dist = Vector3.Distance(transform.position, _aggroTarget.transform.position);
            if (dist > _attackRange * 1.4f) { _state = MonsterAIState.Chase; return; }

            if (_agent != null && _agent.isOnNavMesh)
            {
                if (dist < _kiteDistance)
                {
                    Vector3 away       = (transform.position - _aggroTarget.transform.position).normalized;
                    Vector3 kiteTarget = transform.position + away * (_kiteDistance + 0.5f);
                    _agent.stoppingDistance = 0.5f;
                    _agent.SetDestination(kiteTarget);
                }
                else if (_agent.hasPath)
                {
                    _agent.ResetPath();
                    _agent.stoppingDistance = 0.5f;
                    _agent.velocity         = Vector3.zero;
                }
            }

            Vector3 dir = _aggroTarget.transform.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(
                    transform.rotation, Quaternion.LookRotation(dir), 8f * Time.deltaTime);

            float aiCombat = _entity.Stats.ASPD > 0f ? (1f / _entity.Stats.ASPD) : 1f;
            if (_attackAccumulator >= aiCombat)
            {
                _attackAccumulator -= aiCombat;
                _combat?.ServerAttack(_aggroTarget);
            }
        }

        [Server]
        private void ServerFleeCheck()
        {
            _fleeTimer += Time.deltaTime;
            if (_fleeTimer >= _fleeDuration || _agent == null || !_agent.isOnNavMesh)
            {
                if (_agent != null) _agent.speed = _entity.Stats.MoveSpeed;
                _fleeTimer = 0f;
                EnterReturnHome();
            }
        }

        [Server]
        private void ServerReturnHomeCheck()
        {
            if (_agent == null || !_agent.isOnNavMesh) return;
            if (Vector3.Distance(transform.position, _homePosition) < 1.5f)
            {
                _agent.ResetPath();
                _wasAttacked     = false;
                _patrolWaiting   = false;
                _patrolTargetSet = false;
                if (_regenCoroutine != null) { StopCoroutine(_regenCoroutine); _regenCoroutine = null; }
                _state = MonsterAIState.Patrol;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Path updates (chamados pelo PathUpdateLoop)
        // ══════════════════════════════════════════════════════════════════

        [Server]
        private void UpdateChasePath()
        {
            if (_aggroTarget == null || _agent == null || !_agent.isOnNavMesh) return;
            Vector3 destination = CalculateChaseDestination(_aggroTarget.transform.position);
            _agent.stoppingDistance = 0.2f;
            _agent.SetDestination(destination);
        }

        private Vector3 CalculateChaseDestination(Vector3 playerPos)
        {
            Vector3 toPlayer     = playerPos - transform.position;
            float   dist         = toPlayer.magnitude;
            float   safeStopDist = _attackRange * CHASE_DEST_FRACTION;

            if (dist <= safeStopDist * 0.95f) return transform.position;

            Vector3 direction   = toPlayer.normalized;
            Vector3 destination = playerPos - direction * safeStopDist;

            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                return hit.position;

            return destination;
        }

        [Server]
        private void UpdateReturnHomePath()
        {
            if (_agent == null || !_agent.isOnNavMesh) return;
            _agent.stoppingDistance = 0.5f;
            _agent.SetDestination(_homePosition);
        }

        [Server]
        private void UpdateFleePath()
        {
            if (_aggroTarget == null || _agent == null || !_agent.isOnNavMesh) return;
            Vector3 fleeDir = (transform.position - _aggroTarget.transform.position).normalized;
            Vector3 fleePos = transform.position + fleeDir * (_aggroRange * 1.5f);
            if (NavMesh.SamplePosition(fleePos, out NavMeshHit hit, 5f, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
        }

        [Server]
        private void UpdatePatrolAreaPath()
        {
            if (_agent == null || !_agent.isOnNavMesh || _patrolWaiting) return;
            bool arrived = !_agent.pathPending && _agent.remainingDistance < 0.6f;

            if (_patrolTargetSet && arrived)
            {
                if (_patrolWaitCoroutine == null)
                    _patrolWaitCoroutine = StartCoroutine(PatrolWaitCoroutine());
                return;
            }

            if (!_patrolTargetSet
                && TryGetRandomAreaPoint(_homePosition, _patrolRadius, out Vector3 dest))
            {
                _agent.SetDestination(dest);
                _patrolTargetSet = true;
            }
        }

        private static bool TryGetRandomAreaPoint(Vector3 center, float radius, out Vector3 result)
        {
            for (int i = 0; i < 15; i++)
            {
                Vector2 r2 = Random.insideUnitCircle * radius;
                Vector3 c  = center + new Vector3(r2.x, 0f, r2.y);
                if (NavMesh.SamplePosition(c, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                { result = hit.position; return true; }
            }
            result = center;
            return false;
        }

        // ══════════════════════════════════════════════════════════════════
        // Aggro management — chamado por MonsterCombat ao receber dano
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public void ApplyAggroReaction(NetworkPlayer attacker)
        {
            switch (_disposition)
            {
                case MonsterDisposition.Passive:
                    if (_state != MonsterAIState.Flee && _state != MonsterAIState.ReturnHome && _state != MonsterAIState.Dead)
                    {
                        _aggroTarget = attacker;
                        _fleeTimer   = 0f;
                        _state       = MonsterAIState.Flee;
                        if (_agent != null) _agent.speed = _entity.Stats.MoveSpeed * _fleeSpeedMult;
                    }
                    break;

                case MonsterDisposition.Neutral:
                    _wasAttacked = true;
                    if (_state == MonsterAIState.Idle || _state == MonsterAIState.Patrol || _state == MonsterAIState.ReturnHome)
                    {
                        CancelPatrolWait();
                        _aggroTarget       = attacker;
                        _state             = MonsterAIState.Chase;
                        float ai           = _entity.Stats.ASPD > 0f ? (1f / _entity.Stats.ASPD) : 1f;
                        _attackAccumulator = ai * 0.3f;
                    }
                    break;

                case MonsterDisposition.Aggressive:
                    if (_state == MonsterAIState.Idle || _state == MonsterAIState.Patrol)
                    {
                        CancelPatrolWait();
                        _aggroTarget       = attacker;
                        _state             = MonsterAIState.Chase;
                        float ai           = _entity.Stats.ASPD > 0f ? (1f / _entity.Stats.ASPD) : 1f;
                        _attackAccumulator = ai * 0.3f;
                    }
                    break;
            }
        }

        [Server]
        private void ResetAggro()
        {
            _aggroTarget = null;
            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.ResetPath();
                _agent.stoppingDistance = 0.5f;
                _agent.velocity         = Vector3.zero;
            }
            float ai           = _entity.Stats.ASPD > 0f ? (1f / _entity.Stats.ASPD) : 1f;
            _attackAccumulator = ai * 0.3f;
            _patrolTargetSet   = false;

            if (Vector3.Distance(transform.position, _homePosition) > _leashRange * 0.5f)
                EnterReturnHome();
            else { _patrolWaiting = false; _state = MonsterAIState.Patrol; }
        }

        [Server]
        private void EnterReturnHome()
        {
            _state       = MonsterAIState.ReturnHome;
            _aggroTarget = null;
            CancelPatrolWait();

            if (_agent != null)
            {
                _agent.stoppingDistance = 0.5f;
                _agent.velocity         = Vector3.zero;
            }

            if (_regenCoroutine != null) StopCoroutine(_regenCoroutine);
            _regenCoroutine = StartCoroutine(RegenLoop());
        }

        [Server]
        private IEnumerator RegenLoop()
        {
            while (_state == MonsterAIState.ReturnHome)
            {
                yield return _regenWait;
                if (this == null) break;
                if (_state != MonsterAIState.ReturnHome) break;
                _entity.ServerHealPercent(_regenPercent);
            }
            _regenCoroutine = null;
        }

        private void CancelPatrolWait()
        {
            if (_patrolWaitCoroutine != null)
            {
                StopCoroutine(_patrolWaitCoroutine);
                _patrolWaitCoroutine = null;
            }
            _patrolWaiting = false;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = _disposition switch
            {
                MonsterDisposition.Passive => Color.green,
                MonsterDisposition.Neutral => Color.yellow,
                _                          => Color.red
            };
            Gizmos.DrawWireSphere(transform.position, _aggroRange);

            Gizmos.color = new Color(1f, 0.3f, 0.3f);
            Gizmos.DrawWireSphere(transform.position, _attackRange);

            Gizmos.color = new Color(0f, 1f, 0.5f, 0.5f);
            Gizmos.DrawWireSphere(transform.position, _attackRange * CHASE_DEST_FRACTION);

            Gizmos.color = Color.blue;
            Gizmos.DrawWireSphere(transform.position, _attackRange * _kiteDistanceFraction);

            Gizmos.color = new Color(1f, 1f, 1f, 0.4f);
            Gizmos.DrawWireSphere(transform.position, _leashRange);
        }
#endif
    }
}
