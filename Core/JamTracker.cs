using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Jammers that jam one aircraft, from its Unit.onJam event. Unit.Jam is a ClientRpc: onJam runs on the host and
    /// on every client. One source per jammer with the last jam amount and time; the owner prunes them.
    /// </summary>
    public class JamTracker
    {
        public class Source
        {
            public Unit Jammer;
            public float Amount;
            public float LastTime;
            public Vector3 Direction;   // Free for the owner (EM scan ring: horizontal unit vector with receiver jitter)
        }

        public readonly List<Source> Sources = new List<Source>();
        private readonly Action<Unit.JamEventArgs> _onJam;
        private readonly string _errorContext;
        private readonly bool _modActiveOnly;
        private Aircraft _target;

        /// <param name="errorContext">PatchErrorLog context of the event handler.</param>
        /// <param name="modActiveOnly">Ignore jam events while the mod is off.</param>
        public JamTracker(string errorContext, bool modActiveOnly)
        {
            _onJam = OnJam;
            _errorContext = errorContext;
            _modActiveOnly = modActiveOnly;
        }

        /// <summary>Listens to the jam events of this aircraft (no change when already attached to it).</summary>
        public void Attach(Aircraft target)
        {
            if (_target == target) return;
            Detach();
            if (target == null) return;
            target.onJam += _onJam;
            _target = target;
        }

        public void Detach()
        {
            if (_target != null) _target.onJam -= _onJam;
            _target = null;
        }

        /// <summary>Removes the sources of dead jammers and the sources not refreshed for more than maxAge seconds.</summary>
        public void Prune(float now, float maxAge)
        {
            for (int i = Sources.Count - 1; i >= 0; i--)
            {
                Source src = Sources[i];
                if (src.Jammer == null || src.Jammer.disabled || now - src.LastTime > maxAge) Sources.RemoveAt(i);
            }
        }

        private void OnJam(Unit.JamEventArgs args)
        {
            try
            {
                if (_modActiveOnly && !CnoEvilConfig.IsModActive) return;
                Unit jammer = args.jammingUnit;
                if (jammer == null || jammer == _target || args.jamAmount <= 0.0f) return;

                Source src = null;
                for (int i = 0; i < Sources.Count; i++)
                {
                    if (Sources[i].Jammer == jammer) { src = Sources[i]; break; }
                }
                if (src == null)
                {
                    src = new Source();
                    src.Jammer = jammer;
                    Sources.Add(src);
                }
                src.Amount = args.jamAmount;
                src.LastTime = Time.time;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once(_errorContext, ex);
            }
        }
    }
}
