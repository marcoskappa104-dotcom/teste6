using UnityEngine;
using Mirror;
using RPG.Character;

namespace RPG.Network
{

    [RequireComponent(typeof(NetworkIdentity))]
    public class Projectile : NetworkBehaviour
    {
        [Header("Configuração")]
        [Tooltip("Velocidade angular máxima (deg/s) para seguir o alvo.")]
        [SerializeField] private float maxTurnRate = 360f;

        [Tooltip("Distância de impacto (m). Quando chegar a essa distância do alvo, aplica dano.")]
        [SerializeField] private float impactDistance = 0.6f;

        [Tooltip("Tempo máximo de vida em segundos. Auto-destroi se não acertar.")]
        [SerializeField] private float maxLifetime = 6f;

        [Tooltip("Efeito visual ao impacto (opcional, instanciado client-side).")]
        [SerializeField] private GameObject hitVfxPrefab;

        // ── SyncVars ───────────────────────────────────────────────────────
        // Mantemos a ordem para compatibilidade serializada.
        [SyncVar] private uint    _shooterNetId;
        [SyncVar] private Vector3 _initialDirection;

        // === FIX (Lote 1): geração do alvo no momento do spawn ===
        // Se o monstro respawnar (incrementando spawnGeneration), o projétil
        // detecta e aborta o impacto.
        [SyncVar] private int _expectedTargetGeneration = -1;

        // ── Estado de servidor ─────────────────────────────────────────────
        private float                _speed;
        private float                _damage;
        private bool                 _crit;
        private float                _spawnTime;
        private NetworkBehaviour     _serverTarget;
        private bool                 _hitProcessed;

        // No cliente: fallback se o NetworkTransform falhar
        private float _clientSpawnTime;

        // ══════════════════════════════════════════════════════════════════
        // API do servidor
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public void ServerInitialize(NetworkBehaviour target, uint shooterNetId,
                                     float speed, float damage, bool crit,
                                     int targetSpawnGeneration = -1)
        {
            _serverTarget             = target;
            _shooterNetId             = shooterNetId;
            _speed                    = Mathf.Max(1f, speed);
            _damage                   = Mathf.Max(0f, damage);
            _crit                     = crit;
            _spawnTime                = Time.time;
            _hitProcessed             = false;
            _expectedTargetGeneration = targetSpawnGeneration;

            if (target != null)
            {
                Vector3 dir = (target.transform.position - transform.position);
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.001f)
                {
                    _initialDirection = dir.normalized;
                    transform.rotation = Quaternion.LookRotation(_initialDirection);
                }
                else
                {
                    _initialDirection = transform.forward;
                }
            }
            else
            {
                _initialDirection = transform.forward;
            }
        }

        // Sobrecarga retrocompatível: chamadas antigas (sem targetGeneration)
        // continuam funcionando. Nesses casos, não há check de geração.
        [Server]
        public void ServerInitialize(NetworkBehaviour target, uint shooterNetId,
                                     float speed, float damage, bool crit)
        {
            ServerInitialize(target, shooterNetId, speed, damage, crit, -1);
        }

        public override void OnStartClient()
        {
            _clientSpawnTime = Time.time;

            if (_initialDirection.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(_initialDirection);
        }

        // ══════════════════════════════════════════════════════════════════
        // Update
        // ══════════════════════════════════════════════════════════════════

        private void Update()
        {
            if (isServer)
            {
                ServerUpdate();
                return;
            }

            // Cliente: failsafe se o servidor não destruiu por algum motivo
            if (Time.time - _clientSpawnTime > maxLifetime + 0.5f)
                gameObject.SetActive(false);
        }

        [Server]
        private void ServerUpdate()
        {
            // Timeout
            if (Time.time - _spawnTime > maxLifetime)
            {
                NetworkServer.Destroy(gameObject);
                return;
            }

            Vector3 desiredDir = _initialDirection;

            if (IsTargetTrackable(_serverTarget))
            {
                Vector3 toTarget = _serverTarget.transform.position - transform.position;
                toTarget.y = 0f;
                float sqr = toTarget.sqrMagnitude;

                if (sqr > 0.001f)
                {
                    desiredDir = toTarget.normalized;

                    // === FIX (Lote 1): usa sqrMagnitude consistentemente ===
                    float impactSqr = impactDistance * impactDistance;
                    if (sqr <= impactSqr)
                    {
                        ApplyImpact();
                        return;
                    }
                }
            }
            else if (!ReferenceEquals(_serverTarget, null))
            {
                _serverTarget = null;
            }

            // Rotação clamped
            Vector3 currentForward = transform.forward;
            currentForward.y = 0f;
            if (currentForward.sqrMagnitude > 0.001f)
            {
                currentForward.Normalize();
                float angle = Vector3.Angle(currentForward, desiredDir);
                float maxStep = maxTurnRate * Time.deltaTime;
                if (angle > maxStep)
                {
                    Vector3 cross = Vector3.Cross(currentForward, desiredDir);
                    float sign    = Mathf.Sign(cross.y);
                    Quaternion q  = Quaternion.AngleAxis(maxStep * sign, Vector3.up);
                    desiredDir    = q * currentForward;
                }
                transform.rotation = Quaternion.LookRotation(desiredDir);
            }

            transform.position += transform.forward * (_speed * Time.deltaTime);
        }

        [Server]
        private static bool IsTargetTrackable(NetworkBehaviour nb)
        {
            if (nb == null) return false;
            if (nb is ITargetable t && t.IsDead) return false;
            return true;
        }

        [Server]
        private void ApplyImpact()
        {
            if (_hitProcessed) return;
            _hitProcessed = true;

            // === FIX (Lote 1): verifica geração do monstro ===
            // Se o monstro respawnou entre disparo e impacto, abortamos.
            if (_serverTarget is NetworkMonsterEntity monster)
            {
                if (_expectedTargetGeneration >= 0
                    && monster.SpawnGeneration != _expectedTargetGeneration)
                {
                    // Monstro respawnou — projétil "morre" sem efeito.
                    // Sem dano, sem VFX (esquisito mostrar impacto fantasma).
                    NetworkServer.Destroy(gameObject);
                    return;
                }

                if (!monster.IsDead)
                    monster.ServerTakeProjectileDamage(_shooterNetId, _damage, _crit);
            }
            else if (_serverTarget is NetworkPlayer player && !player.Dead)
            {
                // Reservado para PvP futuro
                player.ServerApplyDamageWithFeedback(_damage);
            }

            RpcOnImpact(transform.position);
            NetworkServer.Destroy(gameObject);
        }

        // ══════════════════════════════════════════════════════════════════
        // VFX no cliente
        // ══════════════════════════════════════════════════════════════════

        [ClientRpc]
        private void RpcOnImpact(Vector3 pos)
        {
            if (Application.isBatchMode) return;
            if (hitVfxPrefab != null)
            {
                var vfx = Instantiate(hitVfxPrefab, pos, Quaternion.identity);
                Destroy(vfx, 2f);
            }
        }
    }
}