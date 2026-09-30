using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Putting the player into a loaded map, and the camera on them.
//
// A new game starts at the map's spawn (an overlay map with no spawn placed
// keeps the game's own start). Continuing a map's save starts where the save
// left off: the game already placed the player at their last checkpoint -
// for a custom map that's in the pocket, which only exists once the map is
// built, so they're put back there now.
internal static class MapSpawn
{
    public const float DefaultCamSize = 752f;
    // Movement.respawn drops the player 12 below their respawn point.
    private static readonly Vector2 RespawnLift = new Vector2(0f, 12f);
    // Anything this near the pocket belongs to a custom map.
    private const float PocketReach = 25000f;

    public static void Place(MapWorld w, MonoBehaviour host)
    {
        _spawnIndex = 0;
        var movement = MapUpgrades.GamePlayer();
        if (movement == null) { Debug.LogWarning("[RechargeMaps] no gameplay Player to put into the map"); return; }
        var player = movement.gameObject;
        var camSize = w.Def.CameraSize > 0 ? w.Def.CameraSize.Value : DefaultCamSize;
        var continuing = MapSaves.ActiveMapId == w.MapId && !MapSaves.StartedFresh;

        if (!w.Overlay)
        {
            // The pocket is far from the level's lights; without their own the player sees nothing.
            movement.lightActive = true;
            if (movement.personalLight != null) movement.personalLight.enabled = true;
        }

        Vector2 target;
        var atSpawn = true;
        if (continuing && (w.Overlay || (movement.respawnPoint - w.Origin).magnitude < PocketReach))
        {
            if (w.Overlay) { FollowWithCamera(movement, player.transform.position, camSize, host); return; }
            target = movement.respawnPoint;
            atSpawn = false;
        }
        else if (w.Overlay && w.Group.KeepSpawn)
        {
            FollowWithCamera(movement, player.transform.position, camSize, host);
            return;
        }
        else target = w.LevelPoint(w.Group.SpawnX ?? w.Group.StartX, w.Group.SpawnY ?? w.Group.StartY);

        if (w.Overlay) LoadZoneAt(target);
        MoveTo(movement, MapWorld.Live(target));
        if (atSpawn) movement.respawnPoint = target + RespawnLift;
        if (movement.cam != null)
        {
            movement.cam.setup(player.transform.position, camSize);
            movement.cam.newTarget(player, movement.cam.defaultoffset, true, Vector2.zero);
        }
        host.StartCoroutine(Settle(movement, target, atSpawn, camSize));
    }

    // ---- the spawn switcher: Q / E go to the previous / next of the map's spawns ----

    private static int _spawnIndex;

    private static List<Vector2> Spawns(MapWorld w)
    {
        var list = new List<Vector2>();
        if (w.Group.SpawnX != null || !w.Overlay || !w.Group.KeepSpawn) list.Add(w.LevelPoint(w.Group.SpawnX ?? w.Group.StartX, w.Group.SpawnY ?? w.Group.StartY));
        foreach (var p in w.Group.Spawns ?? new List<MapPoint>()) list.Add(w.LevelPoint(p.X, p.Y));
        return list;
    }

    public static void TickSwitcher(MapWorld w, MonoBehaviour host)
    {
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb == null || Time.timeScale <= 0f || w.Group.Spawns == null || w.Group.Spawns.Count == 0) return;
        var step = kb.eKey.wasPressedThisFrame ? 1 : kb.qKey.wasPressedThisFrame ? -1 : 0;
        if (step == 0) return;
        var movement = MapUpgrades.GamePlayer();
        var spawns = Spawns(w);
        if (movement == null || spawns.Count == 0) return;
        _spawnIndex = ((_spawnIndex + step) % spawns.Count + spawns.Count) % spawns.Count;
        var target = spawns[_spawnIndex];
        if (w.Overlay) LoadZoneAt(target);
        MoveTo(movement, MapWorld.Live(target));
        movement.respawnPoint = target + RespawnLift;
        var camSize = w.Def.CameraSize > 0 ? w.Def.CameraSize.Value : DefaultCamSize;
        if (movement.cam != null)
        {
            movement.cam.setup(movement.transform.position, camSize);
            movement.cam.newTarget(movement.gameObject, movement.cam.defaultoffset, true, Vector2.zero);
        }
        MapMessage.Show("Spawn " + (_spawnIndex + 1) + " / " + spawns.Count, 1.2f);
        host.StartCoroutine(Settle(movement, target, true, camSize));
    }

    // A spawn in another zone (area 2 below area 1...) loads that zone first, as
    // the level's own zone triggers would on the way there - at once, so the
    // player lands on its ground.
    private static void LoadZoneAt(Vector2 level)
    {
        var loader = Singleton<ZoneLoader>.Instance;
        var zones = loader != null ? Recharge.ModApi.Reflect.GetField<GameObject[]>(loader, "allZones") : null;
        if (zones == null) return;
        int best = -1;
        float bestArea = float.MaxValue, bestDist = float.MaxValue;
        for (int i = 0; i < zones.Length; i++)
        {
            if (zones[i] == null) continue;
            var ts = zones[i].GetComponentsInChildren<Transform>(true);
            if (ts.Length < 2) continue;
            var box = new Rect(MapWorld.LevelOf(ts[1].position), Vector2.zero);
            foreach (var t in ts.Skip(1))
            {
                var p = MapWorld.LevelOf(t.position);
                box = Rect.MinMaxRect(Mathf.Min(box.xMin, p.x), Mathf.Min(box.yMin, p.y), Mathf.Max(box.xMax, p.x), Mathf.Max(box.yMax, p.y));
            }
            var area = box.width * box.height;
            var dist = box.Contains(level) ? 0f : Vector2.Distance(level, new Vector2(Mathf.Clamp(level.x, box.xMin, box.xMax), Mathf.Clamp(level.y, box.yMin, box.yMax)));
            if (dist < bestDist || (dist == bestDist && area < bestArea)) { best = i; bestDist = dist; bestArea = area; }
        }
        if (best < 0 || loader.activeZone == best + 1) return;
        Debug.Log("[RechargeMaps] spawn is in zone " + (best + 1) + "; loading it");
        var load = typeof(ZoneLoader).GetMethod("_LoadZone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (load != null) load.Invoke(loader, new object[] { best + 1 });
        else loader.LoadZone(best + 1, true);
    }

    private static void MoveTo(Movement movement, Vector3 pos)
    {
        movement.transform.position = pos;
        // A Rigidbody2D keeps its own position and would snap the player back.
        var body = movement.GetComponent<Rigidbody2D>();
        if (body == null) return;
        body.position = pos;
        body.linearVelocity = Vector2.zero;
    }

    private static void FollowWithCamera(Movement movement, Vector3 at, float camSize, MonoBehaviour host)
    {
        if (movement.cam == null) return;
        movement.cam.setup(at, camSize);
        movement.cam.newTarget(movement.gameObject, movement.cam.defaultoffset, true, Vector2.zero);
        host.StartCoroutine(HoldCamSize(movement, camSize));
    }

    // Once whatever was mid-flight on the load frame settles (a dash carries
    // the player off; tiles painted this frame get colliders a physics step
    // later), put the player down on the ground at the spot for good.
    private static IEnumerator Settle(Movement movement, Vector2 target, bool atSpawn, float camSize)
    {
        float waited = 0f;
        while (movement != null && movement.dashActive && waited < 1f)
        {
            yield return null;
            waited += Time.unscaledDeltaTime;
        }
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();
        if (movement == null) yield break;
        // From level space again: a long teleport makes FloatingOrigin shift the world meanwhile.
        var pos = SnapToGround(movement.transform, MapWorld.Live(target));
        MoveTo(movement, pos);
        if (atSpawn) movement.respawnPoint = MapWorld.LevelOf(pos) + RespawnLift;
        if (movement.cam != null)
        {
            movement.cam.setup(pos, camSize);
            movement.cam.newTarget(movement.gameObject, movement.cam.defaultoffset, true, Vector2.zero);
        }
        yield return HoldCamSize(movement, camSize);
    }

    // The game's scene start (and a save's camera size) can land after the
    // map's; hold its size a moment, unless a real zoom zone changes it.
    private static IEnumerator HoldCamSize(Movement movement, float camSize)
    {
        var cam = movement != null ? movement.cam : null;
        float held = 0f;
        while (cam != null && held < 1.5f)
        {
            yield return null;
            held += Time.unscaledDeltaTime;
            if (cam == null || movement == null) yield break;
            if (Mathf.Abs(cam.camSize - camSize) > 0.5f && !InCamZone(movement.transform)) cam.setup(cam.transform.position, camSize);
        }
    }

    private static bool InCamZone(Transform player)
    {
        var col = player.GetComponent<Collider2D>();
        if (col == null) return false;
        var hits = new List<Collider2D>();
        var filter = new ContactFilter2D { useTriggers = true };
        filter.NoFilter();
        Physics2D.OverlapCollider(col, filter, hits);
        return hits.Any(h => h != null && h.GetComponent<camSizeTrigger>() != null);
    }

    // Stands the player on the first solid surface below the point (climbing
    // out first if it's inside ground), so every start and respawn is on the floor.
    private static Vector3 SnapToGround(Transform player, Vector3 pos)
    {
        var col = player.GetComponent<Collider2D>();
        if (col == null) return pos;
        player.position = pos;
        Physics2D.SyncTransforms();
        var feet = pos.y - col.bounds.min.y;
        var ground = LayerMask.GetMask("Ground");
        var mask = ground != 0 ? ground : ~0;
        for (int n = 0; n < 400 && InSolid(pos, mask, player); n++) pos.y += 4f;
        RaycastHit2D best = default;
        foreach (var hit in Physics2D.RaycastAll(pos, Vector2.down, 3000f, mask))
        {
            if (hit.collider == null || hit.collider.isTrigger || hit.collider.transform.IsChildOf(player)) continue;
            if (best.collider == null || hit.distance < best.distance) best = hit;
        }
        return best.collider == null ? pos : new Vector3(pos.x, best.point.y + feet + 0.5f, pos.z);
    }

    private static bool InSolid(Vector2 point, int mask, Transform player)
    {
        foreach (var c in Physics2D.OverlapPointAll(point, mask))
            if (c != null && !c.isTrigger && !c.transform.IsChildOf(player)) return true;
        return false;
    }
}
