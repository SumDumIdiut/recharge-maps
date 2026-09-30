using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Tilemaps;

// The editor's stack in the game. Each placed thing keeps the game's own
// drawing order, except that it's lifted in front of anything it overlaps
// that's below it in the stack - so a decoration far from everything never
// jumps in front of the walls, but one put over a spike draws over it.
internal static class MapLayering
{
    public static void Apply(MapWorld w)
    {
        // What's behind the level keeps its place there: it's never lifted over what it overlaps.
        var behind = new HashSet<GameObject>(w.Behind.Concat(w.BehindWalls));
        PutBehind(w);
        var items = w.Stacked.Where(s => s.go != null && !behind.Contains(s.go)).OrderBy(s => s.order)
            .Select(s => s.go.GetComponentsInChildren<Renderer>(true).Where(r => !(r is ParticleSystemRenderer)).ToArray())
            .Where(r => r.Length > 0).ToList();
        var done = new List<Renderer[]>();
        foreach (var renderers in items)
        {
            var bounds = Bounds(renderers);
            foreach (var below in done)
            {
                if (!bounds.Intersects(Bounds(below))) continue;
                var top = below.OrderByDescending(r => SortingLayer.GetLayerValueFromID(r.sortingLayerID)).ThenByDescending(r => r.sortingOrder).First();
                var topLayer = SortingLayer.GetLayerValueFromID(top.sortingLayerID);
                foreach (var r in renderers)
                {
                    var layer = SortingLayer.GetLayerValueFromID(r.sortingLayerID);
                    if (layer > topLayer) continue;
                    if (layer < topLayer) r.sortingLayerID = top.sortingLayerID;
                    if (r.sortingOrder <= top.sortingOrder) r.sortingOrder = top.sortingOrder + 1;
                }
            }
            done.Add(renderers);
        }
        w.Stacked.Clear();
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
            var renderers = go.GetComponentsInChildren<Renderer>(true).Where(r => !(r is ParticleSystemRenderer)).ToArray();
            if (renderers.Length == 0) continue;
            if (ground != null) foreach (var r in renderers) r.sortingLayerID = ground.sortingLayerID;
            var shift = -1 - renderers.Max(r => r.sortingOrder);
            if (shift < 0) foreach (var r in renderers) r.sortingOrder += shift;
        }
        w.Behind.Clear();
        // Behind the walls: just under the lowest of the level's tile layers.
        var lowest = UnityEngine.Object.FindObjectsByType<TilemapRenderer>(FindObjectsSortMode.None)
            .Where(r => r != null && !MapWorld.IsMapObject(r.transform))
            .OrderBy(r => SortingLayer.GetLayerValueFromID(r.sortingLayerID)).ThenBy(r => r.sortingOrder).FirstOrDefault();
        foreach (var go in w.BehindWalls)
        {
            if (go == null) continue;
            var renderers = go.GetComponentsInChildren<Renderer>(true).Where(r => !(r is ParticleSystemRenderer)).ToArray();
            if (renderers.Length == 0) continue;
            if (lowest != null) foreach (var r in renderers) r.sortingLayerID = lowest.sortingLayerID;
            var top = (lowest != null ? lowest.sortingOrder : -20) - 1;
            var shift = top - renderers.Max(r => r.sortingOrder);
            foreach (var r in renderers) r.sortingOrder += shift;
        }
        w.BehindWalls.Clear();
    }

    private static Bounds Bounds(Renderer[] renderers)
    {
        var b = renderers[0].bounds;
        foreach (var r in renderers) b.Encapsulate(r.bounds);
        b.extents = new Vector3(b.extents.x, b.extents.y, 1000f);
        return b;
    }
}
