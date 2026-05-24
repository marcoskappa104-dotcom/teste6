using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.UI;
using System.Collections;

namespace RPG.Network
{

    [RequireComponent(typeof(NetworkIdentity))]
    public class WorldItem : NetworkBehaviour
    {
        [Header("Visual")]
        [SerializeField] private SpriteRenderer spriteRenderer;
        [SerializeField] private TMPro.TMP_Text nameLabel;
        [SerializeField] private GameObject     glowEffect;

        [Header("Visual Root (filho que recebe o bobbing)")]
        [SerializeField] private Transform visualRoot;

        [Header("Configuração")]
        [SerializeField] private float despawnTime  = 60f;
        [SerializeField] private float bobAmplitude = 0.15f;
        [SerializeField] private float bobFrequency = 1.5f;
        [SerializeField] private float pickupRadius = 2.5f;

        [Header("Efeito de Salto")]
        [SerializeField] private bool  useJumpEffect = true;
        [SerializeField] private float jumpHeight    = 1.5f;
        [SerializeField] private float jumpDuration  = 0.6f;

        // Multiplicador anti-cheat: aceita pickups com folga para latência,
        // mas não tão generoso a ponto de viabilizar trapaça óbvia.
        private const float PICKUP_LATENCY_TOLERANCE = 1.5f;

        [SyncVar(hook = nameof(OnItemIdChanged))]   private string  _itemId = "";
        [SyncVar]                                   private Vector3 _spawnOrigin;
        [SyncVar(hook = nameof(OnTargetPosChanged))] private Vector3 _targetPos;

        public string ItemId => _itemId;

        private bool      _picked;
        private float     _startLocalY;
        private bool      _hasVisualRoot;
        private Coroutine _despawnCoroutine;
        private float     _jumpTime;
        private bool      _isJumping;

        // ══════════════════════════════════════════════════════════════════
        // Server
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public void ServerInitialize(string itemId, Vector3 origin, Vector3 target)
        {
            _itemId      = itemId;
            _spawnOrigin = origin;
            _targetPos   = target;
            _despawnCoroutine = StartCoroutine(AutoDespawn());
        }

        [Server]
        private IEnumerator AutoDespawn()
        {
            yield return new WaitForSeconds(despawnTime);
            _despawnCoroutine = null;
            if (!_picked && isServer)
                NetworkServer.Destroy(gameObject);
        }

        // ══════════════════════════════════════════════════════════════════
        // Client
        // ══════════════════════════════════════════════════════════════════

        private void Awake()
        {
            _hasVisualRoot = visualRoot != null;
            if (!_hasVisualRoot)
            {
                Debug.LogWarning($"[WorldItem] '{name}': visualRoot não configurado no prefab. " +
                                 "Bobbing aplicado no transform raiz pode causar jitter em multiplayer.");
            }
        }

        public override void OnStartClient()
        {
            if (_hasVisualRoot)
                _startLocalY = visualRoot.localPosition.y;

            RefreshVisual(_itemId);

            if (useJumpEffect && _hasVisualRoot)
            {
                // Inicia o salto. Não esperamos SyncVars aqui, pois elas vêm no pacote de spawn.
                // Mas garantimos que o visual começa escondido ou no lugar certo.
                StartCoroutine(JumpRoutine());
            }
        }

        private void OnTargetPosChanged(Vector3 oldPos, Vector3 newPos)
        {
            // Mirror sincroniza o transform.position, mas forçamos aqui para garantir
            if (newPos.sqrMagnitude > 0.001f)
                transform.position = newPos;
        }

        private IEnumerator JumpRoutine()
        {
            if (!_hasVisualRoot) yield break;

            _isJumping = true;
            
            // Aguarda um tempo mínimo para garantir que os dados de spawn chegaram
            // e o transform.position foi atualizado pelo Mirror.
            float waitTime = 0.1f;
            while (waitTime > 0)
            {
                if (_targetPos.sqrMagnitude > 0.001f) break;
                waitTime -= Time.deltaTime;
                yield return null;
            }

            // Posição final (onde o objeto principal deve ficar parado)
            Vector3 destination = _targetPos.sqrMagnitude > 0.001f ? _targetPos : transform.position;
            // Posição inicial (de onde o item salta - o monstro)
            Vector3 startPos = _spawnOrigin.sqrMagnitude > 0.001f ? _spawnOrigin : destination;

            // Fixamos o objeto principal no destino
            transform.position = destination;
            
            // O visual começa no monstro (em coordenadas de mundo)
            visualRoot.position = startPos;
            visualRoot.gameObject.SetActive(true);

            float elapsed = 0f;
            float randomHeight   = jumpHeight * Random.Range(1.1f, 1.4f);
            float randomDuration = jumpDuration * Random.Range(0.85f, 1.15f);
            Vector3 rotSpeed     = new Vector3(Random.Range(-350f, 350f), Random.Range(-350f, 350f), Random.Range(-350f, 350f));

            while (elapsed < randomDuration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / randomDuration;

                // Curva de parábola
                float h = Mathf.Sin(t * Mathf.PI) * randomHeight;
                
                // Interpolação linear do mundo entre monstro e destino
                Vector3 currentPos = Vector3.Lerp(startPos, destination, t);
                currentPos.y += h;

                // Move o visualRoot (filho) enquanto o transform (pai) está fixo no destino
                visualRoot.position = currentPos;
                visualRoot.Rotate(rotSpeed * Time.deltaTime);

                yield return null;
            }

            // Finaliza: reseta localmente para o efeito de bobbing (flutuar) começar
            visualRoot.localPosition = new Vector3(0, _startLocalY, 0);
            visualRoot.localRotation = Quaternion.identity;
            _isJumping = false;
        }

        private void OnItemIdChanged(string oldId, string newId) => RefreshVisual(newId);

        private void RefreshVisual(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return;
            var item = ItemDatabase.Instance?.GetItem(itemId);
            if (item == null) return;

            if (spriteRenderer != null && item.Icon != null)
                spriteRenderer.sprite = item.Icon;

            if (nameLabel != null)
            {
                nameLabel.text  = item.DisplayName;
                nameLabel.color = item.RarityColor;
            }

            if (glowEffect != null)
                glowEffect.SetActive(item.Rarity >= ItemRarity.Rare);
        }

        private void Update()
        {
            if (!isClient || !_hasVisualRoot) return;

            // PROTEÇÃO: Se o item "fugir" para o zero após o pulo ou por erro de rede, trazemos ele de volta
            if (!_isJumping && _targetPos.sqrMagnitude > 0.001f)
            {
                // Se a distância for muito grande (indicando teleporte pro zero), força a posição
                if (Vector3.Distance(transform.position, _targetPos) > 0.01f)
                {
                    transform.position = _targetPos;
                }
            }

            if (_isJumping) return;

            float newLocalY = _startLocalY
                + Mathf.Sin(Time.time * bobFrequency * Mathf.PI * 2f) * bobAmplitude;
            var localPos = visualRoot.localPosition;
            localPos.y = newLocalY;
            visualRoot.localPosition = localPos;
        }

        // ══════════════════════════════════════════════════════════════════
        // Pickup — CLAIM-FIRST PATTERN
        // ══════════════════════════════════════════════════════════════════

        [Command(requiresAuthority = false)]
        public void CmdPickUp(uint playerNetId)
        {
            // RACE PROTECTION: reivindica imediatamente. Se algum check falhar
            // depois, revertemos. Garante que dois Cmds simultâneos não passem
            // ambos pelas validações.
            if (_picked) return;

            if (string.IsNullOrEmpty(_itemId))
            {
                Debug.LogWarning("[WorldItem] CmdPickUp em item sem _itemId — SyncVar ainda não chegou?");
                return;
            }

            // Resolve player ANTES de reivindicar (caso netId inválido)
            NetworkPlayer player = null;
            if (NetworkServer.spawned.TryGetValue(playerNetId, out var identity))
                player = identity?.GetComponent<NetworkPlayer>();

            if (player == null || player.Dead) return;

            // RECLAMA o pickup. Daqui pra frente, qualquer falha precisa reverter.
            _picked = true;

            // Range check
            float dist        = Vector3.Distance(transform.position, player.transform.position);
            float maxDistance = pickupRadius * PICKUP_LATENCY_TOLERANCE;
            if (dist > maxDistance)
            {
                _picked = false;
                Debug.LogWarning($"[WorldItem] Pickup fora de range: {dist:0.1}u por {player.CharacterName} (max {maxDistance:0.1})");
                return;
            }

            // Inventory check
            var inventory = player.GetComponent<NetworkInventory>();
            if (inventory == null)
            {
                _picked = false;
                return;
            }

            // Tenta adicionar — pode falhar por inventário cheio
            int slotIndex = inventory.ServerAddItem(_itemId);
            if (slotIndex < 0)
            {
                _picked = false;
                return;
            }

            // ── SUCESSO GARANTIDO DAQUI PRA BAIXO ─────────────────────────
            // Cancela APENAS o despawn — sem StopAllCoroutines, que mataria
            // qualquer coroutine futura que alguém adicionar a este componente.
            if (_despawnCoroutine != null)
            {
                StopCoroutine(_despawnCoroutine);
                _despawnCoroutine = null;
            }

            var    item     = ItemDatabase.Instance?.GetItem(_itemId);
            string itemName = item?.DisplayName ?? _itemId;
            Color  color    = item?.RarityColor ?? Color.white;
            RpcPickupFeedback(playerNetId, itemName, color);

            NetworkServer.Destroy(gameObject);
        }

        [ClientRpc]
        private void RpcPickupFeedback(uint playerNetId, string itemName, Color rarityColor)
        {
            if (Application.isBatchMode) return;

            var localPlayer = NetworkClient.localPlayer;
            if (localPlayer == null) return;
            if (localPlayer.netId != playerNetId) return;

            FloatingTextManager.Instance?.Show(
                $"+ {itemName}", transform.position + Vector3.up, rarityColor);
            UIManager.Instance?.ShowMessage($"Coletou: {itemName}");
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0f, 1f, 0f, 0.3f);
            Gizmos.DrawSphere(transform.position, pickupRadius);
        }
#endif
    }
}
