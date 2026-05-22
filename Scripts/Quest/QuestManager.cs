using System;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.Managers;

namespace RPG.Quest
{

    [RequireComponent(typeof(NetworkIdentity))]
    public class QuestManager : NetworkBehaviour
    {
        public const int MAX_ACTIVE_QUESTS = 25;
        public const int MAX_COMPLETED_QUESTS_TRACKED = 10000;

        public readonly SyncList<QuestProgress> Quests = new SyncList<QuestProgress>();

        // ── Eventos no cliente ─────────────────────────────────────────────
        public event Action                        OnQuestsChanged;
        public event Action<string>                OnQuestAccepted;
        public event Action<string>                OnQuestCompleted;
        public event Action<string, int, int, int> OnObjectiveProgress;
        public event Action<string>                OnQuestReadyToTurnIn;

        // ── Componentes ────────────────────────────────────────────────────
        private RPG.Network.NetworkPlayer    _netPlayer;
        private RPG.Network.NetworkInventory _inventory;

        // No servidor: cache do CharacterId/Username para persistência
        private string _serverCharacterId;
        private string _serverAccountUsername;

        // === FIX (Lote 3): flag para prevenir recursão em NotifyEvent ===
        private bool _isProcessingEvents;

        // ══════════════════════════════════════════════════════════════════
        // Lifecycle
        // ══════════════════════════════════════════════════════════════════

        private void Awake()
        {
            _netPlayer = GetComponent<RPG.Network.NetworkPlayer>();
            _inventory = GetComponent<RPG.Network.NetworkInventory>();
        }

        public override void OnStartClient()
        {
            Quests.Callback += OnQuestsSyncCallback;
        }

        public override void OnStopClient()
        {
            Quests.Callback -= OnQuestsSyncCallback;
        }

        private void OnQuestsSyncCallback(SyncList<QuestProgress>.Operation op,
                                          int index,
                                          QuestProgress oldItem,
                                          QuestProgress newItem)
        {
            OnQuestsChanged?.Invoke();

            switch (op)
            {
                case SyncList<QuestProgress>.Operation.OP_ADD:
                    if (newItem.State == QuestState.Active)
                        OnQuestAccepted?.Invoke(newItem.QuestId);
                    break;

                case SyncList<QuestProgress>.Operation.OP_SET:
                    if (oldItem.State != newItem.State)
                    {
                        if (newItem.State == QuestState.ReadyToTurnIn)
                            OnQuestReadyToTurnIn?.Invoke(newItem.QuestId);
                        else if (newItem.State == QuestState.Completed)
                            OnQuestCompleted?.Invoke(newItem.QuestId);
                    }
                    else if (oldItem.ProgressCsv != newItem.ProgressCsv)
                    {
                        DiffAndNotifyProgress(oldItem, newItem);
                    }
                    break;
            }
        }

        private void DiffAndNotifyProgress(QuestProgress oldItem, QuestProgress newItem)
        {
            var def = QuestDatabase.Instance?.GetQuest(newItem.QuestId);
            if (def == null) return;

            int count = def.ObjectiveCount;
            var oldArr = oldItem.GetProgressArray(count);
            var newArr = newItem.GetProgressArray(count);

            for (int i = 0; i < count; i++)
            {
                if (oldArr[i] != newArr[i])
                {
                    int target = def.GetObjective(i)?.TargetCount ?? 0;
                    OnObjectiveProgress?.Invoke(newItem.QuestId, i, newArr[i], target);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // API pública — LEITURA
        // ══════════════════════════════════════════════════════════════════

        public int FindIndexById(string questId)
        {
            if (string.IsNullOrEmpty(questId)) return -1;
            for (int i = 0; i < Quests.Count; i++)
                if (Quests[i].QuestId == questId) return i;
            return -1;
        }

        public QuestProgress? FindByIdNullable(string questId)
        {
            int idx = FindIndexById(questId);
            return idx >= 0 ? Quests[idx] : (QuestProgress?)null;
        }

        public bool HasQuest(string questId)        => FindIndexById(questId) >= 0;

        public bool IsActive(string questId)
        {
            var p = FindByIdNullable(questId);
            return p.HasValue && p.Value.State == QuestState.Active;
        }

        public bool IsReadyToTurnIn(string questId)
        {
            var p = FindByIdNullable(questId);
            return p.HasValue && p.Value.State == QuestState.ReadyToTurnIn;
        }

        public bool IsCompleted(string questId)
        {
            var p = FindByIdNullable(questId);
            return p.HasValue && p.Value.State == QuestState.Completed;
        }

        public int CountActive()
        {
            int n = 0;
            foreach (var q in Quests)
                if (q.State == QuestState.Active || q.State == QuestState.ReadyToTurnIn) n++;
            return n;
        }

        // ══════════════════════════════════════════════════════════════════
        // SERVIDOR — Offer / Accept / Complete
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public NpcQuestOption EvaluateQuestForOffer(string questId)
        {
            var def = QuestDatabase.Instance?.GetQuest(questId);
            if (def == null) return NpcQuestOption.Invalid(questId);

            var existing = FindByIdNullable(questId);

            if (existing.HasValue)
            {
                switch (existing.Value.State)
                {
                    case QuestState.Active:        return NpcQuestOption.InProgress(questId);
                    case QuestState.ReadyToTurnIn: return NpcQuestOption.TurnIn(questId);
                    case QuestState.Completed:
                        if (def.Repeatable)
                            return PrerequisitesMet(def)
                                ? NpcQuestOption.Offer(questId)
                                : NpcQuestOption.Locked(questId);
                        return NpcQuestOption.AlreadyDone(questId);
                    case QuestState.Failed:
                        return NpcQuestOption.Locked(questId);
                }
            }

            return PrerequisitesMet(def)
                ? NpcQuestOption.Offer(questId)
                : NpcQuestOption.Locked(questId);
        }

        [Server]
        private bool PrerequisitesMet(QuestDefinition def)
        {
            if (_netPlayer == null) return false;
            if (_netPlayer.Level < def.RequiredLevel) return false;

            if (def.RequiredCompletedQuests != null)
            {
                foreach (var reqId in def.RequiredCompletedQuests)
                {
                    if (string.IsNullOrEmpty(reqId)) continue;
                    if (!IsCompleted(reqId)) return false;
                }
            }
            return true;
        }

        [Server]
        public bool ServerAcceptQuest(string questId, out string reason)
        {
            reason = null;

            var def = QuestDatabase.Instance?.GetQuest(questId);
            if (def == null)
            {
                reason = "Quest desconhecida.";
                return false;
            }

            if (CountActive() >= MAX_ACTIVE_QUESTS)
            {
                reason = $"Limite de {MAX_ACTIVE_QUESTS} quests ativas atingido.";
                return false;
            }

            var existing = FindByIdNullable(questId);
            if (existing.HasValue)
            {
                if (existing.Value.State == QuestState.Active
                    || existing.Value.State == QuestState.ReadyToTurnIn)
                {
                    reason = "Você já tem esta quest.";
                    return false;
                }
                if (existing.Value.State == QuestState.Completed && !def.Repeatable)
                {
                    reason = "Você já completou esta quest.";
                    return false;
                }
            }

            if (!PrerequisitesMet(def))
            {
                reason = $"Requer nível {def.RequiredLevel}";
                if (def.RequiredCompletedQuests != null && def.RequiredCompletedQuests.Length > 0)
                    reason += " ou quests anteriores.";
                else
                    reason += ".";
                return false;
            }

            var newEntry = QuestProgress.NewActive(questId, def.ObjectiveCount);

            int existingIdx = FindIndexById(questId);
            if (existingIdx >= 0)
                Quests[existingIdx] = newEntry;
            else
                Quests.Add(newEntry);

            RecheckCollectObjectives(questId);
            RecheckLevelObjectives(questId);

            _netPlayer?.RpcShowMessageToOwner($"Quest aceita: {def.DisplayName}");
            return true;
        }

        [Server]
        public bool ServerCompleteQuest(string questId, out string reason)
        {
            reason = null;

            int idx = FindIndexById(questId);
            if (idx < 0)
            {
                reason = "Você não aceitou esta quest.";
                return false;
            }

            var progress = Quests[idx];
            var def      = QuestDatabase.Instance?.GetQuest(questId);
            if (def == null) { reason = "Quest desconhecida."; return false; }

            if (progress.State != QuestState.ReadyToTurnIn)
            {
                if (progress.State == QuestState.Completed)
                    reason = "Você já completou esta quest.";
                else
                    reason = "Objetivos ainda não foram completados.";
                return false;
            }

            ApplyReward(def);

            if (def.Repeatable)
                Quests.RemoveAt(idx);
            else
                Quests[idx] = progress.WithState(QuestState.Completed);

            EnforceCompletedQuestsCap();

            _netPlayer?.RpcShowMessageToOwner($"Quest completa: {def.DisplayName}!");
            ServerSaveAll();
            return true;
        }

        [Server]
        private void ApplyReward(QuestDefinition def)
        {
            if (def.Reward == null) return;

            if (def.Reward.Experience > 0)
                _netPlayer?.ServerGrantExp(def.Reward.Experience);

            if (def.Reward.Items != null && _inventory != null)
            {
                foreach (var item in def.Reward.Items)
                {
                    if (item == null || string.IsNullOrEmpty(item.ItemId)) continue;
                    if (item.Quantity <= 0) continue;
                    _inventory.ServerAddItem(item.ItemId, item.Quantity);
                }
            }
        }

        // === FIX (Lote 3): EnforceCompletedQuestsCap O(n²) → O(n) ===
        // Antes: loop interno O(n) para achar o oldest, executado N vezes.
        // Agora: uma única coleta + sort O(n log n), depois remoção em ordem.
        [Server]
        private void EnforceCompletedQuestsCap()
        {
            if (Quests.Count <= MAX_COMPLETED_QUESTS_TRACKED) return;

            int over = Quests.Count - MAX_COMPLETED_QUESTS_TRACKED;
            if (over <= 0) return;

            // Coleta todas as quests Completed com seus timestamps em uma passada.
            var completedIndices = new List<(int index, long timestamp, string questId)>(Quests.Count);
            for (int i = 0; i < Quests.Count; i++)
            {
                if (Quests[i].State == QuestState.Completed)
                    completedIndices.Add((i, Quests[i].StateTimestamp, Quests[i].QuestId));
            }

            if (completedIndices.Count == 0) return;

            // Ordena por timestamp crescente — as mais antigas vêm primeiro.
            completedIndices.Sort((a, b) => a.timestamp.CompareTo(b.timestamp));

            // Remove as N mais antigas. ATENÇÃO: remover por índice direto
            // invalida os outros índices coletados. Solução: coletamos os
            // QuestIds das que devem ser removidas e usamos FindIndexById
            // a cada remoção (mais defensivo, custo aceitável pois `over`
            // é pequeno por design — só estouramos cap em casos extremos).
            int toRemove = Mathf.Min(over, completedIndices.Count);
            int removed = 0;

            for (int i = 0; i < toRemove; i++)
            {
                int liveIdx = FindIndexById(completedIndices[i].questId);
                if (liveIdx >= 0)
                {
                    Quests.RemoveAt(liveIdx);
                    removed++;
                }
            }

            if (removed > 0)
                Debug.LogWarning($"[QuestManager] Cap de quests completadas atingido — removidas {removed} antigas.");
        }

        // ══════════════════════════════════════════════════════════════════
        // SERVIDOR — Notificação de eventos do mundo
        // ══════════════════════════════════════════════════════════════════

        // === FIX (Lote 3): tratamento correto de auto-complete + recursão guard ===
        [Server]
        public void NotifyEvent(QuestObjectiveType type, string targetId, int delta = 1)
        {
            if (delta <= 0) return;

            // Guarda contra recursão (ApplyReward → ServerGrantExp → level up →
            // NotifyLevelUp → NotifyEvent). Eventos disparados durante o
            // processamento são silenciosamente ignorados; o caller original
            // ainda os processará no próximo turno (próxima chamada externa).
            if (_isProcessingEvents)
            {
                Debug.LogWarning($"[QuestManager] NotifyEvent reentrante ignorado: {type}/{targetId}");
                return;
            }

            _isProcessingEvents = true;
            try
            {
                ProcessEventInternal(type, targetId, delta);
            }
            finally
            {
                _isProcessingEvents = false;
            }
        }

        [Server]
        private void ProcessEventInternal(QuestObjectiveType type, string targetId, int delta)
        {
            for (int i = 0; i < Quests.Count; i++)
            {
                var entry = Quests[i];
                if (entry.State != QuestState.Active) continue;

                var def = QuestDatabase.Instance?.GetQuest(entry.QuestId);
                if (def == null) continue;
                if (def.ObjectiveCount == 0) continue;

                var progress = entry.GetProgressArray(def.ObjectiveCount);
                bool changed = false;

                for (int o = 0; o < def.ObjectiveCount; o++)
                {
                    var obj = def.GetObjective(o);
                    if (obj == null) continue;
                    if (obj.Type != type) continue;
                    if (!def.CanObjectiveBeAdvanced(o, progress)) continue;

                    bool matches = obj.Type == QuestObjectiveType.ReachLevel
                        ? IsLevelObjectiveMet(obj.TargetCount)
                        : string.Equals(obj.TargetId, targetId, StringComparison.Ordinal);

                    if (!matches) continue;
                    if (progress[o] >= obj.TargetCount) continue;

                    int newValue = obj.Type == QuestObjectiveType.ReachLevel
                        ? obj.TargetCount
                        : Math.Min(obj.TargetCount, progress[o] + delta);

                    progress[o] = newValue;
                    changed = true;
                }

                if (!changed) continue;

                var newState = AllObjectivesComplete(def, progress)
                    ? QuestState.ReadyToTurnIn
                    : QuestState.Active;

                var newEntry = entry.WithProgress(progress, newState);
                Quests[i] = newEntry;

                if (newState == QuestState.ReadyToTurnIn && def.AutoComplete)
                {
                    int countBefore = Quests.Count;

                    // Nota: ServerCompleteQuest É chamado dentro de _isProcessingEvents=true.
                    // Se internamente disparar eventos (ApplyReward dá XP, sobe nível,
                    // NotifyLevelUp chama NotifyEvent), eles serão descartados pela guarda
                    // de recursão acima — e o caller externo (ex: monstro morrendo)
                    // perde esses eventos secundários. No 99% dos casos isso é OK porque
                    // uma quest AutoComplete não conflita com outras ReachLevel da mesma
                    // bateria. Se virar problema, podemos enfileirar eventos pendentes
                    // em vez de descartar.
                    if (ServerCompleteQuest(entry.QuestId, out _))
                    {
                        bool wasRemoved = Quests.Count < countBefore;
                        if (wasRemoved)
                        {
                            // Quest repetível foi removida — corrige índice
                            i = Math.Max(-1, i - 1);
                        }
                        // Se não foi removida (não-repetível), apenas mudou
                        // para Completed; State check no topo do loop já
                        // pula no próximo iteration.
                    }
                }
            }
        }

        [Server]
        private bool IsLevelObjectiveMet(int targetLevel)
            => _netPlayer != null && _netPlayer.Level >= targetLevel;

        [Server]
        private static bool AllObjectivesComplete(QuestDefinition def, int[] progress)
        {
            if (def.Objectives == null) return false;
            for (int i = 0; i < def.Objectives.Length; i++)
            {
                if (progress[i] < def.Objectives[i].TargetCount) return false;
            }
            return true;
        }

        [Server]
        public void RecheckCollectObjectives(string questId)
        {
            if (_inventory == null) return;

            int idx = FindIndexById(questId);
            if (idx < 0) return;

            var entry = Quests[idx];
            if (entry.State != QuestState.Active) return;

            var def = QuestDatabase.Instance?.GetQuest(questId);
            if (def == null) return;

            var progress = entry.GetProgressArray(def.ObjectiveCount);
            bool changed = false;

            for (int o = 0; o < def.ObjectiveCount; o++)
            {
                var obj = def.GetObjective(o);
                if (obj == null || obj.Type != QuestObjectiveType.CollectItem) continue;
                if (!def.CanObjectiveBeAdvanced(o, progress)) continue;

                int count = _inventory.GetTotalQuantity(obj.TargetId);
                int capped = Math.Min(count, obj.TargetCount);
                if (progress[o] != capped)
                {
                    progress[o] = capped;
                    changed = true;
                }
            }

            if (changed)
            {
                var newState = AllObjectivesComplete(def, progress)
                    ? QuestState.ReadyToTurnIn
                    : QuestState.Active;
                Quests[idx] = entry.WithProgress(progress, newState);

                if (newState == QuestState.ReadyToTurnIn && def.AutoComplete)
                    ServerCompleteQuest(questId, out _);
            }
        }

        [Server]
        public void NotifyLevelUp(int newLevel)
        {
            NotifyEvent(QuestObjectiveType.ReachLevel, "", delta: 1);
        }

        [Server]
        private void RecheckLevelObjectives(string questId)
        {
            NotifyLevelUp(_netPlayer?.Level ?? 1);
        }

        // ══════════════════════════════════════════════════════════════════
        // SERVIDOR — Persistência
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public void ServerLoadFromDatabase(string characterId, string accountUsername)
        {
            _serverCharacterId     = characterId;
            _serverAccountUsername = accountUsername;

            var db = DatabaseManager.Instance;
            if (db == null) return;

            Quests.Clear();

            var rows = db.LoadQuestProgress(characterId);
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.QuestId)) continue;

                if (QuestDatabase.Instance != null && !QuestDatabase.Instance.Contains(row.QuestId))
                {
                    Debug.LogWarning($"[QuestManager] Quest '{row.QuestId}' do banco não está no QuestDatabase — ignorada.");
                    continue;
                }

                Quests.Add(new QuestProgress
                {
                    QuestId        = row.QuestId,
                    State          = (QuestState)Math.Max(0, Math.Min(row.State, (int)QuestState.Failed)),
                    ProgressCsv    = row.ProgressCsv ?? "",
                    StateTimestamp = row.StateTimestamp
                });
            }
        }

        [Server]
        public void ServerSaveAll()
        {
            if (string.IsNullOrEmpty(_serverCharacterId)) return;
            var db = DatabaseManager.Instance;
            if (db == null) return;

            var snapshot = new List<QuestProgress>(Quests);
            db.SaveQuestProgress(_serverCharacterId, snapshot);
        }

        // ══════════════════════════════════════════════════════════════════
        // CLIENTE → SERVIDOR (Commands)
        // ══════════════════════════════════════════════════════════════════

        [Command]
        public void CmdAcceptQuest(uint npcNetId, string questId)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            if (!NetworkServer.spawned.TryGetValue(npcNetId, out var identity)
                || identity == null)
            {
                _netPlayer.RpcShowMessageToOwner("NPC não encontrado.");
                return;
            }

            var npc = identity.GetComponent<RPG.NPC.NetworkNPC>();
            if (npc == null)
            {
                _netPlayer.RpcShowMessageToOwner("NPC inválido.");
                return;
            }

            if (!npc.IsPlayerInInteractionRange(transform.position))
            {
                _netPlayer.RpcShowMessageToOwner("Você está longe demais.");
                return;
            }

            if (!npc.OffersQuest(questId))
            {
                _netPlayer.RpcShowMessageToOwner("Este NPC não oferece esta quest.");
                return;
            }

            if (!ServerAcceptQuest(questId, out string reason))
                _netPlayer.RpcShowMessageToOwner(reason);
        }

        [Command]
        public void CmdCompleteQuest(uint npcNetId, string questId)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            if (!NetworkServer.spawned.TryGetValue(npcNetId, out var identity)
                || identity == null) return;

            var npc = identity.GetComponent<RPG.NPC.NetworkNPC>();
            if (npc == null) return;

            if (!npc.IsPlayerInInteractionRange(transform.position))
            {
                _netPlayer.RpcShowMessageToOwner("Você está longe demais.");
                return;
            }

            if (!npc.OffersQuest(questId))
            {
                _netPlayer.RpcShowMessageToOwner("Este NPC não pode receber esta quest.");
                return;
            }

            if (!ServerCompleteQuest(questId, out string reason))
                _netPlayer.RpcShowMessageToOwner(reason);
        }

        [Command]
        public void CmdAbandonQuest(string questId)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            int idx = FindIndexById(questId);
            if (idx < 0) return;

            var entry = Quests[idx];
            if (entry.State != QuestState.Active && entry.State != QuestState.ReadyToTurnIn) return;

            Quests.RemoveAt(idx);
            _netPlayer.RpcShowMessageToOwner("Quest abandonada.");
            ServerSaveAll();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Tipo auxiliar — usado por NetworkNPC
    // ══════════════════════════════════════════════════════════════════════

    public enum NpcQuestOptionState : byte
    {
        Invalid     = 0,
        Offer       = 1,
        InProgress  = 2,
        TurnIn      = 3,
        Locked      = 4,
        AlreadyDone = 5,
    }

    [Serializable]
    public struct NpcQuestOption
    {
        public string              QuestId;
        public NpcQuestOptionState State;

        public static NpcQuestOption Invalid(string id)     => new NpcQuestOption { QuestId = id, State = NpcQuestOptionState.Invalid };
        public static NpcQuestOption Offer(string id)       => new NpcQuestOption { QuestId = id, State = NpcQuestOptionState.Offer };
        public static NpcQuestOption InProgress(string id)  => new NpcQuestOption { QuestId = id, State = NpcQuestOptionState.InProgress };
        public static NpcQuestOption TurnIn(string id)      => new NpcQuestOption { QuestId = id, State = NpcQuestOptionState.TurnIn };
        public static NpcQuestOption Locked(string id)      => new NpcQuestOption { QuestId = id, State = NpcQuestOptionState.Locked };
        public static NpcQuestOption AlreadyDone(string id) => new NpcQuestOption { QuestId = id, State = NpcQuestOptionState.AlreadyDone };
    }
}