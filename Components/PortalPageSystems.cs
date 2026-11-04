using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CnoEvil.Core;

namespace CnoEvil.Components
{
    /// <summary>
    /// SMS: stores page like the reference stores page. The vanilla front view of the aircraft (WeaponPanel/frontProfile
    /// sprite) in grey, gun stations above it, the other stations in one or two rows below it, ordered by their lateral
    /// position on the aircraft, each with a leader line to its place on the drawing. Station box: short name top left,
    /// rounds 'x2' bottom right (RLD while reloading); the selected station has a green frame. The strip at the bottom
    /// shows the selected weapon, its rounds and SAFE / ARM. The page only shows; it changes nothing.
    /// </summary>
    public class PortalPageSms : PortalPage
    {
        private const float BoxW = 62f;
        private const float BoxH = 24f;
        private const float ProfileY = 46f;
        private const float ProfileH = 110f;
        private const float Row1Y = -44f;
        private const float Row2Y = -74f;
        private static readonly Color DrawingColor = new Color(0.49f, 0.49f, 0.49f, 1f);
        private static readonly Color BorderColor = new Color(0.5f, 0.5f, 0.5f, 1f);
        private static readonly Color LeaderColor = new Color(0.35f, 0.35f, 0.35f, 1f);
        private static readonly Color SelectedColor = new Color(0.03f, 1f, 0f, 1f);
        private static readonly Color ArmedColor = new Color(0.74f, 0.74f, 0.74f, 1f);
        private static readonly Color EmptyColor = new Color(0.28f, 0.28f, 0.28f, 1f);
        private static readonly Color CountColor = new Color(1f, 0.52f, 0f, 1f);

        private class Box
        {
            public RectTransform Root;
            public Image Frame;
            public Image Selected;
            public Image Leader;
            public TextMeshProUGUI Name;
            public TextMeshProUGUI Ammo;
            public WeaponStation Station;
            public int LastAmmo = -1;
            public bool LastReloading;
        }

        private struct Placed
        {
            public WeaponStation Station;
            public float Lateral;
        }

        private readonly List<Box> _boxes = new List<Box>();
        private readonly List<Placed> _gunStations = new List<Placed>();
        private readonly List<Placed> _stations = new List<Placed>();
        private Image _profile;
        private TextMeshProUGUI _weapon;
        private TextMeshProUGUI _rounds;
        private TextMeshProUGUI _safety;
        private Image _safetyFrame;
        private int _stationCount = -1;

        protected override void Build()
        {
            Background(Color.black);
            _profile = PortalMFDGraphics.AddImage(PortalMFDGraphics.At("Profile", Root, Mid, new Vector2(0f, ProfileY), new Vector2(W - 60f, ProfileH)),
                DrawingColor, null);
            _profile.preserveAspect = true;

            // Selected weapon strip (bottom)
            float stripW = W - 12f;
            Vector2 stripPos = new Vector2(0f, -H * 0.5f + 18f);
            Image strip = PortalMFDGraphics.Box("Strip", Root, Mid, stripPos, new Vector2(stripW, 24f), new Color(0f, 0f, 0f, 0.83f));
            PortalMFDGraphics.AddImage(PortalMFDGraphics.Stretch("Border", strip.transform, 0f), BorderColor, PortalShape.Frame);
            _weapon = Label(strip.transform, new Vector2(-stripW * 0.5f + 8f + 80f, 0f), new Vector2(160f, 14f), string.Empty, 11f, Color.white, TextAlignmentOptions.Left);
            _rounds = Label(strip.transform, new Vector2(stripW * 0.5f - 96f, 0f), new Vector2(60f, 14f), string.Empty, 11f, CountColor, TextAlignmentOptions.Right);
            Image safety = PortalMFDGraphics.Box("Safety", strip.transform, Mid, new Vector2(stripW * 0.5f - 28f, 0f), new Vector2(46f, 18f), Color.black);
            _safetyFrame = PortalMFDGraphics.AddImage(PortalMFDGraphics.Stretch("Border", safety.transform, 0f), Color.white, PortalShape.Frame);
            _safety = Label(safety.transform, Vector2.zero, new Vector2(46f, 18f), string.Empty, 10.5f, Color.white, TextAlignmentOptions.Center);
        }

        public override void OnShow()
        {
            RectTransform vanilla = Host.FindVanilla("frontProfile");
            Image src = vanilla != null ? vanilla.GetComponent<Image>() : null;
            if (src != null && src.sprite != null) _profile.sprite = src.sprite;
            Show(_profile, _profile.sprite != null);
            RebuildBoxes();
        }

        private void RebuildBoxes()
        {
            Aircraft own = Aircraft;
            List<WeaponStation> stations = own != null ? own.weaponStations : null;
            _stationCount = stations != null ? stations.Count : 0;
            _gunStations.Clear();
            _stations.Clear();
            float maxLateral = 0.1f;
            for (int i = 0; stations != null && i < stations.Count; i++)
            {
                WeaponStation st = stations[i];
                if (!Shown(st)) continue;
                Placed p = new Placed();
                p.Station = st;
                p.Lateral = Lateral(own, st, i);
                maxLateral = Mathf.Max(maxLateral, Mathf.Abs(p.Lateral));
                if (st.WeaponInfo.gun) _gunStations.Add(p);
                else _stations.Add(p);
            }
            _stations.Sort((a, b) => a.Lateral.CompareTo(b.Lateral));

            // Half width of the drawing as shown (preserveAspect), for the leader line ends
            float drawHalf = (W - 60f) * 0.5f;
            if (_profile.sprite != null)
            {
                Rect r = _profile.sprite.rect;
                if (r.height > 0f) drawHalf = Mathf.Min(drawHalf, ProfileH * r.width / r.height * 0.5f);
            }

            int k = 0;
            for (int i = 0; i < _gunStations.Count; i++)
            {
                float x = (i - (_gunStations.Count - 1) * 0.5f) * (BoxW + 6f);
                PlaceBox(k++, _gunStations[i].Station, new Vector2(x, H * 0.5f - 26f), null);
            }

            int n = _stations.Count;
            float usable = W - 12f;
            // One row if it fits, else two staggered rows (neighbours in a row are two steps apart)
            bool twoRows = n * (BoxW + 4f) > usable;
            float step = twoRows ? (usable - BoxW) / (n - 1) : BoxW + 4f;
            for (int i = 0; i < n; i++)
            {
                float x = (i - (n - 1) * 0.5f) * step;
                float y = twoRows && (i % 2 == 1) ? Row2Y : Row1Y;
                float px = _stations[i].Lateral / maxLateral * drawHalf * 0.85f;
                PlaceBox(k++, _stations[i].Station, new Vector2(x, y), new Vector2(px, ProfileY - 6f));
            }

            for (int i = k; i < _boxes.Count; i++)
            {
                _boxes[i].Station = null;
                Show(_boxes[i].Root, false);
                Show(_boxes[i].Leader, false);
            }
        }

        private void PlaceBox(int index, WeaponStation st, Vector2 pos, Vector2? leaderEnd)
        {
            while (_boxes.Count <= index) _boxes.Add(CreateBox());
            Box b = _boxes[index];
            b.Root.anchoredPosition = pos;
            b.Station = st;
            b.LastAmmo = -1;
            b.Name.text = !string.IsNullOrEmpty(st.WeaponInfo.shortName) ? st.WeaponInfo.shortName : st.WeaponInfo.weaponName;
            Show(b.Root, true);
            Show(b.Leader, leaderEnd.HasValue);
            if (leaderEnd.HasValue) PortalMFDGraphics.SetLine(b.Leader.rectTransform, pos + new Vector2(0f, BoxH * 0.5f), leaderEnd.Value, 1f);
        }

        /// <summary>Lateral position (m, right positive) of the station's first weapon on the aircraft; index order if none.</summary>
        private static float Lateral(Aircraft own, WeaponStation st, int index)
        {
            if (own != null && st.Weapons != null)
            {
                for (int i = 0; i < st.Weapons.Count; i++)
                {
                    Weapon w = st.Weapons[i];
                    if (w != null) return own.transform.InverseTransformPoint(w.transform.position).x;
                }
            }
            return index;
        }

        private static bool Shown(WeaponStation st)
        {
            return st != null && st.WeaponInfo != null && !st.WeaponInfo.hideInDisplay;
        }

        private Box CreateBox()
        {
            Box b = new Box();
            // Leaders go in first so that the boxes cover them
            b.Leader = PortalMFDGraphics.Line("Leader", Root, Vector2.zero, Vector2.up, 1f, LeaderColor);
            b.Leader.rectTransform.SetSiblingIndex(_profile.rectTransform.GetSiblingIndex() + 1);
            b.Root = PortalMFDGraphics.At("Station", Root, Mid, Vector2.zero, new Vector2(BoxW, BoxH));
            PortalMFDGraphics.AddImage(b.Root, new Color(0f, 0f, 0f, 0.83f), null);
            b.Frame = PortalMFDGraphics.AddImage(PortalMFDGraphics.Stretch("Frame", b.Root, 0f), BorderColor, PortalShape.Frame);
            b.Selected = PortalMFDGraphics.AddImage(PortalMFDGraphics.Stretch("Selected", b.Root, -1f), SelectedColor, PortalShape.Frame);
            b.Name = Label(b.Root, new Vector2(0f, 5f), new Vector2(BoxW - 6f, 11f), string.Empty, 8f, Color.white, TextAlignmentOptions.Left);
            b.Ammo = Label(b.Root, new Vector2(0f, -5.5f), new Vector2(BoxW - 6f, 11f), string.Empty, 8.5f, CountColor, TextAlignmentOptions.Right);
            return b;
        }

        public override void Tick(float now)
        {
            Aircraft own = Aircraft;
            if (own == null) return;
            if (own.weaponStations != null && own.weaponStations.Count != _stationCount) RebuildBoxes();
            WeaponStation current = own.weaponManager != null ? own.weaponManager.currentWeaponStation : null;

            for (int i = 0; i < _boxes.Count; i++)
            {
                Box b = _boxes[i];
                WeaponStation st = b.Station;
                if (st == null) continue;
                bool selected = st == current;
                Show(b.Selected, selected);
                b.Frame.color = selected ? SelectedColor : BorderColor;
                if (st.Ammo != b.LastAmmo || st.Reloading != b.LastReloading)
                {
                    b.LastAmmo = st.Ammo;
                    b.LastReloading = st.Reloading;
                    b.Ammo.text = st.Reloading ? "RLD" : "x" + st.Ammo;
                    b.Ammo.color = st.Reloading ? PortalMFDGraphics.Amber : (st.Ammo > 0 ? CountColor : EmptyColor);
                    b.Name.color = st.Ammo > 0 || st.Reloading ? ArmedColor : EmptyColor;
                }
            }

            if (current != null && current.WeaponInfo != null)
            {
                SetText(_weapon, current.WeaponInfo.weaponName);
                SetText(_rounds, current.Reloading ? "RLD" : current.Ammo + " / " + current.FullAmmo);
                bool safe = current.SafetyIsOn(own);
                SetText(_safety, safe ? "SAFE" : "ARM");
                Color c = safe ? PortalMFDGraphics.Amber : SelectedColor;
                _safety.color = c;
                _safetyFrame.color = c;
            }
            else
            {
                SetText(_weapon, "NO STORES");
                SetText(_rounds, string.Empty);
                SetText(_safety, string.Empty);
                _safetyFrame.color = BorderColor;
            }
        }
    }

    /// <summary>
    /// EOTS: the vanilla target camera and landing camera screens moved into the page (vanilla turns them on and off
    /// and its target info stays). CAM OFF while both are off.
    /// </summary>
    public class PortalPageEots : PortalPage
    {
        private TextMeshProUGUI _off;

        protected override bool HostsVanilla { get { return true; } }

        protected override void Build()
        {
            Background(Color.black);
            Vector2 box = new Vector2(W - 6f, H - 34f);
            Vector2 center = new Vector2(0f, -10f);
            if (Host.TargetCamDisplay != null) Host.MoveVanilla(Host.TargetCamDisplay.transform as RectTransform, Root, center, box);
            if (Host.LandingCamDisplay != null) Host.MoveVanilla(Host.LandingCamDisplay.transform as RectTransform, Root, center, box);
            _off = Label(Root, Vector2.zero, new Vector2(160f, 16f), "CAM OFF", 13f, new Color(1f, 0.6f, 0f, 1f), TextAlignmentOptions.Center);
            Label(Root, new Vector2(0f, H * 0.5f - 14f), new Vector2(100f, 12f), "EO TARGETING", 9f, PortalMFDGraphics.LabelGrey, TextAlignmentOptions.Center);
        }

        public override void Tick(float now)
        {
            bool on = (Host.TargetCamDisplay != null && Host.TargetCamDisplay.activeSelf) ||
                      (Host.LandingCamDisplay != null && Host.LandingCamDisplay.activeSelf);
            Show(_off, !on);
        }
    }

    /// <summary>
    /// HSI like the reference HSI page: compass card turning with the heading under a fixed ownship cross, heading box
    /// and lubber at the top, an amber bearing needle (head and tail) to the nearest airbase of the own faction and a
    /// green bug on the card at the ground track. Readout boxes in the corners: distance and bearing to the base, fuel
    /// and track, speed and vertical speed, altitude and radar altitude. The card, needle and bug turn every frame.
    /// </summary>
    public class PortalPageHsi : PortalPage
    {
        private static readonly Color BgColor = new Color(0.05f, 0.05f, 0.08f, 1f);
        private static readonly Color RoseColor = new Color(0.85f, 0.85f, 0.85f, 0.9f);
        private static readonly Color NumberColor = new Color(0.85f, 0.85f, 0.85f, 1f);
        private static readonly Color CrossColor = new Color(0.63f, 0.63f, 0.63f, 1f);
        private static readonly Color NeedleColor = new Color(0.91f, 0.66f, 0f, 0.82f);
        private static readonly Color TrackColor = new Color(0.07f, 0.88f, 0f, 1f);
        private static readonly Color ValueColor = new Color(0.82f, 0.82f, 0.82f, 1f);
        private static readonly Color CaptionColor = new Color(0.55f, 0.55f, 0.55f, 1f);
        private static readonly Color BoxBorder = new Color(0.5f, 0.5f, 0.5f, 1f);
        private const float MinTrackSpeed = 5f;

        private float _radius;
        private Vector2 _center;
        private RectTransform _card;
        private readonly List<TextMeshProUGUI> _cardLabels = new List<TextMeshProUGUI>();
        private RectTransform _needle;
        private RectTransform _track;
        private TextMeshProUGUI _hdg;
        private TextMeshProUGUI _dist;
        private TextMeshProUGUI _brg;
        private TextMeshProUGUI _fuel;
        private Image _fuelBar;
        private TextMeshProUGUI _trk;
        private TextMeshProUGUI _spd;
        private TextMeshProUGUI _vs;
        private TextMeshProUGUI _alt;
        private TextMeshProUGUI _ralt;
        private TextMeshProUGUI _spdCaption;
        private TextMeshProUGUI _vsCaption;
        private TextMeshProUGUI _altCaption;
        private TextMeshProUGUI _raltCaption;
        private TextMeshProUGUI _base;
        private Airbase _nearest;
        private string _nearestName;
        private float _nextBaseSearch;
        private float _baseBearing;
        private float _trackDeg;
        private bool _hasTrack;

        protected override void Build()
        {
            Background(BgColor);
            // Room for the heading box and the lubber above the card
            _radius = H * 0.5f - 32f;
            _center = PortalMFDGraphics.ScopeCenter;

            _card = PortalMFDGraphics.At("Card", Root, Mid, _center, new Vector2(1f, 1f));
            PortalMFDGraphics.Shape("Rose", _card, Mid, Vector2.zero, _radius * 2f, PortalShape.RingFine, RoseColor);
            string[] names = { "N", "3", "6", "E", "12", "15", "S", "21", "24", "W", "30", "33" };
            for (int i = 0; i < 72; i++)
            {
                float a = i * 5f * Mathf.Deg2Rad;
                Vector2 d = new Vector2(Mathf.Sin(a), Mathf.Cos(a));
                bool major = i % 6 == 0;
                float len = major ? 10f : (i % 2 == 0 ? 7f : 4f);
                PortalMFDGraphics.Line("Tick", _card, d * _radius, d * (_radius - len), major ? 1.5f : 1f, RoseColor);
                if (major)
                {
                    bool cardinal = i % 18 == 0;
                    TextMeshProUGUI t = Label(_card, d * (_radius - 20f), new Vector2(22f, 12f), names[i / 6], cardinal ? 11f : 9.5f,
                        cardinal ? Color.white : NumberColor, TextAlignmentOptions.Center);
                    _cardLabels.Add(t);
                }
            }

            // Ground track bug on the card rim
            _track = PortalMFDGraphics.At("Track", Root, Mid, _center, new Vector2(1f, 1f));
            PortalMFDGraphics.Shape("Bug", _track, Mid, new Vector2(0f, _radius - 3f), 10f, PortalShape.Diamond, TrackColor);

            // Bearing needle to the base: arrowhead and head shaft, tail shaft on the other side
            _needle = PortalMFDGraphics.At("Needle", Root, Mid, _center, new Vector2(1f, 1f));
            PortalMFDGraphics.Shape("Head", _needle, Mid, new Vector2(0f, _radius - 36f), 12f, PortalShape.TriangleFilled, NeedleColor);
            PortalMFDGraphics.Line("HeadShaft", _needle, new Vector2(0f, _radius * 0.3f), new Vector2(0f, _radius - 40f), 2.5f, NeedleColor);
            PortalMFDGraphics.Line("TailShaft", _needle, new Vector2(0f, -_radius + 30f), new Vector2(0f, -_radius * 0.3f), 2.5f, NeedleColor);

            // Fixed ownship cross (reference 't' lines)
            PortalMFDGraphics.Line("Body", Root, _center + new Vector2(0f, -11f), _center + new Vector2(0f, 11f), 2f, CrossColor);
            PortalMFDGraphics.Line("Wing", Root, _center + new Vector2(-9f, 3f), _center + new Vector2(9f, 3f), 2f, CrossColor);
            PortalMFDGraphics.Line("Tail", Root, _center + new Vector2(-4.5f, -8f), _center + new Vector2(4.5f, -8f), 2f, CrossColor);

            PortalMFDGraphics.Shape("Lubber", Root, Mid, _center + new Vector2(0f, _radius + 6f), 9f, PortalShape.TriangleFilled, Color.white)
                .rectTransform.localEulerAngles = new Vector3(0f, 0f, 180f);
            Image hdgBox = PortalMFDGraphics.Box("HdgBox", Root, Mid, new Vector2(0f, H * 0.5f - 12f), new Vector2(52f, 18f), Color.black);
            PortalMFDGraphics.AddImage(PortalMFDGraphics.Stretch("Border", hdgBox.transform, 0f), Color.white, PortalShape.Frame);
            _hdg = Label(hdgBox.transform, Vector2.zero, new Vector2(52f, 18f), string.Empty, 12f, Color.white, TextAlignmentOptions.Center);

            // Readout boxes; the top left one sits below the page name
            float bx = W * 0.5f - 34f;
            TextMeshProUGUI caption;
            _dist = Readout(new Vector2(-bx, H * 0.5f - 48f), "DIST", out caption);
            _brg = Readout(new Vector2(bx, H * 0.5f - 48f), "BRG", out caption);
            _fuel = Readout(new Vector2(-bx, -H * 0.5f + 104f), "FUEL", out caption);
            _fuelBar = PortalMFDGraphics.Box("FuelBar", _fuel.transform.parent, Mid, new Vector2(0f, -11f), new Vector2(52f, 2f), TrackColor);
            _fuelBar.type = Image.Type.Filled;
            _fuelBar.fillMethod = Image.FillMethod.Horizontal;
            _trk = Readout(new Vector2(bx, -H * 0.5f + 104f), "TRK", out caption);
            _spd = Readout(new Vector2(-bx, -H * 0.5f + 70f), "SPD", out _spdCaption);
            _vs = Readout(new Vector2(bx, -H * 0.5f + 70f), "VS", out _vsCaption);
            _alt = Readout(new Vector2(-bx, -H * 0.5f + 36f), "ALT", out _altCaption);
            _ralt = Readout(new Vector2(bx, -H * 0.5f + 36f), "RALT", out _raltCaption);

            _base = Label(Root, new Vector2(0f, -H * 0.5f + 9f), new Vector2(W - 150f, 12f), string.Empty, 9f, NeedleColor, TextAlignmentOptions.Center);
        }

        /// <summary>Black box with a grey frame: caption (with the unit) on top, value below. Returns the value text.</summary>
        private TextMeshProUGUI Readout(Vector2 pos, string captionText, out TextMeshProUGUI caption)
        {
            Image box = PortalMFDGraphics.Box("Readout", Root, Mid, pos, new Vector2(58f, 28f), Color.black);
            PortalMFDGraphics.AddImage(PortalMFDGraphics.Stretch("Border", box.transform, 0f), BoxBorder, PortalShape.Frame);
            caption = Label(box.transform, new Vector2(0f, 7.5f), new Vector2(52f, 9f), captionText, 7f, CaptionColor, TextAlignmentOptions.Left);
            return Label(box.transform, new Vector2(0f, -4f), new Vector2(52f, 14f), string.Empty, 11f, ValueColor, TextAlignmentOptions.Right);
        }

        public override void Tick(float now)
        {
            Aircraft own = Aircraft;
            if (own == null) return;
            float heading = HeadingDeg(own.transform);
            bool imperial = PlayerSettings.unitSystem == PlayerSettings.UnitSystem.Imperial;
            float alt = own.transform.position.y - Datum.LocalSeaY;
            Vector3 vel = own.rb != null ? own.rb.velocity : own.transform.forward * own.speed;

            SetText(_hdg, string.Format("{0:000}", Mathf.Round(heading) % 360f));
            SetText(_spdCaption, imperial ? "SPD KT" : "SPD KM/H");
            SetText(_spd, (own.speed * (imperial ? 1.94384f : 3.6f)).ToString("0"));
            SetText(_vsCaption, imperial ? "VS FT/MIN" : "VS M/S");
            SetText(_vs, imperial ? (Mathf.Round(vel.y * 196.85f / 10f) * 10f).ToString("+0;-0;0") : vel.y.ToString("+0;-0;0"));
            SetText(_altCaption, imperial ? "ALT FT" : "ALT M");
            SetText(_alt, (alt * (imperial ? 3.28084f : 1f)).ToString("0"));
            SetText(_raltCaption, imperial ? "RALT FT" : "RALT M");
            SetText(_ralt, (own.radarAlt * (imperial ? 3.28084f : 1f)).ToString("0"));

            float fuel = own.GetFuelLevel();
            SetText(_fuel, (fuel * 100f).ToString("0") + "%");
            _fuelBar.fillAmount = Mathf.Clamp01(fuel);
            Color fuelColor = fuel < 0.2f ? PortalMFDGraphics.Amber : TrackColor;
            _fuelBar.color = fuelColor;
            _fuel.color = fuel < 0.2f ? PortalMFDGraphics.Amber : ValueColor;

            _hasTrack = vel.x * vel.x + vel.z * vel.z > MinTrackSpeed * MinTrackSpeed;
            if (_hasTrack) _trackDeg = Mathf.Repeat(Mathf.Atan2(vel.x, vel.z) * Mathf.Rad2Deg, 360f);
            SetText(_trk, _hasTrack ? string.Format("{0:000}", Mathf.Round(_trackDeg) % 360f) : "---");

            if (now >= _nextBaseSearch)
            {
                _nextBaseSearch = now + 2f;
                FindNearestBase(own);
            }
            if (_nearest == null)
            {
                SetText(_base, "NO AIRBASE");
                SetText(_dist, "---");
                SetText(_brg, "---");
                return;
            }
            Vector3 d = _nearest.center.position - own.transform.position;
            _baseBearing = Mathf.Repeat(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, 360f);
            float dist = new Vector2(d.x, d.z).magnitude;
            SetText(_base, _nearestName);
            SetText(_dist, UnitConverter.DistanceReading(dist));
            SetText(_brg, string.Format("{0:000}", Mathf.Round(_baseBearing) % 360f));
        }

        public override void FrameTick()
        {
            Aircraft own = Aircraft;
            if (own == null) return;
            float heading = HeadingDeg(own.transform);
            _card.localEulerAngles = new Vector3(0f, 0f, heading);
            for (int i = 0; i < _cardLabels.Count; i++) _cardLabels[i].rectTransform.localEulerAngles = new Vector3(0f, 0f, -heading);
            Show(_needle, _nearest != null);
            if (_nearest != null) _needle.localEulerAngles = new Vector3(0f, 0f, heading - _baseBearing);
            Show(_track, _hasTrack);
            if (_hasTrack) _track.localEulerAngles = new Vector3(0f, 0f, heading - _trackDeg);
        }

        private void FindNearestBase(Aircraft own)
        {
            _nearest = null;
            float best = float.MaxValue;
            Dictionary<string, Airbase> lookup = FactionRegistry.airbaseLookup;
            if (lookup == null) return;
            Vector3 pos = own.transform.position;
            foreach (KeyValuePair<string, Airbase> kv in lookup)
            {
                Airbase ab = kv.Value;
                if (ab == null || ab.center == null || ab.disabled || ab.CurrentHQ == null || ab.CurrentHQ != own.NetworkHQ) continue;
                if (ab.runways == null || ab.runways.Length == 0) continue;
                float d = (ab.center.position - pos).sqrMagnitude;
                if (d >= best) continue;
                best = d;
                _nearest = ab;
                _nearestName = ab.SavedAirbase != null && !string.IsNullOrEmpty(ab.SavedAirbase.DisplayName) ? ab.SavedAirbase.DisplayName.ToUpper() : kv.Key.ToUpper();
            }
        }
    }
}
