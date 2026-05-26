using System.Collections;
using UnityEngine;
using UnityEngine.AI;
using Mirror;

namespace RPG.Network
{
    /// <summary>
    /// Orquestra a sequência server-side de morte do monstro:
    /// 1. Desativa NavMeshAgent, Collider, NetworkTransform
    /// 2. Troca layer para "Dead"
    /// 3. Aguarda fade visual cliente terminar
    /// 4. Notifica spawner para agendar respawn
    /// 5. Destrói o GameObject na rede
    ///
    /// Extraído do NetworkMonsterEntity para isolar a lógica de teardown.
    /// </summary>
    public class MonsterDeathHandler : MonoBehaviour
    {
        [Tooltip("Segundos extras a aguardar após o fade visual antes de destruir.")]
        [SerializeField] private float _postFadeMargin = 0.5f;

        [Tooltip("Segundos para o respawner agendar o novo spawn.")]
        [SerializeField] private float _respawnDelay = 15f;

        private NetworkMonsterEntity  _entity;
        private MonsterVisualFader    _fader;
        private NetworkMonsterSpawner _spawner;
        private GameObject            _monsterPrefab;
        private Vector3               _homePosition;
        private float                 _patrolRadius;

        public float RespawnDelay => _respawnDelay;

        private void Awake()
        {
            _entity = GetComponent<NetworkMonsterEntity>();
            _fader  = GetComponent<MonsterVisualFader>();
        }

        public void ConfigureRespawn(NetworkMonsterSpawner spawner,
                                     GameObject monsterPrefab,
                                     Vector3 homePosition,
                                     float patrolRadius)
        {
            _spawner       = spawner;
            _monsterPrefab = monsterPrefab;
            _homePosition  = homePosition;
            _patrolRadius  = patrolRadius;
        }

        // ══════════════════════════════════════════════════════════════════
        // Entry point — chamado por NetworkMonsterEntity quando HP <= 0
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public IEnumerator RunDeathSequence(Vector3 deathPos)
        {
            DisableComponents();

            float fadeWait = _postFadeMargin;
            if (_fader != null)
                fadeWait += _fader.TotalDuration;

            yield return new WaitForSeconds(fadeWait);

            if (this == null || !isActiveAndEnabled || !NetworkServer.active)
                yield break;

            if (_spawner != null && _monsterPrefab != null)
                _spawner.ServerNotifyDeath(_monsterPrefab, _homePosition, _patrolRadius, _respawnDelay);

            NetworkServer.Destroy(gameObject);
        }

        [Server]
        private void DisableComponents()
        {
            var agent = GetComponent<NavMeshAgent>();
            if (agent != null)
            {
                if (agent.isOnNavMesh)
                {
                    agent.ResetPath();
                    agent.velocity = Vector3.zero;
                }
                agent.enabled = false;
            }

            var col = GetComponent<Collider>();
            if (col != null) col.enabled = false;

            int deadLayer = LayerMask.NameToLayer("Dead");
            if (deadLayer >= 0) gameObject.layer = deadLayer;

            var nt = GetComponent<NetworkTransformUnreliable>();
            if (nt != null) nt.enabled = false;
        }
    }
}
