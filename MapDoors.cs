using System.Collections.Generic;
using Recharge.ModApi;
using UnityEngine;

// The level's boxes that open a door (the facility's lower section, the atom
// room's gate): a copied box opens its copied door, and every such door
// starts shut until the map's own save has bought its box.
internal static class MapDoors
{
    public static void Apply(MapWorld w)
    {
        foreach (var (box, doorPath) in w.DoorLinks)
        {
            if (box == null || !w.ClonesByPath.TryGetValue(doorPath, out var door) || door == null) continue;
            var ub = box.GetComponentInChildren<upgradeBox>(true);
            var mover = door.GetComponentInChildren<PlatformMover>(true);
            if (ub != null && mover != null) Reflect.TrySetField(ub, "doorToOpen", mover);
        }
        w.DoorLinks.Clear();

        foreach (var ub in Resources.FindObjectsOfTypeAll<upgradeBox>())
        {
            if (ub == null || !ub.gameObject.scene.IsValid()) continue;
            if (ub.globalUpgrade != globalStats.globalUpgradeSet.area2DoorOpen && ub.globalUpgrade != globalStats.globalUpgradeSet.spawnNewAtom) continue;
            var mover = Reflect.TryGetField<PlatformMover>(ub, "doorToOpen");
            if (mover == null) continue;
            // Bought in this map's own save: its upgrade count, not the box (which can still hold the player's main save).
            var bought = globalStats.globalUpgradeDict.TryGetValue(ub.globalUpgrade, out var have) && have > 0.0;
            var copied = MapWorld.IsMapObject(ub.transform);
            // A copied box opens its door itself as it starts; only shut it here.
            if (copied && bought) continue;
            var was = Reflect.TryGetField(mover, "indexInPositions", 0);
            var want = bought ? Mathf.Max(1, was) : 0;
            if (want == was) continue;
            mover.JumpToState(want);
            if (!MapWorld.IsMapObject(mover.transform))
            {
                var m = mover;
                w.OnUnload(() => { if (m != null) m.JumpToState(was); });
            }
        }
    }
}

// The level's long-fall zones (the drops at the ends of courses 7 and 9, the
// long fall in area 1) belong to their zone and only work while the game has
// that zone loaded. A map can put the player there without the zone's loading
// trigger (a spawn, a spawn switch, a teleporter), so it keeps a working copy
// of each one - unless the map deleted it.
internal static class MapLongFalls
{
    public static void Apply(MapWorld w)
    {
        if (!w.Overlay) return;
        int n = 0;
        foreach (var lf in Resources.FindObjectsOfTypeAll<longFallColliderController>())
        {
            if (lf == null || !lf.gameObject.scene.IsValid() || MapWorld.IsMapObject(lf.transform) || !lf.gameObject.activeSelf) continue;
            var copy = Object.Instantiate(lf.gameObject, lf.transform.position, lf.transform.rotation);
            copy.name = lf.name + " (map)";
            copy.transform.localScale = lf.transform.lossyScale;
            copy.transform.SetParent(w.Root, true);
            copy.SetActive(true);
            n++;
        }
        Debug.Log("[RechargeMaps] kept " + n + " long-fall zones working in every zone");
    }
}
