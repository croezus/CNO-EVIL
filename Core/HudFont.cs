using System;
using UnityEngine;
using TMPro;

namespace CnoEvil.Core
{
    /// <summary>
    /// TextMeshPro font of the native displays, for the text the mod adds to them.
    /// </summary>
    public static class HudFont
    {
        private static readonly Func<CombatHUD, TextMeshProUGUI> f_targetText = FastReflection.CreateFieldGetter<CombatHUD, TextMeshProUGUI>("targetText");
        private static readonly Func<CombatHUD, TextMeshProUGUI> f_cmName = FastReflection.CreateFieldGetter<CombatHUD, TextMeshProUGUI>("countermeasureName");
        private static TMP_FontAsset s_hudFont;

        /// <summary>
        /// Font of the combat HUD (target text, else countermeasure name), kept once found.
        /// Without a combat HUD: the first loaded TMP font.
        /// </summary>
        public static TMP_FontAsset Get()
        {
            if (s_hudFont != null) return s_hudFont;
            try
            {
                if (CombatHUD.i != null)
                {
                    s_hudFont = FontOf(f_targetText != null ? f_targetText(CombatHUD.i) : null)
                        ?? FontOf(f_cmName != null ? f_cmName(CombatHUD.i) : null);
                }
            }
            catch { }
            return s_hudFont != null ? s_hudFont : Of(null);
        }

        /// <summary>
        /// Font of the given text, else the first loaded TMP font.
        /// </summary>
        public static TMP_FontAsset Of(TextMeshProUGUI text)
        {
            TMP_FontAsset font = FontOf(text);
            if (font != null) return font;
            TMP_FontAsset[] fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            return fonts != null && fonts.Length > 0 ? fonts[0] : null;
        }

        private static TMP_FontAsset FontOf(TextMeshProUGUI text)
        {
            return text != null && text.font != null ? text.font : null;
        }
    }
}
