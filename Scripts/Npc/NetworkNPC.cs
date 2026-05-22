using System.Collections.Generic;
using UnityEngine;
using Mirror;
using RPG.Character;
using RPG.Quest;

namespace RPG.NPC
{

    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkNPC : NetworkBehaviour, ITargetable
    {
        private const float MIN_INTERACT_COOLDOWN  = 0.3f;

        // === FIX (Lote 3): caps e intervalo de cleanup ===
        private const int   MAX_TRACKED_COOLDOWNS  = 1000;
        private const float COOLDOWN_CLEANUP_INTERVAL = 30f;

        [Header("Identidade")]
        [Tooltip("ID único e estável. Use em quests (TalkToNPC TargetId).")]
        [SerializeField] private string npcId = "npc_unknown";

        [Tooltip("Nome exibido em UI/tooltip/dialog header.")]
        [SerializeField] private string displayName = "NPC";

        [Tooltip("Função (mercador, ferreiro, etc.) — apenas cosmético.")]
        [SerializeField] private string role = "Aldeão";

        [Header("Diálogo")]
        [TextArea(2, 4)]
        [SerializeField] private string defaultGreeting = "Olá, aventureiro!";

        [TextArea(2, 4)]
        [SerializeField] private string noQuestsGreeting = "Volte mais tarde, talvez eu tenha algo para você.";

        [Header("Quests")]
        [SerializeField] private QuestDefinition[] offeredQuests = new QuestDefinition[0];

        [Header("Interação")]
        [SerializeField] private float interactionRange = 4f;
        [SerializeField] private float interactionRangeTolerance = 1.5f;
        [SerializeField] private float interactCooldown = 0.5f;

        [Header("Visual")]
        [SerializeField] private GameObject selectionIndicator;
        [SerializeField] private TMPro.TMP_Text nameTagText;
        [SerializeField] private GameObject questAvailableMarker;
        [SerializeField] private GameObject questTurnInMarker;
        [SerializeField] private Transform billboardTransform;

        // ── ITargetable ────────────────────────────────────────────────────
        public string  NpcId       => npcId;
        public string  DisplayName => displayName;
        public string  Role        => role;
        public float   CurrentHP   => 1f;
        public float   MaxHP       => 1f;
        public bool    IsDead      => false;
        public Vector3 Position    => transform.position;

        public float InteractionRangeReal => interactionRange * Mathf.Max(1f, interactionRangeTolerance);

        public void OnSelected()   { if (selectionIndicator != null) selectionIndicator.SetActive(true);  }
        public void OnDeselected() { if (selectionIndicator != null) selectionIndicator.SetActive(false); }

        // ── Estado server-side ─────────────────────────────────────────────
        private readonly Dictionary<int, float> _interactCooldowns = new Dictionary<int, float>();
        private Coroutine _cooldownCleanupCoroutine;

        private bool _hasQuestMarker;
        private bool _hasTurnInMarker;

        // ══════════════════════════════════════════════════════════════════
        // Lifecycle
        // ══════════════════════════════════════════════════════════════════

        private void Awake()
        {
            if (selectionIndicator != null) selectionIndicator.SetActive(false);
            if (questAvailableMarker != null) questAvailableMarker.SetActive(false);
            if (questTurnInMarker != null) questTurnInMarker.SetActive(false);

            if (nameTagText != null)
                nameTagText.text = !string.IsNullOrEmpty(role)
                    ? $"{displayName}\n<size=70%><color=#AAAAAA>{role}</color></size>"
                    : displayName;
        }

        public override void OnStartServer()
        {
            // === FIX (Lote 3): inicia cleanup periódico ===
            _cooldownCleanupCoroutine = StartCoroutine(CleanupCooldownsLoop());
        }

        public override void OnStopServer()
        {
            if (_cooldownCleanupCoroutine != null)
            {
                StopCoroutine(_cooldownCleanupCoroutine);
                _cooldownCleanupCoroutine = null;
            }
            _interactCooldowns.Clear();
        }

        private System.Collections.IEnumerator CleanupCooldownsLoop()
        {
            var wait = new WaitForSeconds(COOLDOWN_CLEANUP_INTERVAL);
            var expired = new List<int>();

            while (true)
            {
                yield return wait;
                if (this == null || !isServer) yield break;

                float now = Time.time;
                expired.Clear();

                foreach (var kv in _interactCooldowns)
                {
                    if (kv.Value <= now)
                        expired.Add(kv.Key);
                }

                foreach (var id in expired)
                    _interactCooldowns.Remove(id);
            }
        }

        private void LateUpdate()
        {
            if (Application.isBatchMode) return;
            if (billboardTransform == null) return;
            var cam = Camera.main;
            if (cam == null) return;
            billboardTransform.forward = (billboardTransform.position - cam.transform.position).normalized;
        }

        // ══════════════════════════════════════════════════════════════════
        // API server-side
        // ══════════════════════════════════════════════════════════════════

        public bool OffersQuest(string questId)
        {
            if (string.IsNullOrEmpty(questId)) return false;
            if (offeredQuests == null) return false;
            for (int i = 0; i < offeredQuests.Length; i++)
            {
                if (offeredQuests[i] == null) continue;
                if (offeredQuests[i].QuestId == questId) return true;
            }
            return false;
        }

        public bool IsPlayerInInteractionRange(Vector3 playerPosition)
        {
            float dist = Vector3.Distance(transform.position, playerPosition);
            return dist <= InteractionRangeReal;
        }

        [Server]
        public NpcInteractionSnapshot ServerBuildSnapshotFor(RPG.Network.NetworkPlayer player)
        {
            var snap = new NpcInteractionSnapshot
            {
                NpcId       = npcId,
                DisplayName = displayName,
                Role        = role,
                Greeting    = defaultGreeting
            };

            if (player == null || offeredQuests == null || offeredQuests.Length == 0)
            {
                snap.Greeting = !string.IsNullOrEmpty(defaultGreeting) ? defaultGreeting : noQuestsGreeting;
                snap.Options  = new List<NpcQuestOption>();
                return snap;
            }

            var questManager = player.GetComponent<QuestManager>();
            var options      = new List<NpcQuestOption>(offeredQuests.Length);

            foreach (var def in offeredQuests)
            {
                if (def == null) continue;
                if (questManager == null)
                {
                    options.Add(NpcQuestOption.Locked(def.QuestId));
                    continue;
                }
                options.Add(questManager.EvaluateQuestForOffer(def.QuestId));
            }

            snap.Options = options;

            bool hasOffer  = false;
            bool hasTurnIn = false;
            foreach (var op in options)
            {
                if (op.State == NpcQuestOptionState.Offer) hasOffer = true;
                if (op.State == NpcQuestOptionState.TurnIn) hasTurnIn = true;
            }
            if (!hasOffer && !hasTurnIn && options.Count > 0)
                snap.Greeting = noQuestsGreeting;

            return snap;
        }

        // ══════════════════════════════════════════════════════════════════
        // Command de interação
        // ══════════════════════════════════════════════════════════════════

        [Command(requiresAuthority = false)]
        public void CmdInteract(NetworkConnectionToClient sender = null)
        {
            if (sender == null) return;
            var ownerIdentity = sender.identity;
            if (ownerIdentity == null) return;

            var player = ownerIdentity.GetComponent<RPG.Network.NetworkPlayer>();
            if (player == null || player.Dead) return;

            // Cooldown por jogador
            float cooldown = Mathf.Max(MIN_INTERACT_COOLDOWN, interactCooldown);
            int   connId   = sender.connectionId;

            if (_interactCooldowns.TryGetValue(connId, out float nextAllowed)
                && Time.time < nextAllowed)
            {
                return;
            }

            // === FIX (Lote 3): cap defensivo ===
            // Se atingiu o cap, evicta a entrada mais antiga (menor nextAllowed).
            // Em operação normal o cleanup periódico mantém o dicionário limpo,
            // este eviction é o fallback de último recurso.
            if (!_interactCooldowns.ContainsKey(connId)
                && _interactCooldowns.Count >= MAX_TRACKED_COOLDOWNS)
            {
                EvictOldestCooldown();
            }

            _interactCooldowns[connId] = Time.time + cooldown;

            if (!IsPlayerInInteractionRange(player.transform.position))
            {
                player.RpcShowMessageToOwner("Você está longe demais para falar.");
                return;
            }

            var snapshot = ServerBuildSnapshotFor(player);
            TargetOpenDialog(sender, this.netId, snapshot);
        }

        private void EvictOldestCooldown()
        {
            if (_interactCooldowns.Count == 0) return;

            int   oldestKey   = 0;
            float oldestValue = float.MaxValue;

            foreach (var kv in _interactCooldowns)
            {
                if (kv.Value < oldestValue)
                {
                    oldestValue = kv.Value;
                    oldestKey   = kv.Key;
                }
            }

            _interactCooldowns.Remove(oldestKey);
        }

        // ══════════════════════════════════════════════════════════════════
        // Server → Cliente owner
        // ══════════════════════════════════════════════════════════════════

        [TargetRpc]
        private void TargetOpenDialog(NetworkConnectionToClient target,
                                      uint npcNetId,
                                      NpcInteractionSnapshot snapshot)
        {
            if (Application.isBatchMode) return;
            RPG.UI.DialogUI.Instance?.OpenForNpc(npcNetId, snapshot);
        }

        // ══════════════════════════════════════════════════════════════════
        // Quest marker visual (cliente)
        // ══════════════════════════════════════════════════════════════════

        public void ClientUpdateQuestMarker(RPG.Network.NetworkPlayer localPlayer)
        {
            if (Application.isBatchMode) return;
            if (offeredQuests == null || offeredQuests.Length == 0)
            {
                SetMarkerStates(false, false);
                return;
            }

            if (localPlayer == null)
            {
                SetMarkerStates(false, false);
                return;
            }

            var qm = localPlayer.GetComponent<QuestManager>();
            if (qm == null)
            {
                SetMarkerStates(false, false);
                return;
            }

            bool hasOffer  = false;
            bool hasTurnIn = false;

            foreach (var def in offeredQuests)
            {
                if (def == null) continue;

                var existing = qm.FindByIdNullable(def.QuestId);
                if (existing.HasValue)
                {
                    if (existing.Value.State == QuestState.ReadyToTurnIn) { hasTurnIn = true; }
                    else if (existing.Value.State == QuestState.Completed && !def.Repeatable) { /* nada */ }
                    else if (existing.Value.State == QuestState.Completed && def.Repeatable
                             && ClientPrerequisitesMet(localPlayer, qm, def)) { hasOffer = true; }
                }
                else if (ClientPrerequisitesMet(localPlayer, qm, def))
                {
                    hasOffer = true;
                }
            }

            SetMarkerStates(hasOffer, hasTurnIn);
        }

        private static bool ClientPrerequisitesMet(RPG.Network.NetworkPlayer player,
                                                   QuestManager qm,
                                                   QuestDefinition def)
        {
            if (player.Level < def.RequiredLevel) return false;
            if (def.RequiredCompletedQuests != null)
            {
                foreach (var reqId in def.RequiredCompletedQuests)
                {
                    if (string.IsNullOrEmpty(reqId)) continue;
                    if (!qm.IsCompleted(reqId)) return false;
                }
            }
            return true;
        }

        private void SetMarkerStates(bool offer, bool turnIn)
        {
            if (turnIn)
            {
                if (questTurnInMarker   != null) questTurnInMarker.SetActive(true);
                if (questAvailableMarker != null) questAvailableMarker.SetActive(false);
            }
            else if (offer)
            {
                if (questAvailableMarker != null) questAvailableMarker.SetActive(true);
                if (questTurnInMarker    != null) questTurnInMarker.SetActive(false);
            }
            else
            {
                if (questAvailableMarker != null) questAvailableMarker.SetActive(false);
                if (questTurnInMarker    != null) questTurnInMarker.SetActive(false);
            }

            _hasQuestMarker  = offer;
            _hasTurnInMarker = turnIn;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.6f);
            Gizmos.DrawWireSphere(transform.position, interactionRange);

            Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.2f);
            Gizmos.DrawWireSphere(transform.position, InteractionRangeReal);
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            if (interactionRange < 0.5f) interactionRange = 0.5f;
            if (interactionRangeTolerance < 1f) interactionRangeTolerance = 1f;
            if (interactCooldown < MIN_INTERACT_COOLDOWN) interactCooldown = MIN_INTERACT_COOLDOWN;
        }
#endif
    }

    [System.Serializable]
    public struct NpcInteractionSnapshot
    {
        public string                NpcId;
        public string                DisplayName;
        public string                Role;
        public string                Greeting;
        public List<NpcQuestOption>  Options;
    }
}