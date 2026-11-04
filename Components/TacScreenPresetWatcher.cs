using System;
using UnityEngine;
using CnoEvil.Core;
using CnoEvil.Config;
using CnoEvil.Patches;

namespace CnoEvil.Components
{
    /// <summary>
    /// Watches the TacScreen preset settings and applies a change in flight (TacScreenPatches.ApplyPreset), so a new
    /// preset does not need a respawn. The settings are polled: one compare per frame.
    /// </summary>
    public class TacScreenPresetWatcher : MonoBehaviour
    {
        private TacScreen _tacScreen;
        private Aircraft _aircraft;
        private Cockpit _cockpit;
        private bool _initialized;
        private bool _modActive;
        private CnoEvilConfig.TacScreenPreset _preset;

        public void Initialize(TacScreen tacScreen, Aircraft aircraft, Cockpit cockpit)
        {
            _tacScreen = tacScreen;
            _aircraft = aircraft;
            _cockpit = cockpit;
            Remember();
            _initialized = true;
        }

        private void Remember()
        {
            _modActive = CnoEvilConfig.IsModActive;
            _preset = CnoEvilConfig.TacScreenDisplayPreset.Value;
        }

        private void Update()
        {
            if (!_initialized) return;
            try
            {
                CnoEvilConfig.TacScreenPreset preset = CnoEvilConfig.TacScreenDisplayPreset.Value;
                if (CnoEvilConfig.IsModActive == _modActive && preset == _preset) return;
                Remember();
                if (_aircraft == null || _tacScreen == null) return;
                TacScreenPatches.ApplyPreset(_tacScreen, _aircraft, _cockpit);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("TacScreenPresetWatcher.Update", ex);
            }
        }
    }
}
