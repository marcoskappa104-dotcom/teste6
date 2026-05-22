using System.Collections.Generic;
using UnityEngine;

namespace RPG.Quest
{
stIds via este lookup; apenas IDs trafegam na rede.
    /// </summary>
    public class QuestDatabase : MonoBehaviour
    {
        public static QuestDatabase Instance { get; private set; }

        [Header("Registre TODAS as quests do jogo aqui")]
        [SerializeField] private List<QuestDefinition> allQuests = new List<QuestDefinition>();

        private readonly Dictionary<string, QuestDefinition> _lookup = new Dictionary<string, QuestDefinition>();

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            BuildLookup();
        }

        private void BuildLookup()
        {
            _lookup.Clear();
            foreach (var quest in allQuests)
            {
                if (quest == null) continue;

                if (string.IsNullOrEmpty(quest.QuestId))
                {
                    Debug.LogError($"[QuestDatabase] '{quest.name}' tem QuestId vazio.");
                    continue;
                }

                if (_lookup.ContainsKey(quest.QuestId))
                {
                    Debug.LogError($"[QuestDatabase] ID duplicado: '{quest.QuestId}' em '{quest.name}'.");
                    continue;
                }

                _lookup[quest.QuestId] = quest;
            }
            Debug.Log($"[QuestDatabase] {_lookup.Count} quests registradas.");
        }

        public QuestDefinition GetQuest(string questId)
        {
            if (string.IsNullOrEmpty(questId)) return null;
            _lookup.TryGetValue(questId, out var q);
            return q;
        }

        public bool Contains(string questId)
            => !string.IsNullOrEmpty(questId) && _lookup.ContainsKey(questId);

        public IReadOnlyList<QuestDefinition> GetAll() => allQuests;
    }
}
