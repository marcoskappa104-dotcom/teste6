using System.Collections.Generic;
using UnityEngine;

namespace RPG.Quest
{

    [CreateAssetMenu(menuName = "RPG/Quest Definition", fileName = "Quest_New")]
    public class QuestDefinition : ScriptableObject
    {
        [Header("Identificação")]
        [Tooltip("ID único persistente. NUNCA mude após estar em uso.")]
        public string QuestId = "quest_001";

        public string DisplayName = "Nova Quest";

        [TextArea(3, 6)]
        public string Description = "Descrição da quest...";

        [TextArea(2, 4)]
        [Tooltip("Texto mostrado quando a quest é completada (NPC entregando recompensa).")]
        public string CompletionText = "Obrigado pela ajuda!";

        [Header("Pré-Requisitos")]
        [Tooltip("Nível mínimo para aceitar.")]
        [Min(1)] public int RequiredLevel = 1;

        [Tooltip("QuestIds que devem estar COMPLETADAS antes desta. Vazio = sem requisito.")]
        public string[] RequiredCompletedQuests = new string[0];

        [Header("Comportamento")]
        [Tooltip("Se true, pode ser repetida após completar (diária, semanal, etc).\n" +
                 "Senão, fica marcada como Completed permanentemente.")]
        public bool Repeatable = false;

        [Tooltip("Se true, é entregue automaticamente ao completar objetivos\n" +
                 "(não precisa voltar ao NPC). Use para quests simples ou tutoriais.")]
        public bool AutoComplete = false;

        [Header("Objetivos (em ordem)")]
        public QuestObjective[] Objectives = new QuestObjective[0];

        [Header("Recompensa")]
        public QuestReward Reward = new QuestReward();

        [Header("Visual")]
        public Sprite Icon;

        [Tooltip("Categoria — usada para filtros futuros no QuestLog.")]
        public QuestCategory Category = QuestCategory.Side;

        // ── Helpers ────────────────────────────────────────────────────────

        public int ObjectiveCount => Objectives?.Length ?? 0;

        public QuestObjective GetObjective(int index)
        {
            if (Objectives == null || index < 0 || index >= Objectives.Length) return null;
            return Objectives[index];
        }

        /// <summary>
        /// Verifica se um objetivo específico pode ser atualizado agora
        /// (respeitando objetivos sequenciais).
        /// </summary>
        public bool CanObjectiveBeAdvanced(int objectiveIndex, IReadOnlyList<int> progress)
        {
            if (Objectives == null || objectiveIndex < 0 || objectiveIndex >= Objectives.Length)
                return false;

            var obj = Objectives[objectiveIndex];
            if (!obj.Sequential) return true;

            // Sequential: todos os anteriores precisam estar completos
            for (int i = 0; i < objectiveIndex; i++)
            {
                if (progress == null || i >= progress.Count) return false;
                if (progress[i] < Objectives[i].TargetCount) return false;
            }
            return true;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(QuestId))
                Debug.LogWarning($"[QuestDefinition] '{name}' tem QuestId vazio.");

            if (Objectives == null || Objectives.Length == 0)
                Debug.LogWarning($"[QuestDefinition] '{name}' não tem objetivos definidos.");
            else
            {
                for (int i = 0; i < Objectives.Length; i++)
                {
                    if (!Objectives[i].IsValid(out string err))
                        Debug.LogWarning($"[QuestDefinition] '{name}' objetivo {i}: {err}");
                }
            }

            if (RequiredLevel < 1) RequiredLevel = 1;
        }
#endif
    }

    public enum QuestCategory : byte
    {
        Main      = 0,
        Side      = 1,
        Daily     = 2,
        Weekly    = 3,
        Tutorial  = 4,
        Event     = 5,
    }
}
