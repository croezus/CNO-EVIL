using UnityEngine;

namespace CnoEvil.Core
{
    /// <summary>
    /// One action key. A normal key fires when it goes down. A modifier key (Alt, Ctrl, Shift) fires when it is released
    /// after a short tap with no other key, so Alt+Tab, Alt+F4 and other shortcuts do nothing. No key works while a text
    /// field has the input or the game window does not have the focus. Call Pressed every frame, so the tap state stays
    /// correct.
    /// </summary>
    public sealed class TapKey
    {
        // Longest hold (s) of a modifier key that still counts as a tap
        private const float MaxTap_s = 0.5f;

        private bool _armed;
        private float _start;

        public bool Pressed(KeyCode key)
        {
            if (key == KeyCode.None) return false;
            if (!Application.isFocused || NuclearOption.MissionEditorScripts.InputFieldChecker.InsideInputField)
            {
                _armed = false;
                return false;
            }
            if (!IsModifier(key)) return Input.GetKeyDown(key);

            float now = Time.unscaledTime;
            if (Input.GetKeyDown(key))
            {
                _armed = true;
                _start = now;
                return false;
            }
            if (!_armed) return false;
            if (Input.GetKeyUp(key))
            {
                _armed = false;
                return now - _start <= MaxTap_s;
            }
            if (Input.anyKeyDown || !Input.GetKey(key)) _armed = false;
            return false;
        }

        public void Reset()
        {
            _armed = false;
        }

        private static bool IsModifier(KeyCode key)
        {
            return key == KeyCode.LeftAlt || key == KeyCode.RightAlt || key == KeyCode.LeftControl || key == KeyCode.RightControl ||
                   key == KeyCode.LeftShift || key == KeyCode.RightShift || key == KeyCode.AltGr ||
                   key == KeyCode.LeftCommand || key == KeyCode.RightCommand;
        }
    }
}
