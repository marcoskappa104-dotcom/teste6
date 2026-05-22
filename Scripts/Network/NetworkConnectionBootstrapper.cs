using UnityEngine;
using Mirror;

namespace RPG.Network
{

    public class NetworkConnectionBootstrapper : MonoBehaviour
    {
        [Header("Conexão (cliente)")]
        [SerializeField] public string serverAddress = "localhost";
        [SerializeField] public ushort serverPort    = 7777;

        private void Start()
        {
            if (NetworkServer.active || NetworkClient.active)
            {
                Debug.Log("[Bootstrapper] Rede já ativa.");
                return;
            }

            if (NetworkManager.singleton == null)
            {
                Debug.LogError("[Bootstrapper] NetworkManager.singleton é null! " +
                               "Verifique se há um NetworkManager (ou RPGNetworkManager) na cena.");
                return;
            }

            bool isServer = IsServerBuild();
            bool isHost   = IsHostBuild();

            ConfigureKcpPort();

            if (isServer)
            {
                Debug.Log($"[Bootstrapper] SERVIDOR DEDICADO | porta:{serverPort}");
                NetworkManager.singleton.StartServer();
            }
            else if (isHost)
            {
                Debug.Log($"[Bootstrapper] HOST | porta:{serverPort}");
                NetworkManager.singleton.networkAddress = serverAddress;
                NetworkManager.singleton.StartHost();
            }
            else
            {
                Debug.Log($"[Bootstrapper] CLIENTE | {serverAddress}:{serverPort}");
                NetworkManager.singleton.networkAddress = serverAddress;
                NetworkManager.singleton.StartClient();
            }
        }

        private void ConfigureKcpPort()
        {
            var kcp = GetComponentInChildren<kcp2k.KcpTransport>()
                   ?? FindFirstObjectByType<kcp2k.KcpTransport>();
            if (kcp != null)
                kcp.Port = serverPort;
            else
                Debug.LogWarning("[Bootstrapper] KcpTransport não encontrado.");
        }

        public static bool IsServerBuild()
        {
            if (Application.isBatchMode) return true;
            foreach (var arg in System.Environment.GetCommandLineArgs())
                if (string.Equals(arg, "-server", System.StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public static bool IsHostBuild()
        {
            foreach (var arg in System.Environment.GetCommandLineArgs())
                if (string.Equals(arg, "-host", System.StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}