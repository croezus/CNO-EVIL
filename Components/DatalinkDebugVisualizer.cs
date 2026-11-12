using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;
using CnoEvil.Core;

namespace CnoEvil.Components
{
    /// <summary>
    /// Interactive 3D and 2D visualizer for the Tactical Allied Datalink system.
    /// Toggled via configurable hotkey (default: F8).
    /// Renders:
    /// 1. 3D World-Space Datalink Mesh Lines between friendly communication nodes (Cyan).
    /// 2. 3D Shared Radar Track Rays from spotting radar to target (Green/Cyan).
    /// 3. 3D Shared Visual Reconnaissance Rays from spotter to target (Yellow).
    /// 4. 3D Relayed Inbound Missile Warning Rays from detecting radar to missile to defended aircraft (Red/Pulsing).
    /// 5. 2D Screen-Space tactical reticles and badges over nodes and shared contacts.
    /// 6. Real-time on-screen Datalink Diagnostic Telemetry window for validating sensor fusion.
    /// Uses pre-allocated, object-pooled LineRenderers for zero GC allocation.
    /// </summary>
    public class DatalinkDebugVisualizer : MonoBehaviour
    {
        public static DatalinkDebugVisualizer Instance { get; private set; }

        private GameObject _linesRoot;
        private Material _lineMaterial;

        // Pooled LineRenderers for 3D world visualization
        private readonly List<LineRenderer> _meshLines = new List<LineRenderer>();
        private readonly List<LineRenderer> _radarTrackLines = new List<LineRenderer>();
        private readonly List<LineRenderer> _visualContactLines = new List<LineRenderer>();
        private readonly List<LineRenderer> _missileAlertLines = new List<LineRenderer>();

        // 2D GUI Styles & Textures
        private Rect _windowRect = new Rect(20f, 60f, 460f, 0f);
        private readonly List<Rect> _drawnBadgeRects = new List<Rect>(16);

        private struct FusedContact
        {
            public Unit Target;
            public string TargetName;
            public bool HasRadarTrack;
            public bool IsVisual;
            public bool IsRwr;               // No shared radar / visual contact: passive (RWR / ESM) or memory track
            public string BestSourceName;
            public float BestDistanceKm;
            public int TotalFeeds;
            public bool HasFactionTrack;     // The faction has a track: the Q / CEP values below are real
            public float TrackingQuality;    // Faction track Q (0..1)
            public float CepKm;              // Weapon CEP of a launch now: CalculateCep(Q, sensor range)
            public float RingKm;             // PortalMFD uncertainty ring radius
            public bool IsInMemory;
            public bool IsTriangulated;
            public string Method;            // Source of the faction position
            public bool HasEstimate;
            public Vector3 EstimatePos;      // Faction estimate (local position of the aggregation frame)
            public float ErrVsTruthKm;       // Estimate to the true position (debug only)
            public float MemoryAgeS;         // Time since the last active detection
            public float MemoryCapS;         // Memory cap: the track is forgotten after it
            public string QTag;
            public string BadgeText;
            public string RowLeft;
            public string RowRight;
        }
        private readonly List<FusedContact> _fusedContacts = new List<FusedContact>(32);
        private readonly Dictionary<Unit, int> _fusedIndex = new Dictionary<Unit, int>(); // Target -> index in _fusedContacts
        private float _lastContactAggregationTime;
        private int _trackCountAll;
        private int _trackCountFix;
        private int _trackCountMemory;
        private bool _linesHidden;

        private static readonly string[] QPercentStrings;
        private static readonly GUIContent _calcContent = new GUIContent();

        static DatalinkDebugVisualizer()
        {
            QPercentStrings = new string[101];
            for (int i = 0; i <= 100; i++)
            {
                QPercentStrings[i] = string.Format("Q: {0}%", i);
            }
        }

        private static string GetQPercentString(float q)
        {
            int idx = Mathf.Clamp(Mathf.RoundToInt(q * 100f), 0, 100);
            return QPercentStrings[idx];
        }

        private Texture2D _whiteTex;
        private Texture2D _windowBgTex;
        private Texture2D _dividerTex;

        private GUIStyle _windowStyle;
        private GUIStyle _titleStyle;
        private GUIStyle _badgeActiveStyle;
        private GUIStyle _badgeInactiveStyle;
        private GUIStyle _subHeaderStyle;
        private GUIStyle _sectionHeaderStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _valueStyle;
        private GUIStyle _hudTextStyle;
        private GUIStyle _hudTagStyle;

        private void Awake()
        {
            Instance = this;
            InitializeLineRenderers();
            InitializeTextures();
            _windowRect = new Rect(Mathf.Max(20f, Screen.width - 480f), 60f, 460f, 0f);
        }

        private void InitializeTextures()
        {
            _whiteTex = MakeTex(1, 1, Color.white);
            _dividerTex = MakeTex(1, 1, new Color(0.20f, 0.35f, 0.45f, 0.65f));
            _windowBgTex = MakeTex(2, 2, new Color(0.04f, 0.08f, 0.12f, 0.94f));
        }

        private Texture2D MakeTex(int width, int height, Color col)
        {
            Color[] pix = new Color[width * height];
            for (int i = 0; i < pix.Length; i++) pix[i] = col;
            Texture2D result = new Texture2D(width, height);
            result.SetPixels(pix);
            result.Apply();
            return result;
        }

        private void InitializeLineRenderers()
        {
            if (_linesRoot != null) return;

            _linesRoot = new GameObject("CnoEvil_Datalink_Visualizer_Root");
            _linesRoot.transform.SetParent(transform);

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("UI/Default");
            if (shader == null) shader = Shader.Find("Hidden/Internal-Colored");

            if (shader != null)
            {
                _lineMaterial = new Material(shader);
            }
            else
            {
                _lineMaterial = new Material(Shader.Find("Standard"));
            }

            _lineMaterial.hideFlags = HideFlags.DontSave;
        }

        private LineRenderer CreateLine(string name, float width)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(_linesRoot.transform);

            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.material = _lineMaterial;
            lr.startWidth = width;
            lr.endWidth = width;
            lr.positionCount = 2;
            lr.useWorldSpace = true;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.alignment = LineAlignment.View;
            go.SetActive(false);

            return lr;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_linesRoot != null) Destroy(_linesRoot);
            if (_lineMaterial != null) Destroy(_lineMaterial);
            if (_whiteTex != null) Destroy(_whiteTex);
            if (_dividerTex != null) Destroy(_dividerTex);
            if (_windowBgTex != null) Destroy(_windowBgTex);
        }

        private void Update()
        {
            if (CnoEvilConfig.DatalinkOverlayOn)
            {
                _linesHidden = false;
                UpdateActiveVisuals();
                if (Time.time - _lastContactAggregationTime >= 0.15f)
                {
                    _lastContactAggregationTime = Time.time;
                    AggregateFusedContacts();
                }
            }
            else
            {
                if (!_linesHidden)
                {
                    HideAllLines();
                    _linesHidden = true;
                }
            }
        }

        public void HideAllLines()
        {
            for (int i = 0; i < _meshLines.Count; i++)
            {
                if (_meshLines[i] != null && _meshLines[i].gameObject.activeSelf)
                    _meshLines[i].gameObject.SetActive(false);
            }
            for (int i = 0; i < _radarTrackLines.Count; i++)
            {
                if (_radarTrackLines[i] != null && _radarTrackLines[i].gameObject.activeSelf)
                    _radarTrackLines[i].gameObject.SetActive(false);
            }
            for (int i = 0; i < _visualContactLines.Count; i++)
            {
                if (_visualContactLines[i] != null && _visualContactLines[i].gameObject.activeSelf)
                    _visualContactLines[i].gameObject.SetActive(false);
            }
            for (int i = 0; i < _missileAlertLines.Count; i++)
            {
                if (_missileAlertLines[i] != null && _missileAlertLines[i].gameObject.activeSelf)
                    _missileAlertLines[i].gameObject.SetActive(false);
            }
        }

        private void SetLine(LineRenderer lr, Vector3 start, Vector3 end, Color col, float baseWidth, Vector3 camPos)
        {
            if (lr == null) return;
            if (!lr.gameObject.activeSelf) lr.gameObject.SetActive(true);

            float distStart = Vector3.Distance(camPos, start);
            float distEnd = Vector3.Distance(camPos, end);

            float widthStart = Mathf.Clamp(baseWidth * (0.35f + distStart * 0.0006f), 0.35f, 35.0f);
            float widthEnd = Mathf.Clamp(baseWidth * (0.35f + distEnd * 0.0006f), 0.35f, 35.0f);

            lr.startWidth = widthStart;
            lr.endWidth = widthEnd;
            lr.startColor = col;
            lr.endColor = col;
            lr.SetPosition(0, start);
            lr.SetPosition(1, end);
        }

        private void UpdateActiveVisuals()
        {
            var telemetry = DatalinkNetwork.LatestTelemetry;
            if (telemetry == null || !telemetry.NetworkActive)
            {
                HideAllLines();
                return;
            }

            Camera cam = CameraStateManager.i != null && CameraStateManager.i.mainCamera != null ? CameraStateManager.i.mainCamera : Camera.main;
            Vector3 camPos = (cam != null) ? cam.transform.position : Vector3.zero;

            Aircraft playerAircraft = CombatHUD.i != null ? CombatHUD.i.aircraft : null;
            Vector3 playerPos = (playerAircraft != null) ? playerAircraft.transform.position : Vector3.zero;

            // 1. Render Datalink Network Mesh Lines between friendly nodes
            bool showMesh = CnoEvilConfig.ShowDatalinkMeshLines == null || CnoEvilConfig.ShowDatalinkMeshLines.Value;
            int meshIndex = 0;

            if (showMesh && telemetry.Nodes.Count > 1)
            {
                Color meshColor = new Color(0.15f, 0.70f, 0.95f, 0.40f);

                // The faction datalink has no hub and no range limit: every node shares with every other node.
                // One line from the player to each node shows the membership.
                Vector3 hubPos = playerPos;

                for (int i = 0; playerAircraft != null && i < telemetry.Nodes.Count; i++)
                {
                    var node = telemetry.Nodes[i];
                    if (node.Unit == null || node.Unit.disabled || node.Unit == playerAircraft) continue;

                    Vector3 nodePos = node.Unit.transform.position;

                    while (_meshLines.Count <= meshIndex)
                    {
                        _meshLines.Add(CreateLine("DatalinkMesh_" + _meshLines.Count, 1.5f));
                    }

                    SetLine(_meshLines[meshIndex], hubPos, nodePos, meshColor, 1.5f, camPos);
                    meshIndex++;
                }
            }

            for (int i = meshIndex; i < _meshLines.Count; i++)
            {
                if (_meshLines[i].gameObject.activeSelf) _meshLines[i].gameObject.SetActive(false);
            }

            // 2. Render Shared Radar Track Rays (Bright Green / Cyan)
            int radarTrackIndex = 0;
            Color radarTrackColor = new Color(0.20f, 0.95f, 0.55f, 0.85f);

            for (int i = 0; i < telemetry.SharedRadarTracks.Count; i++)
            {
                var track = telemetry.SharedRadarTracks[i];
                if (track.SourceUnit == null || track.SourceUnit.disabled || track.TargetUnit == null || track.TargetUnit.disabled)
                    continue;

                Vector3 startPos = track.SourceUnit.transform.position;
                Vector3 endPos = track.TargetUnit.transform.position;

                while (_radarTrackLines.Count <= radarTrackIndex)
                {
                    _radarTrackLines.Add(CreateLine("DatalinkRadarTrack_" + _radarTrackLines.Count, 2.5f));
                }

                SetLine(_radarTrackLines[radarTrackIndex], startPos, endPos, radarTrackColor, 2.5f, camPos);
                radarTrackIndex++;
            }

            for (int i = radarTrackIndex; i < _radarTrackLines.Count; i++)
            {
                if (_radarTrackLines[i].gameObject.activeSelf) _radarTrackLines[i].gameObject.SetActive(false);
            }

            // 3. Render Shared Visual Reconnaissance Rays (Yellow / Amber)
            int visualIndex = 0;
            Color visualColor = new Color(1.0f, 0.85f, 0.15f, 0.85f);

            for (int i = 0; i < telemetry.SharedVisualContacts.Count; i++)
            {
                var v = telemetry.SharedVisualContacts[i];
                if (v.SpotterUnit == null || v.SpotterUnit.disabled || v.TargetUnit == null || v.TargetUnit.disabled)
                    continue;

                Vector3 startPos = v.SpotterUnit.transform.position;
                Vector3 endPos = v.TargetUnit.transform.position;

                while (_visualContactLines.Count <= visualIndex)
                {
                    _visualContactLines.Add(CreateLine("DatalinkVisualTrack_" + _visualContactLines.Count, 2.0f));
                }

                SetLine(_visualContactLines[visualIndex], startPos, endPos, visualColor, 2.0f, camPos);
                visualIndex++;
            }

            for (int i = visualIndex; i < _visualContactLines.Count; i++)
            {
                if (_visualContactLines[i].gameObject.activeSelf) _visualContactLines[i].gameObject.SetActive(false);
            }

            // 4. Render Relayed Inbound Missile Warning Rays (Red / Pulsing)
            int missileIndex = 0;
            float pulse = 0.6f + 0.4f * Mathf.Sin(Time.time * 8.0f);
            Color missileWarnColor = new Color(1.0f, 0.20f, 0.20f, pulse);

            for (int i = 0; i < telemetry.RelayedMissileAlerts.Count; i++)
            {
                var alert = telemetry.RelayedMissileAlerts[i];
                if (alert.RadarUnit == null || alert.RadarUnit.disabled || alert.MissileUnit == null || alert.MissileUnit.disabled)
                    continue;

                Vector3 radarPos = alert.RadarUnit.transform.position;
                Vector3 missilePos = alert.MissileUnit.transform.position;

                // Segment A: Detecting radar to missile
                while (_missileAlertLines.Count <= missileIndex)
                {
                    _missileAlertLines.Add(CreateLine("DatalinkMissileAlert_" + _missileAlertLines.Count, 3.0f));
                }
                SetLine(_missileAlertLines[missileIndex], radarPos, missilePos, missileWarnColor, 3.0f, camPos);
                missileIndex++;

                // Segment B: Missile to Player aircraft
                if (playerAircraft != null)
                {
                    while (_missileAlertLines.Count <= missileIndex)
                    {
                        _missileAlertLines.Add(CreateLine("DatalinkMissileAlert_" + _missileAlertLines.Count, 3.0f));
                    }
                    SetLine(_missileAlertLines[missileIndex], missilePos, playerPos, missileWarnColor, 3.0f, camPos);
                    missileIndex++;
                }
            }

            for (int i = missileIndex; i < _missileAlertLines.Count; i++)
            {
                if (_missileAlertLines[i].gameObject.activeSelf) _missileAlertLines[i].gameObject.SetActive(false);
            }
        }

        private void OnGUI()
        {
            if (!CnoEvilConfig.DatalinkOverlayOn)
            {
                return;
            }

            InitStyles();

            // 1. Draw 2D Screen-space reticles and badges (non-layout drawing: Repaint only)
            if (Event.current.type == EventType.Repaint)
            {
                DrawScreenSpaceReticles();
            }

            // 2. Draw Tactical Datalink Diagnostic Telemetry Window
            bool showPanel = CnoEvilConfig.ShowDatalinkDiagnosticPanel == null || CnoEvilConfig.ShowDatalinkDiagnosticPanel.Value;
            if (showPanel)
            {
                // Always clamp window within screen boundaries to prevent clipping
                float maxWinX = Mathf.Max(10f, Screen.width - Mathf.Max(460f, _windowRect.width) - 10f);
                float maxWinY = Mathf.Max(10f, Screen.height - Mathf.Max(100f, _windowRect.height) - 10f);
                _windowRect.x = Mathf.Clamp(_windowRect.x, 10f, maxWinX);
                _windowRect.y = Mathf.Clamp(_windowRect.y, 10f, maxWinY);

                _windowRect = GUILayout.Window(9482, _windowRect, DrawDiagnosticWindow, "", _windowStyle);

                _windowRect.x = Mathf.Clamp(_windowRect.x, 10f, maxWinX);
                _windowRect.y = Mathf.Clamp(_windowRect.y, 10f, maxWinY);
            }
        }

        private Rect GetBadgeRect(float guiX, float guiY, float width, float height)
        {
            float startX = guiX + 16f;
            float startY = guiY - height * 0.5f;

            if (startX + width > Screen.width - 15f)
            {
                startX = guiX - width - 16f;
            }

            startX = Mathf.Clamp(startX, 10f, Screen.width - width - 10f);
            startY = Mathf.Clamp(startY, 10f, Screen.height - height - 10f);

            return new Rect(startX, startY, width, height);
        }

        private void TryDrawBadge(Rect rect, string text, Color col)
        {
            for (int i = 0; i < _drawnBadgeRects.Count; i++)
            {
                if (rect.Overlaps(_drawnBadgeRects[i]))
                {
                    return;
                }
            }
            DrawBadgeCard(rect, text, col);
            _drawnBadgeRects.Add(rect);
        }

        private void DrawBadgeCard(Rect rect, string text, Color accentCol)
        {
            Color oldCol = GUI.color;
            GUI.color = new Color(0.04f, 0.08f, 0.14f, 0.90f);
            GUI.DrawTexture(rect, _whiteTex);

            DrawBoxOutline(rect, 1.0f, new Color(accentCol.r, accentCol.g, accentCol.b, 0.65f));
            GUI.color = oldCol;

            _hudTextStyle.normal.textColor = accentCol;
            GUI.Label(rect, text, _hudTextStyle);
        }

        private void AggregateFusedContacts()
        {
            var telemetry = DatalinkNetwork.LatestTelemetry;
            _fusedContacts.Clear();
            _fusedIndex.Clear();
            _trackCountAll = 0;
            _trackCountFix = 0;
            _trackCountMemory = 0;

            // The overlays run only in single player and on the host (CnoEvilConfig.DebugAllowed)
            bool linkActive = telemetry != null && telemetry.NetworkActive;
            if (!linkActive) return;

            Aircraft playerAircraft = CombatHUD.i != null ? CombatHUD.i.aircraft : null;
            FactionHQ playerHq = (telemetry != null && telemetry.PlayerHQ != null)
                ? telemetry.PlayerHQ
                : RWRTriangulationProcessor.GetPlayerFactionHQ();

            // Aggregate shared radar tracks
            for (int i = 0; linkActive && i < telemetry.SharedRadarTracks.Count; i++)
            {
                var track = telemetry.SharedRadarTracks[i];
                if (track.TargetUnit == null || track.TargetUnit.disabled || !ThreatClassification.IsTrackedThreat(track.TargetUnit, playerHq)) continue;

                float slantDistKm = track.SlantRangeMeters * 0.001f;
                int existingIdx;
                if (!_fusedIndex.TryGetValue(track.TargetUnit, out existingIdx)) existingIdx = -1;

                if (existingIdx < 0)
                {
                    FusedContact contact = new FusedContact
                    {
                        Target = track.TargetUnit,
                        TargetName = track.TargetName,
                        HasRadarTrack = true,
                        BestSourceName = track.SourceName,
                        BestDistanceKm = slantDistKm,
                        TotalFeeds = 1
                    };
                    _fusedIndex[contact.Target] = _fusedContacts.Count;
                    _fusedContacts.Add(contact);
                }
                else
                {
                    FusedContact contact = _fusedContacts[existingIdx];
                    contact.TotalFeeds++;
                    // Closest radar source (smallest slant range)
                    if (slantDistKm < contact.BestDistanceKm)
                    {
                        contact.BestSourceName = track.SourceName;
                        contact.BestDistanceKm = slantDistKm;
                    }
                    _fusedContacts[existingIdx] = contact;
                }
            }

            // Aggregate shared visual contacts
            for (int i = 0; linkActive && i < telemetry.SharedVisualContacts.Count; i++)
            {
                var v = telemetry.SharedVisualContacts[i];
                if (v.TargetUnit == null || v.TargetUnit.disabled || !ThreatClassification.IsTrackedThreat(v.TargetUnit, playerHq)) continue;

                float visDistKm = v.DistanceMeters * 0.001f;
                int existingIdx;
                if (!_fusedIndex.TryGetValue(v.TargetUnit, out existingIdx)) existingIdx = -1;

                if (existingIdx < 0)
                {
                    FusedContact contact = new FusedContact
                    {
                        Target = v.TargetUnit,
                        TargetName = v.TargetName,
                        IsVisual = true,
                        BestSourceName = v.SpotterName,
                        BestDistanceKm = visDistKm,
                        TotalFeeds = 1
                    };
                    _fusedIndex[contact.Target] = _fusedContacts.Count;
                    _fusedContacts.Add(contact);
                }
                else
                {
                    FusedContact contact = _fusedContacts[existingIdx];
                    contact.TotalFeeds++;
                    contact.IsVisual = true;
                    // Without a radar track the closest spotter is the source
                    if (!contact.HasRadarTrack && visDistKm < contact.BestDistanceKm)
                    {
                        contact.BestSourceName = v.SpotterName;
                        contact.BestDistanceKm = visDistKm;
                    }
                    _fusedContacts[existingIdx] = contact;
                }
            }

            // Read-only lookup of the faction tracks (every sensor: radar, visual, RWR / ESM, datalink)
            Dictionary<Unit, TriangulationTrack> factionTracks = RWRTriangulationProcessor.GetTrackDictionary(playerHq);
            float now = Time.timeSinceLevelLoad;
            Vector3 playerPos = (playerAircraft != null) ? playerAircraft.transform.position : Vector3.zero;

            if (factionTracks != null)
            {
                foreach (var kvp in factionTracks)
                {
                    Unit target = kvp.Key;
                    TriangulationTrack tr = kvp.Value;
                    if (target == null || target.disabled || tr == null) continue;

                    _trackCountAll++;
                    if (tr.IsTriangulated && tr.TriangulationErrorMeters > 0.0f) _trackCountFix++;
                    if (tr.IsInMemoryState()) _trackCountMemory++;

                    if (!ThreatClassification.IsTrackedThreat(target, playerHq)) continue;

                    int existingIdx;
                    if (!_fusedIndex.TryGetValue(target, out existingIdx)) existingIdx = -1;

                    FusedContact contact;
                    if (existingIdx >= 0)
                    {
                        contact = _fusedContacts[existingIdx];
                    }
                    else
                    {
                        // No shared radar or visual contact this cycle: passive (RWR / ESM) or memory track
                        contact = new FusedContact
                        {
                            Target = target,
                            TargetName = !string.IsNullOrEmpty(target.unitName) ? target.unitName : target.name,
                            IsRwr = true,
                            TotalFeeds = 1
                        };
                    }

                    FillFromFactionTrack(ref contact, tr, now);

                    if (contact.IsRwr)
                    {
                        // Distance from the player to the faction estimate (not to the true position)
                        contact.BestSourceName = contact.Method;
                        contact.BestDistanceKm = (contact.HasEstimate && playerAircraft != null)
                            ? Vector3.Distance(playerPos, contact.EstimatePos) * 0.001f
                            : 0.0f;
                    }

                    if (existingIdx >= 0)
                    {
                        _fusedContacts[existingIdx] = contact;
                    }
                    else
                    {
                        _fusedIndex[contact.Target] = _fusedContacts.Count;
                        _fusedContacts.Add(contact);
                    }
                }
            }

            // Text of the badges and panel rows: built here (every 0.15 s), not every OnGUI call
            for (int i = 0; i < _fusedContacts.Count; i++)
            {
                FusedContact fc = _fusedContacts[i];
                BuildContactText(ref fc);
                _fusedContacts[i] = fc;
            }
        }

        /// <summary>
        /// Q, weapon CEP, ring, method, faction estimate and memory age from the faction track.
        /// </summary>
        private static void FillFromFactionTrack(ref FusedContact fc, TriangulationTrack tr, float now)
        {
            fc.HasFactionTrack = true;
            fc.TrackingQuality = tr.TrackingQuality;
            // Weapon CEP of a launch now (TrackUncertaintyCalculator), not the PortalMFD ring
            fc.CepKm = TrackUncertaintyCalculator.CalculateCep(tr.TrackingQuality, tr.SensorRangeMeters) * 0.001f;
            fc.RingKm = tr.UncertaintyRadiusMeters * 0.001f;
            fc.IsInMemory = tr.IsInMemoryState();
            fc.IsTriangulated = tr.IsTriangulated;
            fc.MemoryAgeS = (tr.LastActiveDetectionTime != 0.0f) ? Mathf.Max(0.0f, now - tr.LastActiveDetectionTime) : 0.0f;
            fc.MemoryCapS = RWRTriangulationProcessor.GetTargetMemoryDuration(fc.Target);

            bool passiveFix = tr.IsTriangulated && tr.TriangulationErrorMeters > 0.0f;
            if (tr.HasRecentPreciseContact)
            {
                fc.Method = "radar / visual contact";
            }
            else if (passiveFix)
            {
                fc.Method = string.Format("passive fix 1σ {0:F1} km{1}", tr.TriangulationErrorMeters * 0.001f, tr.PassiveLocated ? " LOCATED" : "");
            }
            else if (fc.IsInMemory)
            {
                fc.Method = "memory";
            }
            else
            {
                fc.Method = "bearing only";
            }

            // The position the faction believes: fix centre, live precise contact, or the last known position
            fc.HasEstimate = true;
            if (passiveFix)
            {
                fc.EstimatePos = tr.EstimatedCenter.ToLocalPosition();
            }
            else if (tr.HasRecentPreciseContact)
            {
                fc.EstimatePos = fc.Target.transform.position;
            }
            else if (tr.LastKnownTime != 0.0f)
            {
                fc.EstimatePos = tr.LastKnownGlobalPosition.ToLocalPosition();
            }
            else
            {
                fc.HasEstimate = false;
            }
            fc.ErrVsTruthKm = fc.HasEstimate ? Vector3.Distance(fc.EstimatePos, fc.Target.transform.position) * 0.001f : 0.0f;
        }

        private static void BuildContactText(ref FusedContact fc)
        {
            string intel = fc.HasFactionTrack
                ? string.Format("Faction track: Q {0:P0} | CEP {1:F2} km | ring {2:F2} km", fc.TrackingQuality, fc.CepKm, fc.RingKm)
                : "No faction track";
            string mem = fc.IsInMemory ? string.Format(" | MEM {0:F0}/{1:F0} s", fc.MemoryAgeS, fc.MemoryCapS) : "";
            string err = fc.HasEstimate ? string.Format("\nEstimate err vs truth: {0:F2} km (debug)", fc.ErrVsTruthKm) : "";

            if (fc.HasRadarTrack)
            {
                fc.BadgeText = string.Format("◆ [DATALINK RADAR] {0}\nSource: {1} | R: {2:F1} km (source to target){3}\n{4}{5}{6}",
                    fc.TargetName, fc.BestSourceName, fc.BestDistanceKm, fc.IsVisual ? " + visual" : "", intel, mem, err);
            }
            else if (fc.IsVisual)
            {
                fc.BadgeText = string.Format("▲ [VISUAL] {0}\nSpotter: {1} | D: {2:F1} km (spotter to target)\n{3}{4}{5}",
                    fc.TargetName, fc.BestSourceName, fc.BestDistanceKm, intel, mem, err);
            }
            else
            {
                fc.BadgeText = string.Format("◎ [PASSIVE / MEMORY] {0}\nMethod: {1} | D: {2:F1} km (player to estimate)\n{3}{4}{5}",
                    fc.TargetName, fc.Method, fc.BestDistanceKm, intel, mem, err);
            }

            fc.QTag = fc.HasFactionTrack ? GetQPercentString(fc.TrackingQuality) : "Q: --";
            string prefix = fc.HasRadarTrack ? "◆" : (fc.IsVisual ? "▲" : "◎");
            fc.RowLeft = string.Format("{0} {1}{2}", prefix, fc.TargetName, fc.IsInMemory ? " [MEM]" : "");
            fc.RowRight = fc.HasFactionTrack
                ? string.Format("[Q: {0:P0}] {1} ({2:F1} km, CEP: {3:F2} km)", fc.TrackingQuality, fc.BestSourceName, fc.BestDistanceKm, fc.CepKm)
                : string.Format("[no faction track] {0} ({1:F1} km)", fc.BestSourceName, fc.BestDistanceKm);
        }

        private void DrawScreenSpaceReticles()
        {
            Camera cam = CameraStateManager.i != null && CameraStateManager.i.mainCamera != null ? CameraStateManager.i.mainCamera : Camera.main;
            if (cam == null) return;

            var telemetry = DatalinkNetwork.LatestTelemetry;
            if (telemetry == null || !telemetry.NetworkActive) return;

            _drawnBadgeRects.Clear();

            Vector2 screenCenter = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            float gazeRadius = Mathf.Min(Screen.width, Screen.height) * 0.11f;

            // 1. Badges over friendly nodes (icon always visible, badge on gaze hover)
            for (int i = 0; i < telemetry.Nodes.Count; i++)
            {
                var node = telemetry.Nodes[i];
                if (node.Unit == null || node.Unit.disabled) continue;

                Vector3 screenPos = cam.WorldToScreenPoint(node.Unit.transform.position);
                if (screenPos.z <= 1.0f) continue;

                float guiX = screenPos.x;
                float guiY = Screen.height - screenPos.y;
                if (guiX < 20 || guiX > Screen.width - 20 || guiY < 20 || guiY > Screen.height - 20) continue;

                Color nodeCol = new Color(0.20f, 0.75f, 1.0f);
                DrawBoxOutline(new Rect(guiX - 6, guiY - 6, 12, 12), 2.0f, nodeCol);

                if (Vector2.Distance(new Vector2(guiX, guiY), screenCenter) <= gazeRadius)
                {
                    string badgeText = string.Format("■ {0} [{1}]\nSharing: {2} radar / {3} visual",
                        node.UnitName, node.Role, node.SharedRadarCount, node.SharedVisualCount);

                    _calcContent.text = badgeText;
                    Vector2 textSize = _hudTextStyle.CalcSize(_calcContent);
                    float cardWidth = Mathf.Ceil(textSize.x) + 18f;
                    float cardHeight = Mathf.Ceil(textSize.y) + 8f;

                    Rect badgeRect = GetBadgeRect(guiX, guiY, cardWidth, cardHeight);
                    TryDrawBadge(badgeRect, badgeText, nodeCol);
                }
            }

            // 2. Aggregated contacts (fused in Update). Radar and visual contacts at the unit that the sensor sees;
            // passive and memory tracks at the faction estimate (no marker without an estimate)
            for (int i = 0; i < _fusedContacts.Count; i++)
            {
                var contact = _fusedContacts[i];
                if (contact.Target == null || contact.Target.disabled) continue;

                bool sensed = contact.HasRadarTrack || contact.IsVisual;
                if (!sensed && !contact.HasEstimate) continue;
                Vector3 markerWorldPos = sensed ? contact.Target.transform.position : contact.EstimatePos;
                Vector3 screenPos = cam.WorldToScreenPoint(markerWorldPos);
                if (screenPos.z <= 1.0f) continue;

                float guiX = screenPos.x;
                float guiY = Screen.height - screenPos.y;
                if (guiX < 20 || guiX > Screen.width - 20 || guiY < 20 || guiY > Screen.height - 20) continue;

                bool isHovered = Vector2.Distance(new Vector2(guiX, guiY), screenCenter) <= gazeRadius;

                Color col;
                if (contact.HasRadarTrack)
                {
                    col = new Color(0.20f, 0.95f, 0.55f);
                    DrawDiamondOutline(guiX, guiY, 14.0f, 2.0f, col);
                }
                else if (contact.IsVisual)
                {
                    col = new Color(1.0f, 0.85f, 0.15f);
                    DrawBoxOutline(new Rect(guiX - 7, guiY - 7, 14, 14), 2.0f, col);
                }
                else
                {
                    col = contact.IsTriangulated ? new Color(1.0f, 0.60f, 0.15f) : new Color(0.95f, 0.40f, 0.85f);
                    DrawDiamondOutline(guiX, guiY, 15.0f, 1.5f, col);
                    DrawBoxOutline(new Rect(guiX - 5, guiY - 5, 10, 10), 1.5f, col);
                }

                // Compact tracking quality indicator below reticle
                DrawReticleTag(guiX, guiY + 12f, contact.QTag, col);

                if (isHovered && contact.BadgeText != null)
                {
                    _calcContent.text = contact.BadgeText;
                    Vector2 textSize = _hudTextStyle.CalcSize(_calcContent);
                    float cardWidth = Mathf.Ceil(textSize.x) + 18f;
                    float cardHeight = Mathf.Ceil(textSize.y) + 8f;

                    Rect badgeRect = GetBadgeRect(guiX, guiY, cardWidth, cardHeight);
                    TryDrawBadge(badgeRect, contact.BadgeText, col);
                }
            }

            // 3. Badges over relayed missile warnings (icon always visible, badge on gaze hover)
            for (int i = 0; i < telemetry.RelayedMissileAlerts.Count; i++)
            {
                var alert = telemetry.RelayedMissileAlerts[i];
                if (alert.MissileUnit == null || alert.MissileUnit.disabled) continue;

                Vector3 screenPos = cam.WorldToScreenPoint(alert.MissileUnit.transform.position);
                if (screenPos.z <= 1.0f) continue;

                float guiX = screenPos.x;
                float guiY = Screen.height - screenPos.y;
                if (guiX < 20 || guiX > Screen.width - 20 || guiY < 20 || guiY > Screen.height - 20) continue;

                Color warnCol = new Color(1.0f, 0.22f, 0.22f);
                DrawBoxOutline(new Rect(guiX - 9, guiY - 9, 18, 18), 2.5f, warnCol);

                if (Vector2.Distance(new Vector2(guiX, guiY), screenCenter) <= gazeRadius)
                {
                    string warnText = string.Format("▼ [HOSTILE MISSILE, RELAYED] seen by {0}\nDist to player: {1:F1} km",
                        alert.RadarName, alert.DistToPlayer * 0.001f);

                    _calcContent.text = warnText;
                    Vector2 textSize = _hudTextStyle.CalcSize(_calcContent);
                    float cardWidth = Mathf.Ceil(textSize.x) + 18f;
                    float cardHeight = Mathf.Ceil(textSize.y) + 8f;

                    Rect warnRect = GetBadgeRect(guiX, guiY, cardWidth, cardHeight);
                    TryDrawBadge(warnRect, warnText, warnCol);
                }
            }
        }

        private void DrawDiagnosticWindow(int windowId)
        {
            var telemetry = DatalinkNetwork.LatestTelemetry;

            // Header Section
            GUILayout.BeginHorizontal();
            GUILayout.Label("TACTICAL ALLIED DATALINK", _titleStyle);
            bool online = telemetry != null && telemetry.NetworkActive;
            GUILayout.Label(online ? "[LINK ONLINE]" : "[OFFLINE]", online ? _badgeActiveStyle : _badgeInactiveStyle);
            GUILayout.EndHorizontal();

            KeyCode toggleKey = (CnoEvilConfig.ToggleDatalinkVisualizerKey != null) ? CnoEvilConfig.ToggleDatalinkVisualizerKey.Value : KeyCode.F8;
            GUILayout.Label(string.Format("Network Faction: {0} | Cycle: {1:F2}s | Toggle: {2}",
                telemetry != null ? telemetry.FactionName : "None",
                telemetry != null ? telemetry.CycleIntervalSeconds : 0.0f, toggleKey), _subHeaderStyle);

            DrawDivider();

            // Section 1: Connected Allied Nodes
            int nodeCount = telemetry != null ? telemetry.Nodes.Count : 0;
            GUILayout.Label(string.Format("FACTION DATALINK NODES ({0}, no range limit)", nodeCount), _sectionHeaderStyle);

            if (telemetry != null && telemetry.Nodes.Count > 0)
            {
                for (int i = 0; i < Mathf.Min(6, telemetry.Nodes.Count); i++)
                {
                    var node = telemetry.Nodes[i];
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(string.Format("• {0}", node.UnitName), _labelStyle, GUILayout.Width(220));
                    GUILayout.Label(string.Format("[{0}] Tracks: {1}", node.Role, node.SharedRadarCount + node.SharedVisualCount), _valueStyle);
                    GUILayout.EndHorizontal();
                }
                if (telemetry.Nodes.Count > 6)
                {
                    GUILayout.Label(string.Format("  ... and {0} more allied units connected", telemetry.Nodes.Count - 6), _subHeaderStyle);
                }
            }
            else
            {
                GUILayout.Label("  No allied datalink nodes currently transmitting.", _labelStyle);
            }

            DrawDivider();

            // Section 2: Shared Sensor Tracks & ESM Triangulation
            int radarCount = telemetry != null ? telemetry.SharedRadarTracks.Count : 0;
            int visualCount = telemetry != null ? telemetry.SharedVisualContacts.Count : 0;
            int rwrCount = 0;
            for (int i = 0; i < _fusedContacts.Count; i++)
            {
                if (_fusedContacts[i].IsRwr) rwrCount++;
            }

            GUILayout.Label(string.Format("FUSED THREAT TRACKS (radar reports: {0} | visual reports: {1} | passive / memory: {2})",
                radarCount, visualCount, rwrCount), _sectionHeaderStyle);

            if (_fusedContacts.Count > 0)
            {
                int shown = 0;
                for (int i = 0; i < _fusedContacts.Count && shown < 8; i++)
                {
                    var fc = _fusedContacts[i];
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(fc.RowLeft, _labelStyle, GUILayout.Width(170));
                    GUILayout.Label(fc.RowRight, _valueStyle);
                    GUILayout.EndHorizontal();
                    shown++;
                }
                if (_fusedContacts.Count > 8)
                {
                    GUILayout.Label(string.Format("  ... and {0} more tracks in database", _fusedContacts.Count - 8), _subHeaderStyle);
                }
            }
            else
            {
                GUILayout.Label("  No hostile tracks currently shared across network.", _labelStyle);
            }

            // Section 3: Relayed Threat Alerts (if any)
            if (telemetry != null && telemetry.RelayedMissileAlerts.Count > 0)
            {
                DrawDivider();
                GUILayout.Label(string.Format("RELAYED HOSTILE MISSILES ({0}, within 35 km)", telemetry.RelayedMissileAlerts.Count), _sectionHeaderStyle);
                for (int i = 0; i < telemetry.RelayedMissileAlerts.Count; i++)
                {
                    var m = telemetry.RelayedMissileAlerts[i];
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("▼ HOSTILE MISSILE", _labelStyle, GUILayout.Width(180));
                    GUILayout.Label(string.Format("seen by {0} ({1:F1} km from player)", m.RadarName, m.DistToPlayer * 0.001f), _valueStyle);
                    GUILayout.EndHorizontal();
                }
            }

            // Section 4: all faction tracks (counted in the aggregation)
            if (_trackCountAll > 0)
            {
                DrawDivider();
                GUILayout.Label(string.Format("FACTION TRACKS (all: {0} | passive fix: {1} | in memory: {2})",
                    _trackCountAll, _trackCountFix, _trackCountMemory), _sectionHeaderStyle);
            }

            GUI.DragWindow(new Rect(0, 0, 10000, 24));
        }

        private void DrawDivider()
        {
            GUILayout.Space(3);
            Rect r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(1), GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint && _dividerTex != null)
            {
                GUI.DrawTexture(r, _dividerTex);
            }
            GUILayout.Space(3);
        }

        private void DrawBoxOutline(Rect r, float thickness, Color col)
        {
            Color oldCol = GUI.color;
            GUI.color = col;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, thickness), _whiteTex);
            GUI.DrawTexture(new Rect(r.x, r.y + r.height - thickness, r.width, thickness), _whiteTex);
            GUI.DrawTexture(new Rect(r.x, r.y, thickness, r.height), _whiteTex);
            GUI.DrawTexture(new Rect(r.x + r.width - thickness, r.y, thickness, r.height), _whiteTex);
            GUI.color = oldCol;
        }

        private void DrawDiamondOutline(float cx, float cy, float radius, float thickness, Color col)
        {
            Vector3 top = new Vector3(cx, cy - radius, 0);
            Vector3 right = new Vector3(cx + radius, cy, 0);
            Vector3 bottom = new Vector3(cx, cy + radius, 0);
            Vector3 left = new Vector3(cx - radius, cy, 0);

            DrawGuiLine(top, right, thickness, col);
            DrawGuiLine(right, bottom, thickness, col);
            DrawGuiLine(bottom, left, thickness, col);
            DrawGuiLine(left, top, thickness, col);
        }

        private void DrawGuiLine(Vector3 p1, Vector3 p2, float width, Color col)
        {
            Color old = GUI.color;
            GUI.color = col;
            Matrix4x4 matrix = GUI.matrix;

            float angle = Vector3.Angle(p2 - p1, Vector2.right);
            if (p1.y > p2.y) angle = -angle;

            GUIUtility.ScaleAroundPivot(new Vector2((p2 - p1).magnitude, width), new Vector2(p1.x, p1.y + 0.5f));
            GUIUtility.RotateAroundPivot(angle, new Vector2(p1.x, p1.y + 0.5f));
            GUI.DrawTexture(new Rect(p1.x, p1.y, 1, 1), _whiteTex);

            GUI.matrix = matrix;
            GUI.color = old;
        }

        private void InitStyles()
        {
            if (_windowStyle != null) return;

            _windowStyle = new GUIStyle(GUI.skin.box);
            _windowStyle.normal.background = _windowBgTex;
            _windowStyle.padding = new RectOffset(14, 14, 10, 12);
            _windowStyle.border = new RectOffset(1, 1, 1, 1);

            _titleStyle = new GUIStyle(GUI.skin.label);
            _titleStyle.fontSize = 12;
            _titleStyle.fontStyle = FontStyle.Bold;
            _titleStyle.normal.textColor = new Color(0.20f, 0.85f, 1.0f);
            _titleStyle.alignment = TextAnchor.MiddleLeft;

            _badgeActiveStyle = new GUIStyle(GUI.skin.label);
            _badgeActiveStyle.fontSize = 11;
            _badgeActiveStyle.fontStyle = FontStyle.Bold;
            _badgeActiveStyle.normal.textColor = new Color(0.20f, 0.95f, 0.55f);
            _badgeActiveStyle.alignment = TextAnchor.MiddleRight;

            _badgeInactiveStyle = new GUIStyle(GUI.skin.label);
            _badgeInactiveStyle.fontSize = 11;
            _badgeInactiveStyle.fontStyle = FontStyle.Bold;
            _badgeInactiveStyle.normal.textColor = new Color(0.85f, 0.45f, 0.20f);
            _badgeInactiveStyle.alignment = TextAnchor.MiddleRight;

            _subHeaderStyle = new GUIStyle(GUI.skin.label);
            _subHeaderStyle.fontSize = 10;
            _subHeaderStyle.normal.textColor = new Color(0.55f, 0.65f, 0.75f);
            _subHeaderStyle.alignment = TextAnchor.MiddleLeft;

            _sectionHeaderStyle = new GUIStyle(GUI.skin.label);
            _sectionHeaderStyle.fontSize = 11;
            _sectionHeaderStyle.fontStyle = FontStyle.Bold;
            _sectionHeaderStyle.normal.textColor = new Color(0.35f, 0.78f, 0.95f);
            _sectionHeaderStyle.padding = new RectOffset(0, 0, 4, 2);

            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.fontSize = 11;
            _labelStyle.normal.textColor = new Color(0.72f, 0.80f, 0.90f);
            _labelStyle.wordWrap = false;
            _labelStyle.alignment = TextAnchor.MiddleLeft;

            _valueStyle = new GUIStyle(GUI.skin.label);
            _valueStyle.fontSize = 11;
            _valueStyle.fontStyle = FontStyle.Bold;
            _valueStyle.normal.textColor = new Color(0.92f, 0.96f, 1.0f);

            _hudTextStyle = new GUIStyle(GUI.skin.label);
            _hudTextStyle.fontSize = 11;
            _hudTextStyle.fontStyle = FontStyle.Bold;
            _hudTextStyle.padding = new RectOffset(0, 0, 0, 0);
            _hudTextStyle.margin = new RectOffset(0, 0, 0, 0);
            _hudTextStyle.alignment = TextAnchor.UpperLeft;
            _hudTextStyle.wordWrap = false;
            _hudTextStyle.clipping = TextClipping.Clip;

            _hudTagStyle = new GUIStyle(GUI.skin.label);
            _hudTagStyle.fontSize = 10;
            _hudTagStyle.fontStyle = FontStyle.Bold;
            _hudTagStyle.padding = new RectOffset(2, 2, 0, 0);
            _hudTagStyle.margin = new RectOffset(0, 0, 0, 0);
            _hudTagStyle.alignment = TextAnchor.MiddleCenter;
            _hudTagStyle.wordWrap = false;
        }

        private void DrawReticleTag(float cx, float cy, string text, Color accentCol)
        {
            if (_hudTagStyle == null) return;
            _calcContent.text = text;
            Vector2 textSize = _hudTagStyle.CalcSize(_calcContent);
            float w = Mathf.Ceil(textSize.x) + 6f;
            float h = Mathf.Ceil(textSize.y) + 2f;
            Rect tagRect = new Rect(cx - (w * 0.5f), cy, w, h);

            Color old = GUI.color;
            GUI.color = new Color(0.04f, 0.08f, 0.14f, 0.85f);
            GUI.DrawTexture(tagRect, _whiteTex);

            DrawBoxOutline(tagRect, 1.0f, new Color(accentCol.r, accentCol.g, accentCol.b, 0.65f));
            GUI.color = old;

            _hudTagStyle.normal.textColor = accentCol;
            GUI.Label(tagRect, text, _hudTagStyle);
        }
    }
}
