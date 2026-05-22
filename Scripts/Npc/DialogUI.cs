using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Mirror;
using RPG.Quest;
using RPG.NPC;

namespace RPG.UI
{

    public class DialogUI : MonoBehaviour
    {
        public static DialogUI Instance { get; private set; }

        [Header("Painel raiz")]
        [SerializeField] private GameObject panel;
        [SerializeField] private Button     closeButton;

        [Header("Header")]
        [SerializeField] private TMP_Text npcNameText;
        [SerializeField] private TMP_Text npcRoleText;

        [Header("Conteúdo")]
        [SerializeField] private TMP_Text greetingText;

        [Header("Lista de Opções")]
        [SerializeField] private Transform  optionsContainer;
        [SerializeField] private GameObject optionButtonPrefab;

        [Header("Sub-painel: detalhes da quest")]
        [SerializeField] private GameObject questDetailsPanel;
        [SerializeField] private TMP_Text   questTitleText;
        [SerializeField] private TMP_Text   questDescriptionText;
        [SerializeField] private TMP_Text   questObjectivesText;
        [SerializeField] private TMP_Text   questRewardsText;
        [SerializeField] private Button     questAcceptButton;
        [SerializeField] private Button     questCompleteButton;
        [SerializeField] private Button     questBackButton;

        [Header("Atalhos de teclado")]
        [SerializeField] private bool    closeOnEscape = true;

        private uint                   _activeNpcNetId;
        private NpcInteractionSnapshot _currentSnapshot;
        private QuestDefinition        _viewingQuest;
        private NpcQuestOption         _viewingOption;
        private readonly List<GameObject> _pooledOptions = new List<GameObject>();

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            if (panel != null) panel.SetActive(false);
            if (questDetailsPanel != null) questDetailsPanel.SetActive(false);

            if (closeButton        != null) closeButton.onClick.AddListener(Close);
            if (questAcceptButton  != null) questAcceptButton.onClick.AddListener(OnAcceptClicked);
            if (questCompleteButton != null) questCompleteButton.onClick.AddListener(OnCompleteClicked);
            if (questBackButton    != null) questBackButton.onClick.AddListener(ShowMainPage);
        }

        private void OnDestroy()
        {
            if (closeButton         != null) closeButton.onClick.RemoveListener(Close);
            if (questAcceptButton   != null) questAcceptButton.onClick.RemoveListener(OnAcceptClicked);
            if (questCompleteButton != null) questCompleteButton.onClick.RemoveListener(OnCompleteClicked);
            if (questBackButton     != null) questBackButton.onClick.RemoveListener(ShowMainPage);

            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (!IsOpen) return;
            if (UIInputUtils.IsTypingInInputField()) return;
            if (closeOnEscape && Input.GetKeyDown(KeyCode.Escape)) Close();
        }

        public bool IsOpen => panel != null && panel.activeSelf;

        // ══════════════════════════════════════════════════════════════════
        // API pública
        // ══════════════════════════════════════════════════════════════════

        public void OpenForNpc(uint npcNetId, NpcInteractionSnapshot snapshot)
        {
            _activeNpcNetId  = npcNetId;
            _currentSnapshot = snapshot;

            if (panel != null) panel.SetActive(true);
            ShowMainPage();
        }

        public void Close()
        {
            _activeNpcNetId = 0;
            _viewingQuest   = null;

            if (panel != null) panel.SetActive(false);
            if (questDetailsPanel != null) questDetailsPanel.SetActive(false);
            ClearOptions();
        }

        // ══════════════════════════════════════════════════════════════════
        // Páginas
        // ══════════════════════════════════════════════════════════════════

        private void ShowMainPage()
        {
            if (questDetailsPanel != null) questDetailsPanel.SetActive(false);

            if (npcNameText    != null) npcNameText.text    = _currentSnapshot.DisplayName ?? "";
            if (npcRoleText    != null) npcRoleText.text    = _currentSnapshot.Role ?? "";
            if (greetingText   != null) greetingText.text   = _currentSnapshot.Greeting ?? "";

            BuildOptionButtons();
        }

        private void BuildOptionButtons()
        {
            ClearOptions();
            if (optionsContainer == null || optionButtonPrefab == null) return;
            if (_currentSnapshot.Options == null) return;

            foreach (var option in _currentSnapshot.Options)
            {
                if (option.State == NpcQuestOptionState.Invalid) continue;

                var def = QuestDatabase.Instance?.GetQuest(option.QuestId);
                if (def == null) continue;

                var go  = Instantiate(optionButtonPrefab, optionsContainer);
                _pooledOptions.Add(go);

                var btn   = go.GetComponent<Button>();
                var label = go.GetComponentInChildren<TMP_Text>();

                if (label != null)
                    label.text = FormatOptionLabel(option.State, def.DisplayName);

                bool clickable = option.State == NpcQuestOptionState.Offer
                              || option.State == NpcQuestOptionState.TurnIn
                              || option.State == NpcQuestOptionState.InProgress;

                if (btn != null)
                {
                    btn.interactable = clickable;
                    if (clickable)
                    {
                        var capturedOption = option;
                        var capturedDef    = def;
                        btn.onClick.AddListener(() => ShowQuestDetails(capturedDef, capturedOption));
                    }
                }
            }
        }

        private static string FormatOptionLabel(NpcQuestOptionState state, string title)
        {
            return state switch
            {
                NpcQuestOptionState.Offer       => $"<color=#FFD700>[!]</color> {title}",
                NpcQuestOptionState.TurnIn      => $"<color=#88FF88>[?]</color> {title}",
                NpcQuestOptionState.InProgress  => $"<color=#88AAFF>[…]</color> {title}",
                NpcQuestOptionState.Locked      => $"<color=#888888>[bloqueada]</color> {title}",
                NpcQuestOptionState.AlreadyDone => $"<color=#888888>[completa]</color> {title}",
                _ => title
            };
        }

        private void ShowQuestDetails(QuestDefinition def, NpcQuestOption option)
        {
            if (def == null) return;
            _viewingQuest  = def;
            _viewingOption = option;

            if (questDetailsPanel == null) return;
            questDetailsPanel.SetActive(true);

            if (questTitleText       != null) questTitleText.text       = def.DisplayName;
            if (questDescriptionText != null) questDescriptionText.text = def.Description;

            // Objetivos
            if (questObjectivesText != null)
                questObjectivesText.text = BuildObjectivesPreview(def);

            // Recompensas
            if (questRewardsText != null)
                questRewardsText.text = BuildRewardsPreview(def);

            // Botões: visibilidade depende do State
            bool canAccept   = option.State == NpcQuestOptionState.Offer;
            bool canComplete = option.State == NpcQuestOptionState.TurnIn;

            if (questAcceptButton   != null) questAcceptButton.gameObject.SetActive(canAccept);
            if (questCompleteButton != null) questCompleteButton.gameObject.SetActive(canComplete);
        }

        private static string BuildObjectivesPreview(QuestDefinition def)
        {
            if (def.Objectives == null || def.Objectives.Length == 0)
                return "<i>Sem objetivos.</i>";

            var sb = new System.Text.StringBuilder(128);
            sb.AppendLine("<b>Objetivos:</b>");
            foreach (var obj in def.Objectives)
            {
                if (obj == null) continue;
                sb.AppendLine("• " + obj.FormatDescription(0));
            }
            return sb.ToString().TrimEnd();
        }

        private static string BuildRewardsPreview(QuestDefinition def)
        {
            if (def.Reward == null || !def.Reward.HasAnyReward())
                return "";

            var sb = new System.Text.StringBuilder(128);
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

        // ══════════════════════════════════════════════════════════════════
        // Handlers
        // ══════════════════════════════════════════════════════════════════

        private void OnAcceptClicked()
        {
            if (_viewingQuest == null) return;
            var qm = NetworkClient.localPlayer?.GetComponent<QuestManager>();
            if (qm == null) return;
            qm.CmdAcceptQuest(_activeNpcNetId, _viewingQuest.QuestId);
            Close();
        }

        private void OnCompleteClicked()
        {
            if (_viewingQuest == null) return;
            var qm = NetworkClient.localPlayer?.GetComponent<QuestManager>();
            if (qm == null) return;
            qm.CmdCompleteQuest(_activeNpcNetId, _viewingQuest.QuestId);
            Close();
        }

        // ══════════════════════════════════════════════════════════════════
        // Pool
        // ══════════════════════════════════════════════════════════════════

        private void ClearOptions()
        {
            foreach (var go in _pooledOptions)
            {
                if (go == null) continue;
                var btn = go.GetComponent<Button>();
                if (btn != null) btn.onClick.RemoveAllListeners();
                Destroy(go);
            }
            _pooledOptions.Clear();
        }
    }
}
