using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Transmit budget of the self-protection jammer (RadarJammer) on the shared aircraft power supply (the kJ of the
    /// vanilla charge indicator). Vanilla draws only powerUsage x supply curve, and the engines refill the supply quickly,
    /// so the jammer never runs dry. With the mod the jammer drains maxCharge / EcmJamDuration_s kJ/s: a full supply is
    /// empty after that many seconds of jamming. The jamming power is proportional to the charge (full supply = full
    /// power, empty = none). The engine refill is limited to maxCharge / EcmRechargeTime_s kJ/s plus the power drawn by
    /// the other users (radar, ...), so they keep their vanilla supply and only the jammer deficit refills slowly.
    /// An aircraft with a jamming pod or a laser keeps the vanilla refill. Fire only records the jamming; the supply
    /// tick applies the drain, where the charge field is writable.
    /// </summary>
    public static class EcmChargeBudget
    {
        // Below this charge (3 s of jamming with the default budget) the AI prefers chaff
        private const float AiMinCharge = 0.25f;
        // Seconds between searches for a jamming pod or laser on the aircraft (the loadout can change on rearm)
        private const float HeavyUserScanInterval = 5.0f;

        private class SupplyState
        {
            // Since the last supply tick: sum of the vanilla jammer draw rates (kW), to separate the other users,
            // energy the vanilla jammer drew (kJ) and seconds of jamming
            public float JammerRate;
            public float JammerEnergy;
            public float JammerTime;
            public float ChargeBefore;
            public float DrawnBefore;
            public bool HasHeavyUser;
            public float NextScan;
        }

        private static readonly Dictionary<PowerSupply, SupplyState> s_states = new Dictionary<PowerSupply, SupplyState>();

        public static bool Enabled
        {
            get { return CnoEvilConfig.EcmJamDuration_s.Value > 0.0f; }
        }

        /// <summary>
        /// Called after each vanilla Fire with the power the jammer drew (kW). Returns the output fraction (charge of the
        /// supply, 0-1).
        /// </summary>
        public static float OnFire(PowerSupply supply, float vanillaDrawn)
        {
            SupplyState state;
            if (!s_states.TryGetValue(supply, out state))
            {
                state = new SupplyState();
                s_states[supply] = state;
            }
            float dt = Time.deltaTime;
            state.JammerRate += vanillaDrawn;
            state.JammerEnergy += vanillaDrawn * dt;
            state.JammerTime += dt;
            return Mathf.Clamp01(supply.GetCharge());
        }

        /// <summary>
        /// Before the vanilla supply tick: saves the charge and the power drawn (vanilla resets it).
        /// </summary>
        public static void BeforeSupplyTick(PowerSupply supply, float charge, float powerDrawn)
        {
            SupplyState state;
            if (!s_states.TryGetValue(supply, out state)) return;
            state.ChargeBefore = charge;
            state.DrawnBefore = powerDrawn;
        }

        /// <summary>
        /// After the vanilla supply tick: limits the engine refill to maxCharge / EcmRechargeTime_s plus the draw of the
        /// other users, then sets the jammer drain since the last tick to maxCharge / EcmJamDuration_s.
        /// </summary>
        public static void AfterSupplyTick(PowerSupply supply, Aircraft aircraft, ref float charge, float maxCharge)
        {
            SupplyState state;
            if (!s_states.TryGetValue(supply, out state) || maxCharge <= 0.0f) return;
            float jammerRate = state.JammerRate;
            float jammerEnergy = state.JammerEnergy;
            float jammerTime = state.JammerTime;
            state.JammerRate = 0.0f;
            state.JammerEnergy = 0.0f;
            state.JammerTime = 0.0f;

            float now = Time.timeSinceLevelLoad;
            if (now >= state.NextScan)
            {
                state.NextScan = now + HeavyUserScanInterval;
                state.HasHeavyUser = aircraft != null &&
                                     (aircraft.GetComponentInChildren<JammingPod>(true) != null ||
                                      aircraft.GetComponentInChildren<Laser>(true) != null);
            }

            float gain = charge - state.ChargeBefore;
            if (!state.HasHeavyUser && gain > 0.0f)
            {
                float others = Mathf.Max(0.0f, state.DrawnBefore - jammerRate);
                float limit = (maxCharge / Mathf.Max(0.1f, CnoEvilConfig.EcmRechargeTime_s.Value) + others) * Time.deltaTime;
                if (gain > limit) charge = state.ChargeBefore + limit;
            }

            if (jammerTime > 0.0f)
            {
                float drain = maxCharge / Mathf.Max(0.1f, CnoEvilConfig.EcmJamDuration_s.Value) * jammerTime;
                charge = Mathf.Clamp(charge + jammerEnergy - drain, 0.0f, maxCharge);
            }
        }

        /// <summary>
        /// True while the supply of the jammer has too little charge for the AI to rely on it.
        /// </summary>
        public static bool IsLow(RadarJammer jammer)
        {
            PowerSupply supply = (jammer != null && jammer.aircraft != null) ? jammer.aircraft.GetPowerSupply() : null;
            return supply != null && supply.GetCharge() < AiMinCharge;
        }

        public static void Remove(PowerSupply supply)
        {
            if (supply != null) s_states.Remove(supply);
        }

        public static void ClearAll()
        {
            s_states.Clear();
        }
    }
}
