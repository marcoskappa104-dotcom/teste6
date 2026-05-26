using System.Collections.Generic;
using UnityEngine;
using Mirror;
using RPG.Network;
using System.Linq;
using NetworkPlayer = RPG.Network.NetworkPlayer;

namespace RPG.Managers
{
    public class Party
    {
        public int Id;
        public uint LeaderNetId;
        public List<uint> MemberNetIds = new List<uint>();
        public const int MAX_MEMBERS = 5;

        public Party(int id, uint leaderNetId)
        {
            Id = id;
            LeaderNetId = leaderNetId;
            MemberNetIds.Add(leaderNetId);
        }

        public bool IsFull => MemberNetIds.Count >= MAX_MEMBERS;
    }

    public class PartyManager : MonoBehaviour
    {
        public static PartyManager Instance { get; private set; }

        private readonly Dictionary<int, Party> _parties = new Dictionary<int, Party>();
        private int _nextPartyId = 1;

        // Convites pendentes: TargetNetId -> InviterNetId
        private readonly Dictionary<uint, uint> _pendingInvites = new Dictionary<uint, uint>();

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        [Server]
        public void RegisterHandlers()
        {
            NetworkServer.RegisterHandler<MsgPartyInviteRequest>(OnPartyInviteRequest);
            NetworkServer.RegisterHandler<MsgPartyInviteResponse>(OnPartyInviteResponse);
        }

        [Server]
        private void OnPartyInviteRequest(NetworkConnectionToClient conn, MsgPartyInviteRequest msg)
        {
            var inviter = conn.identity.GetComponent<NetworkPlayer>();
            if (inviter == null || inviter.Dead) return;

            // Busca o alvo pelo nome
            var target = NetworkPlayer.All.FirstOrDefault(p => p.CharacterName.Equals(msg.TargetName, System.StringComparison.OrdinalIgnoreCase));
            
            if (target == null)
            {
                inviter.RpcShowMessageToOwner("Jogador não encontrado.");
                return;
            }

            if (target == inviter)
            {
                inviter.RpcShowMessageToOwner("Você não pode convidar a si mesmo.");
                return;
            }

            if (target.PartyId != 0)
            {
                inviter.RpcShowMessageToOwner("O jogador já está em um grupo.");
                return;
            }

            if (inviter.PartyId != 0)
            {
                var party = GetParty(inviter.PartyId);
                if (party != null)
                {
                    if (party.LeaderNetId != inviter.netId)
                    {
                        inviter.RpcShowMessageToOwner("Apenas o líder pode convidar.");
                        return;
                    }
                    if (party.IsFull)
                    {
                        inviter.RpcShowMessageToOwner("O grupo está cheio.");
                        return;
                    }
                }
            }

            // Envia convite
            _pendingInvites[target.netId] = inviter.netId;
            target.connectionToClient.Send(new MsgPartyInviteReceived
            {
                InviterNetId = inviter.netId,
                InviterName = inviter.CharacterName
            });

            inviter.RpcShowMessageToOwner($"Convite enviado para {target.CharacterName}.");
        }

        [Server]
        private void OnPartyInviteResponse(NetworkConnectionToClient conn, MsgPartyInviteResponse msg)
        {
            var target = conn.identity.GetComponent<NetworkPlayer>();
            if (target == null) return;

            if (!_pendingInvites.TryGetValue(target.netId, out uint inviterNetId) || inviterNetId != msg.InviterNetId)
            {
                return;
            }

            _pendingInvites.Remove(target.netId);

            if (!msg.Accept)
            {
                var inviter = FindPlayer(inviterNetId);
                inviter?.RpcShowMessageToOwner($"{target.CharacterName} recusou seu convite.");
                return;
            }

            // Aceitou
            var inviterPlayer = FindPlayer(inviterNetId);
            if (inviterPlayer == null)
            {
                target.RpcShowMessageToOwner("O convidador não está mais online.");
                return;
            }

            if (inviterPlayer.PartyId == 0)
            {
                // Cria novo grupo
                CreateParty(inviterPlayer, target);
            }
            else
            {
                // Adiciona ao grupo existente
                AddToParty(inviterPlayer.PartyId, target);
            }
        }

        [Server]
        private void CreateParty(NetworkPlayer leader, NetworkPlayer member)
        {
            int partyId = _nextPartyId++;
            var party = new Party(partyId, leader.netId);
            party.MemberNetIds.Add(member.netId);
            _parties[partyId] = party;

            leader.PartyId = partyId;
            member.PartyId = partyId;

            NotifyPartyUpdate(party);
            leader.RpcShowMessageToOwner("Grupo criado!");
            member.RpcShowMessageToOwner($"Você entrou no grupo de {leader.CharacterName}.");
        }

        [Server]
        private void AddToParty(int partyId, NetworkPlayer member)
        {
            if (!_parties.TryGetValue(partyId, out var party)) return;
            if (party.IsFull)
            {
                member.RpcShowMessageToOwner("O grupo está cheio.");
                return;
            }

            party.MemberNetIds.Add(member.netId);
            member.PartyId = partyId;

            NotifyPartyUpdate(party);
            
            foreach (var mId in party.MemberNetIds)
            {
                var p = FindPlayer(mId);
                p?.RpcShowMessageToOwner($"{member.CharacterName} entrou no grupo.");
            }
        }

        [Server]
        public void OnPlayerDisconnect(NetworkPlayer player)
        {
            if (player.PartyId == 0) return;
            RemoveFromParty(player.PartyId, player.netId);
        }

        [Server]
        private void RemoveFromParty(int partyId, uint netId)
        {
            if (!_parties.TryGetValue(partyId, out var party)) return;

            party.MemberNetIds.Remove(netId);
            var player = FindPlayer(netId);
            if (player != null) player.PartyId = 0;

            if (party.MemberNetIds.Count <= 1)
            {
                // Dissolve grupo
                foreach (var mId in party.MemberNetIds)
                {
                    var p = FindPlayer(mId);
                    if (p != null)
                    {
                        p.PartyId = 0;
                        p.RpcShowMessageToOwner("O grupo foi dissolvido.");
                    }
                }
                _parties.Remove(partyId);
            }
            else
            {
                if (party.LeaderNetId == netId)
                {
                    party.LeaderNetId = party.MemberNetIds[0];
                    var newLeader = FindPlayer(party.LeaderNetId);
                    foreach (var mId in party.MemberNetIds)
                    {
                        FindPlayer(mId)?.RpcShowMessageToOwner($"{newLeader?.CharacterName} é o novo líder do grupo.");
                    }
                }
                NotifyPartyUpdate(party);
            }
        }

        [Server]
        private void NotifyPartyUpdate(Party party)
        {
            var msg = new MsgPartyUpdate { Members = new List<PartyMemberData>() };
            foreach (var mId in party.MemberNetIds)
            {
                var p = FindPlayer(mId);
                if (p != null)
                {
                    msg.Members.Add(new PartyMemberData
                    {
                        NetId = p.netId,
                        Name = p.CharacterName,
                        Level = p.Level,
                        HpPercent = p.MaxHP > 0 ? p.CurrentHP / p.MaxHP : 0,
                        IsLeader = p.netId == party.LeaderNetId
                    });
                }
            }

            foreach (var mId in party.MemberNetIds)
            {
                FindPlayer(mId)?.connectionToClient.Send(msg);
            }
        }

        public Party GetParty(int partyId)
        {
            _parties.TryGetValue(partyId, out var party);
            return party;
        }

        private NetworkPlayer FindPlayer(uint netId)
        {
            if (NetworkServer.spawned.TryGetValue(netId, out var identity))
                return identity.GetComponent<NetworkPlayer>();
            return null;
        }
    }
}
