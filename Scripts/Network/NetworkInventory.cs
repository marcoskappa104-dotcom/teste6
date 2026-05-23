using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.Combat;
using RPG.UI;

namespace RPG.Network
{

    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkInventory : NetworkBehaviour
    {
        public const int MAX_INVENTORY_SLOTS = 60;
        public const int GEM_SLOT_COUNT      = 4;

        // ── Sincronização ──────────────────────────────────────────────────
        public readonly SyncList<InventorySlotData> Slots         = new SyncList<InventorySlotData>();
        public readonly SyncList<EquippedItemData>  EquippedItems = new SyncList<EquippedItemData>();

        [SyncVar(hook = nameof(OnGemSlotQChanged))] public string GemSlotQ = "";
        [SyncVar(hook = nameof(OnGemSlotWChanged))] public string GemSlotW = "";
        [SyncVar(hook = nameof(OnGemSlotEChanged))] public string GemSlotE = "";
        [SyncVar(hook = nameof(OnGemSlotRChanged))] public string GemSlotR = "";

        // ── Eventos (cliente) ──────────────────────────────────────────────
        public event Action OnInventoryChanged;
        public event Action OnGemLoadoutChanged;
        public event Action OnEquipmentChanged;
        public event Action<string, int, int> OnPartialPickup;

        // ── Estado do servidor ─────────────────────────────────────────────
        private int           _nextSlotIndex;
        private NetworkPlayer _netPlayer;
        private RPG.Quest.QuestManager _questManager;

        // Purgatório de itens que não couberam durante swap
        private readonly List<(string itemId, int quantity)> _pendingReturns = new();

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            _netPlayer    = GetComponent<NetworkPlayer>();
            _questManager = GetComponent<RPG.Quest.QuestManager>();
        }

        public override void OnStartClient()
        {
            Slots.Callback         += OnSlotsChangedClient;
            EquippedItems.Callback += OnEquippedItemsChangedClient;
        }

        public override void OnStopClient()
        {
            Slots.Callback         -= OnSlotsChangedClient;
            EquippedItems.Callback -= OnEquippedItemsChangedClient;
        }

        public override void OnStartLocalPlayer()
        {
            StartCoroutine(BindUIDelayed());
        }

        private IEnumerator BindUIDelayed()
        {
            yield return null;
            yield return null;

            InventoryUI.Instance?.BindInventory(this);
            PowerGemUI.Instance?.BindInventory(this);
        }

        // ── Hooks ──────────────────────────────────────────────────────────

        private void OnSlotsChangedClient(SyncList<InventorySlotData>.Operation op,
                                          int index, InventorySlotData oldItem, InventorySlotData newItem)
            => OnInventoryChanged?.Invoke();

        private void OnEquippedItemsChangedClient(SyncList<EquippedItemData>.Operation op,
                                                  int index, EquippedItemData oldItem, EquippedItemData newItem)
            => OnEquipmentChanged?.Invoke();

        private void OnGemSlotQChanged(string oldVal, string newVal) => OnGemLoadoutChanged?.Invoke();
        private void OnGemSlotWChanged(string oldVal, string newVal) => OnGemLoadoutChanged?.Invoke();
        private void OnGemSlotEChanged(string oldVal, string newVal) => OnGemLoadoutChanged?.Invoke();
        private void OnGemSlotRChanged(string oldVal, string newVal) => OnGemLoadoutChanged?.Invoke();

        // ══════════════════════════════════════════════════════════════════
        // INVENTÁRIO — API do servidor
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public int ServerAddItem(string itemId, int quantity = 1)
        {
            if (string.IsNullOrEmpty(itemId)) return -1;
            if (quantity <= 0) return -1;

            var db = ItemDatabase.Instance;
            if (db == null || !db.Contains(itemId))
            {
                Debug.LogWarning($"[NetworkInventory] Item '{itemId}' não existe no banco.");
                return -1;
            }

            var item = db.GetItem(itemId);
            if (item == null) return -1;

            quantity = Mathf.Clamp(quantity, 1, ItemData.MAX_STACK_HARD_CAP * MAX_INVENTORY_SLOTS);

            int result = item.IsStackable
                ? AddStackable(item, quantity)
                : AddNonStackable(item, quantity);

            if (result >= 0)
                NotifyQuestCollectItem(itemId);

            return result;
        }

        [Server]
        public int CalculateHowManyFit(string itemId, int quantity)
        {
            if (string.IsNullOrEmpty(itemId) || quantity <= 0) return 0;

            var item = ItemDatabase.Instance?.GetItem(itemId);
            if (item == null) return 0;

            if (!item.IsStackable)
            {
                int freeSlots = MAX_INVENTORY_SLOTS - Slots.Count;
                return Mathf.Min(quantity, Mathf.Max(0, freeSlots));
            }

            int maxStack = item.EffectiveMaxStack;
            int spaceInExisting = 0;
            foreach (var s in Slots)
            {
                if (s.ItemId == itemId && s.Quantity < maxStack)
                    spaceInExisting += (maxStack - s.Quantity);
            }
            int freeSlotsAvail  = Mathf.Max(0, MAX_INVENTORY_SLOTS - Slots.Count);
            int spaceInNewSlots = freeSlotsAvail * maxStack;
            return Mathf.Min(quantity, spaceInExisting + spaceInNewSlots);
        }

        [Server]
        public bool HasRoomForItem(string itemId, int quantity = 1)
            => CalculateHowManyFit(itemId, quantity) >= quantity;

        [Server]
        private void NotifyQuestCollectItem(string itemId)
        {
            if (_questManager == null || string.IsNullOrEmpty(itemId)) return;

            for (int i = 0; i < _questManager.Quests.Count; i++)
            {
                var q = _questManager.Quests[i];
                if (q.State != RPG.Quest.QuestState.Active) continue;

                var def = RPG.Quest.QuestDatabase.Instance?.GetQuest(q.QuestId);
                if (def == null || def.Objectives == null) continue;

                bool relevant = false;
                foreach (var obj in def.Objectives)
                {
                    if (obj == null) continue;
                    if (obj.Type != RPG.Quest.QuestObjectiveType.CollectItem) continue;
                    if (obj.TargetId == itemId) { relevant = true; break; }
                }

                if (relevant)
                    _questManager.RecheckCollectObjectives(q.QuestId);
            }
        }

        [Server]
        private int AddNonStackable(ItemData item, int quantity)
        {
            int firstAffected = -1;
            int added         = 0;

            for (int i = 0; i < quantity; i++)
            {
                if (Slots.Count >= MAX_INVENTORY_SLOTS)
                {
                    _netPlayer?.RpcShowMessageToOwner("Inventário cheio!");

                    if (added > 0 && added < quantity && _netPlayer != null)
                        RpcNotifyPartialPickup(item.ItemId, added, quantity);

                    return firstAffected;
                }

                var slot = new InventorySlotData
                {
                    SlotIndex = _nextSlotIndex++,
                    ItemId    = item.ItemId,
                    Quantity  = 1
                };
                Slots.Add(slot);
                added++;

                if (firstAffected < 0) firstAffected = slot.SlotIndex;
            }

            return firstAffected;
        }

        [Server]
        private int AddStackable(ItemData item, int quantity)
        {
            int maxStack      = item.EffectiveMaxStack;
            int remaining     = quantity;
            int firstAffected = -1;

            // Fase 1: topar stacks existentes
            for (int i = 0; i < Slots.Count && remaining > 0; i++)
            {
                var slot = Slots[i];
                if (slot.ItemId != item.ItemId) continue;
                if (slot.Quantity >= maxStack) continue;

                int room  = maxStack - slot.Quantity;
                int toAdd = Mathf.Min(room, remaining);

                slot.Quantity += toAdd;
                Slots[i]       = slot;

                remaining -= toAdd;

                if (firstAffected < 0) firstAffected = slot.SlotIndex;
            }

            // Fase 2: criar novos stacks
            while (remaining > 0)
            {
                if (Slots.Count >= MAX_INVENTORY_SLOTS)
                {
                    int collected = quantity - remaining;
                    _netPlayer?.RpcShowMessageToOwner(
                        $"Inventário cheio! Coletou {collected}/{quantity} {item.DisplayName}.");

                    if (collected > 0 && _netPlayer != null)
                        RpcNotifyPartialPickup(item.ItemId, collected, quantity);

                    return firstAffected;
                }

                int amountForNewSlot = Mathf.Min(maxStack, remaining);

                var newSlot = new InventorySlotData
                {
                    SlotIndex = _nextSlotIndex++,
                    ItemId    = item.ItemId,
                    Quantity  = amountForNewSlot
                };
                Slots.Add(newSlot);

                remaining -= amountForNewSlot;

                if (firstAffected < 0) firstAffected = newSlot.SlotIndex;
            }

            return firstAffected;
        }

        [TargetRpc]
        private void RpcNotifyPartialPickup(string itemId, int collected, int requested)
        {
            OnPartialPickup?.Invoke(itemId, collected, requested);
        }

        [Server]
        public bool ServerRemoveSlot(int slotIndex)
        {
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].SlotIndex == slotIndex)
                {
                    string removedItemId = Slots[i].ItemId;
                    Slots.RemoveAt(i);

                    if (!string.IsNullOrEmpty(removedItemId))
                        NotifyQuestCollectItem(removedItemId);

                    return true;
                }
            }
            return false;
        }

        [Server]
        public bool ServerRemoveItemById(string itemId)
        {
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].ItemId == itemId)
                {
                    Slots.RemoveAt(i);
                    NotifyQuestCollectItem(itemId);
                    return true;
                }
            }
            return false;
        }

        public bool HasItem(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return false;
            foreach (var slot in Slots)
                if (slot.ItemId == itemId) return true;
            return false;
        }

        public int FindSlotByItemId(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return -1;
            foreach (var slot in Slots)
                if (slot.ItemId == itemId) return slot.SlotIndex;
            return -1;
        }

        [Server]
        public void ServerLoadFromDatabase(string characterId)
        {
            var db = Managers.DatabaseManager.Instance;
            if (db == null) return;

            Slots.Clear();
            _nextSlotIndex = 0;

            var rows = db.LoadInventory(characterId);
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.ItemId)) continue;

                if (ItemDatabase.Instance != null && !ItemDatabase.Instance.Contains(row.ItemId))
                {
                    Debug.LogWarning($"[NetworkInventory] Item '{row.ItemId}' do banco não está no ItemDatabase — ignorado.");
                    continue;
                }

                var slot = new InventorySlotData
                {
                    SlotIndex = row.SlotIndex >= 0 ? row.SlotIndex : _nextSlotIndex,
                    ItemId    = row.ItemId,
                    Quantity  = Mathf.Max(1, row.Quantity)
                };
                Slots.Add(slot);
            }

            // FIX: garante que _nextSlotIndex fica ACIMA do maior SlotIndex carregado.
            // Antes havia risco de colisão se slots eram carregados com índices altos
            // enquanto _nextSlotIndex era resetado para 0.
            if (Slots.Count > 0)
                _nextSlotIndex = Slots.Max(s => s.SlotIndex) + 1;
        }

        [Server]
        public void ServerLoadGemLoadout(string characterId)
        {
            var db = Managers.DatabaseManager.Instance;
            if (db == null) return;

            var loadout = db.LoadGemLoadout(characterId);
            GemSlotQ = ValidateLoadedGemId(loadout.SlotQ);
            GemSlotW = ValidateLoadedGemId(loadout.SlotW);
            GemSlotE = ValidateLoadedGemId(loadout.SlotE);
            GemSlotR = ValidateLoadedGemId(loadout.SlotR);
        }

        [Server]
        private static string ValidateLoadedGemId(string gemId)
        {
            if (string.IsNullOrEmpty(gemId)) return "";
            var db = ItemDatabase.Instance;
            if (db == null) return gemId;
            var item = db.GetItem(gemId);
            if (item == null || !item.IsPowerGem)
            {
                Debug.LogWarning($"[NetworkInventory] Gem '{gemId}' inválida no banco — slot limpo.");
                return "";
            }
            return gemId;
        }

        [Server]
        public void ServerLoadEquippedFromDatabase(string characterId)
        {
            var db = Managers.DatabaseManager.Instance;
            if (db == null) return;

            EquippedItems.Clear();

            var rows = db.LoadEquipped(characterId);
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.ItemId)) continue;

                var itemData = ItemDatabase.Instance?.GetItem(row.ItemId);
                if (itemData == null || !itemData.IsEquipment)
                {
                    Debug.LogWarning($"[NetworkInventory] Equipped item '{row.ItemId}' inválido — ignorado.");
                    continue;
                }

                EquippedItems.Add(new EquippedItemData
                {
                    Slot          = (byte)row.Slot,
                    ItemId        = row.ItemId,
                    Durability    = row.Durability,
                    MaxDurability = row.MaxDurability
                });
            }
        }

        [Server]
        public void ServerSaveAll(string characterId, string username)
        {
            var db = Managers.DatabaseManager.Instance;
            if (db == null) return;

            TryFlushPendingReturns();

            db.SaveInventory(characterId, username, new List<InventorySlotData>(Slots));
            db.SaveGemLoadout(characterId, new PowerGemLoadout
            {
                SlotQ = GemSlotQ ?? "", SlotW = GemSlotW ?? "",
                SlotE = GemSlotE ?? "", SlotR = GemSlotR ?? ""
            });
            db.SaveEquipped(characterId, new List<EquippedItemData>(EquippedItems));
        }

        [Server]
        private void TryFlushPendingReturns()
        {
            if (_pendingReturns.Count == 0) return;

            for (int i = _pendingReturns.Count - 1; i >= 0; i--)
            {
                var (itemId, qty) = _pendingReturns[i];
                int added = ServerAddItem(itemId, qty);
                if (added >= 0)
                {
                    _pendingReturns.RemoveAt(i);
                    var item = ItemDatabase.Instance?.GetItem(itemId);
                    string name = item?.DisplayName ?? itemId;
                    _netPlayer?.RpcShowMessageToOwner($"Item devolvido: {name} ×{qty}");
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // SWAP HELPER
        // ══════════════════════════════════════════════════════════════════

        [Server]
        private bool TrySwapFromInventory(int inventorySlotIndex, string newItemId,
                                          string oldItemId, out string failReason)
        {
            failReason = null;

            if (!TryGetInventorySlot(inventorySlotIndex, out var originalSlot))
            {
                failReason = "Item desapareceu do inventário.";
                return false;
            }

            if (!string.IsNullOrEmpty(oldItemId))
            {
                int hypotheticalFreeSlots = MAX_INVENTORY_SLOTS - Slots.Count + 1;

                var oldItem = ItemDatabase.Instance?.GetItem(oldItemId);
                if (oldItem == null)
                {
                    Debug.LogError($"[NetworkInventory] oldItemId '{oldItemId}' não encontrado no banco. " +
                                   "Equip prossegue mas item antigo não será devolvido.");
                }
                else
                {
                    if (!oldItem.IsStackable && hypotheticalFreeSlots < 1)
                    {
                        failReason = "Sem espaço no inventário para o item antigo.";
                        return false;
                    }
                }
            }

            if (!ServerRemoveSlot(inventorySlotIndex))
            {
                failReason = "Item desapareceu do inventário.";
                Debug.LogError($"[NetworkInventory] TrySwapFromInventory: " +
                               $"remove({inventorySlotIndex}) falhou inesperadamente.");
                return false;
            }

            if (!string.IsNullOrEmpty(oldItemId))
            {
                int returnedSlot = ServerAddItem(oldItemId, 1);
                if (returnedSlot < 0)
                {
                    Debug.LogError($"[NetworkInventory] CAMINHO CATASTRÓFICO: " +
                                   $"oldItem '{oldItemId}' não pôde ser devolvido após validação. " +
                                   $"Adicionando ao purgatório. " +
                                   $"Player: {_netPlayer?.CharacterName ?? "?"}");

                    _pendingReturns.Add((oldItemId, 1));
                    _netPlayer?.RpcShowMessageToOwner(
                        "Aviso: item antigo será devolvido assim que houver espaço.");
                }
            }

            return true;
        }

        // ══════════════════════════════════════════════════════════════════
        // EQUIPAMENTOS — leitura
        // ══════════════════════════════════════════════════════════════════

        [Server]
        private int ServerFindEquippedIndex(EquipmentSlot slot)
        {
            for (int i = 0; i < EquippedItems.Count; i++)
                if (EquippedItems[i].Slot == (byte)slot) return i;
            return -1;
        }

        [Server]
        public string ServerGetEquipped(EquipmentSlot slot)
        {
            int idx = ServerFindEquippedIndex(slot);
            return idx >= 0 ? EquippedItems[idx].ItemId : "";
        }

        public string GetEquipped(EquipmentSlot slot)
        {
            for (int i = 0; i < EquippedItems.Count; i++)
                if (EquippedItems[i].Slot == (byte)slot) return EquippedItems[i].ItemId;
            return "";
        }

        public bool IsSlotOccupied(EquipmentSlot slot) => !string.IsNullOrEmpty(GetEquipped(slot));

        // ══════════════════════════════════════════════════════════════════
        // EQUIPAMENTOS — Commands
        // ══════════════════════════════════════════════════════════════════

        [Command]
        public void CmdEquipItem(int inventorySlotIndex, byte targetSlotByte)
        {
            if (connectionToClient == null) return;
            ServerEquipItem(inventorySlotIndex, targetSlotByte);
        }

        [Command]
        public void CmdAutoEquip(int inventorySlotIndex)
        {
            if (connectionToClient == null) return;
            ServerEquipItem(inventorySlotIndex, (byte)EquipmentSlot.None);
        }

        [Command]
        public void CmdUnequipItem(byte slotByte)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            EquipmentSlot slot = (EquipmentSlot)slotByte;

            if (slot == EquipmentSlot.None || !EquipmentSlotEx.IsActive(slot))
            {
                _netPlayer.RpcShowMessageToOwner("Slot inválido.");
                return;
            }

            int idx = ServerFindEquippedIndex(slot);
            if (idx < 0)
            {
                _netPlayer.RpcShowMessageToOwner("Slot já está vazio.");
                return;
            }

            string itemId = EquippedItems[idx].ItemId;
            if (string.IsNullOrEmpty(itemId))
            {
                EquippedItems.RemoveAt(idx);
                _netPlayer.ServerOnEquipmentChanged();
                return;
            }

            int returnedSlot = ServerAddItem(itemId, 1);
            if (returnedSlot < 0)
            {
                _netPlayer.RpcShowMessageToOwner("Inventário cheio!");
                return;
            }

            EquippedItems.RemoveAt(idx);
            _netPlayer.ServerOnEquipmentChanged();
        }

        [Server]
        private void ServerEquipItem(int inventorySlotIndex, byte targetSlotByte)
        {
            if (_netPlayer == null || _netPlayer.Dead) return;

            if (ItemDatabase.Instance == null)
            {
                _netPlayer.RpcShowMessageToOwner("Banco de itens indisponível. Tente novamente em instantes.");
                Debug.LogError("[NetworkInventory] ServerEquipItem: ItemDatabase.Instance é null.");
                return;
            }

            if (!TryGetInventorySlot(inventorySlotIndex, out var foundSlot))
            {
                _netPlayer.RpcShowMessageToOwner("Item não encontrado no inventário.");
                return;
            }

            var itemData = ItemDatabase.Instance.GetItem(foundSlot.ItemId);
            if (itemData == null)
            {
                _netPlayer.RpcShowMessageToOwner("Item inválido (não está no banco de dados).");
                Debug.LogWarning($"[NetworkInventory] Item '{foundSlot.ItemId}' não encontrado no ItemDatabase.");
                return;
            }

            if (!itemData.IsEquipment)
            {
                _netPlayer.RpcShowMessageToOwner("Este item não pode ser equipado.");
                return;
            }

            EquipmentSlot itemSlot   = itemData.EquipSlot;
            EquipmentSlot targetSlot = (EquipmentSlot)targetSlotByte;

            if (targetSlot == EquipmentSlot.None)
                targetSlot = ResolveAutoEquipSlot(itemSlot);

            if (!EquipmentSlotEx.IsActive(targetSlot))
            {
                _netPlayer.RpcShowMessageToOwner("Slot de equipamento inválido.");
                return;
            }

            if (!EquipmentSlotEx.CanItemFitInSlot(itemSlot, targetSlot))
            {
                _netPlayer.RpcShowMessageToOwner(
                    $"Este item não vai no slot {EquipmentSlotEx.DisplayName(targetSlot)}.");
                return;
            }

            if (!ServerValidateRequirements(itemData, out string reason))
            {
                _netPlayer.RpcShowMessageToOwner(reason);
                return;
            }

            int    existingIdx = ServerFindEquippedIndex(targetSlot);
            string oldItemId   = "";
            if (existingIdx >= 0)
                oldItemId = EquippedItems[existingIdx].ItemId;

            if (!TrySwapFromInventory(inventorySlotIndex, itemData.ItemId,
                                      oldItemId, out string swapError))
            {
                _netPlayer.RpcShowMessageToOwner(swapError);
                return;
            }

            if (existingIdx >= 0)
                EquippedItems.RemoveAt(existingIdx);

            int maxDur = Mathf.Max(0, itemData.MaxDurability);
            EquippedItems.Add(new EquippedItemData
            {
                Slot          = (byte)targetSlot,
                ItemId        = itemData.ItemId,
                Durability    = maxDur > 0 ? maxDur : -1,
                MaxDurability = maxDur
            });

            _netPlayer.ServerOnEquipmentChanged();
        }

        [Server]
        private bool TryGetInventorySlot(int slotIndex, out InventorySlotData found)
        {
            foreach (var s in Slots)
            {
                if (s.SlotIndex == slotIndex)
                {
                    found = s;
                    return true;
                }
            }
            found = default;
            return false;
        }

        [Server]
        private bool TryGetInventorySlotWithListIndex(int slotIndex,
            out InventorySlotData found, out int listIndex)
        {
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].SlotIndex == slotIndex)
                {
                    found     = Slots[i];
                    listIndex = i;
                    return true;
                }
            }
            found     = default;
            listIndex = -1;
            return false;
        }

        [Server]
        private bool ServerValidateRequirements(ItemData item, out string failReason)
        {
            failReason = null;
            if (item?.Requirements == null) return true;

            CharacterRace race  = _netPlayer.GetRaceEnum();
            var           bonus = StatsCalculator.GetRaceBonus(race);

            int totalSTR = _netPlayer.BaseSTR + bonus.STR + _netPlayer.AllocatedSTR;
            int totalAGI = _netPlayer.BaseAGI + bonus.AGI + _netPlayer.AllocatedAGI;
            int totalVIT = _netPlayer.BaseVIT + bonus.VIT + _netPlayer.AllocatedVIT;
            int totalDEX = _netPlayer.BaseDEX + bonus.DEX + _netPlayer.AllocatedDEX;
            int totalINT = _netPlayer.BaseINT + bonus.INT + _netPlayer.AllocatedINT;
            int totalLUK = _netPlayer.BaseLUK + bonus.LUK + _netPlayer.AllocatedLUK;

            return item.Requirements.Check(
                _netPlayer.Level,
                totalSTR, totalAGI, totalVIT, totalDEX, totalINT, totalLUK,
                race, out failReason);
        }

        [Server]
        private EquipmentSlot ResolveAutoEquipSlot(EquipmentSlot itemSlot)
        {
            if (EquipmentSlotEx.IsRing(itemSlot))
            {
                if (string.IsNullOrEmpty(ServerGetEquipped(EquipmentSlot.Ring1))) return EquipmentSlot.Ring1;
                if (string.IsNullOrEmpty(ServerGetEquipped(EquipmentSlot.Ring2))) return EquipmentSlot.Ring2;
                return EquipmentSlot.Ring1;
            }

            if (EquipmentSlotEx.IsEarring(itemSlot))
            {
                if (string.IsNullOrEmpty(ServerGetEquipped(EquipmentSlot.Earring1))) return EquipmentSlot.Earring1;
                if (string.IsNullOrEmpty(ServerGetEquipped(EquipmentSlot.Earring2))) return EquipmentSlot.Earring2;
                return EquipmentSlot.Earring1;
            }

            return itemSlot;
        }

        // ══════════════════════════════════════════════════════════════════
        // JOIAS DO PODER — Commands
        // ══════════════════════════════════════════════════════════════════

        [Command]
        public void CmdEquipGem(int skillSlotIndex, int inventorySlotIndex)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            if (skillSlotIndex < 0 || skillSlotIndex >= GEM_SLOT_COUNT)
            {
                _netPlayer.RpcShowMessageToOwner("Slot de joia inválido.");
                return;
            }

            if (ItemDatabase.Instance == null)
            {
                _netPlayer.RpcShowMessageToOwner("Banco de itens indisponível. Tente novamente em instantes.");
                Debug.LogError("[NetworkInventory] CmdEquipGem: ItemDatabase.Instance é null.");
                return;
            }

            if (!TryGetInventorySlot(inventorySlotIndex, out var foundSlot))
            {
                _netPlayer.RpcShowMessageToOwner("Joia não encontrada no inventário.");
                return;
            }

            var itemData = ItemDatabase.Instance.GetItem(foundSlot.ItemId);
            if (itemData == null)
            {
                _netPlayer.RpcShowMessageToOwner("Joia inválida (não está no banco de dados).");
                return;
            }

            if (!itemData.IsPowerGem)
            {
                _netPlayer.RpcShowMessageToOwner("Este item não é uma Joia do Poder.");
                return;
            }

            string oldGemId = GetGemItemId(skillSlotIndex);

            if (!TrySwapFromInventory(inventorySlotIndex, itemData.ItemId,
                                      oldGemId, out string swapError))
            {
                _netPlayer.RpcShowMessageToOwner(swapError);
                return;
            }

            ServerSetGemSlot(skillSlotIndex, itemData.ItemId);
        }

        [Command]
        public void CmdUnequipGem(int skillSlotIndex)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            if (skillSlotIndex < 0 || skillSlotIndex >= GEM_SLOT_COUNT)
            {
                _netPlayer.RpcShowMessageToOwner("Slot inválido.");
                return;
            }

            string gemId = GetGemItemId(skillSlotIndex);
            if (string.IsNullOrEmpty(gemId)) return;

            int newSlot = ServerAddItem(gemId, 1);
            if (newSlot < 0)
            {
                _netPlayer.RpcShowMessageToOwner("Inventário cheio!");
                return;
            }

            ServerSetGemSlot(skillSlotIndex, "");
        }

        [Server]
        private void ServerSetGemSlot(int index, string itemId)
        {
            switch (index)
            {
                case 0: GemSlotQ = itemId ?? ""; break;
                case 1: GemSlotW = itemId ?? ""; break;
                case 2: GemSlotE = itemId ?? ""; break;
                case 3: GemSlotR = itemId ?? ""; break;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // INVENTÁRIO — Commands diversos
        // ══════════════════════════════════════════════════════════════════

        [Command]
        public void CmdRemoveItem(int inventorySlotIndex)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            if (!ServerRemoveSlot(inventorySlotIndex))
                _netPlayer.RpcShowMessageToOwner("Item não encontrado.");
        }

        [Command]
        public void CmdUseConsumable(int inventorySlotIndex)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            if (ItemDatabase.Instance == null)
            {
                _netPlayer.RpcShowMessageToOwner("Banco de itens indisponível.");
                Debug.LogError("[NetworkInventory] CmdUseConsumable: ItemDatabase.Instance nulo.");
                return;
            }

            if (!TryGetInventorySlotWithListIndex(inventorySlotIndex,
                    out var foundSlot, out int listIndex))
                return;

            var itemData = ItemDatabase.Instance.GetItem(foundSlot.ItemId);
            if (itemData == null || !itemData.IsConsumable) return;

            float heal = SanitizeBuff(itemData.HealAmount);
            float mana = SanitizeBuff(itemData.ManaAmount);

            if (!CanConsumableHaveEffect(heal, mana, out string rejectMsg))
            {
                _netPlayer.RpcShowMessageToOwner(rejectMsg);
                return;
            }

            if (heal > 0f) _netPlayer.ServerApplyHeal(heal);
            if (mana > 0f) _netPlayer.ServerRestoreMP(mana);

            ServerConsumeOneFromSlot(listIndex, foundSlot);
        }

        [Server]
        private bool CanConsumableHaveEffect(float heal, float mana, out string rejectMsg)
        {
            bool restoresHP = heal > 0f;
            bool restoresMP = mana > 0f;

            if (!restoresHP && !restoresMP)
            {
                rejectMsg = "Este item não tem efeito.";
                return false;
            }

            bool hpFull = _netPlayer.CurrentHP >= _netPlayer.MaxHP - 0.01f;
            bool mpFull = _netPlayer.CurrentMP >= _netPlayer.MaxMP - 0.01f;

            if (restoresHP && !restoresMP && hpFull)
            {
                rejectMsg = "Você já está com HP máximo!";
                return false;
            }

            if (!restoresHP && restoresMP && mpFull)
            {
                rejectMsg = "Você já está com MP máximo!";
                return false;
            }

            if (restoresHP && restoresMP && hpFull && mpFull)
            {
                rejectMsg = "HP e MP já estão no máximo!";
                return false;
            }

            rejectMsg = null;
            return true;
        }

        [Server]
        private void ServerConsumeOneFromSlot(int listIndex, InventorySlotData slot)
        {
            if (listIndex < 0 || listIndex >= Slots.Count) return;

            string itemId = slot.ItemId;

            if (slot.Quantity > 1)
            {
                slot.Quantity -= 1;
                Slots[listIndex] = slot;
            }
            else
            {
                Slots.RemoveAt(listIndex);
            }

            NotifyQuestCollectItem(itemId);
        }

        private static float SanitizeBuff(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
            return Mathf.Clamp(value, 0f, GameConstants.Combat.MAX_HP);
        }

        // ══════════════════════════════════════════════════════════════════
        // JOIAS — Leitura
        // ══════════════════════════════════════════════════════════════════

        public string GetGemItemId(int skillSlotIndex) => skillSlotIndex switch
        {
            0 => GemSlotQ ?? "",
            1 => GemSlotW ?? "",
            2 => GemSlotE ?? "",
            3 => GemSlotR ?? "",
            _ => ""
        };

        public SkillData GetEquippedSkill(int skillSlotIndex)
        {
            string gemId = GetGemItemId(skillSlotIndex);
            if (string.IsNullOrEmpty(gemId)) return null;
            return ItemDatabase.Instance?.GetItem(gemId)?.EmbeddedSkill;
        }

        public int EquippedGemCount()
        {
            int count = 0;
            for (int i = 0; i < GEM_SLOT_COUNT; i++)
                if (!string.IsNullOrEmpty(GetGemItemId(i))) count++;
            return count;
        }

        // ══════════════════════════════════════════════════════════════════
        // EQUIPAMENTO — Agregação de bônus
        // ══════════════════════════════════════════════════════════════════

        public EquipmentBonuses BuildEquipmentBonuses()
            => EquipmentSlotEx.AggregateBonuses(EquippedItems);

        public int  EquippedItemCount() => EquippedItems.Count;
        public int  FreeSlotCount()     => Mathf.Max(0, MAX_INVENTORY_SLOTS - Slots.Count);
        public bool IsFull()            => Slots.Count >= MAX_INVENTORY_SLOTS;

        public int GetTotalQuantity(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return 0;
            int total = 0;
            foreach (var slot in Slots)
                if (slot.ItemId == itemId) total += slot.Quantity;
            return total;
        }
    }
}