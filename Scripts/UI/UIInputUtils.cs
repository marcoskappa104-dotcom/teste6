using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

namespace RPG.UI
{
    public static class UIInputUtils
    {
        /// <summary>
        /// True se o EventSystem está com foco em um InputField (TMP ou legacy).
        /// Útil para bloquear hotkeys quando o jogador está digitando.
        /// </summary>
        public static bool IsTypingInInputField()
        {
            var es = EventSystem.current;
            if (es == null) return false;

            var selected = es.currentSelectedGameObject;
            if (selected == null) return false;

            return selected.GetComponent<TMP_InputField>() != null
                || selected.GetComponent<InputField>()    != null;
        }

        /// <summary>
        /// True se o pointer (mouse/touch) está atualmente sobre um elemento de UI.
        /// Útil para evitar que clique de gameplay (mover, atacar) seja processado
        /// quando o player está interagindo com a UI.
        /// </summary>
        public static bool IsPointerOverUI()
        {
            var es = EventSystem.current;
            return es != null && es.IsPointerOverGameObject();
        }
    }
}
