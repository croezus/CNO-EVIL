using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Components
{
    /// <summary>
    /// Attitude display in place of the vanilla BasicFlightInstruments screen: sky and ground, pitch ladder, fixed
    /// aircraft symbol, heading strip, speed box and radar altitude box. It is a child of the vanilla panel, so a
    /// PortalMFD cover that hides the panel hides it too. Vanilla keeps running; its graphics are only disabled.
    /// Purely visual: it reads the own aircraft, the same on host and client.
    /// </summary>
    public class CockpitAttitudeDisplay : MonoBehaviour
    {
        private const float TextRate_s = 0.05f;
        private const float MinWidth = 130f;
        private const float MinHeight = 120f;
        private const float VisiblePitch_deg = 90f;
        private const float StripHeight = 18f;
        private const float VisibleHeading_deg = 60f;
        private const int MaxHeadingTicks = 14;
        private const int LadderLabelCount = 12;

        private static readonly Vector2 Mid = new Vector2(0.5f, 0.5f);
        private static readonly Color Sky = new Color(0.10f, 0.22f, 0.72f, 1f);
        private static readonly Color Ground = new Color(0.42f, 0.28f, 0.14f, 1f);
        private static readonly Color Strip = new Color(0f, 0f, 0f, 0.5f);

        private Aircraft _aircraft;
        private TextMeshProUGUI _vanillaHeading;
        private TextMeshProUGUI _vanillaAirspeed;
        private TextMeshProUGUI _vanillaAltitude;
        private TextMeshProUGUI _vanillaClimb;
        private Image _vanillaHorizon;
        private Image _vanillaVsi;
        private Image _vanillaAoA;

        private Graphic[] _vanillaGraphics;
        private bool[] _vanillaEnabled;
        private bool _ready;
        private bool _shown;

        private RectTransform _panel;
        private RectTransform _attitude;
        private CockpitDatalinkPage _datalinkPage;
        private CnoEvilConfig.CockpitPage _page;
        private bool _pageApplied;
        private TMP_FontAsset _font;
        private RectTransform _rollPivot;
        private RectTransform _pitchShift;
        private HudDashGraphic _headingTicks;
        private TextMeshProUGUI _headingText;
        private TextMeshProUGUI _speedText;
        private TextMeshProUGUI _altitudeText;
        private TextMeshProUGUI _speedUnit;
        private TextMeshProUGUI _altitudeUnit;
        private float _width;
        private float _height;
        private float _unitsPerDeg;
        private readonly TextMeshProUGUI[] _ladderLabels = new TextMeshProUGUI[LadderLabelCount];
        private readonly Vector2[] _ladderLabelPos = new Vector2[LadderLabelCount];
        private int _ladderLabelCount;

        private float _nextText;
        private int _lastHeading = int.MinValue;
        private int _lastSpeed = int.MinValue;
        private int _lastAltitude = int.MinValue;
        private int _lastMetric = -1;

        public void Initialize(Aircraft aircraft, TextMeshProUGUI heading, TextMeshProUGUI airspeed, TextMeshProUGUI altitude,
            TextMeshProUGUI climb, Image horizon, Image vsi, Image aoa)
        {
            _aircraft = aircraft;
            _vanillaHeading = heading;
            _vanillaAirspeed = airspeed;
            _vanillaAltitude = altitude;
            _vanillaClimb = climb;
            _vanillaHorizon = horizon;
            _vanillaVsi = vsi;
            _vanillaAoA = aoa;

            _ready = heading != null && airspeed != null && altitude != null && climb != null && horizon != null && vsi != null && aoa != null &&
                     transform is RectTransform;
            if (!_ready)
            {
                if (CnoEvilPlugin.Log != null)
                    CnoEvilPlugin.Log.LogWarning(string.Format("CockpitAttitudeDisplay: {0} has an incomplete flight instrument panel, vanilla stays.", name));
                return;
            }
            // A second instrument component that uses the graphics of another panel (Su-30 GlassHud): the display would
            // disable graphics outside its panel and cover other screens
            Component[] parts = { heading, airspeed, altitude, climb, horizon, vsi, aoa };
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].transform.IsChildOf(transform)) continue;
                _ready = false;
                if (CnoEvilPlugin.Log != null)
                    CnoEvilPlugin.Log.LogInfo(string.Format("CockpitAttitudeDisplay: {0} uses graphics outside its panel ({1}), vanilla stays.", name, parts[i].name));
                return;
            }
            if (IsVanillaCockpit())
            {
                _ready = false;
                if (CnoEvilPlugin.Log != null)
                    CnoEvilPlugin.Log.LogInfo(string.Format("CockpitAttitudeDisplay: {0} of a cockpit that keeps its vanilla instruments, vanilla stays.", name));
            }
        }

        // TacScreen prefabs whose flight instrument screens stay vanilla (user choice): the Su-30 shows them on round dials
        private static readonly string[] s_vanillaPrefabs = { "tacScreen_Su30" };

        private bool IsVanillaCockpit()
        {
            TacScreen tac = GetComponentInParent<TacScreen>(true);
            if (tac == null) return false;
            for (int i = 0; i < s_vanillaPrefabs.Length; i++)
            {
                if (tac.name.IndexOf(s_vanillaPrefabs[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        internal TMP_FontAsset Font { get { return _font; } }

        private static readonly TapKey s_pageKey = new TapKey();

        /// <summary>
        /// Page key, once per frame from CnoEvilPlugin.Update, so that two panels in one cockpit do not flip it twice.
        /// The page is saved in the config like MinimapZoom; each display follows it.
        /// </summary>
        public static void HandlePageKey()
        {
            try
            {
                if (!s_pageKey.Pressed(CnoEvilConfig.CockpitDisplayPageKey.Value)) return;
                if (!CnoEvilConfig.CockpitAttitudeDisplay.Value) return;
                if (CombatHUD.i == null || CombatHUD.i.aircraft == null) return;
                CnoEvilConfig.CockpitDisplayPage.Value = CnoEvilConfig.CockpitDisplayPage.Value == CnoEvilConfig.CockpitPage.Attitude
                    ? CnoEvilConfig.CockpitPage.Datalink : CnoEvilConfig.CockpitPage.Attitude;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CockpitAttitudeDisplay.HandlePageKey", ex);
            }
        }

        public static void ClearAll()
        {
            s_pageKey.Reset();
        }

        private static bool Wanted()
        {
            return CnoEvilConfig.IsModActive && CnoEvilConfig.CockpitAttitudeDisplay.Value;
        }

        private void Update()
        {
            try
            {
                if (!_ready) return;
                bool wanted = Wanted();
                if (wanted != _shown) SetShown(wanted);
                if (_shown && (!_pageApplied || CnoEvilConfig.CockpitDisplayPage.Value != _page)) ApplyPage(CnoEvilConfig.CockpitDisplayPage.Value);
            }
            catch (Exception ex)
            {
                // A half-built panel must not cover vanilla
                _ready = false;
                if (_panel != null) _panel.gameObject.SetActive(false);
                PatchErrorLog.Once("CockpitAttitudeDisplay.Update", ex);
            }
        }

        private void ApplyPage(CnoEvilConfig.CockpitPage page)
        {
            bool datalink = page == CnoEvilConfig.CockpitPage.Datalink;
            if (datalink && _datalinkPage == null && _aircraft != null)
            {
                _datalinkPage = new CockpitDatalinkPage(this, _aircraft, _panel, _width, _height);
            }
            _page = page;
            _pageApplied = true;
            _attitude.gameObject.SetActive(!datalink);
            if (_datalinkPage != null) _datalinkPage.SetVisible(datalink);
        }

        private void SetShown(bool on)
        {
            if (on && _panel == null)
            {
                CollectVanillaGraphics();
                Build();
            }
            _shown = on;
            if (_panel != null) _panel.gameObject.SetActive(on);
            if (_vanillaGraphics == null) return;
            for (int i = 0; i < _vanillaGraphics.Length; i++)
            {
                Graphic g = _vanillaGraphics[i];
                if (g != null) g.enabled = on ? false : _vanillaEnabled[i];
            }
        }

        /// <summary>
        /// The graphics that the new display replaces: the four readouts, the horizon and the AoA and VSI ladders with
        /// their labels. Other children of the panel (extra readouts of modded cockpits) stay.
        /// </summary>
        private void CollectVanillaGraphics()
        {
            Transform[] parts =
            {
                _vanillaHeading.transform, _vanillaAirspeed.transform, _vanillaAltitude.transform, _vanillaClimb.transform,
                PanelChild(_vanillaHorizon.transform), LadderOf(_vanillaVsi.transform), LadderOf(_vanillaAoA.transform)
            };
            System.Collections.Generic.List<Graphic> all = new System.Collections.Generic.List<Graphic>();
            for (int i = 0; i < parts.Length; i++)
            {
                Graphic[] found = parts[i].GetComponentsInChildren<Graphic>(true);
                for (int k = 0; k < found.Length; k++)
                {
                    if (!all.Contains(found[k])) all.Add(found[k]);
                }
            }
            _vanillaGraphics = all.ToArray();
            _vanillaEnabled = new bool[_vanillaGraphics.Length];
            for (int i = 0; i < _vanillaGraphics.Length; i++) _vanillaEnabled[i] = _vanillaGraphics[i].enabled;
        }

        private Transform PanelChild(Transform node)
        {
            Transform start = node;
            while (node.parent != null && node.parent != transform) node = node.parent;
            // Not below this panel: only the node itself, never the top of the hierarchy
            return node.parent == transform ? node : start;
        }

        private Transform LadderOf(Transform indicator)
        {
            return indicator.parent != null && indicator.parent != transform ? indicator.parent : indicator;
        }

        /// <summary>
        /// The size comes from the place of the vanilla readouts, not from the panel rect: on some cockpits the rect
        /// goes onto the next screen (RAH-72).
        /// </summary>
        private void MeasureContent(RectTransform root, out float centerX, out float centerY)
        {
            float top = float.MinValue, bottom = float.MaxValue;
            AddBounds(root, _vanillaHeading.rectTransform, ref top, ref bottom);
            AddBounds(root, _vanillaAirspeed.rectTransform, ref top, ref bottom);
            AddBounds(root, _vanillaAltitude.rectTransform, ref top, ref bottom);
            AddBounds(root, _vanillaClimb.rectTransform, ref top, ref bottom);

            _height = Mathf.Max(top - bottom, MinHeight);
            _width = Mathf.Clamp(root.rect.width, MinWidth, Mathf.Max(_height, MinWidth));
            centerY = (top + bottom) * 0.5f;
            RectTransform heading = _vanillaHeading.rectTransform;
            centerX = root.InverseTransformPoint(heading.TransformPoint(heading.rect.center)).x;
        }

        private static void AddBounds(RectTransform root, RectTransform text, ref float top, ref float bottom)
        {
            Rect r = text.rect;
            float yMax = root.InverseTransformPoint(text.TransformPoint(new Vector3(r.center.x, r.yMax, 0f))).y;
            float yMin = root.InverseTransformPoint(text.TransformPoint(new Vector3(r.center.x, r.yMin, 0f))).y;
            top = Mathf.Max(top, Mathf.Max(yMax, yMin));
            bottom = Mathf.Min(bottom, Mathf.Min(yMax, yMin));
        }

        private void Build()
        {
            RectTransform root = (RectTransform)transform;
            float centerX, centerY;
            MeasureContent(root, out centerX, out centerY);
            TMP_FontAsset font = HudFont.Of(_vanillaHeading);
            _font = font;
            _unitsPerDeg = _height / VisiblePitch_deg;

            _panel = PortalMFDGraphics.At("CnoEvil_AttitudeDisplay", root, Mid, Vector2.zero, new Vector2(_width, _height));
            _panel.localPosition = new Vector3(centerX, centerY, 0f);
            // A stencil mask, as on the turned portal slots: a RectMask2D clips in canvas space and fails on a turned panel.
            // The mask graphic is opaque, a transparent mesh is culled and would mask everything.
            PortalMFDGraphics.AddImage(_panel, Color.black, null);
            _panel.gameObject.AddComponent<Mask>().showMaskGraphic = false;

            _attitude = PortalMFDGraphics.At("Attitude", _panel, Mid, Vector2.zero, new Vector2(_width, _height));
            BuildHorizon(font);
            BuildAircraftSymbol();
            BuildHeadingStrip(font);
            BuildValueBoxes(font);
        }

        private void BuildHorizon(TMP_FontAsset font)
        {
            _rollPivot = PortalMFDGraphics.At("Roll", _attitude, Mid, Vector2.zero, Vector2.zero);
            _pitchShift = PortalMFDGraphics.At("Pitch", _rollPivot, Mid, Vector2.zero, Vector2.zero);

            // The fill reaches past the panel corners at any roll, also with the horizon 90 deg away
            float diagonal = Mathf.Sqrt(_width * _width + _height * _height);
            float fillHeight = 90f * _unitsPerDeg + diagonal;
            Vector2 fillSize = new Vector2(diagonal * 2f, fillHeight);
            PortalMFDGraphics.Box("Sky", _pitchShift, Mid, new Vector2(0f, fillHeight * 0.5f), fillSize, Sky);
            PortalMFDGraphics.Box("Ground", _pitchShift, Mid, new Vector2(0f, -fillHeight * 0.5f), fillSize, Ground);

            float longHalf = _width * 0.18f;
            float shortHalf = _width * 0.09f;
            Vector2[] points = new Vector2[2 * 37];
            float[] widths = new float[37];
            int count = 0;
            for (int deg = -90; deg <= 90; deg += 5)
            {
                if (deg == 0) continue;
                float half = deg % 10 == 0 ? longHalf : shortHalf;
                float y = deg * _unitsPerDeg;
                points[count * 2] = new Vector2(-half, y);
                points[count * 2 + 1] = new Vector2(half, y);
                widths[count] = 1f;
                count++;
            }
            points[count * 2] = new Vector2(-diagonal, 0f);
            points[count * 2 + 1] = new Vector2(diagonal, 0f);
            widths[count] = 1.5f;
            count++;

            HudDashGraphic ladder = NewLines("Ladder", _pitchShift, Color.white, 1f);
            ladder.Points = points;
            ladder.Widths = widths;
            ladder.Count = count;
            ladder.SetVerticesDirty();

            float fontSize = _height * 0.052f;
            float labelX = longHalf + fontSize * 1.1f;
            Vector2 labelSize = new Vector2(fontSize * 2f, fontSize * 1.4f);
            for (int deg = -90; deg <= 90; deg += 30)
            {
                if (deg == 0) continue;
                string label = Mathf.Abs(deg).ToString();
                float y = deg * _unitsPerDeg;
                AddLadderLabel("LadderL" + deg, new Vector2(-labelX, y), labelSize, label, fontSize, font);
                AddLadderLabel("LadderR" + deg, new Vector2(labelX, y), labelSize, label, fontSize, font);
            }
        }

        private void AddLadderLabel(string labelName, Vector2 pos, Vector2 size, string label, float fontSize, TMP_FontAsset font)
        {
            _ladderLabels[_ladderLabelCount] = NewText(labelName, _pitchShift, pos, size, label, fontSize, font);
            _ladderLabelPos[_ladderLabelCount] = pos;
            _ladderLabelCount++;
        }

        /// <summary>
        /// Text with the font and material of the vanilla readouts. It is not maskable: text under the stencil mask
        /// did not draw in the cockpit. The readouts are inside the panel; ShowLadderLabels clips the ladder labels.
        /// </summary>
        internal TextMeshProUGUI NewText(string textName, Transform parent, Vector2 pos, Vector2 size, string text, float fontSize, TMP_FontAsset font)
        {
            TextMeshProUGUI t = PortalMFDGraphics.Text(textName, parent, Mid, pos, size, text, fontSize, Color.white, TextAlignmentOptions.Center, font);
            t.maskable = false;
            Material material = _vanillaHeading.fontSharedMaterial;
            if (material != null && font == _vanillaHeading.font) t.fontSharedMaterial = material;
            return t;
        }

        /// <summary>A ladder label shows only while it is fully inside the panel, below the heading strip.</summary>
        private void ShowLadderLabels(float roll, float pitch)
        {
            float sin = Mathf.Sin(roll * Mathf.Deg2Rad);
            float cos = Mathf.Cos(roll * Mathf.Deg2Rad);
            float shift = pitch * _unitsPerDeg;
            float margin = _height * 0.05f;
            float maxX = _width * 0.5f - margin;
            float maxY = _height * 0.5f - StripHeight - margin;
            float minY = -_height * 0.5f + margin;
            for (int i = 0; i < _ladderLabelCount; i++)
            {
                Vector2 p = _ladderLabelPos[i];
                float y = p.y - shift;
                float panelX = p.x * cos - y * sin;
                float panelY = p.x * sin + y * cos;
                bool visible = Mathf.Abs(panelX) < maxX && panelY < maxY && panelY > minY;
                if (_ladderLabels[i].enabled != visible) _ladderLabels[i].enabled = visible;
            }
        }

        private void BuildAircraftSymbol()
        {
            float inner = _width * 0.07f;
            float outer = _width * 0.19f;
            float drop = _height * 0.035f;
            Vector2[] points =
            {
                new Vector2(-outer, 0f), new Vector2(-inner, 0f),
                new Vector2(-inner, 0f), new Vector2(-inner, -drop),
                new Vector2(outer, 0f), new Vector2(inner, 0f),
                new Vector2(inner, 0f), new Vector2(inner, -drop),
                new Vector2(-1.5f, 0f), new Vector2(1.5f, 0f)
            };

            HudDashGraphic outline = NewLines("SymbolOutline", _attitude, Color.black, 4.5f);
            outline.Points = points;
            outline.Count = 5;
            outline.SetVerticesDirty();
            HudDashGraphic symbol = NewLines("Symbol", _attitude, Color.white, 2.5f);
            symbol.Points = points;
            symbol.Count = 5;
            symbol.SetVerticesDirty();
        }

        private void BuildHeadingStrip(TMP_FontAsset font)
        {
            Vector2 stripPos = new Vector2(0f, (_height - StripHeight) * 0.5f);
            PortalMFDGraphics.Box("HeadingStrip", _attitude, Mid, stripPos, new Vector2(_width, StripHeight), Strip);
            _headingTicks = NewLines("HeadingTicks", _attitude, Color.white, 1f);
            _headingTicks.Points = new Vector2[MaxHeadingTicks * 2];
            _headingText = ValueBox("Heading", stripPos, new Vector2(_width * 0.26f, StripHeight - 2f), font);
        }

        private void BuildValueBoxes(TMP_FontAsset font)
        {
            float boxWidth = _width * 0.27f;
            float boxHeight = _height * 0.1f;
            float x = (_width - boxWidth) * 0.5f - 2f;
            _speedText = ValueBox("Speed", new Vector2(-x, 0f), new Vector2(boxWidth, boxHeight), font);
            _altitudeText = ValueBox("Altitude", new Vector2(x, 0f), new Vector2(boxWidth, boxHeight), font);

            float unitSize = _height * 0.042f;
            Vector2 unitBox = new Vector2(boxWidth, unitSize * 1.4f);
            float unitY = -(boxHeight + unitBox.y) * 0.5f;
            _speedUnit = NewText("SpeedUnit", _attitude, new Vector2(-x, unitY), unitBox, string.Empty, unitSize, font);
            _altitudeUnit = NewText("AltitudeUnit", _attitude, new Vector2(x, unitY), unitBox, string.Empty, unitSize, font);
        }

        private TextMeshProUGUI ValueBox(string boxName, Vector2 pos, Vector2 size, TMP_FontAsset font)
        {
            PortalMFDGraphics.Box(boxName + "Box", _attitude, Mid, pos, size, Color.black);
            PortalMFDGraphics.AddImage(PortalMFDGraphics.At(boxName + "Frame", _attitude, Mid, pos, size), Color.white, PortalShape.Frame);
            return NewText(boxName + "Text", _attitude, pos, size, string.Empty, size.y * 0.8f, font);
        }

        internal HudDashGraphic NewLines(string lineName, Transform parent, Color lineColor, float thickness)
        {
            RectTransform rt = PortalMFDGraphics.At(lineName, parent, Mid, Vector2.zero, Vector2.zero);
            HudDashGraphic lines = rt.gameObject.AddComponent<HudDashGraphic>();
            lines.raycastTarget = false;
            lines.color = lineColor;
            lines.Thickness = thickness;
            return lines;
        }

        private void LateUpdate()
        {
            if (!_shown || _panel == null || _aircraft == null || !_pageApplied) return;
            try
            {
                if (_page == CnoEvilConfig.CockpitPage.Datalink)
                {
                    if (_datalinkPage != null) _datalinkPage.Tick();
                    return;
                }

                Transform t = _aircraft.transform;
                // From the axes, not eulerAngles: no jump near the vertical
                float pitch = Mathf.Asin(Mathf.Clamp(t.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
                float roll = Mathf.Atan2(-t.right.y, t.up.y) * Mathf.Rad2Deg;
                _rollPivot.localRotation = Quaternion.Euler(0f, 0f, roll);
                _pitchShift.localPosition = new Vector3(0f, -pitch * _unitsPerDeg, 0f);
                ShowLadderLabels(roll, pitch);

                float now = Time.unscaledTime;
                if (now < _nextText) return;
                _nextText = now + TextRate_s;
                RefreshReadouts(t.eulerAngles.y);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CockpitAttitudeDisplay.LateUpdate", ex);
            }
        }

        /// <summary>Text is rebuilt only when the shown number changes. Unit factors as in the vanilla UnitConverter.</summary>
        private void RefreshReadouts(float heading)
        {
            bool metric = PlayerSettings.unitSystem == 0;
            int metricFlag = metric ? 1 : 0;
            if (metricFlag != _lastMetric)
            {
                _lastMetric = metricFlag;
                _speedUnit.text = metric ? "km/h" : "kt";
                _altitudeUnit.text = metric ? "m" : "ft";
            }

            int speed = Mathf.RoundToInt(_aircraft.speed * (metric ? 3.6f : 1.94384f));
            if (speed != _lastSpeed)
            {
                _lastSpeed = speed;
                _speedText.text = speed.ToString();
            }

            int altitude = Mathf.RoundToInt(_aircraft.radarAlt * (metric ? 1f : 3.28084f));
            if (altitude != _lastAltitude)
            {
                _lastAltitude = altitude;
                _altitudeText.text = altitude.ToString();
            }

            int shownHeading = Mathf.RoundToInt(heading) % 360;
            if (shownHeading != _lastHeading)
            {
                _lastHeading = shownHeading;
                _headingText.text = shownHeading.ToString("000");
            }
            RefreshHeadingTicks(heading);
        }

        private void RefreshHeadingTicks(float heading)
        {
            float unitsPerDeg = _width / VisibleHeading_deg;
            float halfSpan = VisibleHeading_deg * 0.5f;
            float top = _height * 0.5f - 2f;
            Vector2[] points = _headingTicks.Points;
            int count = 0;
            int first = Mathf.CeilToInt((heading - halfSpan) / 5f) * 5;
            for (int deg = first; deg <= heading + halfSpan && count < MaxHeadingTicks; deg += 5)
            {
                float x = (deg - heading) * unitsPerDeg;
                float length = deg % 10 == 0 ? 7f : 4f;
                points[count * 2] = new Vector2(x, top);
                points[count * 2 + 1] = new Vector2(x, top - length);
                count++;
            }
            _headingTicks.Count = count;
            _headingTicks.SetVerticesDirty();
        }
    }
}
