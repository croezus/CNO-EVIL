using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Components
{
    /// <summary>
    /// EM scan ring of the TacScreen.
    /// Three closed lines around the ownship show the received energy by bearing and threat class:
    /// yellow = detection (search, track, incoming noise jamming), orange = SARH guidance (illuminator or CW),
    /// red = active radar seekers. Yellow sits at a fixed world range, orange just outside it, red just inside.
    /// A bin of 6 deg moves outward with the energy that comes from its direction:
    /// RWR pings (measured bearing, decay at each sweep visit) and incoming noise jamming (cos^43 lobe).
    /// Auto gain: after each full sweep the strongest bin gets the full offset; a square root curve lifts weak bins.
    /// Random noise blips twitch single bins (display only). The bumps show no range.
    /// Illumination below the emitter's detection threshold moves a bin inward instead: fixed scale, no auto gain,
    /// deeper the further the SNR is below the threshold (full depth at 20 dB below). An outward bump wins the bin.
    /// The sweep, decay and line order are those of the reference.
    /// </summary>
    public class TacScreenEmScanRing : MonoBehaviour
    {
        private enum EmScanClass
        {
            Detect = 0,
            Guidance = 1,
            Active = 2
        }

        private const int ClassCount = 3;
        private const int BinCount = 60;               // 6 deg bins, world (north) frame
        private const int BinsPerFrame = 6;            // A full sweep in 10 cycles of 3 frames
        private const float LobePower = 43.0f;         // Jammer lobe: max(0, cos)^43, -3 dB at ~10 deg
        private const double RwrDecay = 4.0;           // RWR energy of a bin / 4 at each sweep visit
        private const float MinFullScaleEnergy = 0.1f; // Gain limit: energy below this never gets the full offset
        private const float JamHoldSeconds = 0.5f;     // Vanilla pod tick is 0.2 s; hold over missed ticks
        private const float JamFadeSeconds = 1.0f;     // Then fade to 0
        private const float MinRadiusFraction = 0.1f;  // Base radius limits, fraction of the scope radius
        private const float MaxRadiusFraction = 0.75f;
        private const float NoiseMinInterval = 0.25f;  // Seconds between noise blips of a line
        private const float NoiseMaxInterval = 1.2f;
        private const float NoiseMinAmp = 0.04f;       // Blip amplitude, fraction of the bump height
        private const float NoiseMaxAmp = 0.12f;
        private const float NoiseDecaySeconds = 0.15f;
        private const float MaxInsetFraction = 0.6f;   // Deepest dip, fraction of the line's base radius

        // Same hues as the TacScreen strobes (search, track, missile)
        private static readonly Color DetectColor = new Color(1.0f, 0.85f, 0.15f, 0.60f);
        private static readonly Color GuidanceColor = new Color(1.0f, 0.55f, 0.0f, 0.65f);
        private static readonly Color ActiveColor = new Color(1.0f, 0.12f, 0.12f, 0.70f);

        private class JamSource
        {
            public Unit Jammer;
            public float Amount;
            public float LastTime;
            public Vector3 Direction;   // Horizontal unit vector to the jammer, with receiver jitter
        }

        private Aircraft _aircraft;
        private PhysRWRReceiver _rwr;
        private Action<Unit.JamEventArgs> _onJam;
        private bool _subscribed;

        private readonly EmScanLineGraphic[] _lines = new EmScanLineGraphic[ClassCount];
        private readonly double[][] _rwrEnergy = new double[ClassCount][];
        private readonly float[][] _offset01 = new float[ClassCount][];
        private readonly float[][] _belowLevel = new float[ClassCount][];  // Held dip depth of below-threshold pings
        private readonly float[][] _inset01 = new float[ClassCount][];
        private readonly float[][] _noise01 = new float[ClassCount][];   // Display only: not in the energy or gain
        private readonly float[] _nextNoise = new float[ClassCount];
        private readonly float[] _binSin = new float[BinCount];
        private readonly float[] _binCos = new float[BinCount];

        private readonly Dictionary<Unit, float> _lastPing = new Dictionary<Unit, float>();
        private readonly Dictionary<Unit, float> _lastBelowPing = new Dictionary<Unit, float>();
        private readonly List<JamSource> _jams = new List<JamSource>();
        private readonly List<Unit> _stale = new List<Unit>();

        private int _idx;
        private int _phase;
        private double _maxOnSweep;
        private double _gain = 1.0 / MinFullScaleEnergy;

        public static TacScreenEmScanRing Create(Transform parent, Aircraft aircraft, PhysRWRReceiver rwr)
        {
            GameObject go = new GameObject("TacScreen_EmScanRing");
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);

            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = Vector2.zero;
            rt.anchoredPosition = Vector2.zero;
            rt.localScale = Vector3.one;
            rt.localEulerAngles = Vector3.zero;

            // Behind the tracks and strobes of the tactical layer
            go.transform.SetAsFirstSibling();

            TacScreenEmScanRing ring = go.AddComponent<TacScreenEmScanRing>();
            ring.Setup(aircraft, rwr);
            return ring;
        }

        private void Setup(Aircraft aircraft, PhysRWRReceiver rwr)
        {
            _aircraft = aircraft;
            _rwr = rwr;

            for (int i = 0; i < BinCount; i++)
            {
                float az = i * (360.0f / BinCount) * Mathf.Deg2Rad;
                _binSin[i] = Mathf.Sin(az);
                _binCos[i] = Mathf.Cos(az);
            }

            // Draw order Detect, Guidance, Active (Active on top)
            CreateLine(EmScanClass.Detect, DetectColor);
            CreateLine(EmScanClass.Guidance, GuidanceColor);
            CreateLine(EmScanClass.Active, ActiveColor);

            // Unit.Jam is a ClientRpc: onJam runs on the host and on every client
            _onJam = OnJam;
            if (_aircraft != null)
            {
                _aircraft.onJam += _onJam;
                _subscribed = true;
            }
        }

        private void CreateLine(EmScanClass cls, Color color)
        {
            int c = (int)cls;
            _rwrEnergy[c] = new double[BinCount];
            _offset01[c] = new float[BinCount];
            _belowLevel[c] = new float[BinCount];
            _inset01[c] = new float[BinCount];
            _noise01[c] = new float[BinCount];

            GameObject go = new GameObject("EmScan_" + cls);
            go.layer = gameObject.layer;
            go.transform.SetParent(transform, false);

            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = Vector2.zero;

            EmScanLineGraphic line = go.AddComponent<EmScanLineGraphic>();
            line.raycastTarget = false;
            line.color = color;
            line.Thickness = Mathf.Clamp(CnoEvilConfig.TacScreenEmScanRingThickness.Value, 0.5f, 6.0f);
            line.Points = new Vector2[BinCount];
            _lines[c] = line;
        }

        private void OnJam(Unit.JamEventArgs args)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                Unit jammer = args.jammingUnit;
                if (jammer == null || jammer == _aircraft || args.jamAmount <= 0.0f) return;

                JamSource src = null;
                for (int i = 0; i < _jams.Count; i++)
                {
                    if (_jams[i].Jammer == jammer) { src = _jams[i]; break; }
                }
                if (src == null)
                {
                    src = new JamSource();
                    src.Jammer = jammer;
                    _jams.Add(src);
                }
                src.Amount = args.jamAmount;
                src.LastTime = Time.time;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("TacScreenEmScanRing.OnJam", ex);
            }
        }

        /// <summary>
        /// Called each frame by TacScreenTacticalDisplay while the tactical layer is visible.
        /// </summary>
        /// <param name="scopeRadius">Scope radius (px).</param>
        /// <param name="pixelsPerMeter">TacScreen scale (px per world meter).</param>
        public void Tick(float scopeRadius, float pixelsPerMeter)
        {
            if (_aircraft == null) return;

            // Yellow line at a world range, kept on the scope; orange outside it, red inside it
            float baseRadius = Mathf.Clamp(CnoEvilConfig.TacScreenEmScanRingRangeMeters.Value * pixelsPerMeter,
                MinRadiusFraction * scopeRadius, MaxRadiusFraction * scopeRadius);
            float gap = Mathf.Max(0.0f, CnoEvilConfig.TacScreenEmScanRingSpacing.Value * scopeRadius);
            float maxOffset = Mathf.Max(0.0f, CnoEvilConfig.TacScreenEmScanRingBumpHeight.Value * scopeRadius);

            IngestPings();
            UpdateJams();
            UpdateNoise();

            // Reference coroutine: sweep + first line, then the second, then the third, one per frame
            switch (_phase)
            {
                case 0:
                    Sweep();
                    PushLine(EmScanClass.Detect, baseRadius, maxOffset);
                    break;
                case 1:
                    PushLine(EmScanClass.Guidance, baseRadius + gap, maxOffset);
                    break;
                default:
                    PushLine(EmScanClass.Active, Mathf.Max(1.0f, baseRadius - gap), maxOffset);
                    break;
            }
            _phase = (_phase + 1) % 3;
        }

        // Receiver noise: short random twitches of 1-3 bins on each line
        private void UpdateNoise()
        {
            float now = Time.time;
            float decay = Mathf.Exp(-Time.deltaTime / NoiseDecaySeconds);
            float scale = Mathf.Max(0.0f, CnoEvilConfig.TacScreenEmScanRingNoise.Value);

            for (int c = 0; c < ClassCount; c++)
            {
                float[] noise = _noise01[c];
                for (int i = 0; i < BinCount; i++) noise[i] *= decay;

                if (now < _nextNoise[c]) continue;
                _nextNoise[c] = now + UnityEngine.Random.Range(NoiseMinInterval, NoiseMaxInterval);
                if (scale <= 0.0f) continue;

                int center = UnityEngine.Random.Range(0, BinCount);
                int halfWidth = UnityEngine.Random.Range(0, 2);
                float amp = UnityEngine.Random.Range(NoiseMinAmp, NoiseMaxAmp) * scale;
                for (int k = -halfWidth; k <= halfWidth; k++)
                {
                    int bin = (center + k + BinCount) % BinCount;
                    float a = (k == 0) ? amp : amp * 0.5f;
                    if (a > noise[bin]) noise[bin] = a;
                }
            }
        }

        private void IngestPings()
        {
            if (_rwr == null) return;
            foreach (RwrThreatEntry threat in _rwr.ActiveThreats)
            {
                Unit emitter = threat.EmitterUnit;
                if (emitter == null) continue;

                float last;
                if (_lastPing.TryGetValue(emitter, out last) && threat.LastPingTime <= last) continue;
                _lastPing[emitter] = threat.LastPingTime;

                // Measured direction (tier jitter included), never the true emitter position
                Vector3 dir = threat.LastDirection;
                if (dir.x * dir.x + dir.z * dir.z < 1e-6f) continue;

                _rwrEnergy[(int)Classify(threat)][AzimuthBin(dir)] += threat.SignalStrength01;
            }

            // Below-threshold illumination: hold the deepest dip of the bin until the sweep visits it
            foreach (RwrThreatEntry paint in _rwr.BelowThresholdPings)
            {
                Unit emitter = paint.EmitterUnit;
                if (emitter == null) continue;

                float last;
                if (_lastBelowPing.TryGetValue(emitter, out last) && paint.LastPingTime <= last) continue;
                _lastBelowPing[emitter] = paint.LastPingTime;

                Vector3 dir = paint.LastDirection;
                if (dir.x * dir.x + dir.z * dir.z < 1e-6f) continue;

                float[] level = _belowLevel[(int)Classify(paint)];
                int bin = AzimuthBin(dir);
                if (paint.SignalStrength01 > level[bin]) level[bin] = paint.SignalStrength01;
            }
        }

        // Per ping: the state of an emitter changes (search -> SARH guidance)
        private static EmScanClass Classify(RwrThreatEntry threat)
        {
            if (threat.IsActiveSeeker || threat.EmitterUnit is Missile) return EmScanClass.Active;
            if (threat.State == RwrThreatState.MissileGuidance) return EmScanClass.Guidance;
            return EmScanClass.Detect;
        }

        private void UpdateJams()
        {
            float now = Time.time;
            Vector3 own = _aircraft.transform.position;
            float jitter = (_rwr != null) ? _rwr.EffectiveBearingJitterDegrees : 0.0f;

            for (int i = _jams.Count - 1; i >= 0; i--)
            {
                JamSource src = _jams[i];
                if (src.Jammer == null || src.Jammer.disabled || JamLevel(src, now) <= 0.0f)
                {
                    _jams.RemoveAt(i);
                    continue;
                }

                // Angle of arrival of the noise, with the receiver error (new error each frame)
                Vector3 to = src.Jammer.transform.position - own;
                to.y = 0.0f;
                if (to.sqrMagnitude < 1.0f) continue;
                if (jitter > 0.0f) to = Quaternion.Euler(0.0f, UnityEngine.Random.Range(-jitter, jitter), 0.0f) * to;
                src.Direction = to.normalized;
            }
        }

        private static float JamLevel(JamSource src, float now)
        {
            float age = now - src.LastTime;
            if (age <= JamHoldSeconds) return src.Amount;
            return src.Amount * Mathf.Clamp01(1.0f - (age - JamHoldSeconds) / JamFadeSeconds);
        }

        private void Sweep()
        {
            float now = Time.time;
            for (int k = 0; k < BinsPerFrame; k++)
            {
                _idx = (_idx + 1) % BinCount;
                if (_idx == 0)
                {
                    // New sweep: strongest bin of the last sweep gets the full offset
                    _gain = 1.0 / Math.Max(_maxOnSweep, MinFullScaleEnergy);
                    _maxOnSweep = 0.0;
                    PruneStale();
                }

                for (int c = 0; c < ClassCount; c++)
                {
                    double e = _rwrEnergy[c][_idx];
                    if (c == (int)EmScanClass.Detect) e += JamEnergy(_idx, now);
                    _rwrEnergy[c][_idx] /= RwrDecay;

                    if (e > _maxOnSweep) _maxOnSweep = e;
                    _offset01[c][_idx] = Mathf.Sqrt(Mathf.Min((float)(e * _gain), 1.0f));

                    // Fixed scale: the dip depth keeps its dB meaning
                    _inset01[c][_idx] = Mathf.Sqrt(_belowLevel[c][_idx]);
                    _belowLevel[c][_idx] /= (float)RwrDecay;
                }
            }
        }

        private double JamEnergy(int bin, float now)
        {
            double sum = 0.0;
            float bx = _binSin[bin];
            float bz = _binCos[bin];
            for (int i = 0; i < _jams.Count; i++)
            {
                JamSource src = _jams[i];
                float dot = bx * src.Direction.x + bz * src.Direction.z;
                if (dot <= 0.0f) continue;
                sum += JamLevel(src, now) * Mathf.Pow(dot, LobePower);
            }
            return sum;
        }

        private void PushLine(EmScanClass cls, float baseRadius, float maxOffset)
        {
            EmScanLineGraphic line = _lines[(int)cls];
            if (line == null) return;

            // Heading-up layer: rotate the world bins by the ownship yaw
            Vector3 fwd = _aircraft.transform.forward;
            float heading = (fwd.x * fwd.x + fwd.z * fwd.z > 1e-6f) ? Mathf.Atan2(fwd.x, fwd.z) : 0.0f;
            float step = 360.0f / BinCount * Mathf.Deg2Rad;

            line.Thickness = Mathf.Clamp(CnoEvilConfig.TacScreenEmScanRingThickness.Value, 0.5f, 6.0f);

            float[] off = _offset01[(int)cls];
            float[] inset = _inset01[(int)cls];
            float[] noise = _noise01[(int)cls];
            float maxInset = Mathf.Min(maxOffset, MaxInsetFraction * baseRadius);
            Vector2[] pts = line.Points;
            for (int i = 0; i < BinCount; i++)
            {
                float rel = i * step - heading;
                float r = (off[i] > 0.0f || inset[i] <= 0.0f)
                    ? baseRadius + Mathf.Min(off[i] + noise[i], 1.0f) * maxOffset
                    : baseRadius - inset[i] * maxInset + noise[i] * maxOffset;
                pts[i] = new Vector2(Mathf.Sin(rel) * r, Mathf.Cos(rel) * r);
            }
            line.SetVerticesDirty();
        }

        private static int AzimuthBin(Vector3 dir)
        {
            float az = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            if (az < 0.0f) az += 360.0f;
            return Mathf.Clamp(Mathf.FloorToInt(az / 360.0f * BinCount), 0, BinCount - 1);
        }

        private void PruneStale()
        {
            _stale.Clear();
            foreach (KeyValuePair<Unit, float> kvp in _lastPing)
            {
                if (kvp.Key == null || kvp.Key.disabled) _stale.Add(kvp.Key);
            }
            for (int i = 0; i < _stale.Count; i++)
            {
                _lastPing.Remove(_stale[i]);
            }

            _stale.Clear();
            foreach (KeyValuePair<Unit, float> kvp in _lastBelowPing)
            {
                if (kvp.Key == null || kvp.Key.disabled) _stale.Add(kvp.Key);
            }
            for (int i = 0; i < _stale.Count; i++)
            {
                _lastBelowPing.Remove(_stale[i]);
            }
        }

        /// <summary>
        /// Clears all energy (ownship lost or disabled).
        /// </summary>
        public void ResetEnergy()
        {
            for (int c = 0; c < ClassCount; c++)
            {
                if (_rwrEnergy[c] != null) Array.Clear(_rwrEnergy[c], 0, BinCount);
                if (_offset01[c] != null) Array.Clear(_offset01[c], 0, BinCount);
                if (_belowLevel[c] != null) Array.Clear(_belowLevel[c], 0, BinCount);
                if (_inset01[c] != null) Array.Clear(_inset01[c], 0, BinCount);
                if (_noise01[c] != null) Array.Clear(_noise01[c], 0, BinCount);
            }
            _jams.Clear();
            _lastPing.Clear();
            _lastBelowPing.Clear();
            _maxOnSweep = 0.0;
            _gain = 1.0 / MinFullScaleEnergy;
        }

        private void OnDestroy()
        {
            if (_subscribed && _aircraft != null) _aircraft.onJam -= _onJam;
            _subscribed = false;
        }
    }
}
