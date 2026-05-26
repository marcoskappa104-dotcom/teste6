using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Mirror;
using RPG.Network;
using System.Collections;
using System.Collections.Generic;

namespace RPG.UI
{
    public class ChatUI : MonoBehaviour
    {
        public static ChatUI Instance { get; private set; }

        [Header("UI Components")]
        [SerializeField] private GameObject      chatPanel;
        [SerializeField] private TMP_Text       chatHistoryText;
        [SerializeField] private TMP_InputField chatInputField;
        [SerializeField] private ScrollRect     scrollRect;

        [Header("Settings")]
        [SerializeField] private int maxMessages = 50;
        [SerializeField] private Color globalColor = Color.white;
        [SerializeField] private Color partyColor  = new Color(0.3f, 0.8f, 1f); // Azul claro
        [SerializeField] private Color systemColor = Color.yellow;

        private readonly List<string> _messages = new List<string>();
        private float _lastSubmitTime;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            if (chatHistoryText != null) chatHistoryText.text = "";
            
            // Configura o InputField para modo Single Line e vincula o evento de Submit
            if (chatInputField != null)
            {
                chatInputField.lineType = TMP_InputField.LineType.SingleLine;
                chatInputField.onSubmit.AddListener(OnSubmit);
            }

            // Registra o handler de mensagem no cliente
            NetworkClient.RegisterHandler<MsgChatMessage>(OnReceiveMessage);
        }

        private void Update()
        {
            // O Update agora só cuida de abrir o chat se ele não estiver focado
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                // Proteção: não abre o chat se acabou de enviar uma mensagem (evita abrir/fechar no mesmo frame)
                if (Time.time - _lastSubmitTime < 0.15f) return;

                if (chatInputField != null && !chatInputField.isFocused)
                {
                    chatInputField.ActivateInputField();
                    chatInputField.Select();
                }
            }
        }

        private void OnSubmit(string text)
        {
            _lastSubmitTime = Time.time;

            // Captura o texto antes de limpar
            string messageToSend = text.Trim();

            // LIMPEZA AGRESSIVA: Limpa o texto de todas as formas possíveis no Unity
            if (chatInputField != null)
            {
                chatInputField.text = "";
                chatInputField.SetTextWithoutNotify("");
                chatInputField.DeactivateInputField();
            }
            
            // Força a limpeza visual da seleção do sistema de eventos
            UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null);

            // Se a mensagem for vazia, apenas sai
            if (string.IsNullOrWhiteSpace(messageToSend)) return;

            Debug.Log($"[ChatUI] Enviando para rede: {messageToSend}");

            // Lógica de canais
            ChatChannel channel = ChatChannel.Global;
            if (messageToSend.StartsWith("/p "))
            {
                channel = ChatChannel.Party;
                messageToSend = messageToSend.Substring(3).Trim();
            }

            if (!string.IsNullOrEmpty(messageToSend))
            {
                if (NetworkClient.active)
                {
                    NetworkClient.Send(new MsgChatMessage
                    {
                        Channel = channel,
                        Text = messageToSend
                    });
                }
            }
        }

        private void OnReceiveMessage(MsgChatMessage msg)
        {
            Debug.Log($"[ChatUI] Mensagem recebida de {msg.SenderName}: {msg.Text}");
            string colorHex = ColorUtility.ToHtmlStringRGB(GetColorForChannel(msg.Channel));
            string prefix = GetPrefixForChannel(msg.Channel);
            
            string formattedMessage = $"<color=#{colorHex}>{prefix}<b>{msg.SenderName}:</b> {msg.Text}</color>";
            
            _messages.Add(formattedMessage);
            if (_messages.Count > maxMessages) _messages.RemoveAt(0);

            UpdateChatDisplay();
        }

        private void UpdateChatDisplay()
        {
            if (chatHistoryText == null) return;
            
            chatHistoryText.text = string.Join("\n", _messages);
            Debug.Log($"[ChatUI] Histórico atualizado. Total de mensagens: {_messages.Count}");
            
            // Auto-scroll para o fim
            if (scrollRect != null)
            {
                Canvas.ForceUpdateCanvases();
                scrollRect.verticalNormalizedPosition = 0f;
            }
        }

        private Color GetColorForChannel(ChatChannel channel)
        {
            return channel switch
            {
                ChatChannel.Global => globalColor,
                ChatChannel.Party  => partyColor,
                ChatChannel.System => systemColor,
                _ => Color.white
            };
        }

        private string GetPrefixForChannel(ChatChannel channel)
        {
            return channel switch
            {
                ChatChannel.Global => "[Global] ",
                ChatChannel.Party  => "[Grupo] ",
                ChatChannel.System => "[SISTEMA] ",
                _ => ""
            };
        }
    }
}
