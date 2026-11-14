namespace CnoEvil.Core
{
    /// <summary>
    /// Single-target track (STT, "hard lock") of an emitting radar on one target, which the target RWR shows as Track.
    /// A designated target alone is a track-while-scan track: the radar continues its scan and the target RWR hears
    /// search pulses only (ADR-36).
    /// </summary>
    public static class RadarLockModel
    {
        /// <summary>
        /// True when the radar of radarUnit holds target in STT:
        /// an aircraft that has the target designated (WeaponManager target list, the HMD box) with a SARH station
        /// selected, which needs STT before launch; any other unit (SAM, ship, radar site) whose weapon or turret has
        /// the target, the vanilla Unit.CheckIsTarget test of the vanilla RWR warning (OnRadarWarning.isTarget).
        /// An aircraft with an ARH, IR or other station selected keeps the target in TWS.
        /// </summary>
        public static bool IsSingleTargetTrack(Unit radarUnit, Unit target)
        {
            if (radarUnit == null || target == null) return false;

            Aircraft aircraft = radarUnit as Aircraft;
            if (aircraft != null)
            {
                WeaponManager weapons = aircraft.weaponManager;
                if (weapons == null || !weapons.CheckIsTarget(target)) return false;
                WeaponStation station = weapons.currentWeaponStation;
                if (station == null || station.Ammo <= 0) return false;
                return MunitionCatalog.Get(station.WeaponInfo).Kind == SeekerKind.SemiActiveRadar;
            }

            return radarUnit.CheckIsTarget(target);
        }
    }
}
