using UnityEngine;
using Mirror;
using RPG.Data;
using System.Collections.Generic;

namespace RPG.Managers
{

    public class ItemDropManager : MonoBehaviour
    {
        public static ItemDropManager Instance { get; private set; }

        [Header("Prefab do Item no Mundo")]
        [Tooltip("Precisa ter NetworkIdentity + WorldItem.")]
        [SerializeField] private GameObject worldItemPrefab;

        [Header("Configuração")]
        [SerializeField] private float spawnHeightOffset = 0.3f;
        [SerializeField] private float dropScatterRadius = 1.5f;

        private const int MAX_DROPS_PER_SPAWN = 16;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            if (worldItemPrefab == null)
                Debug.LogError("[ItemDropManager] worldItemPrefab NÃO CONFIGURADO. " +
                               "Drops não funcionarão!");
            else if (worldItemPrefab.GetComponent<RPG.Network.WorldItem>() == null)
                Debug.LogError("[ItemDropManager] worldItemPrefab não tem WorldItem component.");
            else if (worldItemPrefab.GetComponent<NetworkIdentity>() == null)
                Debug.LogError("[ItemDropManager] worldItemPrefab não tem NetworkIdentity.");
        }

        /// <summary>
        /// Sorteia drops usando a nova estrutura de LootTable.
        /// </summary>
        [Server]
        public void ServerSpawnFromTable(Vector3 originPosition, LootTable table)
        {
            if (table == null) return;
            var drops = table.GetDrops();
            if (drops == null || drops.Count == 0) return;

            Vector3 landPosition = GetLandPosition(originPosition);
            int dropIndex = 0;

            foreach (var drop in drops)
            {
                if (dropIndex >= MAX_DROPS_PER_SPAWN) break;
                Vector3 pos = ScatterPosition(landPosition, dropIndex++);
                SpawnWorldItem(pos, drop.itemId, originPosition, drop.quantity);
            }
        }

        /// <summary>
        /// Sorteia drops para um monstro morto.
        /// guaranteedDrops são sempre spawnados (independente de chance).
        /// </summary>
        [Server]
        public void ServerSpawnDrop(
            Vector3         originPosition,
            List<LootEntry> customDropTable = null,
            List<string>    guaranteedDrops = null)
        {
            if (!NetworkServer.active) return;

            if (worldItemPrefab == null)
            {
                Debug.LogWarning("[ItemDropManager] worldItemPrefab não configurado.");
                return;
            }

            Vector3 landPosition = GetLandPosition(originPosition);
            int dropIndex = 0;

            // 1. Drops garantidos (com cap defensivo)
            if (guaranteedDrops != null)
            {
                int limit = Mathf.Min(guaranteedDrops.Count, MAX_DROPS_PER_SPAWN);
                for (int i = 0; i < limit; i++)
                {
                    Vector3 pos = ScatterPosition(landPosition, dropIndex++);
                    SpawnWorldItem(pos, guaranteedDrops[i], originPosition, 1);
                }
            }

            // 2. Drops da tabela (cada item rola sua própria chance)
            if (customDropTable != null)
            {
                foreach (var entry in customDropTable)
                {
                    if (entry.Item == null) continue;
                    if (dropIndex >= MAX_DROPS_PER_SPAWN) break;

                    // Rola a chance individual do item configurada na tabela
                    float roll = Random.Range(0f, 100f);
                    if (roll <= entry.DropChance)
                    {
                        Vector3 pos = ScatterPosition(landPosition, dropIndex++);
                        SpawnWorldItem(pos, entry.Item.ItemId, originPosition, 1);
                    }
                }
            }
        }

        private Vector3 GetLandPosition(Vector3 origin)
        {
            Vector3 landPosition = origin;
            if (Physics.Raycast(origin + Vector3.up, Vector3.down, out RaycastHit hit, 5f))
            {
                if (hit.point.sqrMagnitude > 0.001f || origin.sqrMagnitude < 0.001f)
                {
                    landPosition = hit.point;
                }
            }
            return landPosition;
        }

        /// <summary>
        /// Valida o item ANTES de instanciar para evitar memory leak.
        /// </summary>
        [Server]
        private void SpawnWorldItem(Vector3 targetPosition, string itemId, Vector3 origin, int quantity)
        {
            if (string.IsNullOrEmpty(itemId)) return;

            if (ItemDatabase.Instance == null)
            {
                Debug.LogWarning($"[ItemDropManager] ItemDatabase.Instance nulo. Drop '{itemId}' ignorado.");
                return;
            }

            if (!ItemDatabase.Instance.Contains(itemId))
            {
                Debug.LogWarning($"[ItemDropManager] Item '{itemId}' não existe no banco. Ignorado.");
                return;
            }

            var go   = Instantiate(worldItemPrefab, targetPosition, Quaternion.identity);
            var item = go.GetComponent<RPG.Network.WorldItem>();

            if (item == null)
            {
                Debug.LogError("[ItemDropManager] worldItemPrefab não tem WorldItem component.");
                Destroy(go);
                return;
            }

            item.ServerInitialize(itemId, origin, targetPosition, quantity);
            NetworkServer.Spawn(go);
        }

        /// <summary>
        /// Distribui drops em direções aleatórias ao redor do centro.
        /// </summary>
        private Vector3 ScatterPosition(Vector3 center, int index)
        {
            // Usa um ângulo baseado no index + aleatório para garantir dispersão mesmo se Random falhar
            float baseAngle = Random.Range(0f, 360f);
            float angle     = (baseAngle + (index * (360f / 4f))) * Mathf.Deg2Rad; 
            
            // Distância entre 1.0 e o raio máximo
            float r = Random.Range(Mathf.Min(1.0f, dropScatterRadius), dropScatterRadius);
            
            return new Vector3(
                center.x + Mathf.Cos(angle) * r,
                center.y + spawnHeightOffset,
                center.z + Mathf.Sin(angle) * r);
        }
    }
}