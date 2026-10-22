using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CnoEvil.Core;

namespace CnoEvil.Components
{
    /// <summary>
    /// RWR page: azimuth display of the ownship receiver (PhysRWRReceiver), heading-up, built like the reference page.
    /// Scope: the rim, an inner ring at 0.5 R and a small center cross.
    /// Each threat is a letter (threat class when the receiver can classify, otherwise U; M for a missile) with:
    /// an air hat over an airborne emitter, both half circles while it tracks the ownship, the top half circle on the
    /// newest contact, a diamond on the main threat (strongest signal), a blinking lock line from the icon to the rim,
    /// a dark disc behind it and red color while it supports a missile (held 1 s). For 5 s after a threat starts
    /// guiding a missile the bottom half circle blinks and a yellow arc blinks on the launch bearing.
    /// The nearest incoming missile (datalink warning or active seeker) shows as two orange arcs inside the rim.
    /// Bearing is the measured bearing (tier jitter); distance from the center shows signal strength, not range.
    /// Sizes follow the reference layout scaled by K = scope diameter / 160; icons and lines are 20% smaller (IconScale).
    /// </summary>
    public class PortalPageRwr : PortalPage
    {
        private const float RefDiameter = 160f;
        // Icons and lines 20% below the reference size so close threats overlap less
        private const float IconScale = 0.8f;
        private const float LineWidth = 1.2f;
        private const int MaxThreats = 16;
        private const float SupportHoldTime = 1f;
        private const float LaunchTime = 5f;
        private const float LaunchHalfPeriod = 0.15f;
        private const float LaunchArcDeg = 90f;
        private const float MwsArcDeg = 36f;

        private static readonly Color BgColor = new Color(0f, 0.02f, 0.03f, 1f);
        private static readonly Color ScopeColor = new Color(0.1f, 0.7f, 0.1f, 1f);
        private static readonly Color NormalColor = new Color(0f, 1f, 0f, 1f);
        private static readonly Color MissileColor = new Color(1f, 0f, 0f, 1f);
        private static readonly Color LaunchColor = new Color(1f, 0.85f, 0f, 1f);
        private static readonly Color MwsColor = new Color(1f, 0.42f, 0f, 1f);
        private static readonly Color DarkBgColor = new Color(0f, 0f, 0f, 0.9f);

        private class Icon
        {
            public RectTransform Root;
            public Image DarkBg;
            public TextMeshProUGUI Text;
            public Image Diamond;
            public Image Hat;
            public Image Top;
            public Image Bottom;
            public Image LockLine;
            public Unit Unit;
            public bool Locked;
            public float LockedAt;
            public float LaunchAt;
            public Vector2 Pos;
        }

        private class History
        {
            public float FirstSeen;
            public bool Guiding;
            public float LaunchAt = -1000f;
            public bool Locked;
            public float LockedAt;
            public float SupportAt = -1000f;
            public int Seen;
        }

        private readonly List<Icon> _icons = new List<Icon>();
        private readonly Dictionary<Unit, History> _history = new Dictionary<Unit, History>();
        private readonly List<Unit> _stale = new List<Unit>();
        private int _tickCount;
        private float _radius;
        private float _k;
        private float _s;
        private RectTransform _scope;
        private Image _launchArc;
        private float _launchAt = -1000f;
        private readonly Image[] _mwsArcs = new Image[2];
        private TextMeshProUGUI _status;

        protected override void Build()
        {
            Background(BgColor);
            // Same center and radius as the TSD scope, so the rim matches the outer range ring
            _radius = H * 0.5f - PortalMFDGraphics.ScopeInset;
            _k = _radius * 2f / RefDiameter;
            // Reference RadarTemplate icon scale 0.7, reduced by IconScale
            _s = 0.7f * _k * IconScale;
            _scope = PortalMFDGraphics.At("Scope", Root, Mid, PortalMFDGraphics.ScopeCenter, new Vector2(1f, 1f));

            // Two rings with equal spacing (rim and 0.5 R) and a small center cross
            PortalMFDGraphics.Shape("Rim", _scope, Mid, Vector2.zero, _radius * 2f, PortalShape.RingFine, ScopeColor);
            PortalMFDGraphics.Shape("Inner", _scope, Mid, Vector2.zero, _radius, PortalShape.RingFine, ScopeColor);
            float arm = _radius * 0.06f * IconScale;
            PortalMFDGraphics.Line("CrossH", _scope, new Vector2(-arm, 0f), new Vector2(arm, 0f), LineWidth, ScopeColor);
            PortalMFDGraphics.Line("CrossV", _scope, new Vector2(0f, -arm), new Vector2(0f, arm), LineWidth, ScopeColor);

            // Launch detection: arc toward the launcher, blinks for 5 s (reference launchThreatDir)
            _launchArc = PortalMFDGraphics.Arc("LaunchArc", _scope, Vector2.zero, _radius * 2f - 20f * _k, LaunchArcDeg, PortalShape.RingBold, LaunchColor);
            _launchArc.gameObject.SetActive(false);
            // Nearest incoming missile: two arcs (reference mwsThreatDir)
            _mwsArcs[0] = PortalMFDGraphics.Arc("MwsArc", _scope, Vector2.zero, _radius * 2f - 33f * _k, MwsArcDeg, PortalShape.RingBold, MwsColor);
            _mwsArcs[1] = PortalMFDGraphics.Arc("MwsArc", _scope, Vector2.zero, _radius * 2f - 45f * _k, MwsArcDeg, PortalShape.RingBold, MwsColor);
            for (int i = 0; i < _mwsArcs.Length; i++) _mwsArcs[i].gameObject.SetActive(false);

            _status = Label(Root, new Vector2(0f, -H * 0.5f + 12f), new Vector2(120f, 12f), string.Empty, 8f,
                new Color(0.06f, 0.6f, 0.06f, 1f), TextAlignmentOptions.Center);
        }

        private Icon GetIcon(int index)
        {
            while (_icons.Count <= index)
            {
                // Reference RadarTemplate (32 units) scaled to this scope
                float s = _s;
                Icon ic = new Icon();
                ic.Root = PortalMFDGraphics.At("Threat", _scope, Mid, Vector2.zero, new Vector2(32f * s, 32f * s));
                ic.LockLine = PortalMFDGraphics.Line("LockLine", ic.Root, Vector2.zero, Vector2.up, LineWidth, NormalColor);
                ic.DarkBg = PortalMFDGraphics.Shape("DarkBg", ic.Root, Mid, Vector2.zero, 46.4f * s, PortalShape.Disc, DarkBgColor);
                ic.Text = Label(ic.Root, Vector2.zero, new Vector2(32f * s, 16f * s), string.Empty, 15f * s, NormalColor, TextAlignmentOptions.Center);
                ic.Diamond = PortalMFDGraphics.Shape("Diamond", ic.Root, Mid, Vector2.zero, 31f * s, PortalShape.Diamond, NormalColor);
                ic.Hat = PortalMFDGraphics.Shape("Hat", ic.Root, Mid, new Vector2(0f, 8f * s), 23f * s, PortalShape.AirHat, NormalColor);
                ic.Hat.rectTransform.sizeDelta = new Vector2(23f * s, 7.3f * s);
                ic.Top = PortalMFDGraphics.Shape("Top", ic.Root, Mid, Vector2.zero, 33.5f * s, PortalShape.HalfRingTop, NormalColor);
                ic.Bottom = PortalMFDGraphics.Shape("Bottom", ic.Root, Mid, Vector2.zero, 33.5f * s, PortalShape.HalfRingTop, NormalColor);
                ic.Bottom.rectTransform.localEulerAngles = new Vector3(0f, 0f, 180f);
                ic.Root.gameObject.SetActive(false);
                _icons.Add(ic);
            }
            Icon icon = _icons[index];
            if (!icon.Root.gameObject.activeSelf) icon.Root.gameObject.SetActive(true);
            return icon;
        }

        public override void Tick(float now)
        {
            Aircraft own = Aircraft;
            PhysRWRReceiver rwr = own != null ? PhysRWRReceiver.Get(own) : null;
            int n = 0;
            _tickCount++;
            bool mws = false;
            float mwsBrg = 0f;
            if (rwr != null)
            {
                bool classify = rwr.Capabilities.CanClassifyThreats;
                RwrThreatEntry strongest = null;
                Unit newest = null;
                float newestTime = float.MinValue;
                RwrThreatEntry seeker = null;
                foreach (RwrThreatEntry t in rwr.ActiveThreats)
                {
                    if (t == null || t.EmitterUnit == null) continue;
                    History h = UpdateHistory(t, now);
                    if (IsMissile(t))
                    {
                        if (seeker == null || t.SignalStrength01 > seeker.SignalStrength01) seeker = t;
                        continue;
                    }
                    if (strongest == null || t.SignalStrength01 > strongest.SignalStrength01) strongest = t;
                    if (h.FirstSeen > newestTime)
                    {
                        newestTime = h.FirstSeen;
                        newest = t.EmitterUnit;
                    }
                }

                // Signal scaling: stronger nearer the center, between the deadzone and the rim (reference WorldToRWRPosition)
                float iconHalf = 16f * _s;
                float inner = 15f * _k + iconHalf;
                float outer = _radius - iconHalf;
                foreach (RwrThreatEntry t in rwr.ActiveThreats)
                {
                    if (n >= MaxThreats) break;
                    if (t == null || t.EmitterUnit == null) continue;
                    History h = _history[t.EmitterUnit];
                    Icon ic = GetIcon(n++);

                    float b = t.RelativeBearing * Mathf.Deg2Rad;
                    float r = Mathf.Lerp(inner, outer, 1f - Mathf.Sqrt(Mathf.Clamp01(t.SignalStrength01)));
                    ic.Pos = new Vector2(Mathf.Sin(b), Mathf.Cos(b)) * r;
                    ic.Root.anchoredPosition = ic.Pos;
                    PlaceLockLine(ic);

                    bool missile = IsMissile(t);
                    bool support = !missile && now - h.SupportAt < SupportHoldTime;
                    ic.Locked = h.Locked;
                    ic.LockedAt = h.LockedAt;
                    ic.LaunchAt = missile ? -1000f : h.LaunchAt;

                    if (ic.Unit != t.EmitterUnit)
                    {
                        ic.Unit = t.EmitterUnit;
                        ic.Text.text = Symbol(t, classify);
                    }

                    Color c = (missile ? h.Locked : support) ? MissileColor : NormalColor;
                    ic.Text.color = c;
                    ic.Top.color = c;
                    ic.Bottom.color = c;
                    ic.Diamond.color = c;
                    ic.Hat.color = c;
                    ic.LockLine.color = c;

                    Show(ic.Diamond, !missile && t == strongest);
                    Show(ic.Hat, !missile && classify && t.EmitterUnit is Aircraft);
                    Show(ic.Top, h.Locked || (!missile && t.EmitterUnit == newest) || now - ic.LaunchAt < LaunchTime);
                    Show(ic.DarkBg, missile || support || h.Locked);
                    // Missiles and threats supporting a missile draw over the others
                    if ((missile || support) && ic.Root.GetSiblingIndex() != _scope.childCount - 1) ic.Root.SetAsLastSibling();
                }

                // Nearest datalink missile warning, else the strongest active seeker
                float heading = HeadingDeg(own.transform);
                Vector3 ownPos = own.transform.position;
                float nearest = float.MaxValue;
                foreach (DatalinkThreatEntry d in rwr.ActiveDatalinkThreats)
                {
                    if (d == null || !d.IsMissile) continue;
                    Vector3 v = d.EstimatedPosition - ownPos;
                    if (v.sqrMagnitude >= nearest) continue;
                    nearest = v.sqrMagnitude;
                    mwsBrg = Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg - heading;
                    mws = true;
                }
                if (!mws && seeker != null)
                {
                    mws = true;
                    mwsBrg = seeker.RelativeBearing;
                }
                SetText(_status, string.Empty);
            }
            else
            {
                SetText(_status, "RWR OFF");
                _launchAt = -1000f;
            }

            for (int i = 0; i < _mwsArcs.Length; i++)
            {
                Show(_mwsArcs[i], mws);
                if (mws) _mwsArcs[i].rectTransform.localEulerAngles = new Vector3(0f, 0f, MwsArcDeg * 0.5f - mwsBrg);
            }
            for (int i = n; i < _icons.Count; i++)
            {
                Show(_icons[i].Root, false);
                _icons[i].Unit = null;
            }
            DropStaleHistory();
        }

        private static bool IsMissile(RwrThreatEntry t)
        {
            return t.IsActiveSeeker || t.EmitterUnit is Missile;
        }

        /// <summary>
        /// First time heard, lock start, missile support (held 1 s) and missile launch (start of guidance) per emitter.
        /// A launch also points the launch arc at the emitter.
        /// </summary>
        private History UpdateHistory(RwrThreatEntry t, float now)
        {
            History h;
            if (!_history.TryGetValue(t.EmitterUnit, out h))
            {
                h = new History();
                h.FirstSeen = now;
                _history[t.EmitterUnit] = h;
            }
            bool locked = t.State == RwrThreatState.Track || t.State == RwrThreatState.MissileGuidance || t.IsActiveSeeker;
            if (locked && !h.Locked) h.LockedAt = now;
            h.Locked = locked;

            bool guiding = t.State == RwrThreatState.MissileGuidance;
            if (guiding) h.SupportAt = now;
            if (guiding && !h.Guiding && !IsMissile(t))
            {
                h.LaunchAt = now;
                _launchAt = now;
                _launchArc.rectTransform.localEulerAngles = new Vector3(0f, 0f, LaunchArcDeg * 0.5f - t.RelativeBearing);
            }
            h.Guiding = guiding;
            h.Seen = _tickCount;
            return h;
        }

        private void DropStaleHistory()
        {
            _stale.Clear();
            foreach (KeyValuePair<Unit, History> kv in _history)
            {
                if (kv.Key == null || kv.Value.Seen != _tickCount) _stale.Add(kv.Key);
            }
            for (int i = 0; i < _stale.Count; i++) _history.Remove(_stale[i]);
        }

        /// <summary>Lock line: from the icon edge out to the rim, along the radial through the icon.</summary>
        private void PlaceLockLine(Icon ic)
        {
            float len = ic.Pos.magnitude;
            Vector2 d = len > 0.01f ? ic.Pos / len : Vector2.up;
            float start = 16.75f * _s;
            float end = Mathf.Max(start, _radius - len);
            PortalMFDGraphics.SetLine(ic.LockLine.rectTransform, d * start, d * end, LineWidth);
        }

        /// <summary>
        /// Every frame: lock line blink, bottom half circle (lock, or 3 Hz blink for 5 s after a launch), launch arc blink.
        /// </summary>
        public override void FrameTick()
        {
            float now = Time.time;
            for (int i = 0; i < _icons.Count; i++)
            {
                Icon ic = _icons[i];
                if (!ic.Root.gameObject.activeSelf) continue;
                bool launch = now - ic.LaunchAt < LaunchTime;
                bool bottom = launch ? Mathf.RoundToInt(now * 6f) % 2 == 0 : ic.Locked;
                Show(ic.Bottom, bottom);
                Show(ic.LockLine, ic.Locked && Mathf.Repeat(now - ic.LockedAt, 0.2f) > 0.075f);
            }
            float age = now - _launchAt;
            Show(_launchArc, age < LaunchTime && PortalMFDGraphics.Blink(age, LaunchHalfPeriod));
        }

        private static string Symbol(RwrThreatEntry t, bool classify)
        {
            if (IsMissile(t)) return "M";
            if (!classify) return "U";
            Unit u = t.EmitterUnit;
            if (u is Aircraft) return TargetIdentification.GetLabel(u, IdentTier.Mid).Substring(0, 1);
            if (u is Ship) return "N";
            string cls = TargetIdentification.GetLabel(u, IdentTier.Mid);
            if (cls == "SAM") return "S";
            if (cls == "AAA") return "A";
            return "R";
        }
    }
}
