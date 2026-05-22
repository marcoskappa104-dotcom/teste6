using System.Collections;
using UnityEngine;
using Mirror;
using RPG.Quest;

namespace RPG.NPC
{

    public class NpcQuestMarkerUpdater : MonoBehaviour
    {
        [Tooltip("Intervalo mínimo entre updates globais (anti-thrash em listas longas).")]
        [SerializeField] private float minUpdateInterval = 0.5f;

        private QuestManager _localQM;
        private bool         _subscribed;
        private bool         _updatePending;
        private Coroutine    _scheduledUpdate;

        private void OnEnable()
        {
            StartCoroutine(BindLocalPlayerLoop());
        }

        private void OnDisable()
        {
            UnsubscribeFromLocal();
            if (_scheduledUpdate != null)
            {
                StopCoroutine(_scheduledUpdate);
                _scheduledUpdate = null;
            }
        }

        private IEnumerator BindLocalPlayerLoop()
        {
            var wait = new WaitForSeconds(0.5f);
            while (_localQM == null)
            {
                if (NetworkClient.active && NetworkClient.localPlayer != null)
                {
                    _localQM = NetworkClient.localPlayer.GetComponent<QuestManager>();
                    if (_localQM != null)
                    {
                        SubscribeToLocal();
                        ScheduleUpdate();
                    }
                }
                yield return wait;
            }
        }

        private void SubscribeToLocal()
        {
            if (_subscribed || _localQM == null) return;
            _localQM.OnQuestsChanged += OnQuestsChanged;
            _subscribed = true;
        }

        private void UnsubscribeFromLocal()
        {
            if (!_subscribed || _localQM == null) return;
            _localQM.OnQuestsChanged -= OnQuestsChanged;
            _subscribed = false;
        }

        private void OnQuestsChanged() => ScheduleUpdate();

        private void ScheduleUpdate()
        {
            if (_updatePending) return;
            _updatePending = true;
            _scheduledUpdate = StartCoroutine(UpdateAfterDelay());
        }

        private IEnumerator UpdateAfterDelay()
        {
            yield return new WaitForSeconds(minUpdateInterval);
            _scheduledUpdate = null;
            _updatePending   = false;

            var localPlayer = NetworkClient.localPlayer?.GetComponent<RPG.Network.NetworkPlayer>();

            // Itera todos os NetworkNPC ativos. Spawned é a fonte de verdade
            // sob Mirror; usá-la é mais robusto que FindObjectsOfType.
            foreach (var kv in NetworkClient.spawned)
            {
                if (kv.Value == null) continue;
                var npc = kv.Value.GetComponent<NetworkNPC>();
                if (npc == null) continue;
                npc.ClientUpdateQuestMarker(localPlayer);
            }
        }
    }
}
