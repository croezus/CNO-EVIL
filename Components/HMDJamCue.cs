using System;
using System.Collections.Generic;
using UnityEngine;
using NuclearOption.UIStyleSystem;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Components
{
    /// <summary>
    /// HMD cue for the jammers that jam the own aircraft (Unit.onJam): dense vertical noise dashes that flicker in an azimuth
    /// sector around the horizon. Each dash fades in and out over its short life, sparkles at random, and a brightness wave
    /// runs across the sector. The sector width comes from the RWR tier (HMDJamSectorTier1..3_deg). The jammer
    /// is always inside the sector but not at its centre: the centre drifts up to a quarter of the width from the true azimuth.
    /// No elevation and no range are shown. The vanilla jamming marker is hidden (RadarWarning_JammingIcon_Position_Patch).
    /// </summary>
    public class HMDJamCue : MonoBehaviour
    {
        private const int MaxSlots = 4;
        private const int DashesPerSource = 72;
        private const float SourceMaxAgeSeconds = 1.5f;
        private const float BandHalfHeightDeg = 12.0f;
        private const float DashMinLengthDeg = 0.4f;
        private const float DashMaxLengthDeg = 1.6f;
        private const float DashMinLifeSeconds = 0.12f;
        private const float DashMaxLifeSeconds = 0.35f;
        private const float OffsetMinHoldSeconds = 1.5f;
        private const float OffsetMaxHoldSeconds = 2.5f;
        private const float ProjectDistanceM = 10000.0f;
        private const float DashThicknessPx = 2.0f;
        private const float DashMinWidthPx = 1.4f;
        private const float DashMaxWidthPx = 3.0f;
        private const float SparkleChance = 0.12f;      // Per dash per frame
        private const float WaveSpeedHz = 1.6f;         // Brightness wave across the sector
        private const float WaveHalfWidth = 0.25f;      // Fraction of the sector width

        /// <summary>Noise state of one jammer. Dash azimuths are fractions of the sector width, so a width change applies at once.</summary>
        private class Sector
        {
            public float Offset;        // Fraction of the width, -0.25..0.25
            public float OffsetTarget;
            public float NextTargetTime;
            public float Seed;          // 0..1, desyncs the wave and breathing of different jammers
            public readonly float[] DashU = new float[DashesPerSource];       // -0.5..0.5 of the width
            public readonly float[] DashEl = new float[DashesPerSource];      // deg from the horizon
            public readonly float[] DashLen = new float[DashesPerSource];     // deg
            public readonly float[] DashBorn = new float[DashesPerSource];
            public readonly float[] DashLife = new float[DashesPerSource];
            public readonly float[] DashSeed = new float[DashesPerSource];    // Perlin row of the flicker
        }

        private class Slot
        {
            public GameObject Root;
            public HudDashGraphic Dashes;
            public readonly Vector2[] Points = new Vector2[DashesPerSource * 2];
            public readonly float[] Alphas = new float[DashesPerSource];
            public readonly float[] Widths = new float[DashesPerSource];
        }

        private readonly JamTracker _jams = new JamTracker("HMDJamCue.OnJam", true);
        private readonly Dictionary<Unit, Sector> _sectors = new Dictionary<Unit, Sector>();
        private readonly List<Unit> _staleSectors = new List<Unit>();
        private readonly Slot[] _slots = new Slot[MaxSlots];
        private Aircraft _aircraft;
        private PhysRWRReceiver _rwr;
        private GameObject _layer;
        private RectTransform _layerRect;

        public void Initialize(FlightHud flightHud, Aircraft aircraft)
        {
            _aircraft = aircraft;
            _rwr = (aircraft != null) ? PhysRWRReceiver.Get(aircraft) : null;
            _jams.Detach();
            _jams.Sources.Clear();
            _sectors.Clear();
            if (aircraft != null) _jams.Attach(aircraft);
        }

        private void OnDestroy()
        {
            _jams.Detach();
            if (_layer != null) Destroy(_layer);
        }

        private void EnsureLayer()
        {
            if (_layer != null) return;
            Transform parent = (CombatHUD.i != null && CombatHUD.i.iconLayer != null) ? CombatHUD.i.iconLayer : transform;

            _layer = new GameObject("CnoEvil_HMDJamCue");
            _layer.transform.SetParent(parent, false);
            _layerRect = _layer.AddComponent<RectTransform>();
            _layerRect.anchorMin = Vector2.zero;
            _layerRect.anchorMax = Vector2.one;
            _layerRect.pivot = new Vector2(0.5f, 0.5f);
            _layerRect.anchoredPosition = Vector2.zero;
            _layerRect.sizeDelta = Vector2.zero;
            _layerRect.localScale = Vector3.one;

            for (int i = 0; i < MaxSlots; i++)
            {
                Slot slot = new Slot();
                slot.Root = new GameObject("JamCue_" + i);
                slot.Root.transform.SetParent(_layer.transform, false);
                RectTransform rootRt = slot.Root.AddComponent<RectTransform>();
                rootRt.anchorMin = Vector2.zero;
                rootRt.anchorMax = Vector2.one;
                rootRt.sizeDelta = Vector2.zero;

                slot.Dashes = slot.Root.AddComponent<HudDashGraphic>();
                slot.Dashes.raycastTarget = false;
                slot.Dashes.Thickness = DashThicknessPx;
                slot.Dashes.Points = slot.Points;
                slot.Dashes.Alphas = slot.Alphas;
                slot.Dashes.Widths = slot.Widths;

                slot.Root.SetActive(false);
                _slots[i] = slot;
            }
        }

        private void HideAll()
        {
            if (_layer == null) return;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (_slots[i] != null && _slots[i].Root.activeSelf) _slots[i].Root.SetActive(false);
            }
        }

        private static Color CueColor()
        {
            try
            {
                ColorTheme theme = ThemeManager.Active.ColorTheme;
                Color c = Color.Lerp(theme.HudUnitFlash, theme.HudUnitHostile, 0.5f);
                c.a = 1.0f;     // The per-dash flicker sets the alpha
                return c;
            }
            catch
            {
                return new Color(1.0f, 0.45f, 0.3f, 1.0f);
            }
        }

        /// <summary>Full sector width (deg) for the RWR tier of the own aircraft.</summary>
        private float SectorWidthDeg()
        {
            RwrTier tier = (_rwr != null) ? _rwr.Capabilities.Tier : RwrTier.Tier2_Standard;
            float width;
            switch (tier)
            {
                case RwrTier.Tier1_Basic: width = CnoEvilConfig.HMDJamSectorTier1_deg.Value; break;
                case RwrTier.Tier3_Advanced: width = CnoEvilConfig.HMDJamSectorTier3_deg.Value; break;
                default: width = CnoEvilConfig.HMDJamSectorTier2_deg.Value; break;
            }
            return Mathf.Clamp(width, 1.0f, 180.0f);
        }

        private void LateUpdate()
        {
            try
            {
                if (!CnoEvilConfig.IsModActive || !CnoEvilConfig.EnableHMDJamCue.Value || _aircraft == null || _aircraft.disabled)
                {
                    HideAll();
                    return;
                }

                _jams.Prune(Time.time, SourceMaxAgeSeconds);
                PruneSectors();
                if (_jams.Sources.Count == 0)
                {
                    HideAll();
                    return;
                }

                Camera cam = (CameraStateManager.i != null) ? CameraStateManager.i.mainCamera : null;
                if (cam == null)
                {
                    HideAll();
                    return;
                }

                EnsureLayer();
                if (_rwr == null) _rwr = PhysRWRReceiver.Get(_aircraft);
                Color color = CueColor();
                float widthDeg = SectorWidthDeg();

                int used = 0;
                for (int i = 0; i < _jams.Sources.Count && used < MaxSlots; i++)
                {
                    Slot slot = _slots[used];
                    if (DrawSource(_jams.Sources[i].Jammer, slot, cam, color, widthDeg))
                    {
                        if (!slot.Root.activeSelf) slot.Root.SetActive(true);
                        used++;
                    }
                }
                for (int i = used; i < MaxSlots; i++)
                {
                    if (_slots[i].Root.activeSelf) _slots[i].Root.SetActive(false);
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("HMDJamCue.LateUpdate", ex);
            }
        }

        // Drops the noise state of jammers that left the source list
        private void PruneSectors()
        {
            if (_sectors.Count == 0) return;
            _staleSectors.Clear();
            foreach (KeyValuePair<Unit, Sector> kvp in _sectors)
            {
                bool present = false;
                for (int i = 0; i < _jams.Sources.Count; i++)
                {
                    if (_jams.Sources[i].Jammer == kvp.Key) { present = true; break; }
                }
                if (!present) _staleSectors.Add(kvp.Key);
            }
            for (int i = 0; i < _staleSectors.Count; i++) _sectors.Remove(_staleSectors[i]);
        }

        private Sector GetSector(Unit jammer, float now)
        {
            Sector sector;
            if (_sectors.TryGetValue(jammer, out sector)) return sector;

            sector = new Sector();
            sector.Offset = UnityEngine.Random.Range(-0.25f, 0.25f);
            sector.OffsetTarget = sector.Offset;
            sector.NextTargetTime = now + UnityEngine.Random.Range(OffsetMinHoldSeconds, OffsetMaxHoldSeconds);
            sector.Seed = UnityEngine.Random.value;
            for (int i = 0; i < DashesPerSource; i++)
            {
                SpawnDash(sector, i, now);
                sector.DashBorn[i] -= UnityEngine.Random.Range(0.0f, sector.DashLife[i]);   // Staggered, not all at age 0
            }
            _sectors[jammer] = sector;
            return sector;
        }

        private static void SpawnDash(Sector sector, int i, float now)
        {
            sector.DashU[i] = UnityEngine.Random.Range(-0.5f, 0.5f);
            sector.DashEl[i] = UnityEngine.Random.Range(-BandHalfHeightDeg, BandHalfHeightDeg);
            sector.DashLen[i] = UnityEngine.Random.Range(DashMinLengthDeg, DashMaxLengthDeg);
            sector.DashBorn[i] = now;
            sector.DashLife[i] = UnityEngine.Random.Range(DashMinLifeSeconds, DashMaxLifeSeconds);
            sector.DashSeed[i] = UnityEngine.Random.Range(0.0f, 100.0f);
        }

        private bool DrawSource(Unit jammer, Slot slot, Camera cam, Color color, float widthDeg)
        {
            if (_rwr == null || jammer == null) return false;

            // Blind cones: the receiver does not hear a jammer there
            Vector3 dirToJammer;
            if (!_rwr.CanHearJammer(jammer, out dirToJammer)) return false;
            if (dirToJammer.x * dirToJammer.x + dirToJammer.z * dirToJammer.z < 0.0001f) return false;

            float now = Time.time;
            float dt = Time.deltaTime;
            Sector sector = GetSector(jammer, now);

            // The centre drifts within a quarter of the width from the true azimuth; the jammer stays inside the sector
            if (now >= sector.NextTargetTime)
            {
                sector.OffsetTarget = UnityEngine.Random.Range(-0.25f, 0.25f);
                sector.NextTargetTime = now + UnityEngine.Random.Range(OffsetMinHoldSeconds, OffsetMaxHoldSeconds);
            }
            sector.Offset = Mathf.MoveTowards(sector.Offset, sector.OffsetTarget, 0.25f * dt);

            float trueAzDeg = Mathf.Atan2(dirToJammer.x, dirToJammer.z) * Mathf.Rad2Deg;
            float centreAzDeg = trueAzDeg + sector.Offset * widthDeg;

            // Brightness wave that runs across the sector (0 at the left edge, 1 at the right) and a slow breathing of the whole band
            float wavePhase = Mathf.Repeat(sector.Seed + now * WaveSpeedHz, 1.0f);
            float breathing = 0.85f + 0.15f * Mathf.Sin(now * 7.0f + sector.Seed * 6.2832f);

            Vector3 camPos = cam.transform.position;
            Vector3 camFwd = cam.transform.forward;
            int count = 0;
            for (int i = 0; i < DashesPerSource; i++)
            {
                if (now >= sector.DashBorn[i] + sector.DashLife[i]) SpawnDash(sector, i, now);

                // Fade in and out over the life, then Perlin flicker or a random dim sparkle
                float age01 = Mathf.Clamp01((now - sector.DashBorn[i]) / sector.DashLife[i]);
                float envelope = Mathf.Sin(age01 * Mathf.PI);
                float flicker = (UnityEngine.Random.value < SparkleChance)
                    ? UnityEngine.Random.Range(0.15f, 0.35f)
                    : Mathf.Lerp(0.55f, 1.0f, Mathf.PerlinNoise(sector.DashSeed[i], now * 9.0f));

                float waveDist = Mathf.Abs(sector.DashU[i] + 0.5f - wavePhase);
                waveDist = Mathf.Min(waveDist, 1.0f - waveDist);
                float wave = Mathf.Clamp01(1.0f - waveDist / WaveHalfWidth);

                float alpha = envelope * flicker * Mathf.Lerp(0.45f, 1.0f, wave) * breathing;
                if (alpha < 0.01f) continue;

                float azDeg = centreAzDeg + sector.DashU[i] * widthDeg;
                Vector3 lowDir = Direction(azDeg, sector.DashEl[i]);
                Vector3 highDir = Direction(azDeg, sector.DashEl[i] + sector.DashLen[i]);
                if (Vector3.Dot(lowDir, camFwd) <= 0.05f || Vector3.Dot(highDir, camFwd) <= 0.05f) continue;

                slot.Points[count * 2] = ScreenToLayer(cam.WorldToScreenPoint(camPos + lowDir * ProjectDistanceM));
                slot.Points[count * 2 + 1] = ScreenToLayer(cam.WorldToScreenPoint(camPos + highDir * ProjectDistanceM));
                slot.Alphas[count] = alpha;
                slot.Widths[count] = Mathf.Lerp(DashMinWidthPx, DashMaxWidthPx, wave);
                count++;
            }

            slot.Dashes.Count = count;
            slot.Dashes.color = color;
            slot.Dashes.SetVerticesDirty();
            return count > 0;
        }

        // World direction from azimuth (deg from +Z toward +X) and elevation (deg above the horizon)
        private static Vector3 Direction(float azDeg, float elDeg)
        {
            float az = azDeg * Mathf.Deg2Rad;
            float el = elDeg * Mathf.Deg2Rad;
            float cosEl = Mathf.Cos(el);
            return new Vector3(Mathf.Sin(az) * cosEl, Mathf.Sin(el), Mathf.Cos(az) * cosEl);
        }

        // Screen-space overlay HUD: screen pixels -> local position in the cue layer (centre origin)
        private Vector2 ScreenToLayer(Vector3 screen)
        {
            Vector3 local = _layerRect.InverseTransformPoint(new Vector3(screen.x, screen.y, 0.0f));
            return new Vector2(local.x, local.y);
        }
    }
}
