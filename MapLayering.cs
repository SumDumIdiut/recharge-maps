using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Tilemaps;

// The editor's stack in the game. Each placed thing keeps the game's own
// drawing order, except that it's lifted in front of anything it overlaps
// that's below it in the stack - so a decoration far from everything never
// jumps in front of the walls, but one put over a spike draws over it.
// Sprites sort by their renderers; course screens (UI) by their canvas.
internal static class MapLayering
{
    public static void Apply(MapWorld w)
    {
        // What's behind the level keeps its place there: it's never lifted over what it overlaps.
        var behind = new HashSet<GameObject>(w.Behind.Concat(w.BehindWalls));
        PutBehind(w);
        var stacked = w.Stacked.Where(s => s.go != null && !behind.Contains(s.go)).OrderBy(s => s.order).ToList();
        // Kept, so things built later (the course screens) can take their place among them.
        w.Layered.AddRange(stacked);
        var items = stacked.Select(s => Sortable.Of(s.go)).Where(r => r.Length > 0).ToList();
        var done = new List<Sortable[]>();
        foreach (var parts in items)
        {
            var bounds = Bounds(parts);
            foreach (var below in done)
            {
                if (!bounds.Intersects(Bounds(below))) continue;
                var top = below.OrderByDescending(r => SortingLayer.GetLayerValueFromID(r.LayerId)).ThenByDescending(r => r.Order).First();
                var topLayer = SortingLayer.GetLayerValueFromID(top.LayerId);
                foreach (var r in parts)
                {
                    var layer = SortingLayer.GetLayerValueFromID(r.LayerId);
                    if (layer > topLayer) continue;
                    if (layer < topLayer) r.LayerId = top.LayerId;
                    if (r.Order <= top.Order) r.Order = top.Order + 1;
                }
            }
            done.Add(parts);
        }
        w.Stacked.Clear();
    }

    // Things built after the map's objects, placed in the stack among them.
    public static void Restack(MapWorld w)
    {
        if (w.Stacked.Count == 0 && w.Behind.Count == 0 && w.BehindWalls.Count == 0) return;
        w.Stacked.AddRange(w.Layered);
        w.Layered.Clear();
        Apply(w);
    }

    // Behind the level: on the ground's sorting layer, shifted so the thing's top
    // order is -1 - under the ground (5) and objects, over the backgrounds (< 0),
    // keeping its own parts' order among themselves.
    private static void PutBehind(MapWorld w)
    {
        var ground = w.RealTilemap("ground")?.GetComponent<TilemapRenderer>();
        foreach (var go in w.Behind)
        {
            if (go == null) continue;
            var parts = Sortable.Of(go);
            if (parts.Length == 0) continue;
            if (ground != null) foreach (var r in parts) r.LayerId = ground.sortingLayerID;
            var shift = -1 - parts.Max(r => r.Order);
            if (shift < 0) foreach (var r in parts) r.Order += shift;
        }
        w.Behind.Clear();
        // Behind the walls: just under the lowest of the level's tile layers.
        var lowest = UnityEngine.Object.FindObjectsByType<TilemapRenderer>(FindObjectsSortMode.None)
            .Where(r => r != null && !MapWorld.IsMapObject(r.transform))
            .OrderBy(r => SortingLayer.GetLayerValueFromID(r.sortingLayerID)).ThenBy(r => r.sortingOrder).FirstOrDefault();
        foreach (var go in w.BehindWalls)
        {
            if (go == null) continue;
            var parts = Sortable.Of(go);
            if (parts.Length == 0) continue;
            if (lowest != null) foreach (var r in parts) r.LayerId = lowest.sortingLayerID;
            var top = (lowest != null ? lowest.sortingOrder : -20) - 1;
            var shift = top - parts.Max(r => r.Order);
            foreach (var r in parts) r.Order += shift;
        }
        w.BehindWalls.Clear();
    }

    private static Bounds Bounds(Sortable[] parts)
    {
        var b = parts[0].Bounds;
        foreach (var r in parts) b.Encapsulate(r.Bounds);
        b.extents = new Vector3(b.extents.x, b.extents.y, 1000f);
        return b;
    }

    // A thing's sorted parts: its renderers, and the canvases its UI sorts by.
    private sealed class Sortable
    {
        private Renderer _r;
        private Canvas _c;

        public static Sortable[] Of(GameObject go) =>
            go.GetComponentsInChildren<Renderer>(true).Where(r => !(r is ParticleSystemRenderer)).Select(r => new Sortable { _r = r })
                .Concat(go.GetComponentsInChildren<Canvas>(true).Where(c => c.isRootCanvas || c.overrideSorting).Select(c => new Sortable { _c = c }))
                .ToArray();

        public int LayerId
        {
            get => _r != null ? _r.sortingLayerID : _c.sortingLayerID;
            set { if (_r != null) _r.sortingLayerID = value; else _c.sortingLayerID = value; }
        }

        public int Order
        {
            get => _r != null ? _r.sortingOrder : _c.sortingOrder;
            set { if (_r != null) _r.sortingOrder = value; else _c.sortingOrder = value; }
        }

        public Bounds Bounds
        {
            get
            {
                if (_r != null) return _r.bounds;
                var corners = new Vector3[4];
                ((RectTransform)_c.transform).GetWorldCorners(corners);
                var b = new Bounds(corners[0], Vector3.zero);
                foreach (var p in corners) b.Encapsulate(p);
                return b;
            }
        }
    }
}
