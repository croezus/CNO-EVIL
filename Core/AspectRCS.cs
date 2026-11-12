using System;
using UnityEngine;

namespace CnoEvil.Core
{
    public struct AspectRCSProfile
    {
        public float NoseRCS;    // θ = 0°, φ = 0° (m²)
        public float BeamRCS;    // θ = ±90°, φ = 0° (m²)
        public float TailRCS;    // θ = ±180°, φ = 0° (m²)
        public float DorsalRCS;  // φ = +90° (top) (m²)
        public float VentralRCS; // φ = -90° (belly) (m²)
        public bool IsHelicopter;
        public float RotorHERM_RCS; // Micro-Doppler spike RCS

        // Game radarSize: VLO airframes 0.001-0.01 (FS-20, KR-67, AB-4, EW-25, SFB-81); lowest conventional
        // fixed-wing is FS-12 / UFO at 0.08. The class boundary (RWR tier, radar template) sits in that gap.
        public const float StealthRcsThreshold = 0.02f;
        // The aspect profile blends from stealth to conventional across this window (log scale, 0.5 at the threshold)
        public const float StealthBlendLowRcs = 0.01f;
        public const float StealthBlendHighRcs = 0.04f;

        // Stealth multipliers (nose, beam, tail, dorsal, ventral)
        private const float StealthNose = 0.2f, StealthBeam = 3.5f, StealthTail = 1.5f, StealthDorsal = 4.0f, StealthVentral = 3.5f;

        /// <summary>Discrete stealth class of an airframe: radarSize at or below StealthRcsThreshold.</summary>
        public static bool IsStealthAirframe(float radarSize)
        {
            return radarSize > 0.0f && radarSize <= StealthRcsThreshold;
        }

        /// <summary>Weight of the stealth profile: 1 at or below StealthBlendLowRcs, 0 at or above StealthBlendHighRcs, log-linear between.</summary>
        public static float StealthWeight(float radarSize)
        {
            if (radarSize <= StealthBlendLowRcs) return 1.0f;
            if (radarSize >= StealthBlendHighRcs) return 0.0f;
            return Mathf.InverseLerp(Mathf.Log10(StealthBlendHighRcs), Mathf.Log10(StealthBlendLowRcs), Mathf.Log10(radarSize));
        }

        public static AspectRCSProfile CreateFighter(float baseRcs)
        {
            // Conventional 4th gen fighter / attack aircraft multipliers, blended toward stealth (FS-20, KR-67, EW-25)
            return CreateBlended(baseRcs, 0.7f, 2.8f, 1.6f, 3.2f, 2.5f);
        }

        private static AspectRCSProfile CreateBlended(float baseRcs, float nose, float beam, float tail, float dorsal, float ventral)
        {
            float w = StealthWeight(baseRcs);

            AspectRCSProfile p;
            p.IsHelicopter = false;
            p.RotorHERM_RCS = 0.0f;
            p.NoseRCS = Mathf.Max(0.0002f, baseRcs * Mathf.Lerp(nose, StealthNose, w));
            p.BeamRCS = baseRcs * Mathf.Lerp(beam, StealthBeam, w);
            p.TailRCS = baseRcs * Mathf.Lerp(tail, StealthTail, w);
            p.DorsalRCS = baseRcs * Mathf.Lerp(dorsal, StealthDorsal, w);
            p.VentralRCS = baseRcs * Mathf.Lerp(ventral, StealthVentral, w);
            return p;
        }

        public static AspectRCSProfile CreateHelicopter(float baseRcs)
        {
            AspectRCSProfile p;
            p.IsHelicopter = true;
            p.NoseRCS = baseRcs * 0.8f;
            p.BeamRCS = baseRcs * 3.0f;
            p.TailRCS = baseRcs * 1.4f;
            p.DorsalRCS = baseRcs * 3.5f;
            p.VentralRCS = baseRcs * 2.2f;
            p.RotorHERM_RCS = baseRcs * 0.5f; // Blade reflection flash, on the scale of the game's helicopter radarSize (0.02-0.03)
            return p;
        }

        public static AspectRCSProfile CreateMissile(float baseRcs)
        {
            AspectRCSProfile p;
            p.IsHelicopter = false;
            p.RotorHERM_RCS = 0.0f;
            p.NoseRCS = Mathf.Max(0.01f, baseRcs * 0.3f);
            p.BeamRCS = Mathf.Max(0.08f, baseRcs * 1.8f);
            p.TailRCS = Mathf.Max(0.04f, baseRcs * 1.0f);
            p.DorsalRCS = Mathf.Max(0.08f, baseRcs * 1.5f);
            p.VentralRCS = Mathf.Max(0.08f, baseRcs * 1.5f);
            return p;
        }

        /// <summary>
        /// Ships, ground vehicles and structures, seen mostly along near-horizontal paths:
        /// the broadside dominates, the bow/stern return scales with the beam-to-length ratio,
        /// and the top-down return with the beam-to-height ratio (radar looking down on the deck).
        /// </summary>
        public static AspectRCSProfile CreateSurface(float broadsideRcs, float length, float width, float height)
        {
            AspectRCSProfile p;
            p.IsHelicopter = false;
            p.RotorHERM_RCS = 0.0f;

            float beamToLength = (length > 0.1f && width > 0.1f) ? width / length : 0.5f;
            float beamToHeight = (width > 0.1f && height > 0.1f) ? width / height : 1.0f;

            p.BeamRCS = broadsideRcs;
            p.NoseRCS = broadsideRcs * Mathf.Max(0.2f, beamToLength);
            p.TailRCS = p.NoseRCS * 1.2f;
            p.DorsalRCS = broadsideRcs * Mathf.Clamp(beamToHeight, 0.3f, 2.0f);
            p.VentralRCS = p.NoseRCS; // Hull below the waterline / ground is practically never illuminated
            return p;
        }

        public static AspectRCSProfile CreateLargeAircraft(float baseRcs)
        {
            // Large conventional airframe (VL-49); stealth flying wings (SFB-81, AB-4) blend to the stealth multipliers
            return CreateBlended(baseRcs, 0.75f, 3.5f, 2.0f, 4.5f, 4.0f);
        }
    }

    public static class AspectRCS
    {
        private static Func<Weapon, Hardpoint> _weaponHardpointRef;
        private static Func<Weapon, WeaponMount> _weaponMountRef;
        private static Func<BayDoor, float> _bayDoorOpenAmountRef;
        private static bool _fieldRefsInitialized;

        // Cavity resonance at fully open doors: airframe bay, smaller enclosed pod
        private const float InternalBayCavityRCS = 0.25f;
        private const float EnclosedPodCavityRCS = 0.15f;
        private const float DoorOpenThreshold = 0.05f;
        // Pylon fallback when a weapon has no WeaponMount at all
        private const float FallbackPylonRCS = 0.015f;

        private static void EnsureFieldRefs()
        {
            if (_fieldRefsInitialized) return;
            _fieldRefsInitialized = true;
            _weaponHardpointRef = FastReflection.CreateFieldGetter<Weapon, Hardpoint>("hardpoint");
            _weaponMountRef = FastReflection.CreateFieldGetter<Weapon, WeaponMount>("mount");
            _bayDoorOpenAmountRef = FastReflection.CreateFieldGetter<BayDoor, float>("openAmount");
        }
        /// <summary>
        /// Calculates the dynamic 3D aspect-dependent RCS (m²) for a target seen from a radar position.
        /// Evaluates spherical azimuth and elevation relative to the target's body orientation.
        /// </summary>
        public static float CalculateAspectRCS(
            Transform targetTransform,
            Vector3 radarPosition,
            ref AspectRCSProfile profile)
        {
            if (targetTransform == null) return profile.NoseRCS;

            Vector3 toRadar = radarPosition - targetTransform.position;
            float sqrMag = toRadar.sqrMagnitude;
            if (sqrMag < 0.001f) return profile.NoseRCS;

            // Normalized line of sight vector in world space
            Vector3 losWorld = toRadar / Mathf.Sqrt(sqrMag);

            // Convert to target local frame: +Z forward, +X right, +Y up
            Vector3 losLocal = targetTransform.InverseTransformDirection(losWorld);

            float rxzSq = losLocal.x * losLocal.x + losLocal.z * losLocal.z;
            float rxz = Mathf.Sqrt(rxzSq);
            float cosAz = (rxz > 0.0001f) ? (losLocal.z / rxz) : 1.0f;
            float sinAz = (rxz > 0.0001f) ? (losLocal.x / rxz) : 0.0f;
            float sinEl = Mathf.Clamp(losLocal.y, -1.0f, 1.0f);

            // Horizontal aspect components
            float noseWeight = Mathf.Max(0.0f, cosAz);
            noseWeight = noseWeight * noseWeight * noseWeight * noseWeight; // cos^4 (narrow forward lobe)

            float tailWeight = Mathf.Max(0.0f, -cosAz);
            tailWeight = tailWeight * tailWeight * tailWeight; // cos^3

            float beamWeight = sinAz * sinAz; // sin^2 (broad beam specular lobe)

            // Vertical elevation components
            float dorsalWeight = Mathf.Max(0.0f, sinEl);
            float ventralWeight = Mathf.Max(0.0f, -sinEl);
            float horizontalPlaneWeight = rxzSq; // cosEl^2 = 1 - sinEl^2 = rxzSq

            // Combine horizontal aspect with normalized weights
            float totalHorizWeight = noseWeight + beamWeight + tailWeight;
            float rcsHoriz = (totalHorizWeight > 0.0001f)
                ? ((profile.NoseRCS * noseWeight) + (profile.BeamRCS * beamWeight) + (profile.TailRCS * tailWeight)) / totalHorizWeight
                : profile.NoseRCS;

            // Blend with vertical aspect
            float totalRcs = (rcsHoriz * horizontalPlaneWeight) +
                             (profile.DorsalRCS * dorsalWeight * dorsalWeight) +
                             (profile.VentralRCS * ventralWeight * ventralWeight);

            return Mathf.Max(0.0002f, totalRcs);
        }

        /// <summary>
        /// Calculates the additive RCS (m²) of pylons and stores from the game's own carriage data.
        /// External mounts follow the vanilla model (Hardpoint.SpawnMount / MountedMissile.AttachToHardpoint):
        /// WeaponMount.emptyRCS once per hardpoint plus WeaponMount.GetRCSPerRound() for every loaded round.
        /// Internal bays and enclosed pods (bay doors with a pod shell emptyRCS) are shielded while closed; opening the doors adds a cavity
        /// resonance (once per hardpoint) plus the exposed munitions' own radarSize.
        /// </summary>
        public static float CalculateStoresRCSPenalty(Unit unit)
        {
            if (unit == null || unit.weaponStations == null) return 0.0f;

            EnsureFieldRefs();

            float penalty = 0.0f;
            int stationCount = unit.weaponStations.Count;

            for (int i = 0; i < stationCount; i++)
            {
                WeaponStation st = unit.weaponStations[i];
                if (st == null || st.Weapons == null) continue;

                int weaponCount = st.Weapons.Count;
                for (int w = 0; w < weaponCount; w++)
                {
                    Weapon weapon = st.Weapons[w];
                    if (weapon == null) continue;

                    Hardpoint hp = GetHardpoint(weapon);
                    WeaponMount mount = (_weaponMountRef != null) ? _weaponMountRef(weapon) : null;
                    WeaponInfo info = weapon.info != null ? weapon.info : st.WeaponInfo;

                    // Per-hardpoint terms (pylon, pod shell, cavity) are counted on the first weapon of each hardpoint
                    bool firstOnHardpoint = IsFirstWeaponOnHardpoint(st.Weapons, w, hp);

                    float doorOpen = GetMaxDoorOpen(hp);
                    bool hasBayDoors = hp != null && hp.bayDoors != null && hp.bayDoors.Length > 0;

                    // Bay doors are authoritative; otherwise a vanilla missileBay mount counts as internal
                    // only when the game gives it no carriage RCS (vanilla internal mounts all have RCS = 0)
                    bool isInternal = hasBayDoors || (mount != null && mount.RCS <= 0.0f && mount.missileBay);

                    if (isInternal)
                    {
                        // Enclosed pod: a bay with its own shell return (emptyRCS > 0) that hangs outside the airframe
                        float shellRcs = (mount != null) ? Mathf.Max(0.0f, mount.emptyRCS) : 0.0f;
                        bool isEnclosedPod = shellRcs > 0.0f;

                        // Closed doors: stores are fully shielded; a pod shell keeps its game RCS
                        if (isEnclosedPod && firstOnHardpoint)
                        {
                            penalty += shellRcs;
                        }

                        if (doorOpen > DoorOpenThreshold)
                        {
                            // Cavity resonance / corner reflector while the doors are open
                            if (firstOnHardpoint)
                            {
                                penalty += (isEnclosedPod ? EnclosedPodCavityRCS : InternalBayCavityRCS) * doorOpen;
                            }
                            // Exposed munitions reflect with their own in-flight radar size
                            if (MunitionRCSDatabase.IsMunition(info))
                            {
                                penalty += MunitionRCSDatabase.GetMunitionRadarSize(info) * Mathf.Max(0, weapon.ammo) * doorOpen;
                            }
                        }
                    }
                    else if (mount != null)
                    {
                        // External mount: vanilla carriage data (or the database / override for gaps)
                        float emptyRcs, perRoundRcs;
                        MunitionRCSDatabase.GetMountRCS(mount, out emptyRcs, out perRoundRcs);
                        if (firstOnHardpoint)
                        {
                            penalty += emptyRcs;
                        }
                        if (weapon.ammo > 0 && mount.ammo > 0)
                        {
                            penalty += perRoundRcs * weapon.ammo;
                        }
                    }
                    else if (MunitionRCSDatabase.IsMunition(info))
                    {
                        // Munition without a WeaponMount: pylon plus the munition database carriage value.
                        // Other built-in weapons (guns) add nothing, as in vanilla.
                        if (firstOnHardpoint)
                        {
                            penalty += FallbackPylonRCS;
                        }
                        if (weapon.ammo > 0)
                        {
                            penalty += MunitionRCSDatabase.GetFallbackCarriageRCS(info, weapon.ammo);
                        }
                    }
                }
            }

            penalty += AddonStoresRCS(unit);
            return penalty;
        }

        /// <summary>
        /// Hardpoint add-ons of other mods have no WeaponStation (Aryx ECS-16 / ECS-32 flare pods, ETS-15 targeting pod):
        /// their mount emptyRCS once per hardpoint, as vanilla Hardpoint.SpawnMount.
        /// </summary>
        private static float AddonStoresRCS(Unit unit)
        {
            System.Collections.Generic.List<Weapon> addons = AryxWeaponsCompat.GetAddons(unit);
            float penalty = 0.0f;
            for (int i = 0; i < addons.Count; i++)
            {
                Hardpoint hp = AryxWeaponsCompat.GetHardpoint(addons[i]);
                bool first = true;
                for (int k = 0; k < i; k++)
                {
                    if (AryxWeaponsCompat.GetHardpoint(addons[k]) == hp) { first = false; break; }
                }
                if (!first) continue;
                WeaponMount mount = AryxWeaponsCompat.GetMount(addons[i]);
                if (mount == null) continue;
                float emptyRcs, perRoundRcs;
                MunitionRCSDatabase.GetMountRCS(mount, out emptyRcs, out perRoundRcs);
                penalty += emptyRcs;
            }
            return penalty;
        }

        private static bool IsFirstWeaponOnHardpoint(System.Collections.Generic.List<Weapon> weapons, int index, Hardpoint hp)
        {
            for (int k = 0; k < index; k++)
            {
                Weapon other = weapons[k];
                if (other != null && GetHardpoint(other) == hp) return false;
            }
            return true;
        }

        private static Hardpoint GetHardpoint(Weapon weapon)
        {
            return (_weaponHardpointRef != null) ? _weaponHardpointRef(weapon) : null;
        }

        private static float GetMaxDoorOpen(Hardpoint hp)
        {
            if (hp == null || hp.bayDoors == null || _bayDoorOpenAmountRef == null) return 0.0f;

            float maxOpen = 0.0f;
            for (int d = 0; d < hp.bayDoors.Length; d++)
            {
                BayDoor door = hp.bayDoors[d];
                if (door == null) continue;
                float openAmt = _bayDoorOpenAmountRef(door);
                if (openAmt > maxOpen) maxOpen = openAmt;
            }
            return maxOpen;
        }

        /// <summary>
        /// Returns effective micro-Doppler flash RCS for helicopter rotor blades (HERM).
        /// </summary>
        public static float GetHelicopterRotorFlash(ref AspectRCSProfile profile)
        {
            if (!profile.IsHelicopter) return 0.0f;
            return profile.RotorHERM_RCS;
        }
    }
}
