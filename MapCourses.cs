using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Recharge.ModApi;
using UnityEngine;

// A map's courses, and everything that links to one: its gates, the upgrade
// boxes and items tied to a course (the map's own, or the level's "level:N"),
// and teleporters. Runs once all the map's objects exist.
internal static class MapCourses
{
    public static void Build(MapWorld w)
    {
        var byId = new Dictionary<string, courseScript>();
        var specs = CoursesOf(w.Group);
        for (int i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            var course = CreateCourse(w, i == 0 ? null : spec.Id ?? "course" + i);
            if (course == null) continue;
            SpawnGates(w, spec, course);
            byId[spec.Id ?? ""] = course;
        }

        foreach (var (go, id) in w.Links)
        {
            if (go == null || id == null || id.StartsWith("level:")) continue;
            if (!byId.TryGetValue(id, out var course)) { Debug.LogWarning("[RechargeMaps] no course '" + id + "' in the map to link to"); continue; }
            var box = go.GetComponent<MapUpgradeBox>();
            if (box != null) box.LinkCourse(course);
            else go.transform.SetParent(course.transform, true);
        }

        // A course's clones only run once it has boxes to buy them with.
        foreach (var course in byId.Values)
        {
            var clones = course.transform.Find("Clones");
            var local = course.GetComponentInChildren<localUpgrades>(true);
            if (clones != null) clones.gameObject.SetActive(local != null && local.GetComponentsInChildren<MapUpgradeBox>(true).Length > 0);
        }

        LinkLevelCourses(w);
        WireTeleporters(w);
    }

    // Each start/end pair from the editor, or an old map's single one.
    private static List<MapCourse> CoursesOf(MapGroup group)
    {
        if (group.Courses != null && group.Courses.Count > 0) return group.Courses;
        if (!group.Gates) return new List<MapCourse>();
        return new List<MapCourse> { new MapCourse { StartX = group.StartX, StartY = group.StartY, EndX = group.EndX, EndY = group.EndY, Reward = group.Reward } };
    }

    // The save's course number for a map's course: stable across plays, far
    // above the game's own (FNV-1a).
    public static int StableCourseNumber(string key) => 900000 + (int)(Hash(key) % 100000);

    private static uint Hash(string key)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in key) { hash ^= c; hash *= 16777619; }
            return hash;
        }
    }

    // A copy of the level's course 1 with its content removed: the map gets a
    // real courseScript for timing, rewards, clones and saves.
    private static courseScript CreateCourse(MapWorld w, string extraId)
    {
        var template = RealAssetPalette.Get<courseScript>();
        if (template == null) { Debug.LogError("[RechargeMaps] no course seen in the level yet - can't make the map's courses"); return null; }

        var go = UnityEngine.Object.Instantiate(template.gameObject, MapWorld.Live(w.Origin), Quaternion.identity, w.Root);
        go.name = "Course" + (extraId != null ? "_" + extraId : "");
        go.SetActive(true);
        var disableBits = go.transform.Find("DisableBits");
        if (disableBits != null) foreach (Transform child in disableBits) UnityEngine.Object.Destroy(child.gameObject);

        var course = go.GetComponent<courseScript>();
        course.courseNumber = StableCourseNumber(extraId == null ? w.MapId : w.MapId + "#" + extraId);
        course.init = true;
        // Course 1 is the one shown behind the title screen: isOnPauseMenu makes
        // load() read a canned demo run instead of the course's save.
        Reflect.TrySetField(course, "isOnPauseMenu", false);
        try { course.load(MapSaves.ActiveFolder); }
        catch (Exception e) { Debug.Log("[RechargeMaps] no saved run for the map's course yet: " + e.Message); }

        // Course 1's own boxes: off, but kept so the save's per-box data lines up.
        var local = go.GetComponentInChildren<localUpgrades>(true);
        if (local != null) foreach (Transform box in local.transform) box.gameObject.SetActive(false);
        return course;
    }

    private static void SpawnGates(MapWorld w, MapCourse spec, courseScript course)
    {
        var start = RealAssetPalette.Spawn<startGate>(MapWorld.Live(w.LevelPoint(spec.StartX, spec.StartY)), Quaternion.identity, course.transform);
        var end = RealAssetPalette.Spawn<endGate>(MapWorld.Live(w.LevelPoint(spec.EndX, spec.EndY)), Quaternion.identity, course.transform);
        if (start == null || end == null) { Debug.LogWarning("[RechargeMaps] no course gates seen in the level yet"); return; }

        // Fixtures, not props: kinematic, and triggers - a solid gate the player
        // spawns inside launches them out.
        foreach (var gate in new[] { start.gameObject, end.gameObject })
        {
            var body = gate.GetComponent<Rigidbody2D>();
            if (body != null) body.bodyType = RigidbodyType2D.Kinematic;
            foreach (var col in gate.GetComponents<Collider2D>()) col.isTrigger = true;
        }
        // resetPoint pointed outside the copied object and comes across null.
        if (Reflect.FieldOf<startGate>("resetPoint") != null && Reflect.GetField<GameObject>(start, "resetPoint") == null)
            Reflect.SetField(start, "resetPoint", start.gameObject);

        // The map's reward replaces the game's tiered end-of-course one.
        Reflect.TrySetField(end, "isEndOfCourse", false);
        var reward = spec.Reward;
        if (reward != null && reward.Amount > 0 && Enum.TryParse(reward.Currency, out globalStats.Currencies currency))
        {
            var trigger = end.gameObject.AddComponent<MapRewardTrigger>();
            trigger.Currency = currency;
            trigger.Amount = reward.Amount;
        }
    }

    // Items linked to one of the level's own courses: moved into that course
    // (a box becomes its upgrade) and taken out again on unload.
    private static void LinkLevelCourses(MapWorld w)
    {
        foreach (var (go, id) in w.Links)
        {
            if (go == null || id == null || !id.StartsWith("level:") || !int.TryParse(id.Substring(6), out var n)) continue;
            var course = Resources.FindObjectsOfTypeAll<courseScript>()
                .Where(c => c != null && c.gameObject.scene.IsValid() && c.courseNumber == n && !MapWorld.IsMapObject(c.transform))
                .OrderByDescending(c => RealAssetPalette.GetPath(c.transform).StartsWith("Courses/"))
                .FirstOrDefault();
            if (course == null) { Debug.LogWarning("[RechargeMaps] no level course " + n + " to link to"); continue; }
            var item = go;
            var box = go.GetComponent<MapUpgradeBox>();
            var local = course.GetComponentInChildren<localUpgrades>(true);
            if (box != null) box.LinkCourse(course);
            else go.transform.SetParent(course.transform, true);
            w.OnUnload(() =>
            {
                if (box != null && local != null) box.Unlink(local);
                if (item != null) UnityEngine.Object.Destroy(item);
            });
        }
        w.Links.Clear();
    }

    // Placed teleporters: their own IDs and zones, and up / down links to each
    // other or to the level's teleporters (pointed back at them until unload).
    private static void WireTeleporters(MapWorld w)
    {
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var upField = typeof(TeleporterScript).GetField("upTeleporter", any);
        var downField = typeof(TeleporterScript).GetField("downTeleporter", any);
        var hideField = typeof(TeleporterScript).GetField("objectsToDeactivateIfDisabled", any);
        var byUid = new Dictionary<string, TeleporterScript>();
        foreach (var (go, cfg) in w.Teleports)
        {
            var t = go != null ? go.GetComponentInChildren<TeleporterScript>(true) : null;
            var id = cfg["id"]?.Value<string>();
            if (t != null && id != null) byUid[id] = t;
        }
        TeleporterScript Resolve(JObject r)
        {
            if (r == null) return null;
            var uid = r["uid"]?.Value<string>();
            if (uid != null) return byUid.TryGetValue(uid, out var own) ? own : null;
            var found = w.FindSceneObject(r["path"]?.Value<string>(), MapWorld.Live(new Vector2(r["x"]?.Value<float>() ?? 0f, r["y"]?.Value<float>() ?? 0f)));
            return found != null ? found.GetComponentInChildren<TeleporterScript>(true) : null;
        }
        foreach (var (go, cfg) in w.Teleports)
        {
            var t = go != null ? go.GetComponentInChildren<TeleporterScript>(true) : null;
            if (t == null) continue;
            t.ID = 700000 + (int)(Hash(cfg["id"]?.Value<string>() ?? go.name) % 200000);
            if (cfg["zone"] != null) t.zone = cfg["zone"].Value<int>();
            if (hideField?.GetValue(t) is GameObject[] hide) hideField.SetValue(t, hide.Where(h => h != null && h.transform.IsChildOf(t.transform)).ToArray());
            foreach (var (field, back, key) in new[] { (upField, downField, "up"), (downField, upField, "down") })
            {
                var target = Resolve(cfg[key] as JObject);
                field?.SetValue(t, target);
                if (target == null || byUid.ContainsValue(target) || back == null) continue;
                var old = back.GetValue(target);
                back.SetValue(target, t);
                var level = target;
                w.OnUnload(() => { if (level != null) back.SetValue(level, old); });
            }
        }
        w.Teleports.Clear();
    }
}
