using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Mirror;
using RPG.Quest;

namespace RPG.UI
{

    public class QuestLogUI : MonoBehaviour
    {
        public static QuestLogUI Instance { get; private set; }

        [Header("Painel raiz")]
        [SerializeField] private GameObject panel;
        [SerializeField] private Button     closeButton;

        [Header("Lista")]
        [SerializeField] private Transform  listContainer;
        [SerializeField] private GameObject questEntryPrefab;

        [Header("Detalhes")]
        [SerializeField] private TMP_Text   titleText;
        [SerializeField] private TMP_Text   descriptionText;
        [SerializeField] private TMP_Text   objectivesText;
        [SerializeField] private TMP_Text   rewardText;
        [SerializeField] private Button     abandonButton;

        [Header("Atalhos")]
        [SerializeField] private KeyCode toggleKey     = KeyCode.L;
        [SerializeField] private bool    closeOnEscape = true;

        private QuestManager _qm;
        private string       _selectedQuestId;
        private bool         _subscribed;
        private readonly List<GameObject> _pooledEntries = new List<GameObject>();

        // ══════════════════════════════════════════════════════════════════
        // Lifecycle
        // ══════════════════════════════════════════════════════════════════

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            if (panel != null) panel.SetActive(false);

            if (closeButton   != null) closeButton.onClick.AddListener(Close);
            if (abandonButton != null) abandonButton.onClick.AddListener(OnAbandonClicked);
        }

        private void OnDestroy()
        {
            if (closeButton   != null) closeButton.onClick.RemoveListener(Close);
            if (abandonButton != null) abandonButton.onClick.RemoveListener(OnAbandonClicked);
            UnsubscribeFromQM();
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (UIInputUtils.IsTypingInInputField()) return;

            if (toggleKey != KeyCode.None && Input.GetKeyDown(toggleKey))
                Toggle();
            else if (closeOnEscape && IsOpen && Input.GetKeyDown(KeyCode.Escape))
                Close();
        }

        public bool IsOpen => panel != null && panel.activeSelf;

        // ══════════════════════════════════════════════════════════════════
        // Bind ao QuestManager local
        // ══════════════════════════════════════════════════════════════════

        private bool TryBindQuestManager()
        {
            if (_qm != null) return true;
            if (NetworkClient.localPlayer == null) return false;

            _qm = NetworkClient.localPlayer.GetComponent<QuestManager>();
            if (_qm == null) return false;

            SubscribeToQM();
            return true;
        }

        private void SubscribeToQM()
        {
            if (_subscribed || _qm == null) return;
            _qm.OnQuestsChanged += RefreshList;
            _subscribed = true;
        }

        private void UnsubscribeFromQM()
        {
            if (!_subscribed || _qm == null) return;
            _qm.OnQuestsChanged -= RefreshList;
            _subscribed = false;
        }

        // ══════════════════════════════════════════════════════════════════
        // Open/Close/Refresh
        // ══════════════════════════════════════════════════════════════════

        public void Toggle() { if (IsOpen) Close(); else Open(); }

        public void Open()
        {
            if (panel == null) return;
            panel.SetActive(true);
            if (!TryBindQuestManager())
            {
                if (titleText       != null) titleText.text       = "";
                if (descriptionText != null) descriptionText.text = "Conectando...";
                return;
            }
            RefreshList();
        }

        public void Close()
        {
            if (panel != null) panel.SetActive(false);
        }

        private void RefreshList()
        {
            ClearList();
            if (_qm == null) return;

            // Constrói entradas em ordem: ReadyToTurnIn → Active → resto
            var ordered = new List<QuestProgress>(_qm.Quests.Count);
            foreach (var q in _qm.Quests)
                if (q.State == QuestState.ReadyToTurnIn) ordered.Add(q);
            foreach (var q in _qm.Quests)
                if (q.State == QuestState.Active) ordered.Add(q);

            // Quests Completed não são listadas (mantemos foco no que está em andamento)

            if (ordered.Count == 0)
            {
                if (titleText       != null) titleText.text       = "Sem quests ativas";
                if (descriptionText != null) descriptionText.text = "Procure NPCs com [!] sobre a cabeça.";
                if (objectivesText  != null) objectivesText.text  = "";
                if (rewardText      != null) rewardText.text      = "";
                if (abandonButton   != null) abandonButton.gameObject.SetActive(false);
                _selectedQuestId = null;
                return;
            }

            if (listContainer == null || questEntryPrefab == null) return;

            foreach (var progress in ordered)
            {
                var def = QuestDatabase.Instance?.GetQuest(progress.QuestId);
                if (def == null) continue;

                var go    = Instantiate(questEntryPrefab, listContainer);
                _pooledEntries.Add(go);

                var btn   = go.GetComponent<Button>();
                var label = go.GetComponentInChildren<TMP_Text>();

                if (label != null)
                {
                    string prefix = progress.State == QuestState.ReadyToTurnIn
                        ? "<color=#88FF88>[?]</color> "
                        : "<color=#FFD700>[!]</color> ";
                    label.text = prefix + def.DisplayName;
                }

                if (btn != null)
                {
                    var capturedId = progress.QuestId;
                    btn.onClick.AddListener(() => SelectQuest(capturedId));
                }
            }

            // Manter seleção atual, ou selecionar primeira
            if (string.IsNullOrEmpty(_selectedQuestId)
                || _qm.FindIndexById(_selectedQuestId) < 0)
            {
                _selectedQuestId = ordered[0].QuestId;
            }
            RefreshDetails();
        }

        private void SelectQuest(string questId)
        {
            _selectedQuestId = questId;
            RefreshDetails();
        }

        private void RefreshDetails()
        {
            if (_qm == null || string.IsNullOrEmpty(_selectedQuestId)) return;

            var def = QuestDatabase.Instance?.GetQuest(_selectedQuestId);
            var p   = _qm.FindByIdNullable(_selectedQuestId);
            if (def == null || !p.HasValue) return;

            if (titleText       != null) titleText.text       = def.DisplayName;
            if (descriptionText != null) descriptionText.text = def.Description;

            if (objectivesText != null)
                objectivesText.text = BuildObjectivesProgress(def, p.Value);

            if (rewardText != null)
                rewardText.text = BuildRewardsPreview(def);

            if (abandonButton != null)
                abandonButton.gameObject.SetActive(p.Value.State == QuestState.Active);
        }

        private static string BuildObjectivesProgress(QuestDefinition def, QuestProgress p)
        {
            if (def.Objectives == null || def.Objectives.Length == 0)
                return "";

            var arr = p.GetProgressArray(def.ObjectiveCount);
            var sb  = new System.Text.StringBuilder(128);
            sb.AppendLine("<b>Objetivos:</b>");

            for (int i = 0; i < def.Objectives.Length; i++)
            {
                var obj = def.Objectives[i];
                if (obj == null) continue;
                bool done = arr[i] >= obj.TargetCount;
                string color = done ? "#88FF88" : "#FFFFFF";
                string check = done ? "✓ " : "• ";
                sb.AppendLine($"<color={color}>{check}{obj.FormatDescription(arr[i])}</color>");
            }
            return sb.ToString().TrimEnd();
        }

        private static string BuildRewardsPreview(QuestDefinition def)
        {
            if (def.Reward == null || !def.Reward.HasAnyReward()) return "";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<b>Recompensa:</b>");
            if (def.Reward.Experience > 0)
                sb.AppendLine($"• <color=#88AAFF>{def.Reward.Experience} XP</color>");
            if (def.Reward.Items != null)
            {
                foreach (var item in def.Reward.Items)
                {
                    if (item == null || string.IsNullOrEmpty(item.ItemId)) continue;
                    var data = RPG.Data.ItemDatabase.Instance?.GetItem(item.ItemId);
                    string name = data?.DisplayName ?? item.ItemId;
                    Color  c    = data?.RarityColor ?? Color.white;
                    string hex  = ColorUtility.ToHtmlStringRGB(c);
                    string qty  = item.Quantity > 1 ? $" ×{item.Quantity}" : "";
                    sb.AppendLine($"• <color=#{hex}>{name}</color>{qty}");
                }
            }
            return sb.ToString().TrimEnd();
        }

        private void OnAbandonClicked()
        {
            if (_qm == null || string.IsNullOrEmpty(_selectedQuestId)) return;
            _qm.CmdAbandonQuest(_selectedQuestId);
            _selectedQuestId = null;
        }

        private void ClearList()
        {
            foreach (var go in _pooledEntries)
            {
                if (go == null) continue;
                var btn = go.GetComponent<Button>();
                if (btn != null) btn.onClick.RemoveAllListeners();
                Destroy(go);
            }
            _pooledEntries.Clear();
        }
    }
}
