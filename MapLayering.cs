using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// The editor's stack in the game. Each placed thing keeps the game's own
// drawing order, except that it's lifted in front of anything it overlaps
// that's below it in the stack - so a decoration far from everything never
// jumps in front of the walls, but one put over a spike draws over it.
internal static class MapLayering
{
    public static void Apply(MapWorld w)
    {
        var items = w.Stacked.Where(s => s.go != null).OrderBy(s => s.order)
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

    private static Bounds Bounds(Renderer[] renderers)
    {
        var b = renderers[0].bounds;
        foreach (var r in renderers) b.Encapsulate(r.bounds);
        b.extents = new Vector3(b.extents.x, b.extents.y, 1000f);
        return b;
    }
}
