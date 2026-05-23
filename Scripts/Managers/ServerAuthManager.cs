using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.Managers;
using System.Collections.Generic;
using System.Collections;

namespace RPG.Network
{

    public class ServerAuthManager : MonoBehaviour
    {
        public static ServerAuthManager Instance { get; private set; }

        [Header("Debug")]
        [Tooltip("Logs detalhados do fluxo de auth. DESATIVE em produção.")]
        [SerializeField] private bool debugAuth = false;

        private const int MAX_USERNAME_PAYLOAD_BYTES = 64;
        private const int MAX_HASH_PAYLOAD_BYTES     = 256;
        private const int MAX_TRACKED_IPS            = 10_000;
        private const int MAX_TRACKED_SESSIONS       = 5_000;

        private enum ConnState { Unauthenticated, Authenticated, InGame }

        private class ConnData
        {
            public ConnState   State           = ConnState.Unauthenticated;
            public string      Username        = "";
            public string      CharacterId     = "";
            public AccountData CachedAccount;
            public int         LoginAttempts;
            public string      SessionNonce    = "";
            public float       LastActivityTime;
            public float       LastLoginAttemptTime = -999f;
            public string      RemoteAddress    = "";

            public ConnData() => LastActivityTime = Time.time;
        }

        private class IpData
        {
            public int   FailedAttempts;
            public float BanUntil;
            public float LastAttemptTime;
        }

        private readonly Dictionary<int, ConnData>    _sessions       = new();
        private readonly Dictionary<string, IpData>   _ipBans         = new();

        // ── PROTEÇÃO CONTRA LOGIN DUPLO ────────────────────────────────────
        // Mapeia username (lowercase) → connectionId que está usando essa conta.
        // Quando a conexão cai (OnServerDisconnect), o username é removido.
        private readonly Dictionary<string, int>      _loggedAccounts = new();

        private Coroutine _cleanupCoroutine;

        private const float CLEANUP_INTERVAL = 60f;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (_cleanupCoroutine != null) StopCoroutine(_cleanupCoroutine);
        }

        public void RegisterHandlers()
        {
            NetworkServer.RegisterHandler<MsgLoginRequest>          (OnLoginRequest,           false);
            NetworkServer.RegisterHandler<MsgCreateAccountRequest>  (OnCreateAccountRequest,   false);
            NetworkServer.RegisterHandler<MsgRequestCharacterList>  (OnRequestCharacterList,   false);
            NetworkServer.RegisterHandler<MsgCreateCharacterRequest>(OnCreateCharacterRequest, false);
            NetworkServer.RegisterHandler<MsgSelectCharacter>       (OnSelectCharacter,        false);

            _cleanupCoroutine = StartCoroutine(CleanupExpiredSessions());
            Debug.Log("[ServerAuthManager] Handlers registrados.");
        }

        public void OnServerConnect(NetworkConnectionToClient conn)
        {
            string remoteAddress = string.IsNullOrEmpty(conn?.address)
                ? "unknown"
                : conn.address;

            if (IsIpBanned(remoteAddress))
            {
                Debug.LogWarning($"[ServerAuth] IP banido tentou conectar: {remoteAddress}");
                conn.Send(new MsgLoginResponse
                {
                    Success = false,
                    Error   = "Muitas tentativas falhas. Tente novamente em alguns minutos."
                });
                conn.Disconnect();
                return;
            }

            if (_sessions.Count >= MAX_TRACKED_SESSIONS)
            {
                Debug.LogWarning($"[ServerAuth] Limite de sessões atingido ({MAX_TRACKED_SESSIONS}). " +
                                 "Limpando sessões ociosas antes de aceitar nova.");
                ForceCleanupSessions();
            }

            var session = new ConnData
            {
                SessionNonce  = GameManager.GenerateNonce(),
                RemoteAddress = remoteAddress
            };
            _sessions[conn.connectionId] = session;

            conn.Send(new MsgAuthChallenge { Nonce = session.SessionNonce });
            LogAuth($"Nova conexão: {conn.connectionId} (IP {remoteAddress}) | nonce enviado.");
        }

        public void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            if (_sessions.TryGetValue(conn.connectionId, out var session))
            {
                // ── LIBERA O SLOT DA CONTA AO DESCONECTAR ─────────────────
                // Independente do estado (Authenticated ou InGame), se havia
                // um username associado, remove do dicionário de contas logadas
                // para que o jogador possa entrar novamente.
                if (!string.IsNullOrEmpty(session.Username))
                {
                    string key = session.Username.ToLower();
                    if (_loggedAccounts.TryGetValue(key, out int registeredConnId)
                        && registeredConnId == conn.connectionId)
                    {
                        _loggedAccounts.Remove(key);
                        LogAuth($"Conta '{session.Username}' liberada (connId={conn.connectionId} desconectou).");
                    }
                }
            }

            _sessions.Remove(conn.connectionId);
        }

        // ══════════════════════════════════════════════════════════════════
        // Login
        // ══════════════════════════════════════════════════════════════════

        private void OnLoginRequest(NetworkConnectionToClient conn, MsgLoginRequest msg)
        {
            if (!_sessions.TryGetValue(conn.connectionId, out var session))
            {
                conn.Send(new MsgLoginResponse { Success = false, Error = "Sessão inválida." });
                return;
            }

            if (session.State != ConnState.Unauthenticated)
            {
                conn.Send(new MsgLoginResponse { Success = false, Error = "Já autenticado." });
                return;
            }

            if (Time.time - session.LastLoginAttemptTime < GameConstants.Auth.MIN_TIME_BETWEEN_LOGINS)
            {
                conn.Send(new MsgLoginResponse
                {
                    Success = false,
                    Error   = "Aguarde antes de tentar novamente."
                });
                return;
            }
            session.LastLoginAttemptTime = Time.time;

            session.LoginAttempts++;
            if (session.LoginAttempts > GameConstants.Auth.LOGIN_MAX_PER_CONN)
            {
                Debug.LogWarning($"[ServerAuth] SECURITY: conn:{conn.connectionId} excedeu tentativas.");
                RecordFailedLoginAttempt(session.RemoteAddress);
                conn.Send(new MsgLoginResponse { Success = false, Error = "Muitas tentativas. Tente mais tarde." });
                conn.Disconnect();
                return;
            }

            if (string.IsNullOrWhiteSpace(msg.Username) || string.IsNullOrWhiteSpace(msg.SignedHash))
            {
                conn.Send(new MsgLoginResponse { Success = false, Error = "Dados de login inválidos." });
                return;
            }

            if (msg.Username.Length > MAX_USERNAME_PAYLOAD_BYTES
                || msg.SignedHash.Length > MAX_HASH_PAYLOAD_BYTES)
            {
                Debug.LogWarning($"[ServerAuth] SECURITY: payload anormal de {session.RemoteAddress}");
                RecordFailedLoginAttempt(session.RemoteAddress);
                conn.Send(new MsgLoginResponse { Success = false, Error = "Dados inválidos." });
                conn.Disconnect();
                return;
            }

            if (string.IsNullOrWhiteSpace(session.SessionNonce))
            {
                Debug.LogError($"[ServerAuth] SessionNonce vazio para conn:{conn.connectionId}.");
                conn.Send(new MsgLoginResponse { Success = false, Error = "Erro de sessão. Reconecte." });
                return;
            }

            // ── VERIFICAÇÃO DE CONTA JÁ LOGADA ────────────────────────────
            // Antes de consultar o banco, verifica se alguém já está usando
            // esta conta. Se sim, rejeita imediatamente.
            string usernameLower = msg.Username.Trim().ToLower();
            if (_loggedAccounts.TryGetValue(usernameLower, out int existingConnId))
            {
                // Verifica se a conexão registrada ainda está ativa
                bool existingStillActive = NetworkServer.connections.ContainsKey(existingConnId);

                if (existingStillActive)
                {
                    Debug.LogWarning($"[ServerAuth] SECURITY: conta '{msg.Username}' já está logada " +
                                     $"(connId={existingConnId}). Rejeitando nova tentativa de connId={conn.connectionId}.");
                    conn.Send(new MsgLoginResponse
                    {
                        Success = false,
                        Error   = "Esta conta já está em uso. Feche o jogo no outro dispositivo antes de entrar."
                    });
                    return;
                }
                else
                {
                    // Conexão antiga não existe mais — limpa o registro órfão
                    _loggedAccounts.Remove(usernameLower);
                    LogAuth($"Registro órfão de conta '{msg.Username}' removido (connId={existingConnId} não existe mais).");
                }
            }

            LoginAttemptResult result = default;
            if (DatabaseManager.Instance != null)
                result = DatabaseManager.Instance.TryLoginWithSignedHash(
                    msg.Username, msg.SignedHash, session.SessionNonce);

            if (!result.Success)
            {
                RecordFailedLoginAttempt(session.RemoteAddress);

                string attempts = $"({session.LoginAttempts}/{GameConstants.Auth.LOGIN_MAX_PER_CONN})";
                var failMsg = new MsgLoginResponse
                {
                    Success = false,
                    Error   = $"Usuário ou senha incorretos. {attempts}"
                };

                if (result.SuggestedDelayMs > 0)
                    StartCoroutine(SendDelayed(conn, failMsg, result.SuggestedDelayMs));
                else
                    conn.Send(failMsg);
                return;
            }

            // ── REGISTRA A CONTA COMO LOGADA ──────────────────────────────
            _loggedAccounts[usernameLower] = conn.connectionId;

            session.State            = ConnState.Authenticated;
            session.Username         = result.Account.Username;
            session.CachedAccount    = result.Account;
            session.LoginAttempts    = 0;
            session.LastActivityTime = Time.time;

            ClearIpFailures(session.RemoteAddress);

            conn.Send(new MsgLoginResponse { Success = true, Username = result.Account.Username });
            SendCharacterList(conn, result.Account);

            Debug.Log($"[ServerAuth] Login OK: {result.Account.Username} (IP {session.RemoteAddress})");
        }

        private IEnumerator SendDelayed(NetworkConnectionToClient conn,
                                        MsgLoginResponse msg,
                                        int delayMs)
        {
            yield return new WaitForSeconds(delayMs / 1000f);
            if (conn != null && conn.isReady)
                conn.Send(msg);
        }

        // ══════════════════════════════════════════════════════════════════
        // Rate limit por IP
        // ══════════════════════════════════════════════════════════════════

        private bool IsIpBanned(string ip)
        {
            if (string.IsNullOrEmpty(ip) || ip == "unknown") return false;
            if (!_ipBans.TryGetValue(ip, out var data)) return false;
            return Time.time < data.BanUntil;
        }

        private void RecordFailedLoginAttempt(string ip)
        {
            if (string.IsNullOrEmpty(ip) || ip == "unknown") return;

            if (!_ipBans.ContainsKey(ip) && _ipBans.Count >= MAX_TRACKED_IPS)
            {
                EvictLeastRecentIps(targetSize: MAX_TRACKED_IPS - (MAX_TRACKED_IPS / 10));

                if (_ipBans.Count >= MAX_TRACKED_IPS)
                {
                    Debug.LogError("[ServerAuth] SECURITY: _ipBans cheio mesmo após eviction. " +
                                   "IP novo NÃO trackado. Sob ataque massivo?");
                    return;
                }
            }

            if (!_ipBans.TryGetValue(ip, out var data))
            {
                data = new IpData();
                _ipBans[ip] = data;
            }

            data.FailedAttempts++;
            data.LastAttemptTime = Time.time;

            if (data.FailedAttempts >= GameConstants.Auth.LOGIN_MAX_PER_IP)
            {
                data.BanUntil = Time.time + GameConstants.Auth.IP_BAN_DURATION_SECONDS;
                Debug.LogWarning($"[ServerAuth] SECURITY [{System.DateTime.UtcNow:o}]: " +
                                 $"IP banido por brute-force: {ip} " +
                                 $"({data.FailedAttempts} falhas, ban por " +
                                 $"{GameConstants.Auth.IP_BAN_DURATION_SECONDS}s)");
            }
        }

        private void ClearIpFailures(string ip)
        {
            if (string.IsNullOrEmpty(ip) || ip == "unknown") return;
            _ipBans.Remove(ip);
        }

        private void EvictLeastRecentIps(int targetSize)
        {
            if (_ipBans.Count <= targetSize) return;

            float now      = Time.time;
            int toRemove   = _ipBans.Count - targetSize;

            var inactiveCandidates = new List<KeyValuePair<string, float>>(_ipBans.Count);
            foreach (var kv in _ipBans)
            {
                if (now >= kv.Value.BanUntil)
                    inactiveCandidates.Add(new KeyValuePair<string, float>(kv.Key, kv.Value.LastAttemptTime));
            }
            inactiveCandidates.Sort((a, b) => a.Value.CompareTo(b.Value));

            int removed = 0;
            foreach (var c in inactiveCandidates)
            {
                if (removed >= toRemove) break;
                _ipBans.Remove(c.Key);
                removed++;
            }

            if (removed > 0)
                Debug.Log($"[ServerAuth] Eviction LRU fase 1: removeu {removed} IPs inativos do tracker.");

            int stillToRemove = toRemove - removed;
            if (stillToRemove <= 0) return;

            var activeBans = new List<KeyValuePair<string, float>>(_ipBans.Count);
            foreach (var kv in _ipBans)
            {
                if (now < kv.Value.BanUntil)
                    activeBans.Add(new KeyValuePair<string, float>(kv.Key, kv.Value.LastAttemptTime));
            }
            activeBans.Sort((a, b) => a.Value.CompareTo(b.Value));

            int activeRemoved = 0;
            foreach (var c in activeBans)
            {
                if (activeRemoved >= stillToRemove) break;
                _ipBans.Remove(c.Key);
                activeRemoved++;
            }

            if (activeRemoved > 0)
                Debug.LogWarning($"[ServerAuth] SECURITY: eviction LRU fase 2 removeu " +
                                 $"{activeRemoved} bans ATIVOS antigos (proteção contra DoS de memória). " +
                                 "IPs evictados serão re-banidos se continuarem atacando.");
        }

        /// <summary>
        /// Limpeza forçada de sessões não-InGame expiradas.
        /// Não remove sessões InGame — essas saem em OnServerDisconnect.
        /// </summary>
        private void ForceCleanupSessions()
        {
            float now    = Time.time;
            var toRemove = new List<int>();

            foreach (var kv in _sessions)
            {
                if (kv.Value.State == ConnState.InGame) continue;

                float threshold = GameConstants.Auth.SESSION_TTL_SECONDS * 0.5f;
                if (now - kv.Value.LastActivityTime > threshold)
                    toRemove.Add(kv.Key);
            }

            foreach (var id in toRemove)
            {
                // Libera conta se havia username registrado
                var s = _sessions[id];
                if (!string.IsNullOrEmpty(s.Username))
                {
                    string key = s.Username.ToLower();
                    if (_loggedAccounts.TryGetValue(key, out int cid) && cid == id)
                        _loggedAccounts.Remove(key);
                }
                _sessions.Remove(id);
            }

            if (toRemove.Count > 0)
                Debug.Log($"[ServerAuth] ForceCleanup removeu {toRemove.Count} sessões ociosas.");
        }

        // ══════════════════════════════════════════════════════════════════
        // Criar conta
        // ══════════════════════════════════════════════════════════════════

        private void OnCreateAccountRequest(NetworkConnectionToClient conn, MsgCreateAccountRequest msg)
        {
            if (_sessions.TryGetValue(conn.connectionId, out var session))
            {
                if (Time.time - session.LastLoginAttemptTime < GameConstants.Auth.MIN_TIME_BETWEEN_LOGINS)
                {
                    conn.Send(new MsgCreateAccountResponse
                    {
                        Success = false,
                        Error   = "Aguarde antes de tentar novamente."
                    });
                    return;
                }
                session.LastLoginAttemptTime = Time.time;
            }

            if (string.IsNullOrWhiteSpace(msg.Username))
            {
                conn.Send(new MsgCreateAccountResponse { Success = false, Error = "Username inválido." });
                return;
            }
            if (string.IsNullOrWhiteSpace(msg.PasswordHash))
            {
                conn.Send(new MsgCreateAccountResponse { Success = false, Error = "Senha inválida." });
                return;
            }

            if (msg.Username.Length > MAX_USERNAME_PAYLOAD_BYTES
                || msg.PasswordHash.Length > MAX_HASH_PAYLOAD_BYTES)
            {
                conn.Send(new MsgCreateAccountResponse { Success = false, Error = "Dados inválidos." });
                return;
            }

            if (!IsValidUsername(msg.Username))
            {
                conn.Send(new MsgCreateAccountResponse
                {
                    Success = false,
                    Error   = "Username deve conter apenas letras, números e underscore."
                });
                return;
            }

            var error = DatabaseManager.Instance?.TryCreateAccount(msg.Username, msg.PasswordHash);
            if (error != null)
            {
                conn.Send(new MsgCreateAccountResponse { Success = false, Error = error });
                return;
            }
            conn.Send(new MsgCreateAccountResponse { Success = true });
            Debug.Log($"[ServerAuth] Conta criada: {msg.Username}");
        }

        private static bool IsValidUsername(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return false;
            string trimmed = username.Trim();
            if (trimmed.Length < GameConstants.Auth.USERNAME_MIN_LENGTH
                || trimmed.Length > GameConstants.Auth.USERNAME_MAX_LENGTH) return false;
            foreach (char c in trimmed)
            {
                if (!char.IsLetterOrDigit(c) && c != '_') return false;
            }
            return true;
        }

        // ══════════════════════════════════════════════════════════════════
        // Lista / criar / selecionar personagens
        // ══════════════════════════════════════════════════════════════════

        private void OnRequestCharacterList(NetworkConnectionToClient conn, MsgRequestCharacterList msg)
        {
            if (!RequireAuth(conn, out var session)) return;
            UpdateActivity(session);

            var chars = DatabaseManager.Instance?.LoadCharacters(session.Username)
                        ?? new List<CharacterData>();
            SendCharacterList(conn, session.Username, chars);
        }

        private void SendCharacterList(NetworkConnectionToClient conn, AccountData account)
            => SendCharacterList(conn, account.Username, account.Characters ?? new List<CharacterData>());

        private void SendCharacterList(NetworkConnectionToClient conn, string username, List<CharacterData> chars)
        {
            var list = new List<CharacterSummary>();
            foreach (var ch in chars)
                list.Add(new CharacterSummary
                {
                    CharacterId   = ch.CharacterId,
                    CharacterName = ch.CharacterName,
                    Race          = ch.Race.ToString(),
                    Level         = ch.Level
                });
            conn.Send(new MsgCharacterListResponse { Characters = list });
        }

        private void OnCreateCharacterRequest(NetworkConnectionToClient conn, MsgCreateCharacterRequest msg)
        {
            if (!RequireAuth(conn, out var session)) return;
            UpdateActivity(session);

            if (string.IsNullOrWhiteSpace(msg.Name)
                || msg.Name.Length < GameConstants.Auth.CHARACTER_NAME_MIN
                || msg.Name.Length > GameConstants.Auth.CHARACTER_NAME_MAX)
            {
                conn.Send(new MsgCreateCharacterResponse
                {
                    Success = false,
                    Error   = $"Nome inválido ({GameConstants.Auth.CHARACTER_NAME_MIN} a " +
                              $"{GameConstants.Auth.CHARACTER_NAME_MAX} caracteres)."
                });
                return;
            }

            if (msg.RaceIndex < 0 || !System.Enum.IsDefined(typeof(CharacterRace), msg.RaceIndex))
            {
                conn.Send(new MsgCreateCharacterResponse
                {
                    Success = false,
                    Error   = "Raça inválida."
                });
                return;
            }

            var error = DatabaseManager.Instance?.TryCreateCharacter(
                session.Username, msg.Name, (CharacterRace)msg.RaceIndex);

            if (error != null)
            {
                conn.Send(new MsgCreateCharacterResponse { Success = false, Error = error });
                return;
            }

            var chars = DatabaseManager.Instance?.LoadCharacters(session.Username)
                        ?? new List<CharacterData>();
            var list = new List<CharacterSummary>();
            foreach (var ch in chars)
                list.Add(new CharacterSummary
                {
                    CharacterId   = ch.CharacterId,
                    CharacterName = ch.CharacterName,
                    Race          = ch.Race.ToString(),
                    Level         = ch.Level
                });

            conn.Send(new MsgCreateCharacterResponse { Success = true, UpdatedList = list });
            Debug.Log($"[ServerAuth] Personagem criado: {msg.Name} (conta:{session.Username})");
        }

        private void OnSelectCharacter(NetworkConnectionToClient conn, MsgSelectCharacter msg)
        {
            if (!RequireAuth(conn, out var session)) return;

            if (session.State == ConnState.InGame)
            {
                conn.Send(new MsgSelectCharacterResponse { Success = false, Error = "Já está em jogo." });
                return;
            }

            if (string.IsNullOrWhiteSpace(msg.CharacterId))
            {
                conn.Send(new MsgSelectCharacterResponse
                {
                    Success = false,
                    Error   = "ID de personagem inválido."
                });
                return;
            }

            var charData = DatabaseManager.Instance?.LoadCharacterForAccount(
                msg.CharacterId, session.Username);

            if (charData == null)
            {
                conn.Send(new MsgSelectCharacterResponse
                {
                    Success = false,
                    Error   = "Personagem não encontrado ou não pertence a esta conta."
                });
                Debug.LogWarning($"[ServerAuth] SECURITY: {session.Username} tentou selecionar {msg.CharacterId}");
                return;
            }

            session.State        = ConnState.InGame;
            session.CharacterId  = msg.CharacterId;
            UpdateActivity(session);

            RPGNetworkManager.singleton?.SpawnPlayerForConnection(conn, charData, session.Username);
            Debug.Log($"[ServerAuth] {charData.CharacterName} ({charData.Race}) entrando | conn:{conn.connectionId}");
        }

        // ══════════════════════════════════════════════════════════════════
        // Helpers
        // ══════════════════════════════════════════════════════════════════

        private bool RequireAuth(NetworkConnectionToClient conn, out ConnData session)
        {
            if (!_sessions.TryGetValue(conn.connectionId, out session))
            {
                conn.Send(new MsgErrorResponse { Error = "Sessão inválida." });
                return false;
            }
            if (session.State == ConnState.Unauthenticated)
            {
                conn.Send(new MsgErrorResponse { Error = "Não autenticado." });
                return false;
            }
            return true;
        }

        private static void UpdateActivity(ConnData session)
            => session.LastActivityTime = Time.time;

        private void LogAuth(string msg)
        {
            if (debugAuth) Debug.Log($"[ServerAuth-DEBUG] {msg}");
        }

        // ══════════════════════════════════════════════════════════════════
        // Limpeza periódica
        // ══════════════════════════════════════════════════════════════════

        private IEnumerator CleanupExpiredSessions()
        {
            var wait            = new WaitForSeconds(CLEANUP_INTERVAL);
            var expiredSessions = new List<int>();
            var expiredIps      = new List<string>();

            while (true)
            {
                yield return wait;

                expiredSessions.Clear();
                foreach (var kv in _sessions)
                {
                    // Sessões InGame saem em OnServerDisconnect — não limpar aqui.
                    if (kv.Value.State == ConnState.InGame) continue;

                    if (Time.time - kv.Value.LastActivityTime > GameConstants.Auth.SESSION_TTL_SECONDS)
                        expiredSessions.Add(kv.Key);
                }
                foreach (var id in expiredSessions)
                {
                    var s = _sessions[id];

                    // Libera conta se havia username registrado
                    if (!string.IsNullOrEmpty(s.Username))
                    {
                        string key = s.Username.ToLower();
                        if (_loggedAccounts.TryGetValue(key, out int cid) && cid == id)
                            _loggedAccounts.Remove(key);
                    }

                    _sessions.Remove(id);
                    Debug.Log($"[ServerAuthManager] Sessão expirada removida: connId={id} estado={s.State}");
                }

                expiredIps.Clear();
                foreach (var kv in _ipBans)
                {
                    var data = kv.Value;
                    if (Time.time >= data.BanUntil
                        && Time.time - data.LastAttemptTime > GameConstants.Auth.IP_BAN_DURATION_SECONDS)
                        expiredIps.Add(kv.Key);
                }
                foreach (var ip in expiredIps)
                    _ipBans.Remove(ip);

                if (_ipBans.Count > MAX_TRACKED_IPS)
                    EvictLeastRecentIps(targetSize: MAX_TRACKED_IPS - (MAX_TRACKED_IPS / 10));

                // ── LIMPEZA DE CONTAS LOGADAS ÓRFÃS ───────────────────────
                // Verifica se algum connId em _loggedAccounts não existe mais
                // como conexão ativa. Isso é uma rede de segurança extra — em
                // condições normais OnServerDisconnect já cuida disso.
                var orphanedAccounts = new List<string>();
                foreach (var kv in _loggedAccounts)
                {
                    if (!NetworkServer.connections.ContainsKey(kv.Value))
                        orphanedAccounts.Add(kv.Key);
                }
                foreach (var username in orphanedAccounts)
                {
                    _loggedAccounts.Remove(username);
                    Debug.LogWarning($"[ServerAuth] Conta logada órfã removida na limpeza periódica: '{username}'");
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // API pública — utilitário de debug/admin (opcional)
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Retorna true se a conta (username) está atualmente logada.
        /// Útil para sistemas de admin ou debug.
        /// </summary>
        public bool IsAccountOnline(string username)
        {
            if (string.IsNullOrEmpty(username)) return false;
            return _loggedAccounts.ContainsKey(username.ToLower());
        }

        /// <summary>
        /// Força o kick de uma conta pelo username (para uso admin/anti-cheat).
        /// </summary>
        public void KickAccount(string username, string reason = "Desconectado pelo servidor.")
        {
            if (string.IsNullOrEmpty(username)) return;
            string key = username.ToLower();

            if (!_loggedAccounts.TryGetValue(key, out int connId)) return;

            if (NetworkServer.connections.TryGetValue(connId, out var conn))
            {
                conn.Send(new MsgErrorResponse { Error = reason });
                conn.Disconnect();
                Debug.Log($"[ServerAuth] Conta '{username}' foi kickada. Motivo: {reason}");
            }
            else
            {
                // Conexão não existe mais — remove o registro órfão
                _loggedAccounts.Remove(key);
            }
        }
    }
}
