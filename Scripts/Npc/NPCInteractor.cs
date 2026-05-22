using UnityEngine;
using Mirror;
using RPG.Character;
using RPG.UI;

namespace RPG.NPC
{

    public class NPCInteractor : MonoBehaviour
    {
        [Tooltip("Margem extra além do range do NPC para chamar 'em alcance'. " +
                 "Cliente é otimista; servidor é o juiz final. Não pode ser negativo.")]
        [SerializeField] private float clientRangeBonus = 0.5f;

        private NetworkIdentity _identity;
        private PlayerEntity    _playerEntity;

        private void Awake()
        {
            _identity     = GetComponent<NetworkIdentity>();
            _playerEntity = GetComponent<PlayerEntity>();

            // === FIX (Lote 3): clamp defensivo ===
            if (clientRangeBonus < 0f) clientRangeBonus = 0f;
        }

        public bool TryInteract(NetworkNPC npc)
        {
            if (npc == null) return false;
            if (_identity == null) return false;
            if (_playerEntity != null && _playerEntity.IsDead) return false;

            _playerEntity?.SetTarget(npc);
            UIManager.Instance?.UpdateTargetPanel(npc);

            float dist     = Vector3.Distance(transform.position, npc.Position);
            float maxRange = npc.InteractionRangeReal + clientRangeBonus;
            if (dist > maxRange)
            {
                UIManager.Instance?.ShowMessage("Aproxime-se para conversar.");
                return false;
            }

            npc.CmdInteract();
            return true;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (clientRangeBonus < 0f) clientRangeBonus = 0f;
        }
#endif
    }
}