using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Config;
using CnoEvil.Core;

namespace CnoEvil.Patches
{
    /// <summary>
    /// The missile warning of every aircraft comes from sensor knowledge and a collision-course test
    /// (MissileWarningModel, ADR-28) instead of the true target ID. While the mod is off: vanilla.
    /// </summary>
    [HarmonyPatch(typeof(MissileWarning), "Update")]
    public static class MissileWarning_Update_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(MissileWarning __instance)
        {
            if (!CnoEvilConfig.IsModActive) return true;
            try
            {
                MissileWarningModel.Evaluate(__instance);
                return false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("MissileWarning_Update_Patch", ex);
                return true;
            }
        }
    }

    /// <summary>
    /// AI evasion keeps a missile alert while the missile is warned for its aircraft (MissileWarningModel), not
    /// while the true target ID is its own. The transpiler replaces Missile.targetID in ChooseEvadeMode.
    /// </summary>
    [HarmonyPatch(typeof(AIPilotCombatModes), "ChooseEvadeMode")]
    public static class AIPilotCombatModes_ChooseEvadeMode_Patch
    {
        private static readonly Func<AIPilotCombatModes, Aircraft> f_getAircraft =
            FastReflection.CreateFieldGetter<AIPilotCombatModes, Aircraft>("aircraft");

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo targetId = AccessTools.PropertyGetter(typeof(Missile), "targetID");
            MethodInfo warnedId = AccessTools.Method(typeof(AIPilotCombatModes_ChooseEvadeMode_Patch), "WarnedTargetId");
            var codes = new List<CodeInstruction>(instructions);
            int replaced = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (targetId == null || warnedId == null || !codes[i].Calls(targetId)) continue;
                // Stack: missile -> missile, this -> WarnedTargetId(missile, this)
                var replacement = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, warnedId);
                replacement.labels.AddRange(codes[i].labels);
                replacement.blocks.AddRange(codes[i].blocks);
                codes[i] = replacement;
                codes.Insert(i, new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_0));
                i++;
                replaced++;
            }
            if (replaced == 0 && CnoEvilPlugin.Log != null)
            {
                CnoEvilPlugin.Log.LogWarning("AIPilotCombatModes_ChooseEvadeMode_Patch: Missile.targetID read not found, vanilla target kept");
            }
            return codes;
        }

        /// <summary>
        /// The id of the evading aircraft while its missile warning holds the missile, otherwise none.
        /// While the mod is off: the vanilla target ID.
        /// </summary>
        public static PersistentID WarnedTargetId(Missile missile, AIPilotCombatModes modes)
        {
            if ((object)missile == null) return default(PersistentID);
            if (!CnoEvilConfig.IsModActive) return missile.targetID;
            try
            {
                Aircraft aircraft = (f_getAircraft != null && modes != null) ? f_getAircraft(modes) : null;
                if (aircraft != null && MissileWarningModel.IsWarned(aircraft, missile)) return aircraft.persistentID;
                return default(PersistentID);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("AIPilotCombatModes_ChooseEvadeMode_Patch", ex);
                return missile.targetID;
            }
        }
    }

    /// <summary>
    /// The vanilla missile alarm of the player plays at DatalinkAlarmPitch while every missile of that alarm is
    /// warned only through the datalink; an own RWR or MAWS contact brings the normal pitch back.
    /// </summary>
    [HarmonyPatch]
    public static class ThreatList_MissileAlarm_ManageAlarmSound_Patch
    {
        private static readonly Type s_alarmType = AccessTools.Inner(typeof(ThreatList), "MissileAlarm");
        private static readonly Func<object, List<Missile>> f_missiles =
            FastReflection.CreateFieldGetter<List<Missile>>(s_alarmType, "missiles");
        private static readonly Func<object, AudioSource> f_alarmSource =
            FastReflection.CreateFieldGetter<AudioSource>(s_alarmType, "alarmSource");

        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(s_alarmType, "ManageAlarmSound");
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance)
        {
            try
            {
                AudioSource source = f_alarmSource != null ? f_alarmSource(__instance) : null;
                if (source == null) return;
                float pitch = 1.0f;
                if (CnoEvilConfig.IsModActive && f_missiles != null)
                {
                    Aircraft own = CombatHUD.i != null ? CombatHUD.i.aircraft : null;
                    List<Missile> missiles = f_missiles(__instance);
                    if (own != null && missiles != null && missiles.Count > 0)
                    {
                        bool datalinkOnly = true;
                        for (int i = 0; i < missiles.Count && datalinkOnly; i++)
                        {
                            datalinkOnly = MissileWarningModel.IsDatalinkOnly(own, missiles[i]);
                        }
                        if (datalinkOnly) pitch = Mathf.Clamp(CnoEvilConfig.DatalinkAlarmPitch.Value, 0.1f, 3.0f);
                    }
                }
                if (source.pitch != pitch) source.pitch = pitch;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("ThreatList_MissileAlarm_ManageAlarmSound_Patch", ex);
            }
        }
    }
}
