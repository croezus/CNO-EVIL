using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Components
{
    /// <summary>
    /// Heading-up plan view shared by TSD and NAV: range rings, radial lines, north-referenced labels, the ownship
    /// symbol and the faction picture (PortalContacts). Grey rings, white ownship, hostile red, friendly blue, green
    /// lock circle on the selected target. The outer ring (the range) fits the page height.
    /// </summary>
    public abstract class PortalPagePlan : PortalPage
    {
        protected float ScopeRadius = 140f;
        protected static readonly Vector2 ViewCenter = PortalMFDGraphics.ScopeCenter;
        private static readonly float[] s_ranges = { 5f, 10f, 20f, 40f, 80f };
        private const float MinRangeKm = 5f;
        private const float MaxRangeKm = 80f;

        protected static readonly Color HostileColor = new Color(1f, 0.22f, 0.1f, 1f);
        protected static readonly Color FriendlyColor = new Color(0f, 0.67f, 1f, 1f);

        // Reference TSD: an uncertain track flashes (0.3 s on / off) for 5 s, a weak track glides to its new
        // position (Lerp 7 / s) until it is within sqrt(50) m
        private const float FlashTime = 5f;
        private const float FlashHalfPeriod = 0.3f;
        private const float GlideRate = 7f;
        private const float GlideSnapSq = 50f;

        private class TrackState
        {
            public bool Live;
            public float LostAt = -1000f;
            public Vector3 Shown;
            public bool HasShown;
            public int Seen;
        }

        protected float RangeKm;
        protected RectTransform View;
        private RectTransform _rose;
        private readonly List<TextMeshProUGUI> _roseLabels = new List<TextMeshProUGUI>();
        private readonly TextMeshProUGUI[] _ringLabels = new TextMeshProUGUI[4];
        protected readonly List<PortalContact> Contacts = new List<PortalContact>();
        private PortalSymbolPool _symbols;
        private readonly Dictionary<Unit, TrackState> _states = new Dictionary<Unit, TrackState>();
        private readonly List<Unit> _stale = new List<Unit>();
        private readonly HashSet<Unit> _emitters = new HashSet<Unit>();
        private int _tickCount;
        private float _lastTick;
        protected TextMeshProUGUI ScaleText;

        protected abstract float DefaultRangeKm();
        protected virtual void BuildUnderlay() { }
        /// <summary>Above the rings, below the contact symbols.</summary>
        protected virtual void BuildUnderSymbols() { }
        protected virtual void BuildOverlay() { }
        protected virtual void TickPlan(float headingDeg, float metersToUnits) { }
        /// <summary>Which units the page shows; null shows every collected contact.</summary>
        protected virtual System.Predicate<Unit> ContactFilter(Unit primary) { return null; }
        /// <summary>After the contacts are collected and placed (Contacts).</summary>
        protected virtual void TickAfterContacts(float headingDeg, float metersToUnits) { }
        /// <summary>Type labels next to hostile symbols.</summary>
        protected virtual bool ContactLabels() { return true; }

        protected override void Build()
        {
            Background(Color.black);
            ScopeRadius = H * 0.5f - PortalMFDGraphics.ScopeInset;
            RangeKm = Mathf.Clamp(DefaultRangeKm(), MinRangeKm, MaxRangeKm);
            BuildUnderlay();

            View = PortalMFDGraphics.At("View", Root, Mid, ViewCenter, new Vector2(1f, 1f));

            // Radial lines: brighter at 0 / 90 deg, faint at 45 deg (reference radialLines)
            for (int i = 0; i < 4; i++)
            {
                float a = i * 45f * Mathf.Deg2Rad;
                Vector2 d = new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * ScopeRadius * 1.3f;
                Color c = (i % 2 == 0) ? new Color(0.17f, 0.17f, 0.17f, 1f) : new Color(0.08f, 0.08f, 0.08f, 1f);
                PortalMFDGraphics.Line("Radial", View, -d, d, 0.75f, c);
            }

            for (int i = 1; i <= 4; i++)
            {
                float r = ScopeRadius * i / 4f;
                PortalMFDGraphics.Shape("RangeRing", View, Mid, Vector2.zero, r * 2f, PortalShape.RingFine, PortalMFDGraphics.RingGrey);
                Vector2 p = new Vector2(Mathf.Sin(35f * Mathf.Deg2Rad), Mathf.Cos(35f * Mathf.Deg2Rad)) * r;
                _ringLabels[i - 1] = Label(View, p + new Vector2(8f, 0f), new Vector2(30f, 10f), string.Empty, 7.5f,
                    new Color(0.41f, 0.41f, 0.41f, 1f), TextAlignmentOptions.Left);
            }

            // North-referenced labels on the first ring (reference dirText 0 / 9 / 18 / 27)
            _rose = PortalMFDGraphics.At("Rose", View, Mid, Vector2.zero, new Vector2(1f, 1f));
            string[] dirs = { "0", "9", "18", "27" };
            float lr = ScopeRadius * 0.25f + 9f;
            for (int i = 0; i < 4; i++)
            {
                float a = i * 90f * Mathf.Deg2Rad;
                TextMeshProUGUI t = Label(_rose, new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * lr, new Vector2(20f, 10f), dirs[i], 9f,
                    new Color(0.32f, 0.32f, 0.32f, 1f), TextAlignmentOptions.Center);
                _roseLabels.Add(t);
            }

            BuildUnderSymbols();
            _symbols = new PortalSymbolPool(View, Font);
            PortalMFDGraphics.Shape("Ownship", View, Mid, Vector2.zero, 13.8f, PortalShape.PlayerArrow, Color.white);

            // Range: scale text and + / - (reference ScaleButtons, top right)
            ScaleText = Label(Root, new Vector2(W * 0.5f - 30f, H * 0.5f - 46f), new Vector2(50f, 12f), string.Empty, 10.5f,
                Color.white, TextAlignmentOptions.Right);
            Button(new Vector2(W * 0.5f - 14f, H * 0.5f - 14f), new Vector2(20f, 18f), "+");
            Button(new Vector2(W * 0.5f - 38f, H * 0.5f - 14f), new Vector2(20f, 18f), "-");

            BuildOverlay();
            UpdateRangeTexts();
        }

        public override void CycleRange()
        {
            SetRangeKm(s_ranges[(NearestRange(s_ranges, RangeKm) + 1) % s_ranges.Length]);
        }

        public void SetRangeKm(float km)
        {
            RangeKm = Mathf.Clamp(km, MinRangeKm, MaxRangeKm);
            UpdateRangeTexts();
            if (Root.gameObject.activeInHierarchy) Tick(Time.time);
        }

        private void UpdateRangeTexts()
        {
            SetText(ScaleText, RangeKm.ToString("0") + " KM");
            for (int i = 0; i < 4; i++)
            {
                float km = RangeKm * (i + 1) / 4f;
                SetText(_ringLabels[i], km < 10f ? km.ToString("0.#") : km.ToString("0"));
            }
        }

        public override void Tick(float now)
        {
            Aircraft own = Aircraft;
            if (own == null) return;
            float heading = HeadingDeg(own.transform);
            float hRad = heading * Mathf.Deg2Rad;
            float scale = ScopeRadius / (RangeKm * 1000f);

            _rose.localEulerAngles = new Vector3(0f, 0f, heading);
            for (int i = 0; i < _roseLabels.Count; i++) _roseLabels[i].rectTransform.localEulerAngles = new Vector3(0f, 0f, -heading);

            TickPlan(heading, scale);

            Unit primary = PrimaryTarget();
            PortalContacts.Collect(own, Contacts, ContactFilter(primary));
            CollectEmitters(own);
            float dt = Mathf.Clamp(now - _lastTick, 0f, 0.5f);
            _lastTick = now;
            float glide = 1f - Mathf.Exp(-GlideRate * dt);
            _tickCount++;

            Vector3 ownPos = own.transform.position;
            float limit = ScopeRadius * 1.6f;
            int n = 0;
            int hostiles = 0;
            int friendlies = 0;
            for (int i = 0; i < Contacts.Count; i++)
            {
                PortalContact c = Contacts[i];
                TrackState st = State(c, now, glide);
                // Weak tracks show the glided position; the effects read it from Contacts
                c.WorldPos = st.Shown;
                Contacts[i] = c;

                Vector2 p = HeadingUp(c.WorldPos.x - ownPos.x, c.WorldPos.z - ownPos.z, hRad, scale);
                if (p.sqrMagnitude > limit * limit) continue;
                if (c.Hostile ? hostiles++ >= PortalContacts.MaxHostile : friendlies++ >= PortalContacts.MaxFriendly) continue;

                PortalSymbolPool.Item s = _symbols.Get(n++);
                s.Root.anchoredPosition = p;
                Color col = c.Hostile ? HostileColor : FriendlyColor;
                if (!c.Live) col.a = 0.5f;

                PortalShape shape;
                if (c.Unit is Aircraft) shape = c.Hostile ? PortalShape.AirHat : PortalShape.HalfRingTop;
                else if (c.Unit is Ship) shape = PortalShape.Diamond;
                else shape = c.Hostile ? PortalShape.SquareOutline : PortalShape.Arch;
                Sprite sprite = PortalMFDGraphics.Get(shape);
                if (s.Icon.sprite != sprite) s.Icon.sprite = sprite;
                s.Icon.color = col;

                // Aircraft with a known track point along it, the rest stay upright (reference enemyAir / NoVector)
                float rot = 0f;
                if (c.Unit is Aircraft && c.HasVelocity) rot = heading - Mathf.Atan2(c.Velocity.x, c.Velocity.z) * Mathf.Rad2Deg;
                s.Icon.rectTransform.localEulerAngles = new Vector3(0f, 0f, rot);

                // A track that just lost its live contact flashes before it settles dim
                bool flashing = c.Hostile && !c.Live && now - st.LostAt < FlashTime;
                Show(s.Icon, !flashing || PortalMFDGraphics.Blink(now - st.LostAt, FlashHalfPeriod));

                bool emitter = c.Hostile && _emitters.Contains(c.Unit);
                Show(s.Radar, emitter);
                if (emitter) s.Radar.color = col;

                float ringSize = c.UncertaintyMeters * 2f * scale;
                bool ring = c.Hostile && !c.Live && ringSize > 8f;
                Show(s.Ring, ring);
                if (ring)
                {
                    s.Ring.rectTransform.sizeDelta = new Vector2(ringSize, ringSize);
                    s.Ring.color = new Color(col.r, col.g, col.b, 0.3f);
                }
                Show(s.Lock, c.Unit == primary);

                if (s.Unit != c.Unit || s.Tier != c.Tier)
                {
                    s.Unit = c.Unit;
                    s.Tier = c.Tier;
                    s.Label.text = c.Hostile && ContactLabels() ? TargetIdentification.GetLabel(c.Unit, c.Tier) : string.Empty;
                }
                s.Label.color = col;
            }
            _symbols.HideFrom(n);
            DropStale();

            TickAfterContacts(heading, scale);
        }

        /// <summary>World offset from the ownship to view units (heading-up).</summary>
        public static Vector2 ToView(Vector3 offset, float headingRad, float metersToUnits)
        {
            return HeadingUp(offset.x, offset.z, headingRad, metersToUnits);
        }

        private TrackState State(PortalContact c, float now, float glide)
        {
            TrackState st;
            if (!_states.TryGetValue(c.Unit, out st))
            {
                st = new TrackState();
                st.Live = c.Live;
                _states[c.Unit] = st;
            }
            if (st.Live && !c.Live) st.LostAt = now;
            st.Live = c.Live;
            st.Seen = _tickCount;

            // Live tracks snap; weak (passive / memory) tracks glide
            if (!st.HasShown || c.Live || (st.Shown - c.WorldPos).sqrMagnitude < GlideSnapSq) st.Shown = c.WorldPos;
            else st.Shown = Vector3.Lerp(st.Shown, c.WorldPos, glide);
            st.HasShown = true;
            return st;
        }

        private void DropStale()
        {
            _stale.Clear();
            foreach (KeyValuePair<Unit, TrackState> kv in _states)
            {
                if (kv.Key == null || kv.Value.Seen != _tickCount) _stale.Add(kv.Key);
            }
            for (int i = 0; i < _stale.Count; i++) _states.Remove(_stale[i]);
        }

        /// <summary>Emitters the ownship receiver hears now.</summary>
        private void CollectEmitters(Aircraft own)
        {
            _emitters.Clear();
            PhysRWRReceiver rwr = PhysRWRReceiver.Get(own);
            if (rwr == null) return;
            foreach (RwrThreatEntry t in rwr.ActiveThreats)
            {
                if (t != null && t.EmitterUnit != null) _emitters.Add(t.EmitterUnit);
            }
        }
    }

    /// <summary>
    /// TSD: tactical situation, heading-up. Adds the compass (red north arrow), the target info box of the
    /// selected target with the faction's identification tier, the EM scan ring (TacScreenEmScanRing) of the
    /// AdvancedCanvas display, shown by TacScreenThreatDisplay, and the EW overlays (PortalTsdEffects): jamming pod
    /// beams, lock lines from hostile radars and the own radar coverage. Contacts show as symbols only, without labels.
    /// </summary>
    public class PortalPageTsd : PortalPagePlan
    {
        private RectTransform _compass;
        private TextMeshProUGUI _tgtInfo;
        private GameObject _tgtBox;
        private TacScreenEmScanRing _ring;
        private PortalTsdEffects _effects;
        private Unit _filterPrimary;
        private System.Predicate<Unit> _filter;

        /// <summary>
        /// Air targets, radar SAMs and radars only (ThreatClassification.IsTsdContact), plus the selected target,
        /// so ground clutter does not pile up on the page.
        /// </summary>
        protected override System.Predicate<Unit> ContactFilter(Unit primary)
        {
            _filterPrimary = primary;
            if (_filter == null) _filter = delegate (Unit u) { return u == _filterPrimary || ThreatClassification.IsTsdContact(u); };
            return _filter;
        }

        protected override bool ContactLabels() { return false; }

        protected override void BuildUnderSymbols()
        {
            _effects = new PortalTsdEffects(View);
        }

        protected override void TickAfterContacts(float headingDeg, float metersToUnits)
        {
            if (_effects != null) _effects.Tick(Aircraft, Contacts, headingDeg, metersToUnits, ScopeRadius);
        }

        protected override float DefaultRangeKm()
        {
            return CnoEvilConfig.PortalMFDTsdRange_km != null ? CnoEvilConfig.PortalMFDTsdRange_km.Value : 40f;
        }

        protected override void BuildOverlay()
        {
            // Below the rings, labels and symbols of the plan view (Create puts it first under View)
            if (Aircraft != null)
            {
                PhysRWRReceiver rwr = PhysRWRReceiver.Get(Aircraft);
                if (rwr == null) rwr = Aircraft.gameObject.AddComponent<PhysRWRReceiver>();
                _ring = TacScreenEmScanRing.Create(View, Aircraft, rwr);
            }

            Vector2 cpos = new Vector2(W * 0.5f - 24f, H * 0.5f - 72f);
            PortalMFDGraphics.Shape("CompassRing", Root, Mid, cpos, 26f, PortalShape.Ring, new Color(0.62f, 0.62f, 0.62f, 1f));
            _compass = PortalMFDGraphics.At("Compass", Root, Mid, cpos, new Vector2(1f, 1f));
            PortalMFDGraphics.Shape("North", _compass, Mid, new Vector2(0f, 7f), 9f, PortalShape.TriangleFilled, new Color(0.64f, 0f, 0f, 1f));
            PortalMFDGraphics.Shape("South", _compass, Mid, new Vector2(0f, -7f), 7f, PortalShape.TriangleOutline, new Color(0.62f, 0.62f, 0.62f, 1f))
                .rectTransform.localEulerAngles = new Vector3(0f, 0f, 180f);

            // Target info box, bottom right (reference TgtInfoBox 91.7 x 41.7)
            Image box = PortalMFDGraphics.Box("TgtInfo", Root, Mid, new Vector2(W * 0.5f - 52f, -H * 0.5f + 28f), new Vector2(98f, 46f), new Color(0f, 0f, 0f, 0.49f));
            PortalMFDGraphics.AddImage(PortalMFDGraphics.Stretch("Border", box.transform, 0f), new Color(1f, 1f, 1f, 0.25f), PortalShape.Frame);
            _tgtInfo = Label(box.transform, Vector2.zero, new Vector2(92f, 42f), string.Empty, 8f, Color.white, TextAlignmentOptions.Left);
            _tgtBox = box.gameObject;
        }

        public override void FrameTick()
        {
            if (_ring == null) return;
            CnoEvilConfig.TacScreenThreatDisplayMode mode = CnoEvilConfig.TacScreenThreatDisplay.Value;
            bool show = mode == CnoEvilConfig.TacScreenThreatDisplayMode.EmScanRing || mode == CnoEvilConfig.TacScreenThreatDisplayMode.Both;
            GameObject ringObj = _ring.gameObject;
            if (ringObj.activeSelf != show) ringObj.SetActive(show);
            if (!show) return;
            if (Aircraft == null || Aircraft.disabled)
            {
                _ring.ResetEnergy();
                return;
            }
            // The ring rotates its world bins by the ownship heading itself
            _ring.Tick(ScopeRadius, ScopeRadius / (RangeKm * 1000f));
        }

        protected override void TickPlan(float headingDeg, float metersToUnits)
        {
            _compass.localEulerAngles = new Vector3(0f, 0f, headingDeg);

            Unit primary = PrimaryTarget();
            Show(_tgtBox.transform, primary != null);
            if (primary == null) return;

            FactionHQ hq = RWRTriangulationProcessor.GetPlayerFactionHQ();
            Dictionary<Unit, TriangulationTrack> tracks = hq != null ? RWRTriangulationProcessor.GetTrackDictionary(hq) : null;
            TriangulationTrack track = null;
            if (tracks != null && ThreatClassification.IsTrackedThreat(primary, hq)) tracks.TryGetValue(primary, out track);
            IdentTier tier = IdentTier.High;
            string q = string.Empty;
            Vector3 pos = primary.transform.position;
            // Same switch as the HMD target info
            bool degrade = CnoEvilConfig.EnableHMDTargetIdentDegradation == null || CnoEvilConfig.EnableHMDTargetIdentDegradation.Value;
            if (track != null && degrade)
            {
                tier = TargetIdentification.GetTier(track);
                q = TargetIdentification.FormatQuality(track.TrackingQuality);
                if (track.LastKnownTime > 0f) pos = track.LastKnownGlobalPosition.ToLocalPosition();
            }

            Vector3 d = pos - Aircraft.transform.position;
            float range = new Vector2(d.x, d.z).magnitude;
            float brg = Mathf.Repeat(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, 360f);
            SetText(_tgtInfo, string.Format("{0} {1}\nBRG {2:000}\nRNG {3}",
                TargetIdentification.GetLabel(primary, tier), q, brg, TargetIdentification.FormatRange(range, tier)));
        }
    }

    /// <summary>
    /// NAV: the map image of the level below the plan view, heading-up, with the airbases.
    /// </summary>
    public class PortalPageNav : PortalPagePlan
    {
        private RectTransform _mapLayer;
        private Image _mapImage;
        private MapSettings _mapSettings;
        private readonly List<Image> _bases = new List<Image>();

        protected override float DefaultRangeKm()
        {
            return CnoEvilConfig.PortalMFDNavRange_km != null ? CnoEvilConfig.PortalMFDNavRange_km.Value : 80f;
        }

        protected override void BuildUnderlay()
        {
            _mapLayer = PortalMFDGraphics.At("MapLayer", Root, Mid, ViewCenter, new Vector2(1f, 1f));
            _mapImage = PortalMFDGraphics.AddImage(PortalMFDGraphics.At("Map", _mapLayer, Mid, Vector2.zero, new Vector2(1f, 1f)),
                new Color(0.55f, 0.55f, 0.55f, 1f), null);
            _mapImage.gameObject.SetActive(false);
        }

        public override void OnShow()
        {
            LevelInfo level = NetworkSceneSingleton<LevelInfo>.i;
            _mapSettings = level != null ? level.LoadedMapSettings : null;
            bool has = _mapSettings != null && _mapSettings.MapImage != null;
            if (has && _mapImage.sprite != _mapSettings.MapImage) _mapImage.sprite = _mapSettings.MapImage;
            Show(_mapImage, has);
        }

        protected override void TickPlan(float headingDeg, float metersToUnits)
        {
            Aircraft own = Aircraft;
            _mapLayer.localEulerAngles = new Vector3(0f, 0f, headingDeg);
            if (_mapSettings != null && _mapImage.gameObject.activeSelf)
            {
                // Global (0, 0) is the map center; global x is east, z is north (DynamicMap)
                Vector3 g = own.GlobalPosition().AsVector3();
                _mapImage.rectTransform.sizeDelta = _mapSettings.MapSize * metersToUnits;
                _mapImage.rectTransform.anchoredPosition = new Vector2(-g.x, -g.z) * metersToUnits;
            }

            int n = 0;
            Dictionary<string, Airbase> lookup = FactionRegistry.airbaseLookup;
            if (lookup != null)
            {
                float hRad = headingDeg * Mathf.Deg2Rad;
                Vector3 ownPos = own.transform.position;
                foreach (KeyValuePair<string, Airbase> kv in lookup)
                {
                    Airbase ab = kv.Value;
                    if (ab == null || ab.center == null || ab.disabled) continue;
                    Vector3 p = ab.center.position;
                    Vector2 s = HeadingUp(p.x - ownPos.x, p.z - ownPos.z, hRad, metersToUnits);
                    if (s.sqrMagnitude > ScopeRadius * ScopeRadius * 2.6f) continue;
                    while (_bases.Count <= n)
                    {
                        Image img = PortalMFDGraphics.Shape("Base", View, Mid, Vector2.zero, 12f, PortalShape.Arch, Color.white);
                        img.rectTransform.SetSiblingIndex(0);
                        _bases.Add(img);
                    }
                    Image b = _bases[n++];
                    Show(b, true);
                    b.rectTransform.anchoredPosition = s;
                    FactionHQ hq = ab.CurrentHQ;
                    b.color = hq == null ? new Color(0.62f, 0.62f, 0.62f, 1f) : (hq == own.NetworkHQ ? FriendlyColor : HostileColor);
                }
            }
            for (int i = n; i < _bases.Count; i++) Show(_bases[i], false);
        }
    }
}
