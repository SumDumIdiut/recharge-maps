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

        MoveTo(movement, MapWorld.Live(target));
        if (atSpawn) movement.respawnPoint = target + RespawnLift;
        if (movement.cam != null)
        {
            movement.cam.setup(player.transform.position, camSize);
            movement.cam.newTarget(player, movement.cam.defaultoffset, true, Vector2.zero);
        }
        host.StartCoroutine(Settle(movement, target, atSpawn, camSize));
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
