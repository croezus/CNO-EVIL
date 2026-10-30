using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Config;
using CnoEvil.Core;

namespace CnoEvil.Patches
{
    /// <summary>
    /// AI evasion of a radar missile (ADR-31). Vanilla beams the nearest warned missile on the side of the nose, blends
    /// only 30 % to the beam above 7 s to impact, and fires the selected countermeasure below 8 s while it flies within
    /// 20 deg of the beam. The postfix changes the evade vector:
    /// 1. Drag (AIMissileTactics.UpdateDrag): far missile, horizontal tail to the missile, full commitment.
    /// 2. Turn away (AIMissileTactics.RecentOwnCloud): after an ejection, the beam side away from the own cloud as the
    ///    missile sees it, full commitment.
    /// Then it computes the evade point and the countermeasure trigger again with the vanilla formulas. The pull-up
    /// below 2 s stays vanilla.
    /// </summary>
    [HarmonyPatch(typeof(AIPilotCombatModes), "EvadeModeRadar")]
    public static class AIPilotCombatModes_EvadeModeRadar_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ref GlobalPosition evadeDestination, Aircraft ___aircraft, List<Missile> ___missileAlerts,
            float ___missileImpactTime, ref Vector3 ___missileEvadeVector, Vector3 ___randomErrorVector)
        {
            if (!CnoEvilConfig.IsModActive) return;
            try
            {
                if (___aircraft == null || ___aircraft.rb == null || ___missileAlerts == null || ___missileAlerts.Count == 0) return;
                Missile missile = ___missileAlerts[0];
                if (missile == null) return;

                Vector3 pos = ___aircraft.transform.position;
                Vector3 fromMissile = pos - missile.transform.position;
                Vector3 evade = ___missileEvadeVector;
                bool changed = false;

                if (AIMissileTactics.UpdateDrag(___aircraft, missile, ___missileImpactTime))
                {
                    Vector3 away = new Vector3(fromMissile.x, 0.0f, fromMissile.z);
                    if (away.sqrMagnitude > 1.0f)
                    {
                        evade = away.normalized;
                        changed = true;
                    }
                }
                else
                {
                    ChaffCloud cloud = AIMissileTactics.RecentOwnCloud(___aircraft);
                    if (cloud != null && cloud.Chaff != null && fromMissile.sqrMagnitude > 1.0f)
                    {
                        // Offset of the aircraft from its cloud across the missile line of sight
                        Vector3 los = fromMissile.normalized;
                        Vector3 offset = pos - cloud.Chaff.transform.position;
                        offset -= los * Vector3.Dot(offset, los);
                        if (Vector3.Dot(evade, offset) < 0.0f) evade = -evade;
                        AIMissileTactics.LogTurnAway(___aircraft, cloud);
                        changed = true;
                    }
                }
                if (!changed) return;

                ___missileEvadeVector = evade;
                Vector3 evadePoint = pos + evade * 1000.0f + ___randomErrorVector * 100.0f / Mathf.Max(___aircraft.skill, 0.01f);
                evadeDestination = evadePoint.ToGlobalPosition();

                // Vanilla tested the trigger with its own vector
                bool fire = ___missileImpactTime < 8.0f && Vector3.Angle(evade, ___aircraft.rb.velocity) < 20.0f;
                if (fire != ___aircraft.countermeasureTrigger && ___aircraft.countermeasureManager != null)
                {
                    ___aircraft.Countermeasures(fire, ___aircraft.countermeasureManager.activeIndex);
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("AIPilotCombatModes_EvadeModeRadar_Patch", ex);
            }
        }
    }

}
