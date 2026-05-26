using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Mirror;
using RPG.Network;
using System.Collections.Generic;

namespace RPG.UI
{
    public class PartyMemberWidget : MonoBehaviour
    {
        [SerializeField] private TMP_Text nameText;
        [SerializeField] private TMP_Text levelText;
        [SerializeField] private Slider   hpBar;
        [SerializeField] private Image    leaderIcon;

        public uint MemberNetId { get; private set; }

        public void UpdateInfo(PartyMemberData data)
        {
            MemberNetId = data.NetId;
            if (nameText != null) nameText.text = data.Name;
            if (levelText != null) levelText.text = $"Lv {data.Level}";
            if (hpBar != null) hpBar.value = data.HpPercent;
            if (leaderIcon != null) leaderIcon.enabled = data.IsLeader;
        }
    }

    public class PartyUI : MonoBehaviour
    {
        public static PartyUI Instance { get; private set; }

        [Header("UI Components")]
        [SerializeField] private GameObject      partyPanel;
        [SerializeField] private GameObject      memberPrefab;
        [SerializeField] private Transform       memberContainer;
        [SerializeField] private GameObject      invitePanel;
        [SerializeField] private TMP_Text       inviteText;

        private readonly List<PartyMemberWidget> _widgets = new List<PartyMemberWidget>();
        private uint _pendingInviterNetId;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            if (partyPanel != null) partyPanel.SetActive(false);
            if (invitePanel != null) invitePanel.SetActive(false);

            // Registra handlers no cliente
            NetworkClient.RegisterHandler<MsgPartyUpdate>(OnPartyUpdate);
            NetworkClient.RegisterHandler<MsgPartyInviteReceived>(OnInviteReceived);
        }

        private void OnPartyUpdate(MsgPartyUpdate msg)
        {
            if (partyPanel != null) partyPanel.SetActive(msg.Members.Count > 0);

            // Limpa widgets antigos
            foreach (var widget in _widgets)
            {
                if (widget != null) Destroy(widget.gameObject);
            }
            _widgets.Clear();

            // Cria novos widgets
            foreach (var member in msg.Members)
            {
                var go = Instantiate(memberPrefab, memberContainer);
                var widget = go.GetComponent<PartyMemberWidget>();
                if (widget != null)
                {
                    widget.UpdateInfo(member);
                    _widgets.Add(widget);
                }
            }
        }

        private void OnInviteReceived(MsgPartyInviteReceived msg)
        {
            _pendingInviterNetId = msg.InviterNetId;
            if (inviteText != null) inviteText.text = $"{msg.InviterName} convidou você para o grupo.";
            if (invitePanel != null) invitePanel.SetActive(true);
        }

        public void AcceptInvite()
        {
            NetworkClient.Send(new MsgPartyInviteResponse
            {
                InviterNetId = _pendingInviterNetId,
                Accept = true
            });
            if (invitePanel != null) invitePanel.SetActive(false);
        }

        public void DeclineInvite()
        {
            NetworkClient.Send(new MsgPartyInviteResponse
            {
                InviterNetId = _pendingInviterNetId,
                Accept = false
            });
            if (invitePanel != null) invitePanel.SetActive(false);
        }
    }
}
