using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CnoEvil.Core
{
    /// <summary>
    /// Fixed pool of overlay items on the DynamicMap icon layer (map and minimap), built from the map's radarVisPrefab.
    /// A new icon layer destroys the pool and builds it again; Clear destroys it on scene load and plugin unload.
    /// </summary>
    public class MapOverlayPool<T> where T : class
    {
        public delegate T Factory(Transform parent, GameObject prefab, int index);

        private readonly List<T> _items;
        private readonly int _size;
        private readonly Factory _create;
        private readonly Func<T, GameObject> _root;
        private Transform _parent;

        /// <param name="root">The GameObject of an item, null when destroyed.</param>
        public MapOverlayPool(int size, Factory create, Func<T, GameObject> root)
        {
            _items = new List<T>(size);
            _size = size;
            _create = create;
            _root = root;
        }

        public int Count { get { return _items.Count; } }

        public T this[int index] { get { return _items[index]; } }

        public void Ensure(DynamicMap map)
        {
            Transform parent = map.iconLayer.transform;
            if (_parent != parent)
            {
                Clear();
                _parent = parent;
            }
            if (_items.Count >= _size) return;

            GameObject prefab = MapOverlayPool.RadarVisPrefab(map);
            while (_items.Count < _size)
            {
                _items.Add(_create(parent, prefab, _items.Count));
            }
        }

        public void HideFrom(int start)
        {
            for (int i = start; i < _items.Count; i++)
            {
                GameObject go = _root(_items[i]);
                if (go != null && go.activeSelf) go.SetActive(false);
            }
        }

        public void Clear()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                GameObject go = _root(_items[i]);
                if (go != null) UnityEngine.Object.Destroy(go);
            }
            _items.Clear();
            _parent = null;
        }
    }

    public static class MapOverlayPool
    {
        private static readonly Func<DynamicMap, GameObject> s_getRadarVisPrefab =
            FastReflection.CreateFieldGetter<DynamicMap, GameObject>("radarVisPrefab");

        public static GameObject RadarVisPrefab(DynamicMap map)
        {
            return (s_getRadarVisPrefab != null) ? s_getRadarVisPrefab(map) : null;
        }

        /// <summary>
        /// Image item: a copy of the prefab (the map's own icon look), else a plain Image. Not a raycast target, local transform reset.
        /// </summary>
        public static Image CreateImage(Transform parent, string name, GameObject prefab)
        {
            GameObject go;
            if (prefab != null)
            {
                go = UnityEngine.Object.Instantiate(prefab, parent);
                go.name = name;
            }
            else
            {
                go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                go.transform.SetParent(parent, false);
            }

            Image img = go.GetComponent<Image>();
            if (img == null) img = go.AddComponent<Image>();
            img.raycastTarget = false;
            img.transform.localPosition = Vector3.zero;
            img.transform.localEulerAngles = Vector3.zero;
            img.transform.localScale = Vector3.one;
            return img;
        }
    }
}
