using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Data;
using RPG.UI;
using RPG.Character;

namespace RPG.Network
{
    public enum MonsterDisposition { Passive, Neutral, Aggressive }

    /// <summary>
    /// Entidade de monstro — agora é principalmente orquestrador.
    ///
    /// Responsabilidades mantidas:
    /// - Identidade (nome, level, monsterId)
    /// - Stats derivados
    /// - SyncVars de HP / IsDead / IsMoving / SpawnGeneration
    /// - ITargetable (seleção, healthbar)
    /// - RPCs visuais (damage floating, miss, anim)
    ///
    /// Delegado para componentes irmãos:
    /// - MonsterAI         → estados de IA
    /// - MonsterCombat     → Cmds de ataque, damageLog, drops
    /// - MonsterDeathHandler → sequência de morte server-side
    /// - MonsterVisualFader  → fade no cliente
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkIdentity))]
    [RequireComponent(typeof(MonsterAI))]
    [RequireComponent(typeof(MonsterCombat))]
    [RequireComponent(typeof(MonsterDeathHandler))]
    public class NetworkMonsterEntity : NetworkBehaviour, ITargetable
    {
        [Header("Identidade")]
        [SerializeField] private string monsterDisplayName = "Monstro";
        [SerializeField] private int    level              = 1;

        [Tooltip("ID único para quests do tipo KillMonster.")]
        [SerializeField] private string monsterId = "";

        [Header("Atributos Base (Lv1) — escalam com o nível")]
        [SerializeField] private int baseSTR = 12;
        [SerializeField] private int baseAGI = 8;
        [SerializeField] private int baseVIT = 10;
        [SerializeField] private int baseDEX = 8;
        [SerializeField] private int baseINT = 5;
        [SerializeField] private int baseLUK = 5;

        [Header("Visuals")]
        [SerializeField] private GameObject         selectionIndicator;
        [SerializeField] private MonsterHealthBarUI healthBarUI;
        [SerializeField] private GameObject         visualRoot;

        [Header("Projétil — ponto de impacto")]
        [Tooltip("Onde projéteis miram (centro de massa do monstro).")]
        [SerializeField] private Transform projectileImpactPoint;

        // ── Constantes ─────────────────────────────────────────────────────
        private const float MOVING_UPDATE_INTERVAL = 0.1f;

        // ── SyncVars ───────────────────────────────────────────────────────
        [SyncVar(hook = nameof(OnCurrentHPChanged))] private float _currentHP;
        [SyncVar]                                    private float _maxHP;
        [SyncVar(hook = nameof(OnDeadChanged))]      private bool  _isDead;
        [SyncVar(hook = nameof(OnIsMovingChanged))]  private bool  _isMoving;
        [SyncVar] private int _spawnGeneration = 0;

        public int SpawnGeneration => _spawnGeneration;
        public bool DeathProcessed { get; private set; }

        // ── ITargetable ────────────────────────────────────────────────────
        public string  MonsterId   => monsterId;
        public string  DisplayName => monsterDisplayName;
        public int     Level       => level;
        public float   CurrentHP   => _currentHP;
        public float   MaxHP       => _maxHP;
        public bool    IsDead      => _isDead;
        public Vector3 Position    => transform.position;

        public Vector3 ImpactPoint => projectileImpactPoint != null
            ? projectileImpactPoint.position
            : transform.position + Vector3.up * 1f;

        public DerivedStats Stats { get; private set; }

        public void OnSelected()   { if (selectionIndicator) selectionIndicator.SetActive(true);  }
        public void OnDeselected() { if (selectionIndicator) selectionIndicator.SetActive(false); }

        // ── Componentes ────────────────────────────────────────────────────
        private NavMeshAgent         _agent;
        private Animator             _animator;
        private MonsterAI            _ai;
        private MonsterCombat        _combat;
        private MonsterDeathHandler  _deathHandler;
        private MonsterVisualFader   _fader;

        // ── Estado ─────────────────────────────────────────────────────────
        private bool    _serverResetDone;
        private float   _lastIsMovingUpdateTime;

        private GameObject            _monsterPrefab;
        private NetworkMonsterSpawner _spawner;
        private Vector3               _homePosition;
        private float                 _patrolRadius;

        // --- Hit Flash ---
        private List<Renderer> _renderers;
        private Coroutine      _hitFlashCoroutine;
        private readonly Color _hitColor = new Color(1f, 1f, 1f, 1f);

        // ══════════════════════════════════════════════════════════════════
        // Awake / lifecycle
        // ══════════════════════════════════════════════════════════════════

        private void Awake()
        {
            _agent        = GetComponent<NavMeshAgent>();
            _animator     = GetComponentInChildren<Animator>();
            _ai           = GetComponent<MonsterAI>();
            _combat       = GetComponent<MonsterCombat>();
            _deathHandler = GetComponent<MonsterDeathHandler>();
            _fader        = GetComponent<MonsterVisualFader>();

            // Cache de renderers para o Hit Flash
            if (visualRoot != null)
            {
                _renderers = new List<Renderer>(visualRoot.GetComponentsInChildren<Renderer>());
            }
            else
            {
                _renderers = new List<Renderer>(GetComponentsInChildren<Renderer>());
            }

            baseSTR = Mathf.Max(1, baseSTR);
            baseAGI = Mathf.Max(1, baseAGI);
            baseVIT = Mathf.Max(1, baseVIT);
            baseDEX = Mathf.Max(1, baseDEX);
            baseINT = Mathf.Max(1, baseINT);
            baseLUK = Mathf.Max(1, baseLUK);
            level   = Mathf.Max(1, level);

            Stats = StatsCalculator.CalculateForMonster(
                new BaseAttributes { STR = baseSTR, AGI = baseAGI, VIT = baseVIT,
                                     DEX = baseDEX, INT = baseINT, LUK = baseLUK },
                level);

            _homePosition = transform.position;
            _patrolRadius = 0f;
        }

        public override void OnStartClient()
        {
            if (_agent != null) _agent.enabled = false;

            if (selectionIndicator) selectionIndicator.SetActive(false);
            healthBarUI?.UpdateBar(_currentHP, _maxHP);
            if (visualRoot) visualRoot.SetActive(true);

            _fader?.OnStartClientReset();
        }

        [Server]
        public void SetupMonster(NetworkMonsterSpawner spawner, Vector3 homePos,
                                 float patrolRadius, GameObject prefab = null)
        {
            _spawner            = spawner;
            _homePosition       = homePos;
            _patrolRadius       = Mathf.Max(0f, patrolRadius);
            transform.position  = homePos;

            if (prefab != null) _monsterPrefab = prefab;
            else if (_monsterPrefab == null)
            {
                var ni = GetComponent<NetworkIdentity>();
                if (ni != null && NetworkClient.prefabs.TryGetValue(ni.assetId, out GameObject registered))
                    _monsterPrefab = registered;
            }

            _deathHandler?.ConfigureRespawn(_spawner, _monsterPrefab, _homePosition, _patrolRadius);

            StartCoroutine(ServerResetNextFrame());
        }

        [Server]
        private IEnumerator ServerResetNextFrame()
        {
            yield return null;
            if (!_serverResetDone) ServerReset();
        }

        [Server]
        private void ServerReset()
        {
            _serverResetDone = true;
            _maxHP           = Stats.MaxHP;
            _currentHP       = _maxHP;
            _isDead          = false;
            _isMoving        = false;
            DeathProcessed   = false;

            _spawnGeneration++;

            // Reativa colliders/layer/network transform
            Collider col = GetComponent<Collider>();
            if (col != null) col.enabled = true;

            int targetableLayer = LayerMask.NameToLayer("Targetable");
            if (targetableLayer >= 0) gameObject.layer = targetableLayer;

            var nt = GetComponent<NetworkTransformUnreliable>();
            if (nt != null) nt.enabled = true;

            if (_agent != null)
            {
                _agent.enabled          = true;
                _agent.speed            = Stats.MoveSpeed;
                _agent.angularSpeed     = 360f;
                _agent.acceleration     = 12f;
                _agent.stoppingDistance = 0.5f;
                _agent.velocity         = Vector3.zero;

                if (_agent.isOnNavMesh) _agent.Warp(_homePosition);
                else                    transform.position = _homePosition;
            }
            else
            {
                transform.position = _homePosition;
            }

            _ai?.ServerSetupAndStart(_homePosition, _patrolRadius);
            _combat?.ServerSetup();
        }

        // ══════════════════════════════════════════════════════════════════
        // Update — apenas orquestração
        // ══════════════════════════════════════════════════════════════════

        private void Update()
        {
            if (!isServer) return;
            if (!_serverResetDone) { ServerReset(); return; }
            if (_isDead) return;

            // Sincroniza estado de movimento com frequência reduzida
            if (Time.time - _lastIsMovingUpdateTime >= MOVING_UPDATE_INTERVAL)
            {
                _lastIsMovingUpdateTime = Time.time;
                bool moving = _agent != null && _agent.velocity.sqrMagnitude > 0.05f;
                if (moving != _isMoving) _isMoving = moving;
            }

            _ai?.ServerTick(Time.deltaTime);
        }

        // ══════════════════════════════════════════════════════════════════
        // API server-side usada pelos componentes
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public void ApplyDamageInternal(float dmg)
        {
            if (DeathProcessed || _isDead) return;
            if (dmg <= 0f) return;

            _currentHP = Mathf.Max(0f, _currentHP - dmg);
            
            // Efeito visual de hit (flash)
            RpcPlayHitEffects();

            if (_currentHP <= 0f) ServerDie();
        }

        [Server]
        public void ServerHealPercent(float percent)
        {
            if (_isDead || DeathProcessed) return;
            _currentHP = Mathf.Min(_maxHP, _currentHP + _maxHP * percent);
        }

        [Server]
        private void ServerDie()
        {
            if (DeathProcessed) return;

            Vector3 deathPos = transform.position;
            DeathProcessed   = true;
            _isDead          = true;
            _isMoving        = false;

            _ai?.ServerStop();

            _combat?.ServerDistributeRewardsAndDrops(deathPos);
            _combat?.ServerStopAndClear();

            RpcOnDied(deathPos);

            if (_deathHandler != null)
                StartCoroutine(_deathHandler.RunDeathSequence(deathPos));
        }

        // Cmds delegados — mantidos por compatibilidade com chamadores antigos
        [Server]
        public void ServerTakeProjectileDamage(uint shooterNetId, float dmg, bool crit)
            => _combat?.ServerTakeProjectileDamage(shooterNetId, dmg, crit);

        public void CmdRequestSkill(uint attackerNetId, int skillIndex, bool isPhysical)
            => _combat?.CmdRequestSkill(attackerNetId, skillIndex, isPhysical);

        public void CmdBasicAttack(uint attackerNetId, float clientAttackRange)
            => _combat?.CmdBasicAttack(attackerNetId, clientAttackRange);

        // ══════════════════════════════════════════════════════════════════
        // ClientRpcs visuais (chamados pelo MonsterCombat)
        // ══════════════════════════════════════════════════════════════════

        [ClientRpc]
        public void RpcShowDamageFloating(float dmg, bool crit, Vector3 pos)
        {
            if (Application.isBatchMode) return;
            FloatingTextManager.Instance?.Show(
                crit ? $"CRÍTICO! {dmg:0}" : $"{dmg:0}",
                pos + Vector3.up, crit ? Color.yellow : Color.white);
        }

        [ClientRpc]
        public void RpcShowMiss(Vector3 pos)
        {
            if (Application.isBatchMode) return;
            FloatingTextManager.Instance?.Show("MISS", pos + Vector3.up * 0.5f, Color.gray);
        }

        [ClientRpc]
        public void RpcPlayAnim(string trigger)
        {
            if (Application.isBatchMode) return;
            _animator?.SetTrigger(trigger);
        }

        [ClientRpc]
        public void RpcShowDamageTakenOnPlayer(float dmg, bool crit, Vector3 playerPos)
        {
            if (Application.isBatchMode) return;
            FloatingTextManager.Instance?.Show(
                crit ? $"-{dmg:0} CRÍTICO!" : $"-{dmg:0}",
                playerPos + Vector3.up * 1.8f,
                crit ? new Color(1f, 0.3f, 0f) : new Color(1f, 0.2f, 0.2f));
        }

        [ClientRpc]
        private void RpcOnDied(Vector3 pos)
        {
            if (Application.isBatchMode) return;

            OnDeselected();
            if (healthBarUI != null) healthBarUI.gameObject.SetActive(false);

            var localPlayerGO = NetworkClient.localPlayer;
            if (localPlayerGO != null)
            {
                var playerEntity = localPlayerGO.GetComponent<PlayerEntity>();
                if (playerEntity != null
                    && playerEntity.CurrentTarget is NetworkMonsterEntity current
                    && current == this)
                {
                    UIManager.Instance?.ClearTargetPanel();
                    playerEntity.ClearTarget();
                }
            }

            FloatingTextManager.Instance?.Show("Morto!", pos + Vector3.up, Color.red);
            _fader?.BeginFade();
        }

        [ClientRpc]
        public void RpcPlayHitEffects()
        {
            if (Application.isBatchMode) return;
            if (_hitFlashCoroutine != null) StopCoroutine(_hitFlashCoroutine);
            _hitFlashCoroutine = StartCoroutine(HitFlashRoutine());
        }

        private IEnumerator HitFlashRoutine()
        {
            if (_renderers == null || _renderers.Count == 0) yield break;

            // --- Knockback Visual (Client-side) ---
            Vector3 originalVisualPos = visualRoot != null ? visualRoot.transform.localPosition : Vector3.zero;
            if (visualRoot != null)
            {
                // Move o visual um pouco para trás e para cima
                visualRoot.transform.localPosition = originalVisualPos - Vector3.forward * 0.1f + Vector3.up * 0.05f;
            }

            // Armazena cores originais e seta para branco
            Dictionary<Renderer, Color[]> originalColors = new Dictionary<Renderer, Color[]>();
            foreach (var r in _renderers)
            {
                if (r == null) continue;
                var mats = r.materials;
                Color[] colors = new Color[mats.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i].HasProperty("_Color"))
                    {
                        colors[i] = mats[i].color;
                        mats[i].color = _hitColor;
                    }
                    else if (mats[i].HasProperty("_BaseColor"))
                    {
                        colors[i] = mats[i].GetColor("_BaseColor");
                        mats[i].SetColor("_BaseColor", _hitColor);
                    }
                }
                originalColors[r] = colors;
            }

            yield return new WaitForSeconds(0.08f);

            // Restaura posição visual
            if (visualRoot != null) visualRoot.transform.localPosition = originalVisualPos;

            // Restaura cores originais
            foreach (var kvp in originalColors)
            {
                var r = kvp.Key;
                if (r == null) continue;
                var mats = r.materials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i].HasProperty("_Color"))
                        mats[i].color = kvp.Value[i];
                    else if (mats[i].HasProperty("_BaseColor"))
                        mats[i].SetColor("_BaseColor", kvp.Value[i]);
                }
            }

            _hitFlashCoroutine = null;
        }

        // ══════════════════════════════════════════════════════════════════
        // SyncVar hooks
        // ══════════════════════════════════════════════════════════════════

        private void OnCurrentHPChanged(float _, float v)
        {
            if (Application.isBatchMode) return;

            healthBarUI?.UpdateBar(v, _maxHP);

            var localPlayerGO = NetworkClient.localPlayer;
            if (localPlayerGO != null)
            {
                var pe = localPlayerGO.GetComponent<PlayerEntity>();
                if (pe != null
                    && pe.CurrentTarget is NetworkMonsterEntity current
                    && current == this)
                    UIManager.Instance?.RefreshTargetPanel(this);
            }
        }

        private void OnDeadChanged(bool _, bool dead)
        {
            if (dead && _agent != null) _agent.enabled = false;
        }

        private void OnIsMovingChanged(bool _, bool moving)
        {
            if (Application.isBatchMode) return;
            _animator?.SetBool("IsMoving", moving);
        }
    }
}
