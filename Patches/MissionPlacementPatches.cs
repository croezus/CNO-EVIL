using System;
using System.Reflection;
using HarmonyLib;
using NuclearOption.SavedMission;
using UnityEngine;
using CnoEvil.Config;
using CnoEvil.Core;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Host: makes the air defense plan of the mission (ADR-52) before the start objective spawns the first unit.
    /// </summary>
    [HarmonyPatch]
    public static class SpawnUnitOutcome_Complete_Patch
    {
        // SpawnUnitOutcome is an internal class of the game
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(AccessTools.TypeByName("NuclearOption.SavedMission.Outcomes.SpawnUnitOutcome"), "Complete");
        }

        [HarmonyPrefix]
        public static void Prefix(Objective completedObjective)
        {
            if (!CnoEvilConfig.IsModActive) return;
            try
            {
                DynamicPlacement.PlanMissionStart(completedObjective);
            }
            catch (Exception ex)
            {
                DynamicPlacement.ClearAll();
                PatchErrorLog.Once("SpawnUnitOutcome_Complete_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Host: one spawn call reads the position of the plan. The SavedUnit gets its own values back after the call, so the
    /// Mission keeps the data of the author (the mission editor saves that object).
    /// </summary>
    [HarmonyPatch(typeof(Spawner), "TrySpawnSavedUnitInternal")]
    public static class Spawner_TrySpawnSavedUnitInternal_Patch
    {
        public struct AuthorPose
        {
            public bool Replaced;
            public GlobalPosition Position;
            public Quaternion Rotation;
        }

        [HarmonyPrefix]
        public static void Prefix(SavedUnit savedUnit, out AuthorPose __state)
        {
            __state = new AuthorPose();
            if (!CnoEvilConfig.IsModActive) return;
            try
            {
                GlobalPosition position;
                Quaternion rotation;
                if (!DynamicPlacement.TryGetPose(savedUnit, out position, out rotation)) return;

                __state.Replaced = true;
                __state.Position = savedUnit.globalPosition;
                __state.Rotation = savedUnit.rotation;
                savedUnit.globalPosition = position;
                savedUnit.rotation = rotation;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Spawner_TrySpawnSavedUnitInternal_Patch", ex);
            }
        }

        [HarmonyFinalizer]
        public static void Finalizer(SavedUnit savedUnit, AuthorPose __state)
        {
            if (!__state.Replaced) return;
            savedUnit.globalPosition = __state.Position;
            savedUnit.rotation = __state.Rotation;
        }
    }
}
