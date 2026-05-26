using Mirror;
using System.Collections.Generic;

namespace RPG.Network
{
    // ══════════════════════════════════════════════════════════════════════
    // Autenticação — Challenge / Response
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enviado pelo servidor logo após a conexão.
    /// Cliente deve usar o nonce para assinar a senha no login.
    /// </summary>
    public struct MsgAuthChallenge : NetworkMessage
    {
        public string Nonce; // Base64(16 bytes aleatórios)
    }

    // ══════════════════════════════════════════════════════════════════════
    // Login
    // ══════════════════════════════════════════════════════════════════════

    public struct MsgLoginRequest : NetworkMessage
    {
        public string Username;
        /// <summary>SHA256(SHA256(senha) + nonce).</summary>
        public string SignedHash;
    }

    public struct MsgLoginResponse : NetworkMessage
    {
        public bool   Success;
        public string Error;
        public string Username;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Criar conta
    // ══════════════════════════════════════════════════════════════════════

    public struct MsgCreateAccountRequest : NetworkMessage
    {
        public string Username;
        /// <summary>SHA256(senha) — servidor armazena este valor diretamente.</summary>
        public string PasswordHash;
    }

    public struct MsgCreateAccountResponse : NetworkMessage
    {
        public bool   Success;
        public string Error;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Lista de personagens
    // ══════════════════════════════════════════════════════════════════════

    public struct MsgRequestCharacterList : NetworkMessage { }

    public struct CharacterSummary : NetworkMessage
    {
        public string CharacterId;
        public string CharacterName;
        public string Race;
        public int    Level;
    }

    public struct MsgCharacterListResponse : NetworkMessage
    {
        public List<CharacterSummary> Characters;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Criar personagem
    // ══════════════════════════════════════════════════════════════════════

    public struct MsgCreateCharacterRequest : NetworkMessage
    {
        public string Name;
        public int    RaceIndex; // CharacterRace enum
    }

    public struct MsgCreateCharacterResponse : NetworkMessage
    {
        public bool                   Success;
        public string                 Error;
        public List<CharacterSummary> UpdatedList;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Selecionar personagem e entrar no jogo
    // ══════════════════════════════════════════════════════════════════════

    public struct MsgSelectCharacter : NetworkMessage
    {
        public string CharacterId;
    }

    public struct MsgSelectCharacterResponse : NetworkMessage
    {
        public bool   Success;
        public string Error;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Erros e sincronização de cena
    // ══════════════════════════════════════════════════════════════════════

    public struct MsgErrorResponse : NetworkMessage
    {
        public string Error;
    }

    /// <summary>
    /// Cliente notifica o servidor que a GameplayScene terminou de carregar.
    /// Só então o servidor spawna o player (garante NavMesh pronto).
    /// </summary>
    public struct MsgClientSceneReady : NetworkMessage { }

    // ══════════════════════════════════════════════════════════════════════
    // Chat
    // ══════════════════════════════════════════════════════════════════════

    public enum ChatChannel { Global, Party, System }

    public struct MsgChatMessage : NetworkMessage
    {
        public ChatChannel Channel;
        public string      SenderName;
        public string      Text;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Grupo (Party)
    // ══════════════════════════════════════════════════════════════════════

    public struct MsgPartyInviteRequest : NetworkMessage
    {
        public string TargetName;
    }

    public struct MsgPartyInviteReceived : NetworkMessage
    {
        public uint   InviterNetId;
        public string InviterName;
    }

    public struct MsgPartyInviteResponse : NetworkMessage
    {
        public uint   InviterNetId;
        public bool   Accept;
    }

    public struct MsgPartyUpdate : NetworkMessage
    {
        public List<PartyMemberData> Members;
    }

    public struct PartyMemberData
    {
        public uint   NetId;
        public string Name;
        public int    Level;
        public float  HpPercent;
        public bool   IsLeader;
    }
}
