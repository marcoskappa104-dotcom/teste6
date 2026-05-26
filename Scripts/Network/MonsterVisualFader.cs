using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace RPG.Network
{
    /// <summary>
    /// Cuida do fade visual do monstro no cliente quando ele morre.
    /// Extraído do NetworkMonsterEntity para isolar manipulação de materiais.
    ///
    /// Responsabilidades:
    /// - Aguardar delay configurado
    /// - Clonar materiais e ativar blend mode
    /// - Interpolar alpha para 0
    /// - Desativar visualRoot ao fim
    /// - Restaurar alpha original em respawn
    /// </summary>
    public class MonsterVisualFader : MonoBehaviour
    {
        [Header("Visual")]
        [SerializeField] private GameObject _visualRoot;

        [Header("Tempo")]
        [SerializeField] private float _fadeDelay    = 5f;
        [SerializeField] private float _fadeDuration = 1f;

        // Cache de instâncias clonadas — precisam ser destruídas para não vazar materiais
        private List<Material> _fadeMaterialInstances;
        private Coroutine      _fadeCoroutine;

        public float FadeDelay    => _fadeDelay;
        public float FadeDuration => _fadeDuration;
        /// <summary>Tempo total até o visual sumir completamente.</summary>
        public float TotalDuration => _fadeDelay + _fadeDuration;

        private void OnDisable()
        {
            StopFadeRoutine();
            ReleaseFadeMaterials();
        }

        private void OnDestroy()
        {
            ReleaseFadeMaterials();
        }

        public void OnStartClientReset()
        {
            if (_visualRoot != null) _visualRoot.SetActive(true);
            RestoreVisualsAlpha();
        }

        // ══════════════════════════════════════════════════════════════════
        // API pública (chamada por NetworkMonsterEntity)
        // ══════════════════════════════════════════════════════════════════

        public void BeginFade()
        {
            StopFadeRoutine();
            _fadeCoroutine = StartCoroutine(FadeSequence());
        }

        public void CancelFadeAndRestore()
        {
            StopFadeRoutine();
            ReleaseFadeMaterials();
            RestoreVisualsAlpha();
            if (_visualRoot != null) _visualRoot.SetActive(true);
        }

        // ══════════════════════════════════════════════════════════════════
        // Implementação
        // ══════════════════════════════════════════════════════════════════

        private void StopFadeRoutine()
        {
            if (_fadeCoroutine != null)
            {
                StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = null;
            }
        }

        private IEnumerator FadeSequence()
        {
            yield return new WaitForSeconds(_fadeDelay);
            if (this == null) yield break;

            Renderer[] renderers = null;
            if (_visualRoot != null)
                renderers = _visualRoot.GetComponentsInChildren<Renderer>(true);

            if (renderers != null && renderers.Length > 0)
            {
                _fadeMaterialInstances = new List<Material>();

                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    var mats = r.materials;
                    foreach (var mat in mats)
                    {
                        if (mat == null) continue;
                        _fadeMaterialInstances.Add(mat);
                        ConfigureMaterialForFade(mat);
                    }
                    r.materials = mats;
                }

                var propBlock = new MaterialPropertyBlock();
                float elapsed = 0f;

                while (elapsed < _fadeDuration)
                {
                    if (this == null) yield break;
                    elapsed += Time.deltaTime;
                    float alpha = Mathf.Lerp(1f, 0f, elapsed / _fadeDuration);

                    foreach (var r in renderers)
                    {
                        if (r == null) continue;
                        r.GetPropertyBlock(propBlock);
                        propBlock.SetColor("_Color",     new Color(1f, 1f, 1f, alpha));
                        propBlock.SetColor("_BaseColor", new Color(1f, 1f, 1f, alpha));
                        r.SetPropertyBlock(propBlock);
                    }
                    yield return null;
                }
            }

            if (this != null && _visualRoot != null)
                _visualRoot.SetActive(false);

            ReleaseFadeMaterials();
            _fadeCoroutine = null;
        }

        private static void ConfigureMaterialForFade(Material mat)
        {
            if (mat.HasProperty("_Mode"))
            {
                mat.SetFloat("_Mode", 2f);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.renderQueue = 3000;
            }
            if (mat.HasProperty("_Surface"))
                mat.SetFloat("_Surface", 1f);
        }

        private void ReleaseFadeMaterials()
        {
            if (_fadeMaterialInstances == null) return;
            foreach (var mat in _fadeMaterialInstances)
                if (mat != null) Destroy(mat);
            _fadeMaterialInstances = null;
        }

        private void RestoreVisualsAlpha()
        {
            if (_visualRoot == null) return;
            var renderers = _visualRoot.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
                if (r != null) r.SetPropertyBlock(null);
        }
    }
}
