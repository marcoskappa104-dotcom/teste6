using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Managers;
using RPG.Data;

namespace RPG.Network
{

    public class RPGNetworkManager : NetworkManager
    {
        public static new RPGNetworkManager singleton =>
            NetworkManager.singleton as RPGNetworkManager;

        private const float SPAWN_NAVMESH_RADIUS    = 15f;
        private const float SPAWN_NAVMESH_TIMEOUT   = 5f;
        private const float PENDING_SPAWN_TIMEOUT   = 30f;
        private const float CLEANUP_PENDING_SPAWN_S = 5f;

        // Cap defensivo para _cancelledSpawns. Em operação normal, esse set
        // raramente tem mais que ~10 entradas (jogadores desconectando durante
        // spawn). 1000 é folga generosa antes de drenar.
        private const int MAX_TRACKED_CANCELLED = 1000;

        [Header("Spawnable Prefabs")]
        [Tooltip("Prefabs de monstros e itens (precisam ter NetworkIdentity).")]
        [SerializeField] private List<GameObject> spawnablePrefabs = new List<GameObject>();

        [System.Serializable]
        public class RacePrefabEntry
        {
            public CharacterRace Race;
            public GameObject    Prefab;
        }

        [Header("Prefabs de Jogador por Raça")]
        [Tooltip("Prefabs específicos para cada região/raça.")]
        [SerializeField] private List<RacePrefabEntry> racePrefabs = new List<RacePrefabEntry>();

        private readonly Dictionary<CharacterRace, GameObject> _racePrefabLookup = new();

        [System.Serializable]
        public class WeaponProjectileEntry
        {
            [Tooltip("Categoria de arma à qual este projétil se aplica.")]
            public WeaponType WeaponType = WeaponType.Bow;

            [Tooltip("Prefab do projétil. Precisa de NetworkIdentity + Projectile.")]
            public GameObject ProjectilePrefab;
        }

        [Header("Projéteis por Tipo de Arma")]
        [Tooltip("Para cada WeaponType ranged, um prefab de projétil.\n" +
                 "Bow → flecha. Staff → orb mágica grande. Wand → orb pequena.")]
        [SerializeField] private List<WeaponProjectileEntry> projectilePrefabs = new();

        private readonly Dictionary<WeaponType, GameObject> _projectileLookup = new();

        private struct PendingSpawn
        {
            public NetworkConnectionToClient Conn;
            public CharacterData             CharData;
            public string                    AccountUsername;
            public float                     ExpiresAt;
        }

        private readonly Dictionary<int, PendingSpawn> _pendingSpawns   = new();
        private readonly Dictionary<int, Coroutine>    _spawnCoroutines = new();

        // Tracking de connIds cancelados durante a coroutine de spawn — usado
        // como sinal de "essa coroutine deve abortar".
        private readonly HashSet<int> _cancelledSpawns = new();

        private Coroutine _cleanupCoroutine;

        private bool              _prefabsRegistered;
        private ServerAuthManager _authManager;
        private PartyManager      _partyManager;
        private ChatManager       _chatManager;

        // ══════════════════════════════════════════════════════════════════
        // Lifecycle
        // ══════════════════════════════════════════════════════════════════

        public override void Start()
        {
            base.Start();
            BuildRacePrefabLookup();
            BuildProjectileLookup();
            RegisterSpawnablePrefabs();
        }

        private void BuildRacePrefabLookup()
        {
            _racePrefabLookup.Clear();
            foreach (var entry in racePrefabs)
            {
                if (entry?.Prefab == null) continue;
                if (_racePrefabLookup.ContainsKey(entry.Race))
                {
                    Debug.LogWarning($"[RPGNetworkManager] Duplicado: prefab para {entry.Race}. Mantendo primeiro.");
                    continue;
                }
                _racePrefabLookup[entry.Race] = entry.Prefab;
            }
        }

        private void BuildProjectileLookup()
        {
            _projectileLookup.Clear();
            foreach (var entry in projectilePrefabs)
            {
                if (entry?.ProjectilePrefab == null) continue;
                if (_projectileLookup.ContainsKey(entry.WeaponType))
                {
                    Debug.LogWarning($"[RPGNetworkManager] Duplicado: prefab de projétil " +
                                     $"para {entry.WeaponType}. Mantendo primeiro.");
                    continue;
                }
                if (entry.ProjectilePrefab.GetComponent<NetworkIdentity>() == null)
                {
                    Debug.LogError($"[RPGNetworkManager] Projétil '{entry.ProjectilePrefab.name}' " +
                                   $"sem NetworkIdentity — ignorado.");
                    continue;
                }
                if (entry.ProjectilePrefab.GetComponent<Projectile>() == null)
                {
                    Debug.LogError($"[RPGNetworkManager] Projétil '{entry.ProjectilePrefab.name}' " +
                                   $"sem componente Projectile — ignorado.");
                    continue;
                }
                _projectileLookup[entry.WeaponType] = entry.ProjectilePrefab;
            }
        }

        /// <summary>
        /// Retorna o prefab de projétil para uma categoria de arma.
        /// null se não houver prefab configurado para essa categoria.
        /// Chamado por NetworkMonsterEntity durante spawn de projétil.
        /// </summary>
        [Server]
        public GameObject GetProjectilePrefab(WeaponType weaponType)
        {
            _projectileLookup.TryGetValue(weaponType, out var prefab);
            return prefab;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            // Adiciona Interest Management (Spatial Hashing) programaticamente se não houver um
            if (GetComponent<InterestManagement>() == null)
            {
                var spatial = gameObject.AddComponent<SpatialHashingInterestManagement>();
                spatial.visRange = 60; // Range de visão do jogador
                spatial.rebuildInterval = 0.5f; // Frequência de atualização (2x por segundo)
                Debug.Log("[RPGNetworkManager] Interest Management (Spatial Hashing) configurado.");
            }

            if (playerPrefab == null)
                Debug.LogError("[RPGNetworkManager] playerPrefab não configurado!");

            _authManager = GetComponent<ServerAuthManager>();
            if (_authManager == null)
                _authManager = gameObject.AddComponent<ServerAuthManager>();

            _authManager.RegisterHandlers();

            _partyManager = GetComponent<PartyManager>();
            if (_partyManager == null)
                _partyManager = gameObject.AddComponent<PartyManager>();

            _partyManager.RegisterHandlers();

            _chatManager = GetComponent<ChatManager>();
            if (_chatManager == null)
                _chatManager = gameObject.AddComponent<ChatManager>();

            _chatManager.RegisterHandlers();

            NetworkServer.RegisterHandler<MsgClientSceneReady>(OnClientSceneReady, false);

            _cleanupCoroutine = StartCoroutine(CleanExpiredPendingSpawns());
        }

        public override void OnStopServer()
        {
            foreach (var kv in _spawnCoroutines)
                if (kv.Value != null) StopCoroutine(kv.Value);
            _spawnCoroutines.Clear();
            _pendingSpawns.Clear();
            _cancelledSpawns.Clear();

            if (_cleanupCoroutine != null)
            {
                StopCoroutine(_cleanupCoroutine);
                _cleanupCoroutine = null;
            }

            base.OnStopServer();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            if (!_prefabsRegistered)
                RegisterSpawnablePrefabs();
        }

        public override void OnServerSceneChanged(string sceneName)
        {
            base.OnServerSceneChanged(sceneName);

            foreach (var kv in _spawnCoroutines)
                if (kv.Value != null) StopCoroutine(kv.Value);
            _spawnCoroutines.Clear();
            _pendingSpawns.Clear();
            _cancelledSpawns.Clear();

            _prefabsRegistered = false;
            BuildRacePrefabLookup();
            BuildProjectileLookup();
            RegisterSpawnablePrefabs();
        }

        // ══════════════════════════════════════════════════════════════════
        // Conexões
        // ══════════════════════════════════════════════════════════════════

        public override void OnServerConnect(NetworkConnectionToClient conn)
        {
            base.OnServerConnect(conn);
            _authManager?.OnServerConnect(conn);
        }

        public override void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            int connId = conn.connectionId;

            _pendingSpawns.Remove(connId);

            // Marca a coroutine como cancelada ANTES de stop, para que se ela
            // estiver no meio de um yield, o próximo check feche cedo.
            if (_spawnCoroutines.TryGetValue(connId, out var coroutine))
            {
                AddCancelledSpawn(connId);
                if (coroutine != null) StopCoroutine(coroutine);
                _spawnCoroutines.Remove(connId);
            }

            _authManager?.OnServerDisconnect(conn);
            base.OnServerDisconnect(conn);
        }

        /// <summary>
        /// Adiciona connId ao set de cancelados, drenando entradas antigas
        /// se atingir o cap. Garante crescimento limitado mesmo em cenários
        /// patológicos (botnet de conexões/desconexões).
        /// </summary>
        private void AddCancelledSpawn(int connId)
        {
            if (_cancelledSpawns.Count >= MAX_TRACKED_CANCELLED)
            {
                // Drena metade — não importa qual metade porque connIds órfãos
                // não têm uso prático além de serem checados pela coroutine.
                int toRemove = _cancelledSpawns.Count / 2;
                int removed  = 0;
                var enumerator = _cancelledSpawns.GetEnumerator();
                var toRemoveList = new List<int>(toRemove);
                while (enumerator.MoveNext() && removed < toRemove)
                {
                    toRemoveList.Add(enumerator.Current);
                    removed++;
                }
                foreach (var id in toRemoveList)
                    _cancelledSpawns.Remove(id);

                Debug.LogWarning($"[RPGNetworkManager] _cancelledSpawns atingiu cap " +
                                 $"({MAX_TRACKED_CANCELLED}). Drenadas {removed} entradas antigas.");
            }

            _cancelledSpawns.Add(connId);
        }

        public override void OnServerAddPlayer(NetworkConnectionToClient conn) { }

        public override void OnClientConnect()
        {
            base.OnClientConnect();
        }

        public override void OnClientDisconnect()
        {
            base.OnClientDisconnect();
            ClientAuthHandler.Instance?.OnDisconnectedFromServer();
        }

        // ══════════════════════════════════════════════════════════════════
        // Spawn do player
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public void SpawnPlayerForConnection(
            NetworkConnectionToClient conn,
            CharacterData charData,
            string accountUsername)
        {
            // Tenta pegar o prefab da raça, senão usa o padrão
            _racePrefabLookup.TryGetValue(charData.Race, out var prefab);
            if (prefab == null) prefab = playerPrefab;

            if (prefab == null)
            {
                Debug.LogError($"[RPGNetworkManager] Nenhum prefab de jogador encontrado para {charData.Race}.");
                conn.Send(new MsgSelectCharacterResponse
                {
                    Success = false,
                    Error   = "Erro interno do servidor (sem prefab)."
                });
                return;
            }

            _pendingSpawns[conn.connectionId] = new PendingSpawn
            {
                Conn            = conn,
                CharData        = charData,
                AccountUsername = accountUsername,
                ExpiresAt       = Time.time + PENDING_SPAWN_TIMEOUT
            };

            conn.Send(new MsgSelectCharacterResponse { Success = true });
        }

        [Server]
        private void OnClientSceneReady(NetworkConnectionToClient conn, MsgClientSceneReady msg)
        {
            if (!_pendingSpawns.TryGetValue(conn.connectionId, out var pending)) return;

            if (Time.time > pending.ExpiresAt)
            {
                Debug.LogWarning($"[RPGNetworkManager] Spawn expirado: {pending.CharData?.CharacterName}");
                _pendingSpawns.Remove(conn.connectionId);
                return;
            }

            _pendingSpawns.Remove(conn.connectionId);

            // Garante que connId não está na lista de cancelados (sanity)
            _cancelledSpawns.Remove(conn.connectionId);

            var coroutine = StartCoroutine(DoSpawnPlayer(conn, pending.CharData, pending.AccountUsername));
            _spawnCoroutines[conn.connectionId] = coroutine;
        }

        [Server]
        private IEnumerator DoSpawnPlayer(
            NetworkConnectionToClient conn,
            CharacterData charData,
            string accountUsername)
        {
            int connId = conn?.connectionId ?? -1;

            if (conn == null || !conn.isReady)
            {
                ClearSpawnTracking(connId);
                yield break;
            }

            Vector3 spawnPos = GetSpawnPositionForRace(charData.Race, charData);

            float elapsed = 0f;
            while (elapsed < SPAWN_NAVMESH_TIMEOUT)
            {
                // Checagem a cada yield: se OnServerDisconnect marcou cancelado,
                // sai sem fazer nada
                if (_cancelledSpawns.Contains(connId))
                {
                    _cancelledSpawns.Remove(connId);
                    ClearSpawnTracking(connId);
                    yield break;
                }

                if (NavMesh.SamplePosition(spawnPos, out NavMeshHit hit, SPAWN_NAVMESH_RADIUS, NavMesh.AllAreas))
                {
                    spawnPos = hit.position;
                    break;
                }
                elapsed += Time.deltaTime;
                yield return null;
            }

            // Validações finais antes de tocar o NetworkServer
            if (_cancelledSpawns.Contains(connId)
                || conn == null
                || !conn.isReady
                || !NetworkServer.active)
            {
                _cancelledSpawns.Remove(connId);
                ClearSpawnTracking(connId);
                yield break;
            }

            // Tenta pegar o prefab da raça, senão usa o padrão
            _racePrefabLookup.TryGetValue(charData.Race, out var prefab);
            if (prefab == null) prefab = playerPrefab;

            var playerGO = Instantiate(prefab, spawnPos, Quaternion.identity);
            NetworkServer.AddPlayerForConnection(conn, playerGO);

            var netPlayer = playerGO.GetComponent<NetworkPlayer>();
            if (netPlayer != null)
                netPlayer.ServerInitialize(charData, accountUsername);
            else
                Debug.LogError("[RPGNetworkManager] playerPrefab não tem NetworkPlayer.");

            ClearSpawnTracking(connId);

            Debug.Log($"[Server] Spawnado: {charData.CharacterName} ({charData.Race}) | connId={connId}");
        }

        [Server]
        private void ClearSpawnTracking(int connId)
        {
            if (connId < 0) return;
            _spawnCoroutines.Remove(connId);
        }

        [Server]
        private IEnumerator CleanExpiredPendingSpawns()
        {
            var wait = new WaitForSeconds(CLEANUP_PENDING_SPAWN_S);
            var toRemove = new List<int>();

            while (true)
            {
                yield return wait;

                // Limpa pendingSpawns expirados
                toRemove.Clear();
                foreach (var kv in _pendingSpawns)
                {
                    if (Time.time > kv.Value.ExpiresAt)
                        toRemove.Add(kv.Key);
                }
                foreach (var id in toRemove)
                    _pendingSpawns.Remove(id);

                // Cleanup defensivo de _cancelledSpawns: se algum connId ficou
                // órfão (sem coroutine ativa nem pending), pode ser removido.
                // Isso elimina memory leaks no canto improvável.
                if (_cancelledSpawns.Count > 0)
                {
                    toRemove.Clear();
                    foreach (var connId in _cancelledSpawns)
                    {
                        if (!_spawnCoroutines.ContainsKey(connId)
                            && !_pendingSpawns.ContainsKey(connId))
                        {
                            toRemove.Add(connId);
                        }
                    }
                    foreach (var id in toRemove)
                        _cancelledSpawns.Remove(id);
                }
            }
        }

        public Vector3 GetSpawnPositionForRace(CharacterRace race, CharacterData charData)
        {
            if (charData != null && (charData.PosX != 0 || charData.PosZ != 0))
                return new Vector3(charData.PosX, charData.PosY, charData.PosZ);

            var spawn = GameConstants.InitialSpawn.GetSpawn(race);
            return new Vector3(spawn.x, spawn.y, spawn.z);
        }

        // ══════════════════════════════════════════════════════════════════
        // Registro de prefabs
        // ══════════════════════════════════════════════════════════════════

        private void RegisterSpawnablePrefabs()
        {
            if (_prefabsRegistered) return;

            int registered = 0;
            int skipped    = 0;

            // Registra prefabs de raça
            foreach (var entry in racePrefabs)
            {
                if (entry?.Prefab == null) continue;
                if (!TryRegisterPrefab(entry.Prefab)) continue;
                registered++;
            }

            foreach (var prefab in spawnablePrefabs)
            {
                if (prefab == null) { skipped++; continue; }
                if (!TryRegisterPrefab(prefab)) continue;
                registered++;
            }

            foreach (var entry in projectilePrefabs)
            {
                if (entry?.ProjectilePrefab == null) continue;
                if (TryRegisterPrefab(entry.ProjectilePrefab))
                    registered++;
            }

            _prefabsRegistered = true;
            if (registered > 0)
                Debug.Log($"[RPGNetworkManager] {registered} prefabs registrados " +
                          $"(incluindo {_projectileLookup.Count} projéteis).");
            if (skipped > 0)
                Debug.LogWarning($"[RPGNetworkManager] {skipped} entradas nulas em spawnablePrefabs.");
        }

        private bool TryRegisterPrefab(GameObject prefab)
        {
            var identity = prefab.GetComponent<NetworkIdentity>();
            if (identity == null)
            {
                Debug.LogError($"[RPGNetworkManager] '{prefab.name}' sem NetworkIdentity — ignorado.");
                return false;
            }
            if (!NetworkClient.prefabs.ContainsKey(identity.assetId))
            {
                NetworkClient.RegisterPrefab(prefab);
                return true;
            }
            return false;
        }
    }
}
