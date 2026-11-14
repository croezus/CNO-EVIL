using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CnoEvil.Core;

namespace CnoEvil.Components
{
    /// <summary>
    /// SMS: stores page made of boxes only (no aircraft drawing). One box per pylon: the weapons of a station on one
    /// hardpoint. Gun boxes in the top row, centred; pylons left of the centreline in the left column, right of it in the
    /// right column, centreline pylons in the middle column. Side rows follow the weapon cycle order of the stations, the
    /// left and right pylons of a station by their distance from the centreline, so a symmetric pair shares one row.
    /// Box: short name, then the status (ARMED / SAFE / RLD / EMPTY) and the rounds 'xN'; a low box shows one line. The
    /// boxes of the selected station have a green frame. MASTER ARMED / MASTER SAFE above, the selected weapon and its
    /// rounds below. The page only shows; it changes nothing.
    /// </summary>
    public class PortalPageSms : PortalPage
    {
        private const float Margin = 6f;
        private const float Gap = 4f;
        private const float HeaderSpace = 26f;
        private const float FooterSpace = 20f;
        private const float BoxMaxW = 112f;
        private const float BoxMaxH = 30f;
        private const float BoxMinH = 18f;
        private const float TwoLineMinH = 24f;
        private const float CentreHalfWidth = 0.3f;
        private static readonly Color BoxColor = new Color(0f, 0f, 0f, 0.83f);
        private static readonly Color BorderColor = new Color(0.5f, 0.5f, 0.5f, 1f);
        private static readonly Color SelectedColor = new Color(0.03f, 1f, 0f, 1f);
        private static readonly Color NameColor = new Color(0.74f, 0.74f, 0.74f, 1f);
        private static readonly Color EmptyColor = new Color(0.28f, 0.28f, 0.28f, 1f);
        private static readonly Color MasterArmedColor = new Color(1f, 0.15f, 0.1f, 1f);
        private static readonly Color CountColor = new Color(1f, 0.52f, 0f, 1f);

        private static readonly System.Func<Weapon, Hardpoint> f_hardpoint = FastReflection.CreateFieldGetter<Weapon, Hardpoint>("hardpoint");

        private enum Column { Gun, Left, Centre, Right }

        private class Group
        {
            public WeaponStation Station;
            public readonly List<Weapon> Weapons = new List<Weapon>();
            public float Lateral;
            public Column Column;
            public int Row;
        }

        private class Box
        {
            public RectTransform Root;
            public Image Frame;
            public Image Selected;
            public TextMeshProUGUI Name;
            public TextMeshProUGUI Status;
            public TextMeshProUGUI Count;
            public Group Group;
            public bool TwoLines;
            public int LastCount = -1;
            public int LastState = -1;
            public int LastSelected = -1;
        }

        private enum BoxState { Armed, Safe, Reloading, Empty }

        private readonly List<Group> _groups = new List<Group>();
        private readonly List<Box> _boxes = new List<Box>();
        private readonly Dictionary<Object, Group> _byMount = new Dictionary<Object, Group>();
        private readonly List<Group> _left = new List<Group>();
        private readonly List<Group> _right = new List<Group>();
        private RectTransform _boxRoot;
        private TextMeshProUGUI _master;
        private TextMeshProUGUI _selected;
        private int _stationCount = -1;
        private Aircraft _builtFor;

        protected override void Build()
        {
            Background(Color.black);
            _boxRoot = PortalMFDGraphics.At("Boxes", Root, Mid, Vector2.zero, new Vector2(W, H));
            _master = Label(Root, new Vector2(0f, H * 0.5f - 14f), new Vector2(150f, 14f), string.Empty, 10.5f, MasterArmedColor, TextAlignmentOptions.Center);
            _selected = Label(Root, new Vector2(0f, -H * 0.5f + 10f), new Vector2(W - 2f * Margin, 13f), string.Empty, 9.5f, NameColor, TextAlignmentOptions.Center);
            _selected.overflowMode = TextOverflowModes.Ellipsis;
        }

        public override void OnShow()
        {
            RebuildGroups();
        }

        private void RebuildGroups()
        {
            Aircraft own = Aircraft;
            _builtFor = own;
            List<WeaponStation> stations = own != null ? own.weaponStations : null;
            _stationCount = stations != null ? stations.Count : 0;
            _groups.Clear();

            int gunCount = 0;
            int sideRows = 0;
            int centreRows = 0;
            for (int i = 0; stations != null && i < stations.Count; i++)
            {
                WeaponStation st = stations[i];
                if (!Shown(st)) continue;
                CollectGroups(own, st);
                _left.Clear();
                _right.Clear();
                foreach (Group g in _byMount.Values)
                {
                    if (st.WeaponInfo.gun)
                    {
                        g.Column = Column.Gun;
                        g.Row = gunCount++;
                    }
                    else if (g.Lateral < -CentreHalfWidth) _left.Add(g);
                    else if (g.Lateral > CentreHalfWidth) _right.Add(g);
                    else
                    {
                        g.Column = Column.Centre;
                        g.Row = centreRows++;
                    }
                    _groups.Add(g);
                }
                _left.Sort((a, b) => b.Lateral.CompareTo(a.Lateral));
                _right.Sort((a, b) => a.Lateral.CompareTo(b.Lateral));
                for (int j = 0; j < _left.Count; j++)
                {
                    _left[j].Column = Column.Left;
                    _left[j].Row = sideRows + j;
                }
                for (int j = 0; j < _right.Count; j++)
                {
                    _right[j].Column = Column.Right;
                    _right[j].Row = sideRows + j;
                }
                sideRows += Mathf.Max(_left.Count, _right.Count);
            }

            // Box size: three columns over the page width, rows over the free height
            float boxW = Mathf.Min(BoxMaxW, (W - 2f * Margin - 2f * Gap) / 3f);
            int gunRows = gunCount > 0 ? 1 : 0;
            int rows = gunRows + Mathf.Max(sideRows, centreRows);
            float areaH = H - HeaderSpace - FooterSpace;
            float boxH = rows > 0 ? Mathf.Clamp((areaH - Gap * (rows - 1)) / rows, BoxMinH, BoxMaxH) : BoxMaxH;
            bool twoLines = boxH >= TwoLineMinH;
            float top = H * 0.5f - HeaderSpace;
            float sideX = W * 0.5f - Margin - boxW * 0.5f;
            // Guns side by side, centred; with more than three they share the row width
            float gunW = gunCount > 3 ? (W - 2f * Margin - Gap * (gunCount - 1)) / gunCount : boxW;

            for (int i = 0; i < _groups.Count; i++)
            {
                Group g = _groups[i];
                float x;
                float y;
                float w = boxW;
                if (g.Column == Column.Gun)
                {
                    w = gunW;
                    x = (g.Row - (gunCount - 1) * 0.5f) * (gunW + Gap);
                    y = top - boxH * 0.5f;
                }
                else
                {
                    x = g.Column == Column.Left ? -sideX : (g.Column == Column.Right ? sideX : 0f);
                    y = top - (gunRows + g.Row) * (boxH + Gap) - boxH * 0.5f;
                }
                PlaceBox(i, g, new Vector2(x, y), w, boxH, twoLines);
            }
            for (int i = _groups.Count; i < _boxes.Count; i++)
            {
                _boxes[i].Group = null;
                Show(_boxes[i].Root, false);
            }
        }

        /// <summary>Groups of one station in _byMount: its weapons by hardpoint (a weapon without one is its own group).</summary>
        private void CollectGroups(Aircraft own, WeaponStation st)
        {
            _byMount.Clear();
            List<Weapon> weapons = st.Weapons;
            for (int i = 0; weapons != null && i < weapons.Count; i++)
            {
                Weapon w = weapons[i];
                if (w == null) continue;
                Hardpoint hp = f_hardpoint(w);
                Transform at = hp != null && hp.transform != null ? hp.transform : w.transform;
                Object key = hp != null ? (Object)hp.transform : w;
                if (key == null) key = w;
                Group g;
                if (!_byMount.TryGetValue(key, out g))
                {
                    g = new Group();
                    g.Station = st;
                    g.Lateral = own.transform.InverseTransformPoint(at.position).x;
                    _byMount[key] = g;
                }
                g.Weapons.Add(w);
            }
            if (_byMount.Count == 0)
            {
                // A station without weapon objects: one box with the station rounds
                Group g = new Group();
                g.Station = st;
                _byMount[own] = g;
            }
        }

        private void PlaceBox(int index, Group g, Vector2 pos, float width, float height, bool twoLines)
        {
            while (_boxes.Count <= index) _boxes.Add(CreateBox());
            Box b = _boxes[index];
            b.Group = g;
            b.TwoLines = twoLines;
            b.LastCount = -1;
            b.LastState = -1;
            b.LastSelected = -1;
            b.Root.anchoredPosition = pos;
            b.Root.sizeDelta = new Vector2(width, height);
            Show(b.Root, true);

            WeaponInfo info = g.Station.WeaponInfo;
            b.Name.text = !string.IsNullOrEmpty(info.shortName) ? info.shortName : info.weaponName;
            float left = -width * 0.5f + 5f;
            float right = width * 0.5f - 5f;
            if (twoLines)
            {
                float nameY = height * 0.5f - 8f;
                float lowY = -height * 0.5f + 8f;
                Place(b.Name, new Vector2(0f, nameY), new Vector2(width - 10f, 12f), 10f);
                Show(b.Status, true);
                Place(b.Status, new Vector2(left + 25f, lowY), new Vector2(50f, 12f), 9.5f);
                Place(b.Count, new Vector2(right - 22f, lowY), new Vector2(44f, 12f), 10f);
            }
            else
            {
                Place(b.Name, new Vector2(-18f, 0f), new Vector2(width - 46f, 11f), 8.5f);
                Show(b.Status, false);
                Place(b.Count, new Vector2(right - 16f, 0f), new Vector2(32f, 11f), 9f);
            }
        }

        private static void Place(TextMeshProUGUI t, Vector2 pos, Vector2 size, float fontSize)
        {
            t.rectTransform.anchoredPosition = pos;
            t.rectTransform.sizeDelta = size;
            t.fontSize = fontSize;
        }

        private Box CreateBox()
        {
            Box b = new Box();
            b.Root = PortalMFDGraphics.At("Pylon", _boxRoot, Mid, Vector2.zero, new Vector2(10f, 10f));
            PortalMFDGraphics.AddImage(b.Root, BoxColor, null);
            b.Frame = PortalMFDGraphics.AddImage(PortalMFDGraphics.Stretch("Frame", b.Root, 0f), BorderColor, PortalShape.Frame);
            b.Selected = PortalMFDGraphics.AddImage(PortalMFDGraphics.Stretch("Selected", b.Root, 1f), SelectedColor, PortalShape.Frame);
            b.Name = Label(b.Root, Vector2.zero, new Vector2(10f, 12f), string.Empty, 10f, NameColor, TextAlignmentOptions.Left);
            b.Name.overflowMode = TextOverflowModes.Ellipsis;
            b.Status = Label(b.Root, Vector2.zero, new Vector2(10f, 12f), string.Empty, 9.5f, SelectedColor, TextAlignmentOptions.Left);
            b.Count = Label(b.Root, Vector2.zero, new Vector2(10f, 12f), string.Empty, 10f, Color.white, TextAlignmentOptions.Right);
            return b;
        }

        private static bool Shown(WeaponStation st)
        {
            return st != null && st.WeaponInfo != null && !st.WeaponInfo.hideInDisplay;
        }

        private static int GroupCount(Group g)
        {
            if (g.Weapons.Count == 0) return g.Station.Ammo;
            int n = 0;
            for (int i = 0; i < g.Weapons.Count; i++)
            {
                Weapon w = g.Weapons[i];
                if (w != null) n += w.GetAmmoTotal();
            }
            return n;
        }

        public override void Tick(float now)
        {
            Aircraft own = Aircraft;
            if (own == null) return;
            if (own != _builtFor || (own.weaponStations != null && own.weaponStations.Count != _stationCount)) RebuildGroups();
            WeaponStation current = own.weaponManager != null ? own.weaponManager.currentWeaponStation : null;

            for (int i = 0; i < _boxes.Count; i++)
            {
                Box b = _boxes[i];
                if (b.Group != null) UpdateBox(b, own, b.Group.Station == current);
            }

            bool safe = current == null || current.SafetyIsOn(own);
            SetText(_master, safe ? "MASTER SAFE" : "MASTER ARMED");
            _master.color = safe ? SelectedColor : MasterArmedColor;
            if (current != null && current.WeaponInfo != null)
            {
                SetText(_selected, current.WeaponInfo.weaponName + "   " +
                    (current.Reloading ? "RLD" : current.Ammo + " / " + current.FullAmmo));
            }
            else SetText(_selected, "NO WEAPON SELECTED");
        }

        private void UpdateBox(Box b, Aircraft own, bool selected)
        {
            WeaponStation st = b.Group.Station;
            int sel = selected ? 1 : 0;
            if (sel != b.LastSelected)
            {
                b.LastSelected = sel;
                Show(b.Selected, selected);
                b.Frame.color = selected ? SelectedColor : BorderColor;
            }

            int count = GroupCount(b.Group);
            BoxState state;
            if (st.Reloading) state = BoxState.Reloading;
            else if (count <= 0) state = BoxState.Empty;
            else if (st.SafetyIsOn(own)) state = BoxState.Safe;
            else state = BoxState.Armed;
            int stateKey = (int)state * 2 + sel;
            if (count == b.LastCount && stateKey == b.LastState) return;
            b.LastCount = count;
            b.LastState = stateKey;

            Color stateColor;
            string stateText;
            switch (state)
            {
                case BoxState.Reloading: stateText = "RLD"; stateColor = PortalMFDGraphics.Amber; break;
                case BoxState.Empty: stateText = "EMPTY"; stateColor = EmptyColor; break;
                case BoxState.Safe: stateText = "SAFE"; stateColor = PortalMFDGraphics.Amber; break;
                default: stateText = "ARMED"; stateColor = SelectedColor; break;
            }
            b.Status.text = stateText;
            b.Status.color = stateColor;
            b.Count.text = "x" + count;
            // One line: the count carries the status colour
            b.Count.color = b.TwoLines ? (count > 0 ? Color.white : EmptyColor) : (state == BoxState.Armed ? CountColor : stateColor);
            b.Name.color = count > 0 || state == BoxState.Reloading ? (selected ? Color.white : NameColor) : EmptyColor;
        }
    }

    /// <summary>
    /// EOTS: the vanilla target camera and landing camera screens moved into the page (vanilla turns them on and off
    /// and its target info stays). CAM OFF while both are off.
    /// </summary>
    public class PortalPageEots : PortalPage
    {
        private TextMeshProUGUI _off;
        private TextMeshProUGUI _caption;

        protected override bool HostsVanilla { get { return true; } }

        protected override void Build()
        {
            Background(Color.black);
            Vector2 center = new Vector2(0f, -10f);
            if (Host.TargetCamDisplay != null) Host.MoveVanilla(Host.TargetCamDisplay.transform as RectTransform, Root, center, CamBox());
            if (Host.LandingCamDisplay != null) Host.MoveVanilla(Host.LandingCamDisplay.transform as RectTransform, Root, center, CamBox());
            _off = Label(Root, Vector2.zero, new Vector2(160f, 16f), "CAM OFF", 13f, new Color(1f, 0.6f, 0f, 1f), TextAlignmentOptions.Center);
            _caption = Label(Root, new Vector2(0f, H * 0.5f - 14f), new Vector2(100f, 12f), "EO TARGETING", 9f, PortalMFDGraphics.LabelGrey, TextAlignmentOptions.Center);
        }

        private Vector2 CamBox()
        {
            return new Vector2(W - 6f, H - 34f);
        }

        /// <summary>
        /// The vanilla camera screens can be in one page only: the page takes the size of the new slot itself and
        /// fits the camera screens again (ADR-65, ADR-68).
        /// </summary>
        public override bool ResizeInPlace(float width, float height)
        {
            Width = width;
            Height = height;
            Root.sizeDelta = new Vector2(W, H);
            if (_caption != null) _caption.rectTransform.anchoredPosition = new Vector2(0f, H * 0.5f - 14f);
            Host.RefitVanilla(Root, CamBox());
            return true;
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
            _radius = ScopeRadiusFor(32f);
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
