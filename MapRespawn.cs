using System;
using System.Collections.Generic;
using Recharge.ModApi;
using UnityEngine;

// Death and respawn in maps.
//
// Checkpoints move the player's respawn point, as in the game. Course
// checkpoints are the map's own kind: they only count during a run of their
// course, and a death in that run goes back to the last one touched (the game
// would send it to the course start). Outside a run they do nothing.
//
// Runs on MapManager.Update: Update comes before the Invoke'd Movement.respawn
// in the same frame, so the point set here is the one respawn uses.
internal static class MapRespawn
{
    private static courseScript _runCourse;
    private static Vector2? _runCheckpoint;
    private static bool _wasDead;
    private static bool? _heldSetting;
    private static Vector2? _safeRespawn;

    public static void Reset()
    {
        _runCourse = null;
        _runCheckpoint = null;
        _wasDead = false;
        _heldSetting = null;
        _safeRespawn = null;
    }

    public static void Tick(Movement mv)
    {
        if (mv == null) return;
        var dead = Reflect.GetField<bool>(mv, "isDead");
        GuardRespawnPoint(mv);
        // A run ends at its end gate (death doesn't end it).
        if (_runCourse != null && !Tracking(_runCourse)) { _runCourse = null; _runCheckpoint = null; }

        if (dead)
        {
            if (!_wasDead) Debug.Log("[RechargeMaps] died at " + Level(mv.transform.position) + (_runCheckpoint != null ? "; back to the run's checkpoint " + _runCheckpoint.Value : "; respawn point " + mv.respawnPoint));
            if (_runCheckpoint != null)
            {
                if (_heldSetting == null) _heldSetting = mv.isRespawningAtCheckpoints;
                mv.isRespawningAtCheckpoints = false;
                mv.courseResetPoint = Live(_runCheckpoint.Value);
            }
        }
        else if (_wasDead)
        {
            if (_heldSetting != null) { mv.isRespawningAtCheckpoints = _heldSetting.Value; _heldSetting = null; }
            Debug.Log("[RechargeMaps] respawned at " + Level(mv.transform.position));
            // spikeScript kills from OnTriggerStay, then ignores the player until
            // they leave it - so respawning inside spikes would never kill again.
            foreach (var spike in UnityEngine.Object.FindObjectsByType<spikeScript>(FindObjectsSortMode.None))
            {
                Reflect.TrySetField(spike, "playerKilled", false);
                Reflect.TrySetField(spike, "counter", 0);
            }
        }
        _wasDead = dead;
    }

    // A course checkpoint touched. `owner` null: it counts for any run.
    public static void CourseCheckpointTouched(courseScript owner, Vector3 at, Movement mv)
    {
        var run = owner != null ? (Tracking(owner) ? owner : null) : RunningCourse();
        if (run == null) return;
        var level = Level(at);
        if (Deadly(mv, level)) { Debug.Log("[RechargeMaps] course checkpoint at " + level + " is inside something deadly; not used"); return; }
        if (_runCourse != run || _runCheckpoint != level) Debug.Log("[RechargeMaps] run checkpoint -> " + level);
        _runCourse = run;
        _runCheckpoint = level;
    }

    // Turns the level checkpoint(s) in `go` into course checkpoints; returns the undo.
    public static Action MakeCourseCheckpoint(GameObject go)
    {
        var swapped = new List<(GameObject go, bool blue)>();
        foreach (var cp in go.GetComponentsInChildren<checkpointScript>(true))
        {
            swapped.Add((cp.gameObject, cp.PlayerRespawnsWithBlueBlocksActive));
            var host = cp.gameObject;
            UnityEngine.Object.DestroyImmediate(cp);
            host.AddComponent<MapCourseCheckpoint>();
        }
        return () =>
        {
            foreach (var (host, blue) in swapped)
            {
                if (host == null) continue;
                var own = host.GetComponent<MapCourseCheckpoint>();
                if (own != null) UnityEngine.Object.DestroyImmediate(own);
                if (host.GetComponent<checkpointScript>() == null) host.AddComponent<checkpointScript>().PlayerRespawnsWithBlueBlocksActive = blue;
            }
        };
    }

    // The level's own course checkpoints behave as course checkpoints while a map is loaded.
    public static void ConvertLevelCourseCheckpoints(MapWorld w)
    {
        foreach (var cp in Resources.FindObjectsOfTypeAll<checkpointScript>())
        {
            if (cp == null || !cp.gameObject.scene.IsValid() || MapWorld.IsMapObject(cp.transform)) continue;
            if (!RealAssetPalette.GetPath(cp.transform).StartsWith("Courses/")) continue;
            w.OnUnload(MakeCourseCheckpoint(cp.gameObject));
        }
    }

    private static bool Tracking(courseScript course) => course != null && Reflect.GetField<bool>(course, "tracking");

    private static courseScript RunningCourse()
    {
        foreach (var course in UnityEngine.Object.FindObjectsByType<courseScript>(FindObjectsSortMode.None))
            if (Tracking(course)) return course;
        return null;
    }

    // A checkpoint whose respawn spot is inside something deadly (a true
    // spike's reach, a spike placed over it) would respawn the player into
    // death forever: it's refused and the last safe point kept.
    private static void GuardRespawnPoint(Movement mv)
    {
        if (_safeRespawn == null) { _safeRespawn = mv.respawnPoint; return; }
        if (mv.respawnPoint == _safeRespawn.Value) return;
        if (Deadly(mv, mv.respawnPoint))
        {
            Debug.Log("[RechargeMaps] checkpoint at " + mv.respawnPoint + " is inside something deadly; keeping " + _safeRespawn.Value);
            mv.respawnPoint = _safeRespawn.Value;
            return;
        }
        _safeRespawn = mv.respawnPoint;
    }

    // Would the player, respawned at this level point, stand in a spike?
    private static bool Deadly(Movement mv, Vector2 respawnPoint)
    {
        if (mv == null) return false;
        var body = mv.GetComponent<BoxCollider2D>();
        var size = body != null ? Vector2.Scale(body.size, mv.transform.lossyScale) : new Vector2(20f, 30f);
        var offset = body != null ? Vector2.Scale(body.offset, mv.transform.lossyScale) : Vector2.zero;
        // Movement.respawn puts the player 12 below the point.
        var centre = Live(respawnPoint) + new Vector2(0f, -12f) + offset;
        foreach (var hit in Physics2D.OverlapBoxAll(centre, size, 0f))
            if (hit != null && hit.isTrigger && hit.GetComponent<spikeScript>() != null) return true;
        return false;
    }

    private static Vector2 Origin()
    {
        var fo = Singleton<FloatingOrigin>.Instance;
        return fo != null ? (Vector2)fo.currentOrigin : Vector2.zero;
    }

    private static Vector2 Live(Vector2 level) => level + Origin();
    private static Vector2 Level(Vector3 live) => (Vector2)live - Origin();
}

// A course checkpoint: where a run of its course (the course it sits in, or
// any run when it's in none) carries on from after a death.
internal class MapCourseCheckpoint : MonoBehaviour
{
    private void OnTriggerEnter2D(Collider2D collision)
    {
        if (!collision.gameObject.CompareTag("Player")) return;
        var mv = collision.gameObject.GetComponent<Movement>();
        if (mv == null) return;
        MapRespawn.CourseCheckpointTouched(GetComponentInParent<courseScript>(), transform.position, mv);
    }
}
