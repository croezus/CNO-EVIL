using System.Collections.Generic;
using System.Text;
using UnityEngine;
using TMPro;
using CnoEvil.Core;
using CnoEvil.Config;
using CnoEvil.Patches;

namespace CnoEvil.Components
{
    /// <summary>
    /// Datalink page of the cockpit flight instrument screen (CockpitAttitudeDisplay): the nearest friendly nodes as a
    /// list with distances, then "+N MORE", the datalink tier and the network counters. It reads only local data (node
    /// registry, faction tracks, own sensors), so it works the same on host and client. Hostile units show only as counts.
    /// </summary>
    public class CockpitDatalinkPage
    {
        private const int MaxNodes = 16;
        private const float RefreshRate_s = 1f;
        private const float TopRow = 18f;
        private const float BottomRow = 18f;
        private const float SideInset = 4f;
        private const float DefaultLineFactor = 1.25f;
        private const float DefaultCharFactor = 0.5f;
        // Columns of the distance: "123km" and a gap
        private const int DistanceColumns = 6;
        private const float MetersPerKilometer = 1000f;
        private const float MetersPerNauticalMile = 1852f;
        private const string RadarNodeColor = "#FFFFFF";
        private const string NodeColor = "#33D94D";

        private static readonly Vector2 Mid = new Vector2(0.5f, 0.5f);
        private static readonly Color DividerColor = new Color(0.2f, 0.85f, 0.3f, 0.6f);

        private readonly CockpitAttitudeDisplay _host;
        private readonly Aircraft _own;
        private readonly TargetDetector[] _sensors;
        private readonly HashSet<Unit> _ownContacts = new HashSet<Unit>();
        private readonly StringBuilder _nameRows = new StringBuilder();
        private readonly StringBuilder _distanceRows = new StringBuilder();

        private readonly Unit[] _nodes = new Unit[MaxNodes];
        private readonly float[] _nodeDistSq = new float[MaxNodes];
        private int _nodeCount;

        private RectTransform _root;
        private TextMeshProUGUI _tierText;
        private TextMeshProUGUI _nodeText;
        private TextMeshProUGUI _txText;
        private TextMeshProUGUI _rxText;
        private TextMeshProUGUI _namesText;
        private TextMeshProUGUI _distancesText;
        private TextMeshProUGUI _offText;
        private int _rows;
        private int _maxNameChars;

        private float _nextRefresh;
        private int _lastNodes = -1;
        private int _lastTx = -1;
        private int _lastRx = -1;
        private bool _linkOn = true;

        public CockpitDatalinkPage(CockpitAttitudeDisplay host, Aircraft own, RectTransform parent, float width, float height)
        {
            _host = host;
            _own = own;
            _sensors = own.GetComponentsInChildren<TargetDetector>(true);
            Build(parent, width, height);
        }

        public void SetVisible(bool on)
        {
            _root.gameObject.SetActive(on);
            if (on) _nextRefresh = 0f;
        }

        private void Build(RectTransform parent, float width, float height)
        {
            _root = PortalMFDGraphics.At("DatalinkPage", parent, Mid, Vector2.zero, new Vector2(width, height));
            PortalMFDGraphics.AddImage(_root, Color.black, null);

            float fontSize = height * 0.055f;
            float inner = width - SideInset * 2f;
            float topY = (height - TopRow) * 0.5f;
            float bottomY = -(height - BottomRow) * 0.5f;
            Vector2 row = new Vector2(inner, TopRow);

            _tierText = RowText("Tier", new Vector2(0f, topY), row, fontSize, TextAlignmentOptions.Left);
            _nodeText = RowText("Nodes", new Vector2(0f, topY), row, fontSize, TextAlignmentOptions.Right);
            _txText = RowText("Tx", new Vector2(0f, bottomY), row, fontSize, TextAlignmentOptions.Left);
            _rxText = RowText("Rx", new Vector2(0f, bottomY), row, fontSize, TextAlignmentOptions.Right);

            Vector2 divider = new Vector2(inner, 1f);
            PortalMFDGraphics.Box("DividerTop", _root, Mid, new Vector2(0f, height * 0.5f - TopRow), divider, DividerColor);
            PortalMFDGraphics.Box("DividerBottom", _root, Mid, new Vector2(0f, -height * 0.5f + BottomRow), divider, DividerColor);

            float listHeight = height - TopRow - BottomRow - 4f;
            Vector2 listPos = new Vector2(0f, (BottomRow - TopRow) * 0.5f);
            Vector2 listSize = new Vector2(inner, listHeight);
            _namesText = RowText("Names", listPos, listSize, fontSize, TextAlignmentOptions.TopLeft);
            _distancesText = RowText("Distances", listPos, listSize, fontSize, TextAlignmentOptions.TopRight);

            _rows = Mathf.Clamp(Mathf.FloorToInt(listHeight / RowHeight(fontSize)), 1, MaxNodes);
            _maxNameChars = Mathf.Max(4, Mathf.FloorToInt(inner / CharWidth(fontSize)) - DistanceColumns);

            _offText = RowText("LinkOff", listPos, new Vector2(inner, TopRow), fontSize * 1.3f, TextAlignmentOptions.Center);
            _offText.text = "LINK OFF";
            _offText.enabled = false;
        }

        /// <summary>Height of one list row, measured by TMP from the font and size of the list.</summary>
        private float RowHeight(float fontSize)
        {
            float height = _namesText.GetPreferredValues("0\n0").y - _namesText.GetPreferredValues("0").y;
            return height > 0f ? height : fontSize * DefaultLineFactor;
        }

        /// <summary>Width of one character of the monospaced font, measured by TMP.</summary>
        private float CharWidth(float fontSize)
        {
            float width = _namesText.GetPreferredValues("0000000000").x * 0.1f;
            return width > 0f ? width : fontSize * DefaultCharFactor;
        }

        private TextMeshProUGUI RowText(string textName, Vector2 pos, Vector2 size, float fontSize, TextAlignmentOptions alignment)
        {
            TextMeshProUGUI t = _host.NewText(textName, _root, pos, size, string.Empty, fontSize, _host.Font);
            t.alignment = alignment;
            return t;
        }

        public void Tick()
        {
            float now = Time.unscaledTime;
            if (now < _nextRefresh) return;
            _nextRefresh = now + RefreshRate_s;
            Refresh();
        }

        private void Refresh()
        {
            RefreshTier();
            bool linkOn = CnoEvilConfig.EnableAlliedDatalink.Value;
            if (linkOn != _linkOn)
            {
                _linkOn = linkOn;
                _offText.enabled = !linkOn;
                _lastNodes = _lastTx = _lastRx = -1;
            }
            if (!linkOn)
            {
                _nodeCount = 0;
                _nodeText.text = _txText.text = _rxText.text = _namesText.text = _distancesText.text = string.Empty;
                return;
            }

            int total = CollectNodes();
            RefreshList(total);

            int tx = CollectOwnContacts();
            int rx = CountReceivedContacts();
            if (total != _lastNodes)
            {
                _lastNodes = total;
                _nodeText.text = "NODE " + total;
            }
            if (tx != _lastTx)
            {
                _lastTx = tx;
                _txText.text = "TX " + tx;
            }
            if (rx != _lastRx)
            {
                _lastRx = rx;
                _rxText.text = "RX " + rx;
            }
        }

        /// <summary>The relay rate comes from the base tier; ELINT is added to it, so both show.</summary>
        private void RefreshTier()
        {
            PhysRWRReceiver rwr = PhysRWRReceiver.Get(_own);
            string tier = "DL";
            if (rwr != null)
            {
                bool advanced = rwr.Capabilities.Datalink == DatalinkTier.Advanced;
                if (rwr.IsTier4) tier = advanced ? "ADV+EL" : "BAS+EL";
                else tier = advanced ? "DL ADV" : "DL BAS";
            }
            if (_tierText.text != tier) _tierText.text = tier;
        }

        /// <summary>The nearest friendly nodes, sorted by distance. Returns the count of all friendly nodes.</summary>
        private int CollectNodes()
        {
            _nodeCount = 0;
            FactionHQ hq = _own.NetworkHQ;
            if (hq == null) return 0;

            int total = 0;
            Vector3 ownPos = _own.transform.position;
            List<Unit> units = DatalinkNetwork.OperationalUnits;
            for (int i = 0; i < units.Count; i++)
            {
                Unit u = units[i];
                if (u == null || u == _own || u.disabled || u is Missile || u.NetworkHQ != hq) continue;
                total++;

                Vector3 d = u.transform.position - ownPos;
                float distSq = d.x * d.x + d.z * d.z;
                if (_nodeCount == MaxNodes && distSq >= _nodeDistSq[MaxNodes - 1]) continue;

                int slot = _nodeCount < MaxNodes ? _nodeCount++ : MaxNodes - 1;
                while (slot > 0 && _nodeDistSq[slot - 1] > distSq)
                {
                    _nodes[slot] = _nodes[slot - 1];
                    _nodeDistSq[slot] = _nodeDistSq[slot - 1];
                    slot--;
                }
                _nodes[slot] = u;
                _nodeDistSq[slot] = distSq;
            }
            return total;
        }

        /// <summary>As many rows as fit. When some nodes do not fit, the last row gives their count.</summary>
        private void RefreshList(int total)
        {
            _nameRows.Length = 0;
            _distanceRows.Length = 0;

            bool metric = PlayerSettings.unitSystem == 0;
            float metersPerUnit = metric ? MetersPerKilometer : MetersPerNauticalMile;
            string unit = metric ? "km" : "nm";
            int shown = total > _rows ? _rows - 1 : Mathf.Min(_nodeCount, _rows);

            for (int i = 0; i < shown; i++)
            {
                Unit u = _nodes[i];
                bool radar = u != null && u.radar != null && u.radar.IsOperational();
                string name = UnitNames.NameOf(u);
                int length = Mathf.Min(name.Length, _maxNameChars);
                // A pilot name can hold rich text tags
                _nameRows.Append("<color=").Append(radar ? RadarNodeColor : NodeColor).Append("><noparse>")
                    .Append(name, 0, length).Append("</noparse></color>\n");
                _distanceRows.Append(Mathf.RoundToInt(Mathf.Sqrt(_nodeDistSq[i]) / metersPerUnit)).Append(unit).Append('\n');
            }
            if (total > shown) _nameRows.Append('+').Append(total - shown).Append(" MORE");
            else if (total == 0) _nameRows.Append("NO NODES");

            _namesText.text = _nameRows.ToString();
            _distancesText.text = _distanceRows.ToString();
        }

        /// <summary>Hostile tracked threats that the sensors of the own aircraft see now: what this aircraft gives to the network.</summary>
        private int CollectOwnContacts()
        {
            _ownContacts.Clear();
            FactionHQ hq = _own.NetworkHQ;
            for (int i = 0; i < _sensors.Length; i++)
            {
                TargetDetector sensor = _sensors[i];
                if (sensor == null || sensor.detectedTargets == null) continue;
                for (int k = 0; k < sensor.detectedTargets.Count; k++)
                {
                    Unit u = sensor.detectedTargets[k];
                    if (u == null || u.disabled || u is Missile || u.NetworkHQ == hq) continue;
                    if (ThreatClassification.IsTrackedThreat(u, hq)) _ownContacts.Add(u);
                }
            }
            return _ownContacts.Count;
        }

        /// <summary>Located faction tracks that the own sensors do not see: what the network gives to this aircraft.</summary>
        private int CountReceivedContacts()
        {
            FactionHQ hq = RWRTriangulationProcessor.GetPlayerFactionHQ();
            Dictionary<Unit, TriangulationTrack> tracks = hq != null ? RWRTriangulationProcessor.GetTrackDictionary(hq) : null;
            if (tracks == null) return 0;

            int count = 0;
            foreach (KeyValuePair<Unit, TriangulationTrack> kv in tracks)
            {
                Unit u = kv.Key;
                TriangulationTrack track = kv.Value;
                if (u == null || u.disabled || u is Missile || track == null || track.LastKnownTime == 0f) continue;
                if (_ownContacts.Contains(u) || !ThreatClassification.IsTrackedThreat(u, hq)) continue;
                if (UnitMapIcon_UpdateIcon_Patch.IsLocated(track)) count++;
            }
            return count;
        }
    }
}
