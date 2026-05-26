using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Data;
using RPG.UI;
using RPG.Managers;
using RPG.Character;
using RPG.Combat;

namespace RPG.Network
{
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkIdentity))]
    [RequireComponent(typeof(NetworkInventory))]
    [RequireComponent(typeof(PlayerCooldownTracker))]
    [RequireComponent(typeof(PlayerRegenLoop))]
    public class NetworkPlayer : NetworkBehaviour, ITargetable
    {
        public static readonly HashSet<NetworkPlayer> All = new HashSet<NetworkPlayer>();

        private const float SAVE_INTERVAL                = 30f; // Reduzido de 60s para 30s para maior segurança
        private const float ALLOCATE_MIN_INTERVAL        = 0.3f;
        private const float REGEN_DISPLAY_THRESHOLD      = 1f;
        private const int   MAX_FREE_POINTS              = CharacterData.MAX_LEVEL * CharacterData.POINTS_PER_LEVEL_UP;

        private const float AGENT_ACCELERATION   = 60f;
        private const float AGENT_ANGULAR_SPEED  = 720f;
        private const float AGENT_STOPPING_DIST  = 0.15f;
        private const float AGENT_MIN_SPEED      = 3f;
        private const float AGENT_MAX_SPEED      = 7f;

        public struct PlayerInitData
        {
            public string CharName;
            public int    Race;
            public int    Level;
            public long   Exp;
            public long   ExpToNext;
            public int    FreePoints;
            public int    AllocSTR, AllocAGI, AllocVIT, AllocDEX, AllocINT, AllocLUK;
            public int    BaseSTR,  BaseAGI,  BaseVIT,  BaseDEX,  BaseINT,  BaseLUK;
            public float  CurHP, CurMP;
        }

        // ── SyncVars ───────────────────────────────────────────────────────
        [SyncVar(hook = nameof(OnNetNameChanged))]       public string CharacterName         = "...";
        [SyncVar(hook = nameof(OnRaceStrChanged))]       public string RaceStr               = "Paulista";
        [SyncVar(hook = nameof(OnNetLevelChanged))]      public int    Level                 = 1;

        [SyncVar(hook = nameof(OnNetMaxHPChanged))]      public float  MaxHP                 = 1f;
        [SyncVar(hook = nameof(OnNetHPChanged))]         public float  CurrentHP             = 0f;
        [SyncVar(hook = nameof(OnNetMaxMPChanged))]      public float  MaxMP                 = 1f;
        [SyncVar(hook = nameof(OnNetMPChanged))]         public float  CurrentMP             = 0f;

        [SyncVar(hook = nameof(OnNetMovingChanged))]     public bool   IsMoving              = false;
        [SyncVar(hook = nameof(OnNetExpChanged))]        public long   Experience            = 0;
        [SyncVar(hook = nameof(OnNetExpToNextChanged))]  public long   ExperienceToNextLevel = 100;
        [SyncVar(hook = nameof(OnNetFreePointsChanged))] public int    FreeAttributePoints   = 0;
        [SyncVar(hook = nameof(OnStatsVersionChanged))]  public int    StatsVersion          = 0;
        [SyncVar]                                        public int    PartyId               = 0; // 0 = Sem grupo

        [SyncVar(hook = nameof(OnAllocChanged))] public int AllocatedSTR = 0;
        [SyncVar(hook = nameof(OnAllocChanged))] public int AllocatedAGI = 0;
        [SyncVar(hook = nameof(OnAllocChanged))] public int AllocatedVIT = 0;
        [SyncVar(hook = nameof(OnAllocChanged))] public int AllocatedDEX = 0;
        [SyncVar(hook = nameof(OnAllocChanged))] public int AllocatedINT = 0;
        [SyncVar(hook = nameof(OnAllocChanged))] public int AllocatedLUK = 0;

        [SyncVar] public int BaseSTR = 10;
        [SyncVar] public int BaseAGI = 10;
        [SyncVar] public int BaseVIT = 10;
        [SyncVar] public int BaseDEX = 10;
        [SyncVar] public int BaseINT = 10;
        [SyncVar] public int BaseLUK = 10;

        string  ITargetable.DisplayName => CharacterName;
        float   ITargetable.CurrentHP   => CurrentHP;
        float   ITargetable.MaxHP       => MaxHP;
        bool    ITargetable.IsDead      => Dead;
        Vector3 ITargetable.Position    => transform.position;

        public void OnSelected()   { if (_selectionIndicator) _selectionIndicator.SetActive(true);  }
        public void OnDeselected() { if (_selectionIndicator) _selectionIndicator.SetActive(false); }

        [Header("Visuals")]
        [SerializeField] private GameObject            _selectionIndicator;
        [SerializeField] private TMPro.TMP_Text        _nameTagText;
        [SerializeField] private UnityEngine.UI.Slider _hpBarSlider;

        [Header("Respawn Points")]
        [SerializeField] private Transform[] _respawnPoints;

        // ── Componentes ────────────────────────────────────────────────────
        private NavMeshAgent           _agent;
        private Animator               _animator;
        private PlayerEntity           _playerEntity;
        private NetworkInventory       _inventory;
        private RPG.Quest.QuestManager _questManager;
        private PlayerCooldownTracker  _cooldowns;
        private PlayerRegenLoop        _regenLoop;

        // ── Estado servidor ───────────────────────────────────────────────
        private CharacterData _serverCharData;
        private DerivedStats  _serverStats;
        private string        _serverAccountUsername;
        private float         _autoSaveTimer;
        private float         _lastAllocateTime         = -999f;
        private bool          _isDirty;

        public DerivedStats ServerStats => _serverStats;

        // ── Estado cliente ────────────────────────────────────────────────
        private CharacterRace _cachedRace = CharacterRace.Paulista;
        private bool          _clientInitialized;
        private bool          _pendingClientInit;
        private CharacterData _pendingInitData;
        private bool          _allocDirty;
        private bool          _equipDirty;
        private float         _lastMovingCmdTime;
        private const float MOVING_CMD_INTERVAL = 0.1f;

        public bool Dead => CurrentHP <= 0f;

        // ══════════════════════════════════════════════════════════════════
        // Lifecycle
        // ══════════════════════════════════════════════════════════════════

        private void Awake()
        {
            _agent        = GetComponent<NavMeshAgent>();
            _animator     = GetComponentInChildren<Animator>();
            _playerEntity = GetComponent<PlayerEntity>();
            _inventory    = GetComponent<NetworkInventory>();
            _questManager = GetComponent<RPG.Quest.QuestManager>();
            _cooldowns    = GetComponent<PlayerCooldownTracker>();
            _regenLoop    = GetComponent<PlayerRegenLoop>();
        }

        public override void OnStartServer()
        {
            All.Add(this);
            _autoSaveTimer = SAVE_INTERVAL;
            _cooldowns?.ServerReset();

            // Conecta callbacks de regen
            _regenLoop.SnapshotProvider = BuildRegenSnapshot;
            _regenLoop.ApplyRegen       = ApplyRegenValues;
            _regenLoop.OnRegenTick      += OnServerRegenTick;
        }

        public override void OnStopServer()
        {
            All.Remove(this);
            _regenLoop?.Stop();
            if (_regenLoop != null)
                _regenLoop.OnRegenTick -= OnServerRegenTick;

            if (_serverCharData != null && !string.IsNullOrEmpty(_serverAccountUsername))
                ServerSaveCharacterForced();
        }

        public override void OnStartClient()
        {
            if (_nameTagText        != null) _nameTagText.text = CharacterName;
            if (_selectionIndicator != null) _selectionIndicator.SetActive(false);
            if (!isLocalPlayer && _agent != null) _agent.enabled = false;

            UpdateCachedRace();
        }

        public override void OnStopClient()
        {
            _clientInitialized = false;
            _pendingClientInit = false;
            _pendingInitData   = null;
            _allocDirty        = false;
            _equipDirty        = false;

            if (_inventory != null)
                _inventory.OnEquipmentChanged -= OnClientEquipmentChanged;
        }

        public override void OnStartLocalPlayer()
        {
            if (_agent != null) _agent.enabled = true;

            if (_inventory != null)
                _inventory.OnEquipmentChanged += OnClientEquipmentChanged;

            if (_pendingClientInit && _pendingInitData != null)
            {
                var data = _pendingInitData;
                _pendingClientInit = false;
                _pendingInitData   = null;
                StartCoroutine(DelayedClientInit(data));
            }
        }

        private void Update()
        {
            if (isServer) ServerUpdate();
            if (!isLocalPlayer || Dead) return;

            ClientMovingUpdate();

            if (_allocDirty)
            {
                _allocDirty = false;
                ApplyAllocatedDataToEntity();
            }
            if (_equipDirty)
            {
                _equipDirty = false;
                ApplyEquipmentDataToEntity();
            }
        }

        [Server]
        private void ServerUpdate()
        {
            _autoSaveTimer -= Time.deltaTime;
            if (_autoSaveTimer <= 0f)
            {
                _autoSaveTimer = SAVE_INTERVAL;
                if (_isDirty) ServerSaveCharacterForced();
            }

            _cooldowns?.ServerTick();
        }

        private void ClientMovingUpdate()
        {
            if (_agent == null || !_agent.enabled) return;
            bool moving = _agent.velocity.sqrMagnitude > 0.05f;
            if (moving != IsMoving && Time.time - _lastMovingCmdTime >= MOVING_CMD_INTERVAL)
            {
                _lastMovingCmdTime = Time.time;
                CmdSetMoving(moving);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // NavMeshAgent
        // ══════════════════════════════════════════════════════════════════

        [Server]
        private void ConfigureServerAgent()
        {
            if (_agent == null || _serverStats == null) return;

            _agent.speed            = Mathf.Clamp(_serverStats.MoveSpeed, AGENT_MIN_SPEED, AGENT_MAX_SPEED);
            _agent.acceleration     = AGENT_ACCELERATION;
            _agent.angularSpeed     = AGENT_ANGULAR_SPEED;
            _agent.autoBraking      = false;
            _agent.stoppingDistance = AGENT_STOPPING_DIST;
        }

        // ══════════════════════════════════════════════════════════════════
        // Inicialização
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public void ServerInitialize(CharacterData charData, string accountUsername)
        {
            if (charData == null || string.IsNullOrEmpty(accountUsername))
            {
                Debug.LogError("[NetworkPlayer] ServerInitialize: dados inválidos.");
                return;
            }
            if (string.IsNullOrEmpty(charData.CharacterId))
            {
                Debug.LogError("[NetworkPlayer] ServerInitialize: CharacterId vazio.");
                return;
            }

            _serverAccountUsername = accountUsername;
            _serverCharData        = charData;
            _cachedRace            = charData.Race;

            CharacterName = charData.CharacterName ?? "Player";
            RaceStr       = charData.Race.ToString();
            Level         = Mathf.Clamp(charData.Level, 1, CharacterData.MAX_LEVEL);

            Experience            = Math.Max(0L, charData.Experience);
            ExperienceToNextLevel = Math.Max(0L, charData.ExperienceToNextLevel);

            int freePoints = Math.Max(0, Math.Min(charData.FreeAttributePoints, MAX_FREE_POINTS));
            FreeAttributePoints = freePoints;
            charData.FreeAttributePoints = freePoints;

            int allocLimit = CharacterData.MAX_ALLOCATED_PER_STAT;
            AllocatedSTR = Math.Max(0, Math.Min(charData.AllocatedSTR, allocLimit));
            AllocatedAGI = Math.Max(0, Math.Min(charData.AllocatedAGI, allocLimit));
            AllocatedVIT = Math.Max(0, Math.Min(charData.AllocatedVIT, allocLimit));
            AllocatedDEX = Math.Max(0, Math.Min(charData.AllocatedDEX, allocLimit));
            AllocatedINT = Math.Max(0, Math.Min(charData.AllocatedINT, allocLimit));
            AllocatedLUK = Math.Max(0, Math.Min(charData.AllocatedLUK, allocLimit));

            charData.AllocatedSTR = AllocatedSTR;
            charData.AllocatedAGI = AllocatedAGI;
            charData.AllocatedVIT = AllocatedVIT;
            charData.AllocatedDEX = AllocatedDEX;
            charData.AllocatedINT = AllocatedINT;
            charData.AllocatedLUK = AllocatedLUK;

            BaseSTR = charData.BaseAttributes?.STR ?? 10;
            BaseAGI = charData.BaseAttributes?.AGI ?? 10;
            BaseVIT = charData.BaseAttributes?.VIT ?? 10;
            BaseDEX = charData.BaseAttributes?.DEX ?? 10;
            BaseINT = charData.BaseAttributes?.INT ?? 10;
            BaseLUK = charData.BaseAttributes?.LUK ?? 10;

            _inventory?.ServerLoadFromDatabase(charData.CharacterId);
            _inventory?.ServerLoadGemLoadout(charData.CharacterId);
            _inventory?.ServerLoadEquippedFromDatabase(charData.CharacterId);

            _questManager?.ServerLoadFromDatabase(charData.CharacterId, _serverAccountUsername);

            charData.EquipmentBonuses = _inventory != null
                ? _inventory.BuildEquipmentBonuses()
                : new EquipmentBonuses();

            _serverStats = charData.GetDerivedStats();

            MaxHP     = Mathf.Min(_serverStats.MaxHP, GameConstants.Combat.MAX_HP);
            MaxMP     = Mathf.Min(_serverStats.MaxMP, GameConstants.Combat.MAX_MP);
            CurrentHP = (charData.CurrentHP > 0f && charData.CurrentHP <= MaxHP) ? charData.CurrentHP : MaxHP;
            CurrentMP = (charData.CurrentMP > 0f && charData.CurrentMP <= MaxMP) ? charData.CurrentMP : MaxMP;

            StatsVersion++;

            var savedPos = new Vector3(charData.PosX, charData.PosY, charData.PosZ);
            if (savedPos.sqrMagnitude > 0.01f)
            {
                transform.position = savedPos;
                if (_agent != null && _agent.isOnNavMesh) _agent.Warp(savedPos);
            }

            ConfigureServerAgent();
            _regenLoop?.ServerStart();

            Debug.Log($"[Server] {charData.CharacterName} Lv{Level} inicializado.");
            StartCoroutine(SendInitRpcDelayed(charData));
        }

        [Server]
        private IEnumerator SendInitRpcDelayed(CharacterData charData)
        {
            yield return null;
            yield return null;
            yield return null;

            RpcInitializeLocalPlayer(new PlayerInitData
            {
                CharName   = charData.CharacterName,
                Race       = (int)charData.Race,
                Level      = charData.Level,
                Exp        = charData.Experience,
                ExpToNext  = charData.ExperienceToNextLevel,
                FreePoints = charData.FreeAttributePoints,
                AllocSTR   = charData.AllocatedSTR,
                AllocAGI   = charData.AllocatedAGI,
                AllocVIT   = charData.AllocatedVIT,
                AllocDEX   = charData.AllocatedDEX,
                AllocINT   = charData.AllocatedINT,
                AllocLUK   = charData.AllocatedLUK,
                BaseSTR    = charData.BaseAttributes.STR,
                BaseAGI    = charData.BaseAttributes.AGI,
                BaseVIT    = charData.BaseAttributes.VIT,
                BaseDEX    = charData.BaseAttributes.DEX,
                BaseINT    = charData.BaseAttributes.INT,
                BaseLUK    = charData.BaseAttributes.LUK,
                CurHP      = CurrentHP,
                CurMP      = CurrentMP
            });
        }

        // ══════════════════════════════════════════════════════════════════
        // Equipamento / stats
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public void ServerOnEquipmentChanged()
        {
            if (_serverCharData == null || _inventory == null) return;

            _serverCharData.EquipmentBonuses = _inventory.BuildEquipmentBonuses();
            ServerRecalculateStats();
            ServerSaveCharacterForced();
        }

        [Server]
        private void ServerRecalculateStats()
        {
            _serverStats = _serverCharData.GetDerivedStats();

            MaxHP = Mathf.Min(_serverStats.MaxHP, GameConstants.Combat.MAX_HP);
            MaxMP = Mathf.Min(_serverStats.MaxMP, GameConstants.Combat.MAX_MP);

            if (CurrentHP > MaxHP) CurrentHP = MaxHP;
            if (CurrentMP > MaxMP) CurrentMP = MaxMP;

            _serverCharData.CurrentHP = CurrentHP;
            _serverCharData.CurrentMP = CurrentMP;

            ConfigureServerAgent();
            StatsVersion++;
        }

        // ══════════════════════════════════════════════════════════════════
        // Regen — callbacks fornecidos ao PlayerRegenLoop
        // ══════════════════════════════════════════════════════════════════

        private PlayerRegenLoop.RegenSnapshot BuildRegenSnapshot()
        {
            return new PlayerRegenLoop.RegenSnapshot
            {
                IsDead    = Dead,
                CurrentHP = CurrentHP, MaxHP = MaxHP,
                CurrentMP = CurrentMP, MaxMP = MaxMP,
                Stats     = _serverStats
            };
        }

        [Server]
        private void ApplyRegenValues(float newHP, float newMP)
        {
            CurrentHP = newHP;
            CurrentMP = newMP;
            if (_serverCharData != null)
            {
                _serverCharData.CurrentHP = CurrentHP;
                _serverCharData.CurrentMP = CurrentMP;
            }
        }

        private void OnServerRegenTick(float hpRestored, float mpRestored)
        {
            RpcShowRegenTick(hpRestored, mpRestored);
        }

        // ══════════════════════════════════════════════════════════════════
        // Commands
        // ══════════════════════════════════════════════════════════════════

        [Command]
        public void CmdSetMoving(bool moving)
        {
            if (connectionToClient == null) return;
            if (Dead && moving) return;
            IsMoving = moving;
        }

        [Command]
        public void CmdAllocateAttribute(int attributeIndex)
        {
            if (connectionToClient == null) return;

            if (Time.time - _lastAllocateTime < ALLOCATE_MIN_INTERVAL)
            {
                RpcAllocateRejected("Aguarde um pouco antes de alocar novamente.");
                return;
            }

            if (FreeAttributePoints <= 0 || _serverCharData == null)
            {
                RpcAllocateRejected("Você não tem pontos suficientes.");
                return;
            }

            if (attributeIndex < 0 || attributeIndex > 5)
            {
                RpcAllocateRejected("Atributo inválido.");
                return;
            }

            _lastAllocateTime = Time.time;

            if (IsAllocationLimitExceeded(attributeIndex))
            {
                Debug.LogWarning($"[Security] {CharacterName} tentou alocar atributo {attributeIndex} além do limite.");
                RpcAllocateRejected("Limite de pontos para este atributo atingido.");
                return;
            }

            FreeAttributePoints--;
            _serverCharData.FreeAttributePoints--;

            switch (attributeIndex)
            {
                case 0: AllocatedSTR++; _serverCharData.AllocatedSTR++; break;
                case 1: AllocatedAGI++; _serverCharData.AllocatedAGI++; break;
                case 2: AllocatedVIT++; _serverCharData.AllocatedVIT++; break;
                case 3: AllocatedDEX++; _serverCharData.AllocatedDEX++; break;
                case 4: AllocatedINT++; _serverCharData.AllocatedINT++; break;
                case 5: AllocatedLUK++; _serverCharData.AllocatedLUK++; break;
            }

            ServerRecalculateStats();
            MarkDirty();
        }

        private bool IsAllocationLimitExceeded(int attributeIndex)
        {
            int limit = CharacterData.MAX_ALLOCATED_PER_STAT;
            return attributeIndex switch
            {
                0 => _serverCharData.AllocatedSTR >= limit,
                1 => _serverCharData.AllocatedAGI >= limit,
                2 => _serverCharData.AllocatedVIT >= limit,
                3 => _serverCharData.AllocatedDEX >= limit,
                4 => _serverCharData.AllocatedINT >= limit,
                5 => _serverCharData.AllocatedLUK >= limit,
                _ => true
            };
        }

        [Command]
        public void CmdRequestRespawn()
        {
            if (connectionToClient == null) return;
            if (!Dead) return;
            ServerRespawn();
        }

        [Command]
        public void CmdRequestSelfSkill(int skillIndex)
        {
            if (connectionToClient == null) return;

            if (Dead || _serverStats == null) return;
            if (skillIndex < 0 || skillIndex >= NetworkInventory.GEM_SLOT_COUNT) return;

            var skill = _inventory?.GetEquippedSkill(skillIndex);
            if (skill == null)
            {
                RpcSkillRejected(skillIndex, "Nenhuma joia equipada neste slot.");
                return;
            }

            if (skill.Target != Combat.SkillTarget.Self
                && skill.Type != Combat.SkillType.Heal
                && skill.Type != Combat.SkillType.Buff)
            {
                RpcSkillRejected(skillIndex, "Esta skill precisa de um alvo.");
                return;
            }

            if (!ServerCheckAndSetCooldown(skillIndex, skill.Cooldown))
            {
                if (_cooldowns != null && _cooldowns.TryGetSkillEndTime(skillIndex, out float endTime))
                    RpcSkillRejected(skillIndex, $"{skill.Name}: aguarde {endTime - Time.time:0.0}s");
                return;
            }

            if (CurrentMP < skill.ManaCost)
            {
                RpcSkillRejected(skillIndex, "MP insuficiente!");
                return;
            }

            ServerConsumeMP(skill.ManaCost);

            if (skill.Type == Combat.SkillType.Heal)
            {
                float heal = Mathf.Max(10f, _serverStats.MATK * skill.AtkMultiplier);
                heal = SanitizeAmount(heal);
                float before = CurrentHP;
                CurrentHP    = Mathf.Min(MaxHP, CurrentHP + heal);
                float healed = CurrentHP - before;

                if (_serverCharData != null) _serverCharData.CurrentHP = CurrentHP;
                if (healed > 0f) RpcShowHeal(healed);
            }

            if (!string.IsNullOrEmpty(skill.AnimTrigger))
                RpcPlayAnimation(skill.AnimTrigger);

            RpcSkillConfirmed(skillIndex, skill.Cooldown);
        }

        // ══════════════════════════════════════════════════════════════════
        // Métodos do servidor
        // ══════════════════════════════════════════════════════════════════

        private static float SanitizeAmount(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return 0f;
            return Mathf.Max(0f, v);
        }

        [Server]
        public void ServerApplyDamage(float dmg)
        {
            if (Dead) return;
            dmg = SanitizeAmount(dmg);
            if (dmg <= 0f) return;

            _regenLoop?.NotifyDamageTaken();
            CurrentHP = Mathf.Max(0f, CurrentHP - dmg);
            if (_serverCharData != null) _serverCharData.CurrentHP = CurrentHP;
            if (CurrentHP <= 0f) ServerDie();
        }

        [Server]
        public void ServerApplyDamageWithFeedback(float dmg)
        {
            if (Dead) return;
            dmg = SanitizeAmount(dmg);
            if (dmg <= 0f) return;

            _regenLoop?.NotifyDamageTaken();
            float before    = CurrentHP;
            CurrentHP       = Mathf.Max(0f, CurrentHP - dmg);
            float actual    = before - CurrentHP;

            if (_serverCharData != null) _serverCharData.CurrentHP = CurrentHP;
            if (actual > 0f) RpcShowDamageTaken(actual);
            if (CurrentHP <= 0f) ServerDie();
        }

        [Server]
        public void ServerApplyHeal(float amount)
        {
            if (Dead) return;
            amount = SanitizeAmount(amount);
            if (amount <= 0f) return;

            float before = CurrentHP;
            CurrentHP    = Mathf.Min(MaxHP, CurrentHP + amount);
            float healed = CurrentHP - before;

            if (_serverCharData != null) _serverCharData.CurrentHP = CurrentHP;
            if (healed > 0f) RpcShowHeal(healed);
        }

        [Server]
        public void ServerRestoreMP(float amount)
        {
            if (Dead) return;
            amount = SanitizeAmount(amount);
            if (amount <= 0f) return;

            CurrentMP = Mathf.Min(MaxMP, CurrentMP + amount);
            if (_serverCharData != null) _serverCharData.CurrentMP = CurrentMP;
        }

        [Server]
        public void ServerConsumeMP(float amount)
        {
            amount = SanitizeAmount(amount);
            CurrentMP = Mathf.Max(0f, CurrentMP - amount);
            if (_serverCharData != null) _serverCharData.CurrentMP = CurrentMP;
        }

        [Server]
        public bool ServerCheckAndSetCooldown(int skillIndex, float cooldownDuration)
        {
            return _cooldowns != null
                && _cooldowns.TryCheckAndSetSkill(skillIndex, cooldownDuration,
                    GameConstants.Server.MAX_SKILL_COOLDOWN_SECONDS);
        }

        [Server]
        public bool ServerCheckAndSetCooldownLong(long cooldownKey, float cooldownDuration)
        {
            return _cooldowns != null
                && _cooldowns.TryCheckAndSetBasicAttack(cooldownKey, cooldownDuration,
                    GameConstants.Server.MAX_SKILL_COOLDOWN_SECONDS);
        }

        [Server]
        public void ServerGrantExp(long amount)
        {
            if (_serverCharData == null || amount <= 0) return;
            amount = Math.Min(amount, GameConstants.Server.MAX_XP_PER_GRANT);

            bool leveledUp = _serverCharData.AddExperience(amount);

            Experience            = _serverCharData.Experience;
            ExperienceToNextLevel = _serverCharData.ExperienceToNextLevel;
            Level                 = _serverCharData.Level;

            FreeAttributePoints                  = Mathf.Min(_serverCharData.FreeAttributePoints, MAX_FREE_POINTS);
            _serverCharData.FreeAttributePoints  = FreeAttributePoints;

            if (leveledUp)
            {
                ServerRecalculateStats();
                CurrentHP = MaxHP;
                CurrentMP = MaxMP;
                _serverCharData.CurrentHP = MaxHP;
                _serverCharData.CurrentMP = MaxMP;
                _regenLoop?.ServerStart();
                Debug.Log($"[Server] {CharacterName} → Lv {Level}!");

                _questManager?.NotifyLevelUp(Level);
            }

            DatabaseManager.Instance?.LogEconomy(_serverCharData.CharacterId, "exp_gain", amount);

            // Se subiu de nível ou ganhou muito XP (ex: mais de 10% do necessário para o próximo), salva forçado
            bool largeExpGain = amount > (ExperienceToNextLevel * 0.1f);

            if (leveledUp || largeExpGain) ServerSaveCharacterForced();
            else                           MarkDirty();

            RpcOnExpGained(amount, leveledUp);
        }

        [Server] private void MarkDirty() => _isDirty = true;

        [Server]
        public void ServerSaveCharacterForced()
        {
            if (_serverCharData == null) return;
            if (string.IsNullOrEmpty(_serverCharData.CharacterId)) return;
            if (string.IsNullOrEmpty(_serverAccountUsername)) return;

            _serverCharData.CurrentHP = CurrentHP;
            _serverCharData.CurrentMP = CurrentMP;
            _serverCharData.PosX      = transform.position.x;
            _serverCharData.PosY      = transform.position.y;
            _serverCharData.PosZ      = transform.position.z;

            DatabaseManager.Instance?.SaveCharacter(_serverCharData, _serverAccountUsername);
            _inventory?.ServerSaveAll(_serverCharData.CharacterId, _serverAccountUsername);
            _questManager?.ServerSaveAll();
            _isDirty = false;
        }

        [Server] public void ServerSaveCharacter() => ServerSaveCharacterForced();

        public CharacterRace GetRaceEnum() => _cachedRace;

        private void UpdateCachedRace()
        {
            if (System.Enum.TryParse<CharacterRace>(RaceStr, out var race))
                _cachedRace = race;
            else
                _cachedRace = CharacterRace.Paulista;
        }

        // ══════════════════════════════════════════════════════════════════
        // ClientRpcs
        // ══════════════════════════════════════════════════════════════════

        [ClientRpc]
        private void RpcInitializeLocalPlayer(PlayerInitData d)
        {
            if (!isLocalPlayer) return;

            var data = new CharacterData
            {
                CharacterName         = d.CharName,
                Race                  = (CharacterRace)d.Race,
                Level                 = d.Level,
                Experience            = d.Exp,
                ExperienceToNextLevel = d.ExpToNext,
                FreeAttributePoints   = d.FreePoints,
                AllocatedSTR          = d.AllocSTR,
                AllocatedAGI          = d.AllocAGI,
                AllocatedVIT          = d.AllocVIT,
                AllocatedDEX          = d.AllocDEX,
                AllocatedINT          = d.AllocINT,
                AllocatedLUK          = d.AllocLUK,
                CurrentHP             = d.CurHP,
                CurrentMP             = d.CurMP,
                BaseAttributes = new BaseAttributes
                {
                    STR = d.BaseSTR, AGI = d.BaseAGI, VIT = d.BaseVIT,
                    DEX = d.BaseDEX, INT = d.BaseINT, LUK = d.BaseLUK
                },
                EquipmentBonuses = _inventory != null
                    ? _inventory.BuildEquipmentBonuses()
                    : new EquipmentBonuses()
            };

            if (_playerEntity == null)
            {
                _pendingClientInit = true;
                _pendingInitData   = data;
                return;
            }

            if (_clientInitialized) return;
            _clientInitialized = true;
            StartCoroutine(DelayedClientInit(data));
        }

        private IEnumerator DelayedClientInit(CharacterData data)
        {
            yield return null;

            if (_playerEntity == null)
            {
                _playerEntity = GetComponent<PlayerEntity>();
                if (_playerEntity == null)
                {
                    Debug.LogError("[NetworkPlayer] PlayerEntity não encontrado.");
                    yield break;
                }
            }

            if (_inventory != null)
                data.EquipmentBonuses = _inventory.BuildEquipmentBonuses();

            _playerEntity.InitializeFromServer(data);
            UIManager.Instance?.BindLocalPlayer(_playerEntity);
            AttributeWindowUI.Instance?.BindPlayer(_playerEntity);
        }

        [ClientRpc]
        private void RpcPlayerDied()
        {
            if (_animator != null) _animator.SetBool("IsDead", true);

            if (!isLocalPlayer) return;

            if (_agent != null) { _agent.ResetPath(); _agent.isStopped = true; }
            GetComponent<NetworkPlayerController>()?.SetEnabled(false);
            GetComponent<SkillSystem>()?.CancelCast();

            _playerEntity?.OnServerDeath();
            DeathScreenUI.Show(this);
        }

        [ClientRpc]
        private void RpcOnRespawned(Vector3 position, float hp, float maxHp, float mp, float maxMp)
        {
            if (_animator != null) _animator.SetBool("IsDead", false);

            if (!isLocalPlayer) return;

            if (_agent != null) { _agent.isStopped = false; _agent.Warp(position); }
            GetComponent<NetworkPlayerController>()?.SetEnabled(true);
            _playerEntity?.OnServerRespawn(position, hp, maxHp, mp, maxMp);
            DeathScreenUI.Hide();
        }

        [ClientRpc]
        public void RpcPlayAnimation(string trigger) => _animator?.SetTrigger(trigger);

        [ClientRpc]
        private void RpcOnExpGained(long amount, bool leveledUp)
        {
            if (!isLocalPlayer) return;
            if (Dead && !leveledUp) return;

            FloatingTextManager.Instance?.Show($"+{amount} XP",
                transform.position + Vector3.up * 2f, Color.cyan);

            if (leveledUp)
            {
                FloatingTextManager.Instance?.Show("LEVEL UP!",
                    transform.position + Vector3.up * 2.5f, Color.yellow);
                UIManager.Instance?.ShowMessage("Level up! Você evoluiu!");
            }
        }

        [ClientRpc]
        private void RpcShowDamageTaken(float dmg)
        {
            FloatingTextManager.Instance?.Show($"-{dmg:0}",
                transform.position + Vector3.up * 2f,
                new Color(1f, 0.25f, 0.25f));
        }

        [ClientRpc]
        private void RpcShowRegenTick(float hpRestored, float mpRestored)
        {
            if (!isLocalPlayer) return;
            Vector3 basePos = transform.position + Vector3.up * 2f;
            if (hpRestored >= REGEN_DISPLAY_THRESHOLD)
                FloatingTextManager.Instance?.Show($"+{hpRestored:0} HP",
                    basePos, new Color(0.4f, 1f, 0.4f));
            if (mpRestored >= REGEN_DISPLAY_THRESHOLD)
                FloatingTextManager.Instance?.Show($"+{mpRestored:0} MP",
                    basePos + new Vector3(0.3f, 0.2f, 0f), new Color(0.4f, 0.7f, 1f));
        }

        [ClientRpc]
        private void RpcShowHeal(float amount)
        {
            FloatingTextManager.Instance?.Show($"+{amount:0} HP",
                transform.position + Vector3.up * 1.5f, Color.green);
        }

        [ClientRpc]
        public void RpcSkillConfirmed(int skillIndex, float cooldown)
        {
            if (!isLocalPlayer) return;
            GetComponent<SkillSystem>()?.OnServerSkillConfirmed(skillIndex, cooldown);
        }

        [ClientRpc]
        private void RpcAllocateRejected(string reason)
        {
            if (!isLocalPlayer) return;
            AttributeWindowUI.Instance?.OnAllocationFailed(reason);
        }

        [ClientRpc]
        public void RpcSkillRejected(int skillIndex, string reason)
        {
            if (!isLocalPlayer) return;
            GetComponent<SkillSystem>()?.OnServerSkillRejected(skillIndex, reason);
        }

        [TargetRpc]
        public void RpcShowMessageToOwner(string msg)
        {
            UIManager.Instance?.ShowMessage(msg);
        }

        // ══════════════════════════════════════════════════════════════════
        // Morte / Respawn
        // ══════════════════════════════════════════════════════════════════

        [Server]
        private void ServerDie()
        {
            CurrentHP = 0f;
            _regenLoop?.Stop();
            if (_agent != null && _agent.isOnNavMesh) _agent.ResetPath();

            IsMoving = false;

            // --- Penalidade de Morte ---
            if (_serverCharData != null && Level > 1)
            {
                long penalty = (long)(ExperienceToNextLevel * 0.05f); // 5% do XP do level atual
                _serverCharData.RemoveExperience(penalty);
                Experience = _serverCharData.Experience;
                MarkDirty();
                RpcShowMessageToOwner($"Você morreu e perdeu {penalty} XP.");
            }

            // Limpa cooldowns para evitar bloqueios após respawn
            _cooldowns?.ServerClearAll();

            ServerSaveCharacterForced();
            RpcPlayerDied();
        }

        [Server]
        private void ServerRespawn()
        {
            if (_serverStats == null) return;

            Vector3 pos = GetRespawnPosition();
            transform.position = pos;
            if (_agent != null && _agent.isOnNavMesh) _agent.Warp(pos);

            MaxHP     = Mathf.Min(_serverStats.MaxHP, GameConstants.Combat.MAX_HP);
            MaxMP     = Mathf.Min(_serverStats.MaxMP, GameConstants.Combat.MAX_MP);
            CurrentHP = MaxHP * 0.5f;
            CurrentMP = MaxMP * 0.5f;

            if (_serverCharData != null)
            {
                _serverCharData.CurrentHP = CurrentHP;
                _serverCharData.CurrentMP = CurrentMP;
                ServerSaveCharacterForced();
            }

            _regenLoop?.ResetCombatSuppression();

            ConfigureServerAgent();
            _regenLoop?.ServerStart();

            RpcOnRespawned(pos, CurrentHP, MaxHP, CurrentMP, MaxMP);
        }

        [Server]
        private Vector3 GetRespawnPosition()
        {
            if (_respawnPoints != null && _respawnPoints.Length > 0)
            {
                var pt = _respawnPoints[UnityEngine.Random.Range(0, _respawnPoints.Length)];
                if (pt != null) return pt.position;
            }

            if (_serverCharData != null)
            {
                var nm = RPGNetworkManager.singleton;
                if (nm != null)
                {
                    Vector3 racePos = nm.GetSpawnPositionForRace(_serverCharData.Race, _serverCharData);
                    if (racePos.sqrMagnitude > 0.01f) return racePos;
                }
            }

            if (NavMesh.SamplePosition(Vector3.zero, out NavMeshHit hit, 50f, NavMesh.AllAreas))
                return hit.position;

            return Vector3.zero;
        }

        // ══════════════════════════════════════════════════════════════════
        // SyncVar Hooks
        // ══════════════════════════════════════════════════════════════════

        private void OnNetNameChanged(string _, string v)
        {
            if (_nameTagText != null) _nameTagText.text = v;
        }

        private void OnRaceStrChanged(string _, string __) => UpdateCachedRace();

        private void OnNetMaxHPChanged(float _, float newMax)
        {
            if (_hpBarSlider != null)
            {
                _hpBarSlider.maxValue = Mathf.Max(1f, newMax);
                if (_hpBarSlider.value > newMax) _hpBarSlider.value = newMax;
            }
            if (isLocalPlayer && _playerEntity != null && _playerEntity.IsInitialized)
                _playerEntity.SetHPFromServer(CurrentHP, newMax);
        }

        private void OnNetHPChanged(float _, float newHP)
        {
            if (_hpBarSlider != null)
            {
                _hpBarSlider.maxValue = Mathf.Max(1f, MaxHP);
                _hpBarSlider.value    = Mathf.Clamp(newHP, 0f, _hpBarSlider.maxValue);
                _hpBarSlider.gameObject.SetActive(newHP < MaxHP);
            }
            if (isLocalPlayer && _playerEntity != null && _playerEntity.IsInitialized)
                _playerEntity.SetHPFromServer(newHP, MaxHP);
        }

        private void OnNetMaxMPChanged(float _, float newMax)
        {
            if (isLocalPlayer && _playerEntity != null && _playerEntity.IsInitialized)
                _playerEntity.SetMPFromServer(CurrentMP, newMax);
        }

        private void OnNetMPChanged(float _, float newMP)
        {
            if (isLocalPlayer && _playerEntity != null && _playerEntity.IsInitialized)
                _playerEntity.SetMPFromServer(newMP, MaxMP);
        }

        private void OnNetLevelChanged(int _, int v)
        {
            if (isLocalPlayer) UIManager.Instance?.RefreshLevel(v);
        }

        private void OnNetFreePointsChanged(int _, int newPoints)
        {
            if (!isLocalPlayer) return;
            AttributeWindowUI.Instance?.OnFreePointsUpdated(newPoints);
        }

        private void OnNetMovingChanged(bool _, bool v)
        {
            _animator?.SetBool("IsMoving", v);
        }

        private void OnNetExpChanged(long _, long __)
        {
            if (!isLocalPlayer) return;
            UIManager.Instance?.RefreshExpBar(Experience, ExperienceToNextLevel);
            AttributeWindowUI.Instance?.RefreshXPBar(Experience, ExperienceToNextLevel);
        }

        private void OnNetExpToNextChanged(long _, long __)
        {
            if (!isLocalPlayer) return;
            UIManager.Instance?.RefreshExpBar(Experience, ExperienceToNextLevel);
            AttributeWindowUI.Instance?.RefreshXPBar(Experience, ExperienceToNextLevel);
        }

        private void OnAllocChanged(int _, int __)
        {
            if (isLocalPlayer) _allocDirty = true;
        }

        private void OnClientEquipmentChanged()
        {
            if (isLocalPlayer) _equipDirty = true;
        }

        private void ApplyAllocatedDataToEntity()
        {
            if (_playerEntity?.Data == null) return;

            _playerEntity.Data.BaseAttributes.STR = BaseSTR;
            _playerEntity.Data.BaseAttributes.AGI = BaseAGI;
            _playerEntity.Data.BaseAttributes.VIT = BaseVIT;
            _playerEntity.Data.BaseAttributes.DEX = BaseDEX;
            _playerEntity.Data.BaseAttributes.INT = BaseINT;
            _playerEntity.Data.BaseAttributes.LUK = BaseLUK;

            _playerEntity.Data.AllocatedSTR = AllocatedSTR;
            _playerEntity.Data.AllocatedAGI = AllocatedAGI;
            _playerEntity.Data.AllocatedVIT = AllocatedVIT;
            _playerEntity.Data.AllocatedDEX = AllocatedDEX;
            _playerEntity.Data.AllocatedINT = AllocatedINT;
            _playerEntity.Data.AllocatedLUK = AllocatedLUK;

            if (_playerEntity.IsInitialized)
                _playerEntity.FullRefreshStatsFromData();
        }

        private void ApplyEquipmentDataToEntity()
        {
            if (_playerEntity?.Data == null || _inventory == null) return;
            _playerEntity.Data.EquipmentBonuses = _inventory.BuildEquipmentBonuses();
            if (_playerEntity.IsInitialized)
                _playerEntity.FullRefreshStatsFromData();
        }

        private void OnStatsVersionChanged(int _, int __)
        {
            if (!isLocalPlayer) return;
            if (_playerEntity == null || !_playerEntity.IsInitialized) return;

            if (_inventory != null)
                _playerEntity.Data.EquipmentBonuses = _inventory.BuildEquipmentBonuses();

            if (_playerEntity.Data != null)
            {
                _playerEntity.Data.BaseAttributes.STR = BaseSTR;
                _playerEntity.Data.BaseAttributes.AGI = BaseAGI;
                _playerEntity.Data.BaseAttributes.VIT = BaseVIT;
                _playerEntity.Data.BaseAttributes.DEX = BaseDEX;
                _playerEntity.Data.BaseAttributes.INT = BaseINT;
                _playerEntity.Data.BaseAttributes.LUK = BaseLUK;

                _playerEntity.Data.AllocatedSTR = AllocatedSTR;
                _playerEntity.Data.AllocatedAGI = AllocatedAGI;
                _playerEntity.Data.AllocatedVIT = AllocatedVIT;
                _playerEntity.Data.AllocatedDEX = AllocatedDEX;
                _playerEntity.Data.AllocatedINT = AllocatedINT;
                _playerEntity.Data.AllocatedLUK = AllocatedLUK;
            }

            _allocDirty = false;
            _equipDirty = false;

            _playerEntity.FullRefreshStatsFromData();
        }
    }
}
