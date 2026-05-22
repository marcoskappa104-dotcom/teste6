using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Mirror;

namespace RPG.NPC
{

    public class NetworkNPCSpawner : MonoBehaviour
    {
        [System.Serializable]
        public class NpcSpawn
        {
            [Tooltip("Prefab do NPC. Precisa ter NetworkIdentity + NetworkNPC.")]
            public GameObject npcPrefab;

            [Tooltip("Posição de spawn. Será snapped ao NavMesh se possível.")]
            public Vector3 position;

            [Tooltip("Rotação em Y (graus).")]
            public float yRotation;

            [Tooltip("Rótulo para logs (apenas debug).")]
            public string label = "NPC";
        }

        [SerializeField] private NpcSpawn[] npcs;
        [SerializeField] private bool       logSpawns = true;
        [SerializeField] private float      navMeshWaitTimeout = 8f;

        private const float NAVMESH_SAMPLE_RADIUS = 5f;

        private void Start()
        {
            if (!NetworkServer.active) return;
            if (npcs == null || npcs.Length == 0) return;

            StartCoroutine(SpawnWhenReady());
        }

        private IEnumerator SpawnWhenReady()
        {
            // Aguarda NavMesh igual ao MonsterSpawner — não estritamente
            // necessário para NPCs estáticos, mas garante consistência
            // se eventualmente NPCs forem fazer patrulha.
            float elapsed = 0f;
            while (elapsed < navMeshWaitTimeout)
            {
                if (NavMesh.SamplePosition(Vector3.zero, out _, 200f, NavMesh.AllAreas)
                    || IsAnyNpcPosOnNavMesh())
                    break;
                elapsed += 0.2f;
                yield return new WaitForSeconds(0.2f);
            }

            int spawned = 0;
            foreach (var entry in npcs)
            {
                if (entry == null) continue;
                if (entry.npcPrefab == null)
                {
                    Debug.LogWarning($"[NetworkNPCSpawner] '{entry.label}' sem prefab.");
                    continue;
                }
                if (entry.npcPrefab.GetComponent<NetworkIdentity>() == null
                    || entry.npcPrefab.GetComponent<NetworkNPC>() == null)
                {
                    Debug.LogError($"[NetworkNPCSpawner] '{entry.npcPrefab.name}' sem NetworkIdentity ou NetworkNPC.");
                    continue;
                }

                Vector3 pos = entry.position;
                if (NavMesh.SamplePosition(pos, out NavMeshHit hit, NAVMESH_SAMPLE_RADIUS, NavMesh.AllAreas))
                    pos = hit.position;

                var go = Instantiate(entry.npcPrefab, pos, Quaternion.Euler(0f, entry.yRotation, 0f));
                NetworkServer.Spawn(go);
                spawned++;
            }

            if (logSpawns)
                Debug.Log($"[NetworkNPCSpawner] {spawned} NPCs spawnados.");
        }

        private bool IsAnyNpcPosOnNavMesh()
        {
            foreach (var entry in npcs)
            {
                if (entry == null) continue;
                if (NavMesh.SamplePosition(entry.position, out _, NAVMESH_SAMPLE_RADIUS, NavMesh.AllAreas))
                    return true;
            }
            return false;
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (npcs == null) return;
            Gizmos.color = new Color(0.9f, 0.7f, 0.2f, 0.9f);
            foreach (var entry in npcs)
            {
                if (entry == null) continue;
                Gizmos.DrawSphere(entry.position, 0.35f);
                UnityEditor.Handles.Label(entry.position + Vector3.up * 0.6f, entry.label);
            }
        }
#endif
    }
}
