using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using CnoEvil.Core;

namespace CnoEvil.Components
{
    /// <summary>
    /// EW overlays of the TSD page (PortalPageTsd), drawn above the range rings and below the contact symbols:
    /// - jamming pod beams of the own faction (StandoffJamming.Beams): amber dashes clipped to the beam wedge, like
    ///   the reference jammer line (a Mask that hides its own wedge graphic over a tiled dash strip). The wedge has the
    ///   physics cone angle and ends past the target like the map cone. A diamond marks the target of the own pod.
    /// - lock lines: a red dashed line from each located hostile whose radar tracks the ownship (receiver state above
    ///   Search), brighter while it guides a missile (reference enemyRadarLockTemplate).
    /// - radar coverage: the scan limits (vanilla radarCone, a half-angle around the scanner axis) and the range arc
    ///   of the own radar while it transmits; a radar without a limit (radome) shows a full range circle.
    /// Positions update with the page tick (10 Hz). Fixed pools, nothing is allocated per tick.
    /// </summary>
    public class PortalTsdEffects
    {
        private const int MaxBeams = 8;
        private const int MaxLockLines = 16;
        private const float DashPeriod = 6f;
        private const float BeamStripWidth = 7f;
        private const float MinBeamWidth = 6f;

        private static readonly Color BeamColor = new Color(1f, 0.69f, 0f, 0.51f);
        private static readonly Color JamTargetColor = new Color(1f, 0.75f, 0f, 1f);
        private static readonly Color LockColor = new Color(1f, 0.2f, 0f, 0.4f);
        private static readonly Color GuidanceColor = new Color(1f, 0f, 0f, 0.75f);
        private static readonly Color CoverageColor = new Color(0f, 0.37f, 0f, 1f);

        private class Beam
        {
            public RectTransform Wedge;
            public RawImage Dashes;
        }

        private readonly RectTransform _layer;
        private readonly List<Beam> _beams = new List<Beam>();
        private readonly List<RawImage> _lockLines = new List<RawImage>();
        private readonly Image _jamTarget;
        private readonly RectTransform _coverage;
        private readonly Image _coverageLeft;
        private readonly Image _coverageRight;
        private readonly Image _coverageArc;
        private float _coverageHalf = 60f;
        private readonly Dictionary<Unit, RwrThreatState> _locks = new Dictionary<Unit, RwrThreatState>();

        public PortalTsdEffects(Transform view)
        {
            _layer = PortalMFDGraphics.At("Effects", view, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1f, 1f));

            _coverage = PortalMFDGraphics.At("Coverage", _layer, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1f, 1f));
            _coverageLeft = PortalMFDGraphics.Line("Left", _coverage, Vector2.zero, Vector2.up, 1f, CoverageColor);
            _coverageRight = PortalMFDGraphics.Line("Right", _coverage, Vector2.zero, Vector2.up, 1f, CoverageColor);
            _coverageArc = PortalMFDGraphics.Arc("Arc", _coverage, Vector2.zero, 10f, _coverageHalf * 2f, PortalShape.RingFine, CoverageColor);
            _coverage.gameObject.SetActive(false);

            for (int i = 0; i < MaxBeams; i++)
            {
                Beam b = new Beam();
                b.Wedge = PortalMFDGraphics.Rect("JamBeam", _layer, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(1f, 1f));
                Image wedge = b.Wedge.gameObject.AddComponent<Image>();
                wedge.sprite = RWRConeGraphic.GetOrCreateFlatConeSprite();
                wedge.raycastTarget = false;
                Mask mask = b.Wedge.gameObject.AddComponent<Mask>();
                mask.showMaskGraphic = false;

                RectTransform strip = PortalMFDGraphics.Rect("Dashes", b.Wedge, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0.5f, 0f),
                    Vector2.zero, new Vector2(BeamStripWidth, 0f));
                b.Dashes = strip.gameObject.AddComponent<RawImage>();
                b.Dashes.texture = PortalMFDGraphics.DashTexture();
                b.Dashes.raycastTarget = false;
                b.Wedge.gameObject.SetActive(false);
                _beams.Add(b);
            }

            for (int i = 0; i < MaxLockLines; i++)
            {
                RawImage line = PortalMFDGraphics.DashedLine("LockLine", _layer, 1f, LockColor);
                line.gameObject.SetActive(false);
                _lockLines.Add(line);
            }

            // Above the beams and lines
            _jamTarget = PortalMFDGraphics.Shape("JamTarget", _layer, new Vector2(0.5f, 0.5f), Vector2.zero, 18f, PortalShape.Diamond, JamTargetColor);
            _jamTarget.gameObject.SetActive(false);
        }

        /// <summary>
        /// contacts: the plan view contacts with their shown positions. metersToUnits: plan scale.
        /// </summary>
        public void Tick(Aircraft own, List<PortalContact> contacts, float headingDeg, float metersToUnits, float scopeRadius)
        {
            if (own == null || own.disabled)
            {
                Hide();
                return;
            }
            float hRad = headingDeg * Mathf.Deg2Rad;
            Vector3 ownPos = own.transform.position;
            TickCoverage(own, headingDeg, metersToUnits, scopeRadius);
            TickBeams(own, ownPos, hRad, metersToUnits, scopeRadius);
            TickLockLines(own, contacts, ownPos, hRad, metersToUnits);
        }

        public void Hide()
        {
            Show(_coverage, false);
            Show(_jamTarget, false);
            for (int i = 0; i < _beams.Count; i++) Show(_beams[i].Wedge, false);
            for (int i = 0; i < _lockLines.Count; i++) Show(_lockLines[i], false);
        }

        private void TickCoverage(Aircraft own, float headingDeg, float metersToUnits, float scopeRadius)
        {
            TargetDetector radar = own.radar;
            bool on = radar != null && radar.activated;
            Show(_coverage, on);
            if (!on) return;
            // Past 4 rings the arc is off the page anyway
            float r = Mathf.Min(radar.GetRadarRange() * metersToUnits, scopeRadius * 4f);
            float half = RadarPhysics.ScanHalfAngleDeg(radar);
            bool limited = half < 180f;
            // The wedge turns with the scanner axis; the arc keeps its own rotation from SetArc
            _coverage.localEulerAngles = new Vector3(0f, 0f, limited ? -RadarPhysics.ScanBoresightDeg(radar, headingDeg) : 0f);
            if (half != _coverageHalf)
            {
                _coverageHalf = half;
                PortalMFDGraphics.SetArc(_coverageArc, half * 2f);
            }
            Show(_coverageLeft, limited);
            Show(_coverageRight, limited);
            if (limited)
            {
                float a = half * Mathf.Deg2Rad;
                PortalMFDGraphics.SetLine(_coverageLeft.rectTransform, Vector2.zero, new Vector2(-Mathf.Sin(a), Mathf.Cos(a)) * r, 1f);
                PortalMFDGraphics.SetLine(_coverageRight.rectTransform, Vector2.zero, new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * r, 1f);
            }
            _coverageArc.rectTransform.sizeDelta = new Vector2(r * 2f, r * 2f);
        }

        private void TickBeams(Aircraft own, Vector3 ownPos, float hRad, float metersToUnits, float scopeRadius)
        {
            FactionHQ hq = own.NetworkHQ;
            List<PodBeam> beams = StandoffJamming.Beams;
            float now = Time.timeSinceLevelLoad;
            float halfAngle = StandoffJamming.PhysicsConeDeg * 0.5f;
            float maxLength = scopeRadius * 4f;
            bool targetShown = false;
            int n = 0;
            for (int i = 0; hq != null && i < beams.Count && n < _beams.Count; i++)
            {
                PodBeam pb = beams[i];
                if (!StandoffJamming.IsActive(pb, now)) continue;
                if (pb.Owner == null || pb.Target == null || pb.Owner.NetworkHQ != hq) continue;

                Vector3 from = pb.Owner.transform.position;
                Vector3 to = pb.Target.transform.position;
                Vector2 start = PortalPagePlan.ToView(from - ownPos, hRad, metersToUnits);
                Vector2 target = PortalPagePlan.ToView(to - ownPos, hRad, metersToUnits);
                Vector2 dir = target - start;
                if (dir.sqrMagnitude < 0.01f) continue;

                // Like the map cone: past the target by the physics taper, at most the pod range
                float worldLength = new Vector2(to.x - from.x, to.z - from.z).magnitude * (1f + StandoffJamming.BeyondTargetFraction);
                if (pb.MaxRange > 0f && worldLength > pb.MaxRange) worldLength = pb.MaxRange;
                float length = Mathf.Min(worldLength * metersToUnits, maxLength);
                if (length < 1f) continue;

                Beam b = _beams[n++];
                Show(b.Wedge, true);
                b.Wedge.anchoredPosition = start;
                b.Wedge.localEulerAngles = new Vector3(0f, 0f, -Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg);
                b.Wedge.sizeDelta = new Vector2(Mathf.Max(MinBeamWidth, RWRConeGraphic.GetConeBaseWidth(length, halfAngle)), length);
                b.Dashes.uvRect = new Rect(0f, 0f, 1f, length / DashPeriod);
                Color c = BeamColor;
                if (pb.Owner != own) c.a *= 0.5f;
                b.Dashes.color = c;

                if (pb.Owner == own && !targetShown)
                {
                    targetShown = true;
                    _jamTarget.rectTransform.anchoredPosition = target;
                }
            }
            for (int i = n; i < _beams.Count; i++) Show(_beams[i].Wedge, false);
            Show(_jamTarget, targetShown);
        }

        private void TickLockLines(Aircraft own, List<PortalContact> contacts, Vector3 ownPos, float hRad, float metersToUnits)
        {
            _locks.Clear();
            PhysRWRReceiver rwr = PhysRWRReceiver.Get(own);
            if (rwr != null)
            {
                foreach (RwrThreatEntry t in rwr.ActiveThreats)
                {
                    if (t == null || t.EmitterUnit == null) continue;
                    if (t.State <= RwrThreatState.Search && !t.IsActiveSeeker) continue;
                    RwrThreatState state = t.IsActiveSeeker ? RwrThreatState.MissileGuidance : t.State;
                    RwrThreatState known;
                    if (!_locks.TryGetValue(t.EmitterUnit, out known) || state > known) _locks[t.EmitterUnit] = state;
                }
            }

            int n = 0;
            for (int i = 0; _locks.Count > 0 && i < contacts.Count && n < _lockLines.Count; i++)
            {
                PortalContact c = contacts[i];
                RwrThreatState state;
                if (!c.Hostile || !_locks.TryGetValue(c.Unit, out state)) continue;
                RawImage line = _lockLines[n++];
                Show(line, true);
                bool guidance = state == RwrThreatState.MissileGuidance;
                line.color = guidance ? GuidanceColor : LockColor;
                Vector2 from = PortalPagePlan.ToView(c.WorldPos - ownPos, hRad, metersToUnits);
                PortalMFDGraphics.SetDashed(line, from, Vector2.zero, guidance ? 1.5f : 1f, DashPeriod);
            }
            for (int i = n; i < _lockLines.Count; i++) Show(_lockLines[i], false);
        }

        private static void Show(Component c, bool on)
        {
            if (c != null && c.gameObject.activeSelf != on) c.gameObject.SetActive(on);
        }
    }
}
