using System;
using UnityEngine;

namespace RPG.Quest
{
    /// <summary>
    /// Tipo de objetivo que uma quest pode ter.
    /// Adicionar novos tipos aqui requer suporte em QuestManager.NotifyEvent.
    /// </summary>
    public enum QuestObjectiveType : byte
    {
        /// <summary>Matar X monstros com determinado MonsterId.</summary>
        KillMonster   = 0,

        /// <summary>Coletar X itens com determinado ItemId (verificado no inventário).</summary>
        CollectItem   = 1,

        /// <summary>Conversar com NPC específico (NpcId).</summary>
        TalkToNPC     = 2,

        /// <summary>Alcançar uma região (definida por ReachZoneId no mundo).</summary>
        ReachLocation = 3,

        /// <summary>Atingir um nível mínimo (TargetCount = nível alvo).</summary>
        ReachLevel    = 4,
    }

    [Serializable]
    public class QuestObjective
    {
        [Tooltip("Tipo do objetivo. Determina como TargetId e TargetCount são interpretados.")]
        public QuestObjectiveType Type = QuestObjectiveType.KillMonster;

        [Tooltip("Identificador-alvo:\n" +
                 "• KillMonster: MonsterId (ex: 'wolf_brown')\n" +
                 "• CollectItem: ItemId (ex: 'iron_ore')\n" +
                 "• TalkToNPC: NpcId (ex: 'npc_blacksmith')\n" +
                 "• ReachLocation: ZoneId (ex: 'ancient_ruins')\n" +
                 "• ReachLevel: vazio (usa apenas TargetCount)")]
        public string TargetId = "";

        [Tooltip("Quantidade necessária. Para ReachLevel, é o nível alvo.")]
        [Min(1)]
        public int TargetCount = 1;

        [Tooltip("Descrição mostrada no log de quests. Use {0} para o progresso.\n" +
                 "Ex: 'Mate {0}/3 lobos marrons'.")]
        public string Description = "Objetivo {0}/{1}";

        [Tooltip("Se true, o jogador NÃO pode finalizar este objetivo até completar os anteriores.\n" +
                 "Útil para quests narrativas em sequência.")]
        public bool Sequential = false;

        /// <summary>Validação no Editor — chamada por QuestDefinition.OnValidate.</summary>
        public bool IsValid(out string error)
        {
            error = null;
            if (TargetCount < 1)
            {
                error = $"TargetCount inválido ({TargetCount}).";
                return false;
            }

            if (Type != QuestObjectiveType.ReachLevel && string.IsNullOrWhiteSpace(TargetId))
            {
                error = $"TargetId obrigatório para tipo {Type}.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Formata a descrição com {0} = progresso, {1} = total.
        /// Falha silenciosa se Description não tiver placeholders.
        /// </summary>
        public string FormatDescription(int currentProgress)
        {
            try
            {
                return string.Format(Description, currentProgress, TargetCount);
            }
            catch (FormatException)
            {
                return $"{Description} ({currentProgress}/{TargetCount})";
            }
        }
    }

    /// <summary>
    /// Recompensa concedida ao completar uma quest.
    /// Todos os campos são opcionais — uma quest pode dar apenas XP, apenas itens, ou ambos.
    /// </summary>
    [Serializable]
    public class QuestReward
    {
        [Tooltip("XP concedido. 0 = nenhum.")]
        [Min(0)]
        public long Experience = 0;

        [Tooltip("Itens entregues (ItemId). Ignora itens que não existem no ItemDatabase.")]
        public ItemReward[] Items = new ItemReward[0];

        [Tooltip("Quest desbloqueada após esta (QuestId). Vazio = nenhuma.")]
        public string UnlocksQuestId = "";

        [Serializable]
        public class ItemReward
        {
            public string ItemId = "";
            [Min(1)] public int Quantity = 1;
        }

        public bool HasAnyReward()
            => Experience > 0
            || (Items != null && Items.Length > 0)
            || !string.IsNullOrEmpty(UnlocksQuestId);
    }
}
