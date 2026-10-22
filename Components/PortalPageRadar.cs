using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CnoEvil.Config;
using CnoEvil.Core;

namespace CnoEvil.Components
{
    /// <summary>
    /// RADAR page: B-scope (azimuth across, range up) like the reference radar page. Contacts are what the ownship
    /// sensors hold: radar (and optical) detections at their live position, allied datalink reports dead-reckoned
    /// from the last report. Hostile symbol: orange triangle with a stem along the target track; friendly: blue ring.
    /// Numbers next to a symbol: altitude in thousands (ft for 'Aviation', km for 'Metric'). The selected target gets
    /// a lock line and the lock box (bearing, range, altitude, closure). Like the reference page, the scan line becomes
    /// a solid line on the azimuth of the selected target, and jamming of the ownship (Unit.onJam) puts short strobes
    /// at random ranges on the jammer azimuth, 4 tries per tick weighted by the jam amount, each shown for 1 s.
    /// </summary>
    public class PortalPageRadar : PortalPage
    {
        private const int MaxStrobes = 32;
        private const int StrobeTries = 4;
        private const float StrobeLife = 1f;
        private const float JamHold = 0.5f;
        private static readonly Color ScanColor = new Color(0.25f, 0.57f, 0f, 0.15f);
        private static readonly Color HardLockColor = new Color(0.04f, 0.48f, 0f, 1f);
        private static readonly Color StrobeColor = new Color(0.55f, 1f, 0.3f, 0.9f);

        private class Jammer
        {
            public Unit Unit;
            public float Amount;
            public float LastTime;
        }

        private float ScopeW = 250f;
        private float ScopeH = 230f;
        private const float HalfAzimuth = 60f;
        private const int MaxContacts = 40;
        private const float Persistence = 3.5f;

        private static readonly float[] s_ranges = { 10f, 20f, 40f, 80f };
        private static readonly Color EnemyColor = new Color(1f, 0.39f, 0f, 1f);
        private static readonly Color FriendColor = new Color(0f, 0.67f, 1f, 1f);
        private static readonly Color GridColor = new Color(0.38f, 0.56f, 0f, 0.25f);

        private class Contact
        {
            public Unit Unit;
            public float LastSeen;
            public float LastOwnSensor = -1000f;
            public Vector3 SensorPos;
            public Vector3 SensorVel;
            public DatalinkThreatEntry Report;
        }

        private class Symbol
        {
            public RectTransform Root;
            public Image Icon;
            public TextMeshProUGUI Alt;
        }

        private RectTransform _scope;
        private RectTransform _scan;
        private Image _lockLine;
        private GameObject _lockBox;
        private TextMeshProUGUI _lockValues;
        private TextMeshProUGUI _lockName;
        private TextMeshProUGUI _far;
        private TextMeshProUGUI _half;
        private TextMeshProUGUI _status;
        private float _rangeKm = 40f;
        private bool _rangeInitialized;
        private bool _scanning;
        private bool _scanLocked;
        private float _scanLockX;
        private readonly Dictionary<Unit, Contact> _contacts = new Dictionary<Unit, Contact>();
        private readonly List<Unit> _stale = new List<Unit>();
        private readonly List<Symbol> _symbols = new List<Symbol>();
        private TargetDetector _optical;
        private Image _scanImage;
        private readonly List<Jammer> _jammers = new List<Jammer>();
        private readonly Image[] _strobes = new Image[MaxStrobes];
        private readonly float[] _strobeUntil = new float[MaxStrobes];
        private int _nextStrobe;
        private Aircraft _jamSubscribed;
        private System.Action<Unit.JamEventArgs> _onJam;

        protected override void Build()
        {
            Background(Color.black);
            ScopeW = W - 90f;
            ScopeH = H - 66f;
            _scope = PortalMFDGraphics.At("Scope", Root, Mid, new Vector2(4f, 6f), new Vector2(ScopeW, ScopeH));
            PortalMFDGraphics.AddImage(PortalMFDGraphics.Stretch("Border", _scope, 0f), new Color(0.31f, 0.54f, 0f, 1f), PortalShape.Frame);

            for (int i = 1; i <= 3; i++)
            {
                float fx = (i / 4f - 0.5f) * ScopeW;
                float fy = (i / 4f - 0.5f) * ScopeH;
                PortalMFDGraphics.Line("GridV", _scope, new Vector2(fx, -ScopeH * 0.5f), new Vector2(fx, ScopeH * 0.5f), 0.75f, GridColor);
                PortalMFDGraphics.Line("GridH", _scope, new Vector2(-ScopeW * 0.5f, fy), new Vector2(ScopeW * 0.5f, fy), 0.75f, GridColor);
            }

            _scanImage = PortalMFDGraphics.Line("Scan", _scope, new Vector2(0f, -ScopeH * 0.5f), new Vector2(0f, ScopeH * 0.5f), 2f, ScanColor);
            _scan = _scanImage.rectTransform;
            for (int i = 0; i < MaxStrobes; i++)
            {
                _strobes[i] = PortalMFDGraphics.Box("Strobe", _scope, Mid, Vector2.zero, new Vector2(2f, 5f), StrobeColor);
                _strobes[i].gameObject.SetActive(false);
            }
            _lockLine = PortalMFDGraphics.Line("Lock", _scope, Vector2.zero, Vector2.up, 1f, new Color(0.04f, 0.48f, 0f, 1f));

            Color dist = new Color(0f, 0.67f, 0.03f, 0.74f);
            _far = Label(Root, new Vector2(-ScopeW * 0.5f - 12f, ScopeH * 0.5f + 0f), new Vector2(30f, 10f), string.Empty, 8f, dist, TextAlignmentOptions.Right);
            _half = Label(Root, new Vector2(-ScopeW * 0.5f - 12f, 6f), new Vector2(30f, 10f), string.Empty, 8f, dist, TextAlignmentOptions.Right);
            _status = Label(Root, new Vector2(4f, 40f), new Vector2(160f, 14f), string.Empty, 11.5f, new Color(1f, 0.6f, 0f, 1f), TextAlignmentOptions.Center);

            // Lock box, bottom left (reference LockInfoObj)
            Image box = PortalMFDGraphics.Box("LockInfo", Root, Mid, new Vector2(-W * 0.5f + 46f, -H * 0.5f + 34f), new Vector2(84f, 60f), new Color(0f, 0f, 0f, 0.7f));
            PortalMFDGraphics.Box("Bar", box.transform, Mid, new Vector2(-40f, 0f), new Vector2(2f, 56f), new Color(0.05f, 0.35f, 0f, 1f));
            Label(box.transform, new Vector2(-24f, -2f), new Vector2(26f, 56f), "LOCK\nB\nR\nA\nVc", 7.5f, new Color(0.46f, 0.78f, 0.5f, 1f), TextAlignmentOptions.TopLeft);
            _lockName = Label(box.transform, new Vector2(12f, 23f), new Vector2(56f, 10f), string.Empty, 7.5f, new Color(0.97f, 1f, 0.66f, 1f), TextAlignmentOptions.Left);
            _lockValues = Label(box.transform, new Vector2(12f, -7f), new Vector2(56f, 46f), string.Empty, 7.5f, Color.white, TextAlignmentOptions.TopLeft);
            _lockBox = box.gameObject;

            // Range buttons (reference RangePlus / RangeMinus, bottom right); radar power shows as the status text
            Button(new Vector2(W * 0.5f - 14f, -H * 0.5f + 40f), new Vector2(20f, 18f), "+");
            Button(new Vector2(W * 0.5f - 14f, -H * 0.5f + 18f), new Vector2(20f, 18f), "-");
        }

        public override void OnShow()
        {
            Aircraft own = Aircraft;
            if (own == null) return;
            SubscribeJam(own);
            if (own.radar == null && _optical == null) _optical = own.GetComponentInChildren<TargetDetector>();
            if (own.radar != null && !_rangeInitialized)
            {
                _rangeInitialized = true;
                float maxKm = own.radar.GetRadarRange() * 0.001f;
                _rangeKm = s_ranges[s_ranges.Length - 1];
                for (int i = 0; i < s_ranges.Length; i++)
                {
                    if (s_ranges[i] >= maxKm * 0.75f) { _rangeKm = s_ranges[i]; break; }
                }
            }
            UpdateRangeTexts();
        }

        public override void CycleRange()
        {
            SetRange(s_ranges[(NearestRange(s_ranges, _rangeKm) + 1) % s_ranges.Length]);
        }

        private void SetRange(float km)
        {
            _rangeKm = km;
            UpdateRangeTexts();
            Tick(Time.time);
        }

        private void UpdateRangeTexts()
        {
            SetText(_far, _rangeKm.ToString("0"));
            SetText(_half, (_rangeKm * 0.5f).ToString("0"));
        }

        private Symbol GetSymbol(int index)
        {
            while (_symbols.Count <= index)
            {
                Symbol s = new Symbol();
                s.Root = PortalMFDGraphics.At("Contact", _scope, Mid, Vector2.zero, new Vector2(1f, 1f));
                s.Icon = PortalMFDGraphics.Shape("Icon", s.Root, Mid, Vector2.zero, 11f, PortalShape.RadarEnemy, EnemyColor);
                s.Alt = Label(s.Root, new Vector2(0f, -11f), new Vector2(24f, 9f), string.Empty, 7.5f, new Color(0.95f, 0.37f, 0f, 1f), TextAlignmentOptions.Center);
                s.Root.gameObject.SetActive(false);
                _symbols.Add(s);
            }
            Symbol sym = _symbols[index];
            Show(sym.Root, true);
            return sym;
        }

        public override void Tick(float now)
        {
            Aircraft own = Aircraft;
            if (own == null) return;
            TargetDetector radar = own.radar;
            bool radarOn = radar != null && radar.activated;
            SetText(_status, radar == null ? (_optical != null ? "OPTICAL" : "NO RADAR") : (radarOn ? string.Empty : "RADAR OFF"));
            bool scanning = radarOn || (radar == null && _optical != null);
            _scanning = scanning;
            Show(_scan, scanning);

            Gather(own, radar, now);
            TickStrobes(own, radarOn, now);

            Unit primary = PrimaryTarget();
            Vector3 ownPos = own.transform.position;
            Vector3 ownVel = own.rb != null ? own.rb.velocity : own.transform.forward * own.speed;
            float heading = HeadingDeg(own.transform);
            bool aviation = PlayerSettings.unitSystem == PlayerSettings.UnitSystem.Imperial;
            float rangeM = _rangeKm * 1000f;
            bool lockShown = false;
            int n = 0;

            foreach (KeyValuePair<Unit, Contact> kv in _contacts)
            {
                if (n >= MaxContacts) break;
                Contact c = kv.Value;
                Vector3 pos, vel;
                KnownState(c, now, out pos, out vel);
                Vector3 d = pos - ownPos;
                float range = new Vector2(d.x, d.z).magnitude;
                float az = Mathf.DeltaAngle(heading, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg);
                if (Mathf.Abs(az) > HalfAzimuth || range > rangeM) continue;

                Vector2 p = new Vector2(az / HalfAzimuth * ScopeW * 0.5f, range / rangeM * ScopeH - ScopeH * 0.5f);
                Symbol s = GetSymbol(n++);
                s.Root.anchoredPosition = p;
                bool friend = own.NetworkHQ != null && c.Unit.NetworkHQ == own.NetworkHQ;
                Sprite sprite = PortalMFDGraphics.Get(friend ? PortalShape.RadarFriendly : PortalShape.RadarEnemy);
                if (s.Icon.sprite != sprite) s.Icon.sprite = sprite;
                Color col = friend ? FriendColor : EnemyColor;
                col.a = Mathf.Lerp(0.35f, 1f, 1f - (now - c.LastSeen) / Persistence);
                s.Icon.color = col;
                float track = Mathf.Atan2(vel.x, vel.z) * Mathf.Rad2Deg - heading;
                s.Icon.rectTransform.localEulerAngles = new Vector3(0f, 0f, -track);

                float alt = pos.y - Datum.LocalSeaY;
                int altK = Mathf.RoundToInt(aviation ? alt * 3.28084f / 1000f : alt / 1000f);
                SetText(s.Alt, altK.ToString());
                s.Alt.color = col;

                if (c.Unit == primary)
                {
                    lockShown = true;
                    PortalMFDGraphics.SetLine(_lockLine.rectTransform, new Vector2(0f, -ScopeH * 0.5f), p, 1f);
                    // The scan line holds on the target azimuth (reference hardlockLine)
                    _scanLockX = p.x;
                    float closure = Vector3.Dot(ownVel - vel, d.normalized);
                    SetText(_lockName, friend ? "FRIEND" : TargetIdentification.GetLabel(c.Unit, IdentTier.High));
                    SetText(_lockValues, string.Format("{0:000}\n{1:0.0}\n{2}\n{3:+0;-0}",
                        Mathf.Repeat(heading + az, 360f), range * 0.001f, altK, closure));
                }
            }
            for (int i = n; i < _symbols.Count; i++) Show(_symbols[i].Root, false);
            _scanLocked = lockShown;
            Show(_lockLine, lockShown);
            Show(_lockBox.transform, lockShown);
        }

        /// <summary>Every frame: the scan line sweeps, or holds on the azimuth of the selected target.</summary>
        public override void FrameTick()
        {
            if (!_scanning) return;
            float x = _scanLocked ? _scanLockX : Mathf.Sin(Time.timeSinceLevelLoad * 0.5f * Mathf.PI) * ScopeW * 0.5f;
            _scan.anchoredPosition = new Vector2(x, -ScopeH * 0.5f);
            _scanImage.color = _scanLocked ? HardLockColor : ScanColor;
        }

        private void Gather(Aircraft own, TargetDetector radar, float now)
        {
            if (radar != null && radar.detectedTargets != null) Paint(radar.detectedTargets, own, now);
            if (_optical != null && _optical.detectedTargets != null) Paint(_optical.detectedTargets, own, now);

            PhysRWRReceiver rwr = PhysRWRReceiver.Get(own);
            if (rwr != null)
            {
                foreach (DatalinkThreatEntry d in rwr.ActiveDatalinkThreats)
                {
                    if (d == null || d.IsMissile || d.ThreatUnit == null || d.ThreatUnit.disabled) continue;
                    if (!(d.ThreatUnit is Aircraft)) continue;
                    Contact c = GetContact(d.ThreatUnit, now);
                    c.Report = d;
                }
            }

            _stale.Clear();
            foreach (KeyValuePair<Unit, Contact> kv in _contacts)
            {
                if (kv.Key == null || kv.Key.disabled || now - kv.Value.LastSeen > Persistence) _stale.Add(kv.Key);
            }
            for (int i = 0; i < _stale.Count; i++) _contacts.Remove(_stale[i]);
        }

        private void Paint(List<Unit> units, Aircraft own, float now)
        {
            for (int i = 0; i < units.Count; i++)
            {
                Unit u = units[i];
                if (u == null || u == own || u.disabled || !(u is Aircraft)) continue;
                Contact c = GetContact(u, now);
                c.LastOwnSensor = now;
                c.SensorPos = u.transform.position;
                c.SensorVel = (u.rb != null && u.rb.velocity.sqrMagnitude > 1f) ? u.rb.velocity : u.transform.forward * u.speed;
            }
        }

        private Contact GetContact(Unit u, float now)
        {
            Contact c;
            if (!_contacts.TryGetValue(u, out c))
            {
                c = new Contact();
                c.Unit = u;
                _contacts[u] = c;
            }
            c.LastSeen = now;
            return c;
        }

        #region Jamming strobes

        private void SubscribeJam(Aircraft own)
        {
            if (_jamSubscribed == own) return;
            Unsubscribe();
            if (_onJam == null) _onJam = OnJam;
            // Unit.Jam is a ClientRpc: onJam runs on the host and on every client
            own.onJam += _onJam;
            _jamSubscribed = own;
        }

        private void Unsubscribe()
        {
            if (_jamSubscribed != null && _onJam != null) _jamSubscribed.onJam -= _onJam;
            _jamSubscribed = null;
        }

        public override void Dispose()
        {
            Unsubscribe();
        }

        private void OnJam(Unit.JamEventArgs args)
        {
            try
            {
                Unit jammer = args.jammingUnit;
                if (jammer == null || jammer == Aircraft || args.jamAmount <= 0f) return;
                Jammer j = null;
                for (int i = 0; i < _jammers.Count; i++)
                {
                    if (_jammers[i].Unit == jammer) { j = _jammers[i]; break; }
                }
                if (j == null)
                {
                    j = new Jammer();
                    j.Unit = jammer;
                    _jammers.Add(j);
                }
                j.Amount = args.jamAmount;
                j.LastTime = Time.time;
            }
            catch (System.Exception ex)
            {
                PatchErrorLog.Once("PortalPageRadar.OnJam", ex);
            }
        }

        /// <summary>
        /// Jammers in the scan sector put strobes on their azimuth while the radar transmits.
        /// </summary>
        private void TickStrobes(Aircraft own, bool radarOn, float now)
        {
            for (int i = _jammers.Count - 1; i >= 0; i--)
            {
                Jammer j = _jammers[i];
                if (j.Unit == null || j.Unit.disabled || now - j.LastTime > JamHold) _jammers.RemoveAt(i);
            }

            if (radarOn)
            {
                Vector3 ownPos = own.transform.position;
                float heading = HeadingDeg(own.transform);
                for (int i = 0; i < _jammers.Count; i++)
                {
                    Jammer j = _jammers[i];
                    Vector3 d = j.Unit.transform.position - ownPos;
                    float az = Mathf.DeltaAngle(heading, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg);
                    if (Mathf.Abs(az) > HalfAzimuth) continue;
                    float x = az / HalfAzimuth * ScopeW * 0.5f;
                    // jamAmount is the pod falloff times its power fraction (0..1)
                    float chance = Mathf.Lerp(0.25f, 1f, Mathf.Clamp01(j.Amount));
                    for (int k = 0; k < StrobeTries; k++)
                    {
                        if (Random.value > chance) continue;
                        int s = _nextStrobe;
                        _nextStrobe = (_nextStrobe + 1) % MaxStrobes;
                        _strobes[s].rectTransform.anchoredPosition = new Vector2(x + Random.Range(-1.5f, 1.5f), Random.Range(-0.5f, 0.5f) * ScopeH);
                        _strobeUntil[s] = now + StrobeLife;
                    }
                }
            }

            for (int i = 0; i < MaxStrobes; i++) Show(_strobes[i], radarOn && now < _strobeUntil[i]);
        }

        #endregion

        /// <summary>
        /// Own sensor contact: the position of the last detection. Without one in the last 0.5 s: the datalink
        /// report, else the last detection, both dead-reckoned to now.
        /// </summary>
        private static void KnownState(Contact c, float now, out Vector3 position, out Vector3 velocity)
        {
            bool ownRecent = now - c.LastOwnSensor <= 0.5f;
            if (!ownRecent && c.Report != null && c.Report.ThreatUnit == c.Unit)
            {
                velocity = c.Report.EstimatedVelocity;
                position = c.Report.EstimatedPosition + velocity * Mathf.Max(0f, Time.time - c.Report.LastPingTime);
                return;
            }
            velocity = c.SensorVel;
            position = c.SensorPos + (ownRecent ? Vector3.zero : velocity * Mathf.Max(0f, now - c.LastOwnSensor));
        }
    }
}
