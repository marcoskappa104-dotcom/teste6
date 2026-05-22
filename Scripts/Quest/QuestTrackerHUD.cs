using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Mirror;
using RPG.Quest;

namespace RPG.UI
{

    public class QuestTrackerHUD : MonoBehaviour
    {
        [SerializeField] private GameObject root;
        [SerializeField] private TMP_Text   trackerText;
        [SerializeField] private int        maxQuestsToShow      = 3;
        [SerializeField] private float      bindRetryInterval    = 0.5f;

        private QuestManager _qm;
        private bool         _subscribed;
        private readonly StringBuilder _sb = new StringBuilder(512);

        private void OnEnable()
        {
            InvokeRepeating(nameof(TryBindLoop), 0f, bindRetryInterval);
        }

        private void OnDisable()
        {
            CancelInvoke(nameof(TryBindLoop));
            Unsubscribe();
        }

        private void OnDestroy() => Unsubscribe();

        private void TryBindLoop()
        {
            if (_qm != null) { CancelInvoke(nameof(TryBindLoop)); return; }
            if (!NetworkClient.active) return;
            if (NetworkClient.localPlayer == null) return;

            _qm = NetworkClient.localPlayer.GetComponent<QuestManager>();
            if (_qm == null) return;

            Subscribe();
            CancelInvoke(nameof(TryBindLoop));
            Refresh();
        }

        private void Subscribe()
        {
            if (_subscribed || _qm == null) return;
            _qm.OnQuestsChanged += Refresh;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed || _qm == null) return;
            _qm.OnQuestsChanged -= Refresh;
            _subscribed = false;
        }

        private void Refresh()
        {
            if (trackerText == null || _qm == null) { SetVisible(false); return; }

            // Coleta quests Active/Ready em ordem de prioridade
            var ordered = new List<QuestProgress>(maxQuestsToShow);
            foreach (var q in _qm.Quests)
            {
                if (q.State == QuestState.ReadyToTurnIn) ordered.Add(q);
                if (ordered.Count >= maxQuestsToShow) break;
            }
            foreach (var q in _qm.Quests)
            {
                if (ordered.Count >= maxQuestsToShow) break;
                if (q.State == QuestState.Active) ordered.Add(q);
            }

            if (ordered.Count == 0) { SetVisible(false); return; }

            _sb.Clear();
            for (int i = 0; i < ordered.Count; i++)
            {
                if (i > 0) _sb.AppendLine();

                var p   = ordered[i];
                var def = QuestDatabase.Instance?.GetQuest(p.QuestId);
                if (def == null) continue;

                string nameColor = p.State == QuestState.ReadyToTurnIn ? "#88FF88" : "#FFD700";
                _sb.AppendLine($"<color={nameColor}><b>{def.DisplayName}</b></color>");

                if (p.State == QuestState.ReadyToTurnIn)
                {
                    _sb.AppendLine("  <color=#88FF88>Retorne ao NPC.</color>");
                    continue;
                }

                var arr = p.GetProgressArray(def.ObjectiveCount);
                for (int o = 0; o < def.ObjectiveCount; o++)
                {
                    var obj = def.GetObjective(o);
                    if (obj == null) continue;

                    // Não mostra objetivos sequenciais ainda bloqueados
                    if (!def.CanObjectiveBeAdvanced(o, arr)) continue;

                    bool done = arr[o] >= obj.TargetCount;
                    string color = done ? "#88FF88" : "#CCCCCC";
                    string mark  = done ? "✓" : "•";
                    _sb.AppendLine($"  <color={color}>{mark} {obj.FormatDescription(arr[o])}</color>");
                }
            }

            string txt = _sb.ToString().TrimEnd();
            trackerText.text = txt;
            SetVisible(!string.IsNullOrEmpty(txt));
        }

        private void SetVisible(bool show)
        {
            if (root != null) root.SetActive(show);
            else if (trackerText != null) trackerText.gameObject.SetActive(show);
        }
    }
}
