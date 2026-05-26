using UnityEngine;
using Mirror;
using RPG.Network;
using System.Collections.Generic;
using NetworkPlayer = RPG.Network.NetworkPlayer;

namespace RPG.Managers
{
    public class ChatManager : MonoBehaviour
    {
        public static ChatManager Instance { get; private set; }

        private const int MAX_CHAT_HISTORY = 50;
        private const int MAX_MESSAGE_LENGTH = 128;
        private const float CHAT_RATE_LIMIT = 0.8f; // Segundos entre mensagens

        private readonly Dictionary<uint, float> _lastMessageTimes = new Dictionary<uint, float>();

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        [Server]
        public void RegisterHandlers()
        {
            NetworkServer.RegisterHandler<MsgChatMessage>(OnServerReceiveMessage);
        }

        [Server]
        private void OnServerReceiveMessage(NetworkConnectionToClient conn, MsgChatMessage msg)
        {
            var player = conn.identity.GetComponent<NetworkPlayer>();
            if (player == null)
            {
                Debug.LogWarning("[ChatManager] Mensagem recebida de conexão sem NetworkPlayer.");
                return;
            }

            Debug.Log($"[ChatManager] Mensagem de {player.CharacterName}: {msg.Text} (Canal: {msg.Channel})");

            // Anti-spam
            if (_lastMessageTimes.TryGetValue(player.netId, out float lastTime))
            {
                if (Time.time - lastTime < CHAT_RATE_LIMIT) return;
            }
            _lastMessageTimes[player.netId] = Time.time;

            // Validação de conteúdo
            string cleanText = msg.Text?.Trim();
            if (string.IsNullOrEmpty(cleanText)) return;
            if (cleanText.Length > MAX_MESSAGE_LENGTH) cleanText = cleanText.Substring(0, MAX_MESSAGE_LENGTH);

            // Monta a mensagem final
            var broadcastMsg = new MsgChatMessage
            {
                Channel = msg.Channel,
                SenderName = player.CharacterName,
                Text = cleanText
            };

            // Lógica por canal
            switch (msg.Channel)
            {
                case ChatChannel.Global:
                    // Envia para TODOS os jogadores conectados
                    NetworkServer.SendToAll(broadcastMsg);
                    break;

                case ChatChannel.Party:
                    if (player.PartyId != 0)
                    {
                        var party = PartyManager.Instance?.GetParty(player.PartyId);
                        if (party != null)
                        {
                            foreach (var mId in party.MemberNetIds)
                            {
                                if (NetworkServer.spawned.TryGetValue(mId, out var memberIdentity))
                                {
                                    memberIdentity.connectionToClient.Send(broadcastMsg);
                                }
                            }
                        }
                    }
                    else
                    {
                        player.RpcShowMessageToOwner("Você não está em um grupo.");
                    }
                    break;
            }
        }

        [Server]
        public void SendSystemMessage(string text)
        {
            NetworkServer.SendToAll(new MsgChatMessage
            {
                Channel = ChatChannel.System,
                SenderName = "SISTEMA",
                Text = text
            });
        }
    }
}
