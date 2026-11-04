using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using CnoEvil.Config;
using CnoEvil.Core;

namespace CnoEvil.Components
{
    /// <summary>
    /// Three sliders under the fuel slider of the loadout menu: flares, chaff and decoys. Each row is a copy of the vanilla
    /// FuelSelector row (label, value, right text, slider) without its fuel listener. The three counts together cannot go
    /// above CountermeasureBudget: a slider stops at the free part of the budget. The right panel grows by the height of
    /// the shown rows (its height is fixed in the scene). Lives on the LoadoutSelector object.
    /// </summary>
    public class CountermeasureSliders : MonoBehaviour
    {
        private const int RowCount = 3;
        private static readonly string[] Labels = { "Flares :", "Chaff :", "DRFM :" };
        private static readonly string[] RowNames = { "CnoEvil_FlareSelector", "CnoEvil_ChaffSelector", "CnoEvil_DecoySelector" };

        private readonly GameObject[] _rows = new GameObject[RowCount];
        private readonly Slider[] _sliders = new Slider[RowCount];
        private readonly TMP_Text[] _values = new TMP_Text[RowCount];
        private readonly TMP_Text[] _free = new TMP_Text[RowCount];
        private readonly int[] _counts = new int[RowCount];
        private readonly bool[] _shown = new bool[RowCount];

        private RectTransform _container;
        private RectTransform _panel;
        private float _panelBaseHeight;
        private float _rowStep;
        private AircraftDefinition _definition;
        private bool _hasFlares;
        private bool _lastModActive;
        private LoadoutSelector _selector;
        private int _podFlares;

        /// <summary>
        /// Builds the rows once next to the fuel row of this loadout selector.
        /// </summary>
        public static CountermeasureSliders Attach(LoadoutSelector selector, Slider fuelSlider)
        {
            if (selector == null || fuelSlider == null) return null;
            CountermeasureSliders sliders = selector.GetComponent<CountermeasureSliders>();
            if (sliders != null) return sliders;

            Transform fuelRow = fuelSlider.transform.parent;
            RectTransform container = (fuelRow != null) ? fuelRow.parent as RectTransform : null;
            if (container == null) return null;

            sliders = selector.gameObject.AddComponent<CountermeasureSliders>();
            sliders.Build(fuelRow as RectTransform, container);
            sliders._selector = selector;
            selector.onLoadoutChange += sliders.OnLoadoutChange;
            return sliders;
        }

        /// <summary>
        /// Flares of the selected flare pods (Aryx ECS-16 / ECS-32), shown as "+N" next to the flare count: they come on
        /// top of the budget, for the pylons they take.
        /// </summary>
        private void OnLoadoutChange()
        {
            try
            {
                _podFlares = 0;
                if (_selector != null && AryxWeaponsCompat.Installed)
                {
                    NuclearOption.SavedMission.Loadout loadout = _selector.GenerateLoadoutFromDropdowns();
                    if (loadout != null && loadout.weapons != null)
                    {
                        for (int i = 0; i < loadout.weapons.Count; i++)
                        {
                            _podFlares += AryxWeaponsCompat.PodFlares(loadout.weapons[i]);
                        }
                    }
                }
                UpdateTexts();
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CountermeasureSliders.OnLoadoutChange", ex);
            }
        }

        private void Build(RectTransform fuelRow, RectTransform container)
        {
            _container = container;
            // FuelSelector -> Container -> InfoPanel -> RightPanel
            _panel = (container.parent != null) ? container.parent.parent as RectTransform : null;
            if (_panel != null) _panelBaseHeight = _panel.sizeDelta.y;
            VerticalLayoutGroup layout = container.GetComponent<VerticalLayoutGroup>();
            _rowStep = fuelRow.rect.height + ((layout != null) ? layout.spacing : 0.0f);

            int index = fuelRow.GetSiblingIndex();
            for (int i = 0; i < RowCount; i++)
            {
                GameObject row = Instantiate(fuelRow.gameObject, container, false);
                row.name = RowNames[i];
                row.transform.SetSiblingIndex(index + 1 + i);
                _rows[i] = row;

                Slider slider = row.GetComponentInChildren<Slider>(true);
                // New event: the copy must not call LoadoutSelector.ChangeFuelLevel (persistent listener of the scene)
                slider.onValueChanged = new Slider.SliderEvent();
                slider.wholeNumbers = true;
                slider.minValue = 0.0f;
                slider.maxValue = CountermeasureLoadout.Budget;
                int captured = i;
                slider.onValueChanged.AddListener(delegate(float v) { OnSliderChanged(captured, v); });
                _sliders[i] = slider;

                TMP_Text label = FindText(row.transform, "FuelLabel");
                if (label != null) label.text = Labels[i];
                _values[i] = FindText(row.transform, "FuelPercentage");
                _free[i] = FindText(row.transform, "FuelWeight");
                row.SetActive(false);
            }
        }

        private static TMP_Text FindText(Transform row, string childName)
        {
            Transform t = row.Find(childName);
            return (t != null) ? t.GetComponent<TMP_Text>() : null;
        }

        /// <summary>
        /// LoadoutSelector.AssignAircraft: the saved choice of this aircraft type, rows only for an aircraft with flares.
        /// </summary>
        public void Show(Aircraft preview)
        {
            _definition = (preview != null) ? preview.definition : null;
            _hasFlares = CountermeasureLoadout.HasFlares(preview);
            CmCounts choice = CountermeasureLoadout.GetChoice(_definition);
            _counts[0] = choice.Flares;
            _counts[1] = choice.Chaff;
            _counts[2] = choice.Decoys;
            Refresh();
            OnLoadoutChange();
        }

        /// <summary>
        /// AircraftSelectionMenu.FlyAircraft: send the choice of the shown rows.
        /// </summary>
        public void Submit()
        {
            if (_definition == null || !_shown[0]) return;
            CountermeasureSync.SubmitChoice(_definition, Current());
        }

        private void Update()
        {
            // F10 while the menu is open
            if (CnoEvilConfig.IsModActive != _lastModActive) Refresh();
        }

        private void Refresh()
        {
            _lastModActive = CnoEvilConfig.IsModActive;
            bool active = _lastModActive && _hasFlares && _definition != null;
            _shown[0] = active;
            _shown[1] = active && CnoEvilConfig.ChaffEnabled.Value;
            _shown[2] = active && CnoEvilConfig.DecoyEnabled.Value;

            int budget = CountermeasureLoadout.Budget;
            CmCounts clamped = Current().Clamped(budget);
            if (_shown[0]) _counts[0] = clamped.Flares;
            if (_shown[1]) _counts[1] = clamped.Chaff;
            if (_shown[2]) _counts[2] = clamped.Decoys;

            int shownRows = 0;
            for (int i = 0; i < RowCount; i++)
            {
                if (_rows[i] == null) continue;
                _rows[i].SetActive(_shown[i]);
                if (!_shown[i]) continue;
                shownRows++;
                _sliders[i].maxValue = budget;
                _sliders[i].SetValueWithoutNotify(_counts[i]);
            }

            if (_panel != null) _panel.sizeDelta = new Vector2(_panel.sizeDelta.x, _panelBaseHeight + shownRows * _rowStep);
            if (_container != null) LayoutRebuilder.MarkLayoutForRebuild(_container);
            UpdateTexts();
        }

        private void OnSliderChanged(int index, float value)
        {
            try
            {
                int others = 0;
                for (int i = 0; i < RowCount; i++)
                {
                    if (i != index && _shown[i]) others += _counts[i];
                }
                int max = Mathf.Max(0, CountermeasureLoadout.Budget - others);
                int count = Mathf.Clamp(Mathf.RoundToInt(value), 0, max);
                if (count != Mathf.RoundToInt(value)) _sliders[index].SetValueWithoutNotify(count);
                _counts[index] = count;
                CountermeasureLoadout.SetChoice(_definition, Current());
                UpdateTexts();
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CountermeasureSliders.OnSliderChanged", ex);
            }
        }

        private CmCounts Current()
        {
            return new CmCounts(_shown[0] ? _counts[0] : 0, _shown[1] ? _counts[1] : 0, _shown[2] ? _counts[2] : 0);
        }

        private void UpdateTexts()
        {
            int free = Mathf.Max(0, CountermeasureLoadout.Budget - Current().Total);
            string freeText = "free " + free;
            for (int i = 0; i < RowCount; i++)
            {
                if (!_shown[i]) continue;
                if (_values[i] != null)
                    _values[i].text = (i == 0 && _podFlares > 0) ? _counts[i] + " +" + _podFlares : _counts[i].ToString();
                if (_free[i] != null) _free[i].text = freeText;
            }
        }

        private void OnDestroy()
        {
            if (_selector != null) _selector.onLoadoutChange -= OnLoadoutChange;
            CountermeasureLoadout.Save();
        }
    }
}
