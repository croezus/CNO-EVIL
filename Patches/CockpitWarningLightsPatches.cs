using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Config;
using CnoEvil.Core;
using CnoEvil.Components;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Adds RWR missile-guidance warnings (ARH seeker or SARH illuminator in guidance mode) to the cockpit missile
    /// warning lights. Vanilla flashes them for its known missiles and switches them off (and disables itself)
    /// otherwise; this flashes in the same phase when only the RWR has a missile guidance threat.
    /// </summary>
    [HarmonyPatch(typeof(CockpitWarningLights))]
    public static class CockpitWarningLightsPatches
    {
        [HarmonyPatch("Update")]
        [HarmonyPostfix]
        public static void Update_Postfix(CockpitWarningLights __instance, Aircraft ___aircraft, Renderer[] ___lightRenderers, List<Missile> ___knownMissiles)
        {
            if (!CnoEvilConfig.IsModActive) return;
            if (___knownMissiles != null && ___knownMissiles.Count > 0) return; // Vanilla is already flashing

            try
            {
                Aircraft aircraft = ___aircraft;
                if (aircraft == null || aircraft.disabled || ___lightRenderers == null) return;

                PhysRWRReceiver rwr = PhysRWRReceiver.Get(aircraft);
                if (rwr == null || rwr.GetHighestThreatState() != RwrThreatState.MissileGuidance) return;

                // Same phase as vanilla CockpitWarningLights.Update
                bool flash = Mathf.Sin(Time.timeSinceLevelLoad * 20.0f) > 0.0f;
                for (int i = 0; i < ___lightRenderers.Length; i++)
                {
                    if (___lightRenderers[i] != null) ___lightRenderers[i].enabled = flash;
                }
                // Keep Update running so vanilla switches the lights off once the threat is gone
                __instance.enabled = true;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CockpitWarningLightsPatches.Update_Postfix", ex);
            }
        }
    }

    /// <summary>
    /// MiG-29 plugins (MiG29Compat: MiG-29 Fulcrum and MiG-29SMT, the same InstrumentDriver and FlightState): their
    /// cockpit displays (SPO-15 / right MFD radar warning, caution lamps, SMT stores page target line, custom pages) read
    /// a FlightState that InstrumentDriver.Sample fills from vanilla data: the true position of each radar that paints
    /// the aircraft, CheckIsTarget of enemy fighters, the vanilla missile warning and the true name and distance of the
    /// target. While the mod is active, this postfix writes the sensor knowledge of CNO-EVIL into these fields after each
    /// Sample:
    /// - threats (both plugins): the measured bearings of the RWR (PhysRWRReceiver, tier bearing error and
    ///   classification), lock = track or missile guidance; heard missile seekers and warned missiles
    ///   (MissileWarningModel) as missiles;
    /// - target (SMT only, the Fulcrum FlightState has no target fields): the label of the ident tier
    ///   (TargetIdentification) and the range to the faction estimate, rounded by tier (the same rule as the PLAN page
    ///   target box).
    /// With the mod off (F10) the plugin values stay. The plugins drive the player's own aircraft only.
    /// </summary>
    [HarmonyPatch]
    public static class MiG29InstrumentDriver_Sample_Patch
    {
        // Index of the plugin threat letter table: F, SL, SS, U, U, N; -1 = missile
        private const int LetterFighter = 0;
        private const int LetterLongSam = 1;
        private const int LetterShortSam = 2;
        private const int LetterUnknown = 3;
        private const int LetterShip = 5;
        private const int LetterMissile = -1;

        /// <summary>
        /// Field access of one plugin: the FlightState type differs per plugin. Target setters are null on the Fulcrum.
        /// </summary>
        private sealed class Binding
        {
            public Func<object, Aircraft> GetAircraft;
            public Func<object, object> GetState;
            public Func<object, List<ValueTuple<float, int>>> GetRwr;
            public Action<object, float> SetRwrPower;
            public Action<object, bool> SetLockedOn;
            public Action<object, bool> SetMissileIncoming;
            public Action<object, string> SetTarget;
            public Action<object, float> SetTargetKm;

            public bool Valid
            {
                get
                {
                    return GetAircraft != null && GetState != null && GetRwr != null && SetRwrPower != null &&
                           SetLockedOn != null && SetMissileIncoming != null;
                }
            }

            public bool HasTarget
            {
                get { return SetTarget != null && SetTargetKm != null; }
            }
        }

        private static readonly Dictionary<Type, Binding> s_bindings = new Dictionary<Type, Binding>();

        // Label cache: the label string is rebuilt only when the target or its tier changes
        private static Unit s_labelUnit;
        private static IdentTier s_labelTier;
        private static string s_label;

        public static bool Prepare()
        {
            return TargetMethods().GetEnumerator().MoveNext();
        }

        public static IEnumerable<MethodBase> TargetMethods()
        {
            return MiG29Compat.Methods("InstrumentDriver", "Sample", new Type[] { typeof(float) });
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive || __instance == null) return;
                Binding b = Bind(__instance.GetType());
                if (b == null) return;
                Aircraft aircraft = b.GetAircraft(__instance);
                object state = b.GetState(__instance);
                if (aircraft == null || state == null) return;

                WriteThreats(b, aircraft, state);
                if (b.HasTarget) WriteTarget(b, aircraft, state);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("MiG29InstrumentDriver_Sample_Patch", ex);
            }
        }

        public static void ClearAll()
        {
            s_labelUnit = null;
            s_label = null;
        }

        /// <summary>
        /// The binding of this driver type, made once. Null when the threat fields are not found.
        /// </summary>
        private static Binding Bind(Type driverType)
        {
            Binding b;
            if (s_bindings.TryGetValue(driverType, out b)) return b;

            b = new Binding();
            b.GetAircraft = FastReflection.CreateFieldGetter<Aircraft>(driverType, "ac");
            FieldInfo stateField = AccessTools.Field(driverType, "st");
            Type stateType = (stateField != null) ? stateField.FieldType : null;
            b.GetState = FastReflection.CreateFieldGetter<object>(driverType, "st");
            b.GetRwr = FastReflection.CreateFieldGetter<List<ValueTuple<float, int>>>(stateType, "rwr");
            b.SetRwrPower = CreateSetter<float>(stateType, "rwrPower");
            b.SetLockedOn = CreateSetter<bool>(stateType, "lockedOn");
            b.SetMissileIncoming = CreateSetter<bool>(stateType, "missileIncoming");
            b.SetTarget = CreateSetter<string>(stateType, "target");
            b.SetTargetKm = CreateSetter<float>(stateType, "targetKm");

            if (!b.Valid)
            {
                if (CnoEvilPlugin.Log != null)
                    CnoEvilPlugin.Log.LogWarning("MiG-29 displays (" + driverType.FullName + "): FlightState fields not found, the plugin shows its own data.");
                b = null;
            }
            s_bindings[driverType] = b;
            return b;
        }

        /// <summary>
        /// Radar threats at their measured bearing, then warned missiles that the receiver does not hear. Bearings are
        /// relative to the nose, positive to the right, as the plugin draws them.
        /// </summary>
        private static void WriteThreats(Binding b, Aircraft aircraft, object state)
        {
            List<ValueTuple<float, int>> rwr = b.GetRwr(state);
            if (rwr == null) return;
            rwr.Clear();
            float power = 0f;
            bool locked = false;
            bool missile = false;

            PhysRWRReceiver receiver = PhysRWRReceiver.Get(aircraft);
            if (receiver != null)
            {
                bool classify = receiver.Capabilities.CanClassifyThreats;
                foreach (RwrThreatEntry t in receiver.ActiveThreats)
                {
                    if (t == null || t.EmitterUnit == null) continue;
                    bool seeker = t.IsActiveSeeker || t.EmitterUnit is Missile;
                    rwr.Add(new ValueTuple<float, int>(t.RelativeBearing, seeker ? LetterMissile : Letter(t.EmitterUnit, classify)));
                    power = Mathf.Max(power, Mathf.Clamp01(t.SignalStrength01));
                    if (seeker)
                    {
                        missile = true;
                        locked = true;
                    }
                    else if (t.State == RwrThreatState.Track || t.State == RwrThreatState.MissileGuidance)
                    {
                        locked = true;
                    }
                }
            }

            Dictionary<Missile, MissileWarningModel.Warning> warnings = MissileWarningModel.GetWarnings(aircraft);
            if (warnings != null)
            {
                Vector3 forward = Vector3.ProjectOnPlane(aircraft.transform.forward, Vector3.up);
                Vector3 own = aircraft.transform.position;
                foreach (MissileWarningModel.Warning w in warnings.Values)
                {
                    if (!w.Warned || !w.HasKinematics || w.Missile == null) continue;
                    missile = true;
                    power = 1f;
                    if (receiver != null && receiver.HearsMissile(w.Missile)) continue;
                    Vector3 to = Vector3.ProjectOnPlane(w.Position - own, Vector3.up);
                    rwr.Add(new ValueTuple<float, int>(Vector3.SignedAngle(forward, to, Vector3.up), LetterMissile));
                }
            }

            b.SetRwrPower(state, power);
            b.SetLockedOn(state, locked);
            b.SetMissileIncoming(state, missile);
        }

        /// <summary>
        /// Plugin letter for a radar emitter: the class of the ident Mid tier (the same as the RWR page symbol).
        /// The plugin has no letter for a search radar or a radar gun: they show as U.
        /// </summary>
        private static int Letter(Unit unit, bool classify)
        {
            if (!classify) return LetterUnknown;
            if (unit is Aircraft) return LetterFighter;
            if (unit is Ship) return LetterShip;
            string cls = TargetIdentification.GetLabel(unit, IdentTier.Mid);
            if (cls == "SAM") return LetterLongSam;
            if (cls == "AAA") return LetterShortSam;
            return LetterUnknown;
        }

        /// <summary>
        /// The first unit of the target list, as the plugin takes it. Without a faction position (bearing-only track)
        /// the range is 0.
        /// </summary>
        private static void WriteTarget(Binding b, Aircraft aircraft, object state)
        {
            WeaponManager wm = aircraft.weaponManager;
            List<Unit> list = (wm != null) ? wm.GetTargetList() : null;
            Unit target = (list != null && list.Count > 0) ? list[0] : null;
            if (target == null)
            {
                b.SetTarget(state, null);
                b.SetTargetKm(state, 0f);
                return;
            }

            FactionHQ hq = RWRTriangulationProcessor.GetPlayerFactionHQ();
            TriangulationTrack track = null;
            if (hq != null && ThreatClassification.IsTrackedThreat(target, hq)) RWRTriangulationProcessor.TryPeekTrack(hq, target, out track);
            IdentTier tier = IdentTier.High;
            Vector3 pos = target.transform.position;
            bool ranged = true;
            if (track != null && CnoEvilConfig.EnableHMDTargetIdentDegradation.Value)
            {
                tier = TargetIdentification.GetTier(track);
                GlobalPosition estimate;
                float error;
                if (RWRTriangulationProcessor.TryGetFactionEstimate(track, target, out estimate, out error)) pos = estimate.ToLocalPosition();
                else ranged = false;
            }

            if (target != s_labelUnit || tier != s_labelTier || s_label == null)
            {
                s_labelUnit = target;
                s_labelTier = tier;
                s_label = TargetIdentification.GetLabel(target, tier);
            }
            b.SetTarget(state, s_label);

            float km = 0f;
            if (ranged)
            {
                km = Vector3.Distance(pos, aircraft.transform.position) * 0.001f;
                // Low: nearest 10 km, Mid: nearest 2 km (TargetIdentification.FormatRange)
                if (tier != IdentTier.High)
                {
                    float step = (tier == IdentTier.Low) ? 10f : 2f;
                    km = Mathf.Max(step, Mathf.Round(km / step) * step);
                }
            }
            b.SetTargetKm(state, km);
        }

        private static Action<object, T> CreateSetter<T>(Type type, string fieldName)
        {
            try
            {
                FieldInfo fi = (type != null) ? AccessTools.Field(type, fieldName) : null;
                if (fi == null || fi.IsStatic || fi.FieldType != typeof(T)) return null;
                DynamicMethod dm = new DynamicMethod("Set_" + type.Name + "_" + fieldName, null,
                    new Type[] { typeof(object), typeof(T) }, type, true);
                ILGenerator il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, type);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Stfld, fi);
                il.Emit(OpCodes.Ret);
                return (Action<object, T>)dm.CreateDelegate(typeof(Action<object, T>));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Access to the struct lists of the M2K plugins (RwrThreat, RadarTrack): C# 5 cannot name the types or take a
    /// reference to a list element, so small methods are emitted once.
    /// </summary>
    internal static class M2KStructAccess
    {
        /// <summary>
        /// Reads one field of element index of a List of structs. An enum field reads as its underlying type.
        /// Null when the field or the list type does not match.
        /// </summary>
        public static Func<object, int, T> ElementField<T>(Type listType, string fieldName)
        {
            try
            {
                if (listType == null || !listType.IsGenericType) return null;
                Type element = listType.GetGenericArguments()[0];
                FieldInfo fi = AccessTools.Field(element, fieldName);
                if (fi == null || fi.IsStatic || !element.IsValueType) return null;
                Type ft = fi.FieldType.IsEnum ? Enum.GetUnderlyingType(fi.FieldType) : fi.FieldType;
                if (ft != typeof(T)) return null;
                MethodInfo getItem = listType.GetMethod("get_Item", new Type[] { typeof(int) });
                if (getItem == null) return null;
                DynamicMethod dm = new DynamicMethod("Get_" + element.Name + "_" + fieldName, typeof(T),
                    new Type[] { typeof(object), typeof(int) }, element.Module, true);
                ILGenerator il = dm.GetILGenerator();
                il.DeclareLocal(element);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, listType);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, getItem);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloca_S, (byte)0);
                il.Emit(OpCodes.Ldfld, fi);
                il.Emit(OpCodes.Ret);
                return (Func<object, int, T>)dm.CreateDelegate(typeof(Func<object, int, T>));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Adds a new RwrThreat (bearingDeg, code, level, hat) to a List of RwrThreat. Null when the type does not match.
        /// </summary>
        public static Action<object, float, string, int, bool> ThreatAdder(Type listType)
        {
            try
            {
                if (listType == null || !listType.IsGenericType) return null;
                Type element = listType.GetGenericArguments()[0];
                if (!element.IsValueType) return null;
                FieldInfo bearing = AccessTools.Field(element, "bearingDeg");
                FieldInfo code = AccessTools.Field(element, "code");
                FieldInfo level = AccessTools.Field(element, "level");
                FieldInfo hat = AccessTools.Field(element, "hat");
                MethodInfo add = listType.GetMethod("Add", new Type[] { element });
                if (bearing == null || bearing.FieldType != typeof(float) || code == null || code.FieldType != typeof(string) ||
                    level == null || level.FieldType != typeof(int) || hat == null || hat.FieldType != typeof(bool) || add == null)
                    return null;
                DynamicMethod dm = new DynamicMethod("Add_" + element.Name, null,
                    new Type[] { typeof(object), typeof(float), typeof(string), typeof(int), typeof(bool) }, element.Module, true);
                ILGenerator il = dm.GetILGenerator();
                il.DeclareLocal(element);
                il.Emit(OpCodes.Ldloca_S, (byte)0);
                il.Emit(OpCodes.Initobj, element);
                il.Emit(OpCodes.Ldloca_S, (byte)0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Stfld, bearing);
                il.Emit(OpCodes.Ldloca_S, (byte)0);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Stfld, code);
                il.Emit(OpCodes.Ldloca_S, (byte)0);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Stfld, level);
                il.Emit(OpCodes.Ldloca_S, (byte)0);
                il.Emit(OpCodes.Ldarg_S, (byte)4);
                il.Emit(OpCodes.Stfld, hat);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, listType);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Callvirt, add);
                il.Emit(OpCodes.Ret);
                return (Action<object, float, string, int, bool>)dm.CreateDelegate(typeof(Action<object, float, string, int, bool>));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static Type FieldType(Type owner, string fieldName)
        {
            FieldInfo fi = (owner != null) ? AccessTools.Field(owner, fieldName) : null;
            return (fi != null) ? fi.FieldType : null;
        }
    }

    /// <summary>
    /// M2K plugin (M2KMfd, ADR-64): the RWR page of the left screen reads RwrState, which FillRwr (10 Hz) fills from
    /// vanilla data: the true position of each radar that painted the aircraft (onRadarWarning), the radars that jam it
    /// (onJam) and the vanilla known missiles. While the mod is active, this postfix writes the sensor knowledge of
    /// CNO-EVIL into RwrState.threats and RwrState.missiles after each FillRwr, as for the MiG-29 plugins:
    /// - radar threats at the measured bearing of the RWR (PhysRWRReceiver: tier bearing error and classification);
    ///   level 2 (lock) for track or missile guidance, else 1; hat (the radar guides a missile) for missile guidance;
    /// - heard missile seekers and warned missiles that the receiver does not hear (MissileWarningModel) as missiles,
    ///   at most 4 (the plugin limit);
    /// - no jammer entries: the bearing of a jammer comes from the passive tracks of the faction, not from this page.
    /// The tones, counters and the jammer and Spirale states of the page stay from the plugin.
    /// </summary>
    [HarmonyPatch]
    public static class M2KMfd_FillRwr_Patch
    {
        private const int MaxMissiles = 4;
        private const int LevelSearch = 1;
        private const int LevelLock = 2;

        private static bool s_bound;
        private static Func<object, Aircraft> s_getAircraft;
        private static Func<object, object> s_getState;
        private static Func<object, IList> s_getThreats;
        private static Func<object, List<float>> s_getMissiles;
        private static Action<object, float, string, int, bool> s_addThreat;

        public static bool Prepare()
        {
            return M2KCompat.Method("M2KMfd", "FillRwr", Type.EmptyTypes).Count > 0;
        }

        public static IEnumerable<MethodBase> TargetMethods()
        {
            return M2KCompat.Method("M2KMfd", "FillRwr", Type.EmptyTypes);
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive || __instance == null) return;
                if (!Bind(__instance.GetType())) return;
                Aircraft aircraft = s_getAircraft(__instance);
                object state = s_getState(__instance);
                if (aircraft == null || state == null) return;
                IList threats = s_getThreats(state);
                List<float> missiles = s_getMissiles(state);
                if (threats == null || missiles == null) return;
                WriteThreats(aircraft, threats, missiles);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("M2KMfd_FillRwr_Patch", ex);
            }
        }

        private static bool Bind(Type mfdType)
        {
            if (s_bound) return s_addThreat != null;
            s_bound = true;
            Type stateType = M2KStructAccess.FieldType(mfdType, "rwr");
            s_getAircraft = FastReflection.CreateFieldGetter<Aircraft>(mfdType, "ac");
            s_getState = FastReflection.CreateFieldGetter<object>(mfdType, "rwr");
            s_getThreats = FastReflection.CreateFieldGetter<IList>(stateType, "threats");
            s_getMissiles = FastReflection.CreateFieldGetter<List<float>>(stateType, "missiles");
            s_addThreat = M2KStructAccess.ThreatAdder(M2KStructAccess.FieldType(stateType, "threats"));
            if (s_getAircraft == null || s_getState == null || s_getThreats == null || s_getMissiles == null || s_addThreat == null)
            {
                s_addThreat = null;
                if (CnoEvilPlugin.Log != null) CnoEvilPlugin.Log.LogWarning("M2K RWR page: RwrState fields not found, the plugin shows its own data.");
            }
            return s_addThreat != null;
        }

        /// <summary>
        /// Locks first, then the other radars (the plugin list is sorted by priority). Bearings are relative to the nose,
        /// positive to the right, as M2KMfd.Bearing gives them.
        /// </summary>
        private static void WriteThreats(Aircraft aircraft, IList threats, List<float> missiles)
        {
            threats.Clear();
            missiles.Clear();
            PhysRWRReceiver receiver = PhysRWRReceiver.Get(aircraft);
            if (receiver != null)
            {
                bool classify = receiver.Capabilities.CanClassifyThreats;
                for (int pass = 0; pass < 2; pass++)
                {
                    foreach (RwrThreatEntry t in receiver.ActiveThreats)
                    {
                        if (t == null || t.EmitterUnit == null) continue;
                        if (t.IsActiveSeeker || t.EmitterUnit is Missile)
                        {
                            if (pass == 0 && missiles.Count < MaxMissiles) missiles.Add(t.RelativeBearing);
                            continue;
                        }
                        bool locked = t.State == RwrThreatState.Track || t.State == RwrThreatState.MissileGuidance;
                        if (locked != (pass == 0)) continue;
                        s_addThreat(threats, t.RelativeBearing, Code(t.EmitterUnit, classify), locked ? LevelLock : LevelSearch,
                            t.State == RwrThreatState.MissileGuidance);
                    }
                }
            }

            Dictionary<Missile, MissileWarningModel.Warning> warnings = MissileWarningModel.GetWarnings(aircraft);
            if (warnings == null) return;
            Vector3 forward = Vector3.ProjectOnPlane(aircraft.transform.forward, Vector3.up);
            Vector3 own = aircraft.transform.position;
            foreach (MissileWarningModel.Warning w in warnings.Values)
            {
                if (missiles.Count >= MaxMissiles) break;
                if (!w.Warned || !w.HasKinematics || w.Missile == null) continue;
                if (receiver != null && receiver.HearsMissile(w.Missile)) continue;
                Vector3 to = Vector3.ProjectOnPlane(w.Position - own, Vector3.up);
                missiles.Add(Vector3.SignedAngle(forward, to, Vector3.up));
            }
        }

        /// <summary>
        /// Page code of a radar emitter: AI for an aircraft, the class of the ident Mid tier for a SAM or AAA radar, S for
        /// a search radar (as the plugin shows a radar without weapons), SHP for a ship; U when the receiver cannot
        /// classify or the class is not known. Constant strings: no allocation.
        /// </summary>
        private static string Code(Unit unit, bool classify)
        {
            if (!classify) return "U";
            if (unit is Aircraft) return "AI";
            if (unit is Ship) return "SHP";
            string cls = TargetIdentification.GetLabel(unit, IdentTier.Mid);
            if (cls == "SAM") return "SAM";
            if (cls == "AAA") return "AAA";
            if (cls == "RDR") return "S";
            return "U";
        }
    }

    /// <summary>
    /// M2K plugin (M2KVtb, ADR-64): the VTB radar page on the centre scope shows the hits of the own radar and, with
    /// its datalink option, the tracks of the vanilla faction database at their true position. While the mod is
    /// active, this postfix removes after each Fill the datalink tracks of hostile threats that the faction has not
    /// located (UnitMapIcon_UpdateIcon_Patch.IsLocatedThreat: the same gate as the map icon). A located threat stays: the map shows
    /// it at the same place. The hits of the own radar stay: they passed the radar physics of CNO-EVIL.
    /// </summary>
    [HarmonyPatch]
    public static class M2KVtb_Fill_Patch
    {
        // RadarTrack.side: TrackSide.Friendly
        private const int SideFriendly = 1;

        private static bool s_bound;
        private static bool s_valid;
        private static Func<object, object> s_getState;
        private static Func<object, IList> s_getTracks;
        private static Func<object, List<Unit>> s_getUnits;
        private static Func<object, int, bool> s_getDatalink;
        private static Func<object, int, int> s_getSide;
        private static Func<object, int, int> s_getId;

        public static bool Prepare()
        {
            return M2KCompat.Method("M2KVtb", "Fill", new Type[] { typeof(float) }).Count > 0;
        }

        public static IEnumerable<MethodBase> TargetMethods()
        {
            return M2KCompat.Method("M2KVtb", "Fill", new Type[] { typeof(float) });
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive || __instance == null) return;
                if (!Bind(__instance.GetType())) return;
                object state = s_getState(__instance);
                IList tracks = (state != null) ? s_getTracks(state) : null;
                List<Unit> units = s_getUnits(__instance);
                if (tracks == null || units == null || tracks.Count == 0) return;
                FactionHQ hq = RWRTriangulationProcessor.GetPlayerFactionHQ();
                if (hq == null) return;

                for (int i = tracks.Count - 1; i >= 0; i--)
                {
                    if (!s_getDatalink(tracks, i) || s_getSide(tracks, i) == SideFriendly) continue;
                    int id = s_getId(tracks, i);
                    Unit unit = (id >= 0 && id < units.Count) ? units[id] : null;
                    if (unit == null || !ThreatClassification.IsTrackedThreat(unit, hq)) continue;
                    if (UnitMapIcon_UpdateIcon_Patch.IsLocatedThreat(hq, unit)) continue;
                    tracks.RemoveAt(i);
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("M2KVtb_Fill_Patch", ex);
            }
        }

        private static bool Bind(Type vtbType)
        {
            if (s_bound) return s_valid;
            s_bound = true;
            Type stateType = M2KStructAccess.FieldType(vtbType, "st");
            Type listType = M2KStructAccess.FieldType(stateType, "tracks");
            s_getState = FastReflection.CreateFieldGetter<object>(vtbType, "st");
            s_getTracks = FastReflection.CreateFieldGetter<IList>(stateType, "tracks");
            s_getUnits = FastReflection.CreateFieldGetter<List<Unit>>(vtbType, "units");
            s_getDatalink = M2KStructAccess.ElementField<bool>(listType, "datalink");
            s_getSide = M2KStructAccess.ElementField<int>(listType, "side");
            s_getId = M2KStructAccess.ElementField<int>(listType, "id");
            s_valid = s_getState != null && s_getTracks != null && s_getUnits != null && s_getDatalink != null && s_getSide != null && s_getId != null;
            if (!s_valid && CnoEvilPlugin.Log != null) CnoEvilPlugin.Log.LogWarning("M2K VTB page: RadarTrack fields not found, the plugin shows its own datalink tracks.");
            return s_valid;
        }
    }

    /// <summary>
    /// M2K plugin (M2KEWRwr, ADR-64): the small RWR scope that M2KEW draws on the HUD places its symbols at the true
    /// position of each emitter, without a unit reference, so its data cannot be replaced. While the mod is active the
    /// scope stays hidden (the M2K RWR page shows the same threats with CNO-EVIL data). M2KMfd hides it by default too.
    /// With the mod off (F10) the next Tick of the plugin shows it again.
    /// </summary>
    [HarmonyPatch]
    public static class M2KEWRwr_Tick_Patch
    {
        private static Func<GameObject> s_getRoot;

        public static bool Prepare()
        {
            List<MethodBase> list = M2KCompat.Method("M2KEWRwr", "Tick", Type.EmptyTypes);
            if (list.Count == 0) return false;
            s_getRoot = StaticGetter(M2KCompat.FindType("M2KEWRwr"), "root");
            return s_getRoot != null;
        }

        public static IEnumerable<MethodBase> TargetMethods()
        {
            return M2KCompat.Method("M2KEWRwr", "Tick", Type.EmptyTypes);
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            try
            {
                if (!CnoEvilConfig.IsModActive || s_getRoot == null) return;
                GameObject root = s_getRoot();
                if (root != null && root.activeSelf) root.SetActive(false);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("M2KEWRwr_Tick_Patch", ex);
            }
        }

        private static Func<GameObject> StaticGetter(Type type, string fieldName)
        {
            try
            {
                FieldInfo fi = (type != null) ? AccessTools.Field(type, fieldName) : null;
                if (fi == null || !fi.IsStatic || fi.FieldType != typeof(GameObject)) return null;
                DynamicMethod dm = new DynamicMethod("Get_" + type.Name + "_" + fieldName, typeof(GameObject), Type.EmptyTypes, type, true);
                ILGenerator il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldsfld, fi);
                il.Emit(OpCodes.Ret);
                return (Func<GameObject>)dm.CreateDelegate(typeof(Func<GameObject>));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// M2K plugin (M2KMfd, ADR-64): with its option "Camera on the right screen" the plugin moves the vanilla target
    /// and landing camera screens to the right screen each frame (CameraOnRight), and puts them back when the camera
    /// is off. The EOTS page of the portal moves the same screens. While the portal is on for this TacScreen, the
    /// plugin gets rightShown = false, so it keeps the screens at their vanilla place. Before the portal turns on,
    /// Release puts back the screens that the plugin moved (TacScreenPortalMFD.BeforeEngage): else the EOTS page would
    /// take them from the plugin, and the plugin would move them back on its next frame.
    /// </summary>
    [HarmonyPatch]
    public static class M2KMfd_CameraOnRight_Patch
    {
        private static readonly object[] s_off = { false };
        private static Func<object, TacScreen> s_getTac;
        private static MethodInfo s_method;
        // The plugin component of the last TacScreen it drove (the player aircraft)
        private static object s_lastMfd;
        private static TacScreen s_lastTac;

        public static bool Prepare()
        {
            List<MethodBase> list = TargetMethods() as List<MethodBase>;
            if (list == null || list.Count == 0) return false;
            s_method = list[0] as MethodInfo;
            s_getTac = FastReflection.CreateFieldGetter<TacScreen>(s_method.DeclaringType, "tac");
            if (s_getTac == null) return false;
            TacScreenPortalMFD.BeforeEngage = Release;
            return true;
        }

        public static IEnumerable<MethodBase> TargetMethods()
        {
            return M2KCompat.Method("M2KMfd", "CameraOnRight", new Type[] { typeof(bool) });
        }

        [HarmonyPrefix]
        public static void Prefix(object __instance, ref bool rightShown)
        {
            try
            {
                if (__instance == null || s_getTac == null) return;
                TacScreen tac = s_getTac(__instance);
                s_lastMfd = __instance;
                s_lastTac = tac;
                if (CnoEvilConfig.IsModActive && TacScreenPortalMFD.IsEngagedOn(tac)) rightShown = false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("M2KMfd_CameraOnRight_Patch", ex);
            }
        }

        /// <summary>
        /// The plugin puts back the camera screens it moved on this TacScreen (CameraOnRight(false) now).
        /// </summary>
        public static void Release(TacScreen tacScreen)
        {
            if ((object)tacScreen == null || tacScreen != s_lastTac || s_method == null) return;
            UnityEngine.Object mfd = s_lastMfd as UnityEngine.Object;
            if (mfd == null) return;
            s_method.Invoke(s_lastMfd, s_off);
        }

        public static void ClearAll()
        {
            s_lastMfd = null;
            s_lastTac = null;
        }
    }
}
