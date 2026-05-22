using UnityEngine;
using UnityEngine.SceneManagement;

namespace RPG.Network
{

    public class ServerEntryPoint : MonoBehaviour
    {
        [Header("Cena do servidor (precisa estar no Build Settings)")]
        [SerializeField] private string serverSceneName = "GameplayScene";

        private void Awake()
        {
            if (!NetworkConnectionBootstrapper.IsServerBuild()) return;

            string currentScene = SceneManager.GetActiveScene().name;
            if (currentScene == serverSceneName) return;

            Debug.Log($"[ServerEntryPoint] Servidor detectado. Carregando '{serverSceneName}'.");
            SceneManager.LoadScene(serverSceneName);
        }
    }
}
