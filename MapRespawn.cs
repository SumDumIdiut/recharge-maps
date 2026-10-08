using Recharge.ModApi;
using UnityEngine;

// Death and respawn in maps are the game's own: checkpoints, course
// checkpoints and respawns all run the game's code unchanged. This only logs
// them, and sets the blocks when block swap is bought (the game waits for the
// next course start or respawn, using the same swapBlocks call).
internal static class MapRespawn
{
    private static bool _wasDead;
    private static bool? _hadBlockSwap;

    public static void Reset()
    {
        _wasDead = false;
        _hadBlockSwap = null;
    }

    public static void Tick(Movement mv)
    {
        if (mv == null) return;
        if (_hadBlockSwap == false && mv.blockSwapUnlocked) Singleton<colouredBlockSwapper>.Instance?.swapBlocks(true);
        _hadBlockSwap = mv.blockSwapUnlocked;

        var dead = Reflect.GetField<bool>(mv, "isDead");
        if (dead && !_wasDead)
        {
            // Movement.respawn's own choice: the course reset point unless the Checkpoints setting is on.
            var toCourse = !mv.isRespawningAtCheckpoints && mv.courseResetPoint != Vector2.zero;
            Debug.Log("[RechargeMaps] died at " + Level(mv.transform.position) + "; respawns at the " + (toCourse ? "course reset point " + (mv.courseResetPoint - (Vector2)FloatingOriginOffset()) : "checkpoint " + mv.respawnPoint) + " (Checkpoints setting " + (mv.isRespawningAtCheckpoints ? "on" : "off") + ")");
        }
        if (!dead && _wasDead) Debug.Log("[RechargeMaps] respawned at " + Level(mv.transform.position));
        _wasDead = dead;
    }

    private static Vector3 FloatingOriginOffset()
    {
        var fo = Singleton<FloatingOrigin>.Instance;
        return fo != null ? (Vector3)fo.currentOrigin : Vector3.zero;
    }

    private static Vector2 Level(Vector3 live)
    {
        var fo = Singleton<FloatingOrigin>.Instance;
        return (Vector2)live - (fo != null ? (Vector2)fo.currentOrigin : Vector2.zero);
    }
}
