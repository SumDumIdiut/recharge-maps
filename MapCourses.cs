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
            if (spec.Level != null)
            {
                var copied = CopyLevelCourse(w, spec, spec.Id ?? "course" + i);
                if (copied != null) { byId[spec.Id ?? ""] = copied; StackScreen(w, spec, copied); }
                continue;
            }
            var course = CreateCourse(w, i == 0 ? null : spec.Id ?? "course" + i);
            if (course == null) continue;
            SpawnGates(w, spec, course);
            PlaceScreen(w, spec, course);
            StackScreen(w, spec, course);
            byId[spec.Id ?? ""] = course;
        }
        MapLayering.Restack(w);

        foreach (var (go, id) in w.Links)
        {
            if (go == null || id == null || id.StartsWith("level:")) continue;
            if (!byId.TryGetValue(id, out var course)) { Debug.LogWarning("[RechargeMaps] no course '" + id + "' in the map to link to"); continue; }
            var box = go.GetComponent<MapUpgradeBox>();
            var levelBox = go.GetComponent<upgradeBox>();
            if (box != null) box.LinkCourse(course);
            else if (levelBox != null) LinkLevelBox(levelBox, course);
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
        // Every finish line logs the run and the best time it leaves, the level's courses' too.
        foreach (var end in UnityEngine.Object.FindObjectsByType<endGate>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (end != null && end.gameObject.scene.IsValid() && Reflect.GetField<bool>(end, "isEndOfCourse") && end.GetComponent<MapFinishLog>() == null)
            {
                var log = end.gameObject.AddComponent<MapFinishLog>();
                w.OnUnload(() => { if (log != null) UnityEngine.Object.Destroy(log); });
            }
    }

    // A copy of one of the level's own course boxes, moved onto a map course: it buys for that course.
    private static void LinkLevelBox(upgradeBox box, courseScript course)
    {
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var local = course.GetComponentInChildren<localUpgrades>(true);
        if (local == null) return;
        box.transform.SetParent(local.transform, true);
        typeof(upgradeBox).GetField("course", any)?.SetValue(box, course);
        typeof(upgradeBox).GetField("clones", any)?.SetValue(box, course.GetComponentInChildren<clonesScript>(true));
        typeof(upgradeBox).GetField("localUpgradeScript", any)?.SetValue(box, local);
        if (typeof(localUpgrades).GetField("ChildBoxes", any)?.GetValue(local) is List<upgradeBox> boxes && !boxes.Contains(box)) boxes.Add(box);
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

    // One of the level's courses copied whole: its own gates, screen, boxes' holder and
    // clones, all still linked to it, without the things the map copies on their own.
    // Its gates and screen move by as much as the editor moved them.
    private static courseScript CopyLevelCourse(MapWorld w, MapCourse spec, string id)
    {
        var lv = spec.Level;
        var path = lv["path"]?.Value<string>();
        var source = w.FindSceneObject(path, MapWorld.Live(new Vector2(lv["srcX"]?.Value<float>() ?? 0f, lv["srcY"]?.Value<float>() ?? 0f)));
        if (source == null) { Debug.LogWarning("[RechargeMaps] no level course at '" + path + "' to copy"); return null; }
        var go = UnityEngine.Object.Instantiate(source.gameObject, w.Staging, false);
        var refs = MapObjects.InnerRefs(go, path);
        MapObjects.Prune(go, lv);
        var relinked = MapObjects.Relink(w, refs);
        // Its boxes are copied on their own and linked back in (LinkLevelBox): no dead entries meanwhile.
        foreach (var local in go.GetComponentsInChildren<localUpgrades>(true))
            if (typeof(localUpgrades).GetField("ChildBoxes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(local) is List<upgradeBox> boxes) boxes.RemoveAll(b => b == null);
        go.transform.SetParent(w.ParentLike(source.parent), false);
        go.transform.position = w.Point(lv);
        go.name = source.name;
        var course = go.GetComponent<courseScript>();
        course.courseNumber = StableCourseNumber(w.MapId + "#" + id);
        course.init = true;
        Reflect.TrySetField(course, "isOnPauseMenu", false);
        // Its zone switches the level's own copy on and off, never this one.
        var bits = go.transform.Find("DisableBits");
        if (bits != null) bits.gameObject.SetActive(true);
        go.SetActive(true);
        try { course.load(MapSaves.ActiveFolder); }
        catch (Exception e) { Debug.Log("[RechargeMaps] no saved run for the map's course yet: " + e.Message); ResetRun(course); }

        Vector3 Moved(string x, string y, float toX, float toY)
        {
            if (lv[x] == null || lv[y] == null) return Vector3.zero;
            var d = (Vector3)(w.LevelPoint(toX, toY) - w.LevelPoint(lv[x].Value<float>(), lv[y].Value<float>()));
            d.z = 0f;
            return d;
        }
        var start = go.GetComponentInChildren<startGate>(true);
        var anchor = start != null ? start.transform.position - (Vector3)w.LevelPoint(lv["startX"]?.Value<float>() ?? 0f, lv["startY"]?.Value<float>() ?? 0f) : Vector3.zero;
        if (start != null)
        {
            var d = Moved("startX", "startY", spec.StartX, spec.StartY);
            start.transform.position += d;
            var reset = Reflect.GetField<GameObject>(start, "resetPoint");
            if (reset != null && reset.transform.IsChildOf(go.transform) && !reset.transform.IsChildOf(start.transform)) reset.transform.position += d;
        }
        var end = go.GetComponentsInChildren<endGate>(true).FirstOrDefault(e => Reflect.GetField<bool>(e, "isEndOfCourse"));
        if (end != null) end.transform.position += Moved("endX", "endY", spec.EndX, spec.EndY);
        var canvas = bits != null ? bits.Find("Canvas") : null;
        if (canvas != null && spec.ScreenX != null && spec.ScreenY != null) canvas.position += Moved("screenX", "screenY", spec.ScreenX.Value, spec.ScreenY.Value);
        if (end != null) SetReward(course, end, spec.Reward, false);
        if (spec.Resets != null && start != null)
        {
            var resets = go.GetComponentsInChildren<endGate>(true).Where(e => e != end && !Reflect.GetField<bool>(e, "isEndOfCourse")).ToList();
            PlaceResets(course, resets, spec.Resets, r => { var p = anchor + (Vector3)w.LevelPoint(r.X, r.Y); p.z = start.transform.position.z; return p; }, (end != null ? (Component)end : start).gameObject.layer);
        }
        EnsureResetPoint(course, start);
        Debug.Log("[RechargeMaps] copied level course '" + path + "' as course " + course.courseNumber + " (" + relinked + " links to its things copied apart)");
        return course;
    }

    // A course with no save starts like a new one. The copy carries the template's public run
    // state (its clones' path, count and speed, which would run in the map) and, by field
    // initialiser, a best time of 99999999; put both back, and the board with them.
    private static void ResetRun(courseScript course)
    {
        Reflect.TrySetField(course, "bestPathTime", 99999999f);
        var clones = Reflect.TryGetField<clonesScript>(course, "clones") ?? course.GetComponentInChildren<clonesScript>(true);
        if (clones != null)
        {
            clones.clonePath = new Vector2[0];
            clones.cloneSprites = new Sprite[0];
            clones.cloneScales = new Vector2[0];
            clones.pathLength = 0f;
            clones.cloneCount = 0;
            clones.cloneEndVelocity = Vector2.zero;
        }
        // The board's best time and clone count: the game's own texts (they wait for its Start otherwise, which sets them).
        try { Reflect.InvokeMethod(course, "updateStringLocalisations"); }
        catch (Exception e) { Debug.LogWarning("[RechargeMaps] couldn't refresh the course board: " + e.Message); }
    }

    // A level course back to a new game's state (no map save for it): no run, no clones, tiers and
    // boxes unbought. Only used by MapSaves' safety net; a normal map start never loads Base Game over it.
    public static void StartFresh(courseScript course)
    {
        ResetRun(course);
        course.tier = 0;
        course.rewardTier = 0;
        if (course.localUpgradesScript != null)
            foreach (Transform box in course.localUpgradesScript.transform)
            {
                var ub = box.GetComponent<upgradeBox>();
                if (ub != null) ub.TimesUsed = 0;
            }
        RefreshReward(course);
    }

    // The reward after a change of baseReward; before the course's Start, that works it out itself.
    private static void RefreshReward(courseScript course)
    {
        if (!Reflect.TryGetField(course, "startHasRun", false)) return;
        try { course.UpdateReward(); }
        catch (Exception e) { Debug.LogWarning("[RechargeMaps] couldn't refresh the course reward: " + e.Message); }
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
        // Course 1's content goes; its screen (the Canvas board) stays for PlaceScreen.
        var disableBits = go.transform.Find("DisableBits");
        if (disableBits != null)
            foreach (var child in disableBits.Cast<Transform>().Where(c => c.name != "Canvas").ToList())
                UnityEngine.Object.DestroyImmediate(child.gameObject);

        var course = go.GetComponent<courseScript>();
        course.courseNumber = StableCourseNumber(extraId == null ? w.MapId : w.MapId + "#" + extraId);
        course.init = true;
        // Course 1 is the one shown behind the title screen: isOnPauseMenu makes
        // load() read a canned demo run instead of the course's save.
        Reflect.TrySetField(course, "isOnPauseMenu", false);
        // Course 1's V-man prize isn't the map's.
        course.completionReward = 0;
        try { course.load(MapSaves.ActiveFolder); }
        catch (Exception e) { Debug.Log("[RechargeMaps] no saved run for the map's course yet: " + e.Message); ResetRun(course); }

        // Course 1's own boxes: off, but kept so the save's per-box data lines up.
        var local = go.GetComponentInChildren<localUpgrades>(true);
        if (local != null) foreach (Transform box in local.transform) box.gameObject.SetActive(false);
        return course;
    }

    // The course's screen (the board with its reward, best time and clones), moved so the
    // board image itself sits where the editor draws it: the editor's point (the centre of
    // the level board's three texts) plus the board's offset from them (Editor SCREEN_BOX).
    // Anchoring on the Screen image, not an average of whatever texts the canvas holds at
    // load (a run timer, a boost label), keeps both sides exactly aligned. The "ONE" title
    // is not in this canvas: it is the CourseNumber text of the level's entry panel, which
    // map copies of that panel hide (MapObjects.HideCourseNumber).
    private static readonly Vector2 BoardFromPoint = new Vector2(0f, 3.5f);

    private static void PlaceScreen(MapWorld w, MapCourse spec, courseScript course)
    {
        var bits = course.transform.Find("DisableBits");
        var canvas = bits != null ? bits.Find("Canvas") : null;
        if (spec.ScreenX == null || spec.ScreenY == null || canvas == null)
        {
            if (canvas != null) canvas.gameObject.SetActive(false);
            return;
        }
        var board = (canvas.Find("Screen") ?? canvas) as RectTransform;
        if (board == null) return;
        var corners = new Vector3[4];
        board.GetWorldCorners(corners);
        var centre = (corners[0] + corners[2]) / 2f;
        var want = MapWorld.Live(w.LevelPoint(spec.ScreenX.Value, spec.ScreenY.Value) + BoardFromPoint);
        var delta = want - centre;
        delta.z = 0f;
        canvas.position += delta;
        Debug.Log("[RechargeMaps] course screen " + course.courseNumber + ": board was centred at " + (Vector2)MapWorld.LevelOf(centre) + ", editor wants " + (w.LevelPoint(spec.ScreenX.Value, spec.ScreenY.Value) + BoardFromPoint) + " (moved " + (Vector2)delta + ")");
    }

    // The screen's place in the editor's stack, for MapLayering.
    private static void StackScreen(MapWorld w, MapCourse spec, courseScript course)
    {
        var canvas = course.transform.Find("DisableBits")?.Find("Canvas");
        if (canvas == null || !canvas.gameObject.activeSelf) return;
        // The editor's draw order (a shift of the canvas's own).
        if (spec.OrderDelta != 0) foreach (var c in canvas.GetComponentsInChildren<Canvas>(true)) if (c.isRootCanvas || c.overrideSorting) c.sortingOrder += spec.OrderDelta;
        if (spec.Behind) (spec.Depth == 2 ? w.BehindWalls : w.Behind).Add(canvas.gameObject);
        else if (spec.Order != null) w.Stacked.Add((spec.Order.Value, canvas.gameObject));
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
        // Explicitly link the start gate to this course so it knows which course to start.
        if (Reflect.FieldOf<startGate>("course") != null)
            Reflect.SetField(start, "course", course);

        Reflect.TrySetField(end, "isEndOfCourse", true);
        SetReward(course, end, spec.Reward, true);
        if (spec.Resets != null) PlaceResets(course, new List<endGate>(), spec.Resets, r => MapWorld.Live(w.LevelPoint(r.X, r.Y)), end.gameObject.layer);
        EnsureResetPoint(course, start);
    }

    // The finish stays the game's own, which records the best time, the clones and the save.
    // A cash reward becomes the course's base reward (paid there, with the game's multipliers);
    // another currency is paid by a trigger beside it. With none set, the course pays what its source does.
    private static void SetReward(courseScript course, endGate end, MapReward reward, bool template)
    {
        var amount = reward?.Amount ?? 0;
        var cash = amount > 0 && reward.Currency == "Cash" && amount <= int.MaxValue;
        if (amount > 0 && !cash && Enum.TryParse(reward.Currency, out globalStats.Currencies currency))
        {
            var trigger = end.gameObject.AddComponent<MapRewardTrigger>();
            trigger.Currency = currency;
            trigger.Amount = amount;
        }
        // No reward (or another currency's) on a template: cash is the template's own (course 1's) unless a trigger pays instead.
        if (cash) Reflect.TrySetField(course, "baseReward", (int)Math.Ceiling(amount));
        else if (amount > 0) Reflect.TrySetField(course, "baseReward", 0);
        RefreshReward(course);
    }

    // Timer resets: endGates that stop a run without finishing it. The course's own are
    // reused (moved and resized to the editor's), the rest added or switched off.
    private static void PlaceResets(courseScript course, List<endGate> have, List<MapResetGate> resets, Func<MapResetGate, Vector3> at, int layer)
    {
        for (int i = 0; i < resets.Count; i++)
        {
            var r = resets[i];
            endGate gate;
            if (i < have.Count) gate = have[i];
            else
            {
                var go = new GameObject("Timer reset") { layer = layer };
                go.transform.SetParent(course.transform, false);
                gate = go.AddComponent<endGate>();
            }
            gate.transform.position = at(r);
            var box = gate.GetComponent<BoxCollider2D>();
            if (box == null) box = gate.gameObject.AddComponent<BoxCollider2D>();
            var s = gate.transform.lossyScale;
            box.isTrigger = true;
            box.offset = new Vector2(r.Dx / s.x, r.Dy / s.y);
            box.size = new Vector2(Mathf.Abs(r.W / s.x), Mathf.Abs(r.H / s.y));
        }
        for (int i = resets.Count; i < have.Count; i++)
        {
            // Not still the course's respawn spot (Destroy only lands at frame end).
            if (course.resetPoint == have[i].transform) course.resetPoint = null;
            UnityEngine.Object.Destroy(have[i]);
        }
    }

    // The level's courses respawn a failed run at courseScript.resetPoint (a spot about 75
    // below the start gate's centre, in every level course) and startGate.resetPoint. A
    // map course keeps the level's own where it still has one; where it came across null
    // (it pointed outside the copy, or was removed with the course's content) it gets one
    // at that same offset from the gate, so the player lands where a level course puts them.
    private static readonly Vector3 ResetBelowStart = new Vector3(0f, -75f, 0f);

    private static void EnsureResetPoint(courseScript course, startGate start)
    {
        if (course == null || start == null) return;
        var fromGate = Reflect.FieldOf<startGate>("resetPoint") != null ? Reflect.GetField<GameObject>(start, "resetPoint") : null;
        var how = "the level's";
        if (course.resetPoint == null)
        {
            Transform spot = fromGate != null ? fromGate.transform : null;
            if (spot == null)
            {
                var go = new GameObject("Course reset point");
                go.transform.SetParent(course.transform, false);
                go.transform.position = start.transform.position + ResetBelowStart;
                spot = go.transform;
                how = "made 75 below the start gate";
            }
            else how = "the start gate's";
            course.resetPoint = spot;
        }
        if (fromGate == null && Reflect.FieldOf<startGate>("resetPoint") != null)
            Reflect.SetField(start, "resetPoint", course.resetPoint.gameObject);
        Debug.Log("[RechargeMaps] course " + course.courseNumber + " respawns a run at " + how + " reset point, " + (course.resetPoint.position - start.transform.position) + " from its start gate");
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

// Logs a course's finish: the run's time and the best time the course keeps.
// The run is read as of the last physics step: the gate may stop it first.
internal class MapFinishLog : MonoBehaviour
{
    private courseScript _course;
    private bool _tracking;
    private float _time;
    private double _cash;

    private void FixedUpdate()
    {
        if (_course == null) _course = GetComponentInParent<courseScript>();
        if (_course == null) return;
        _tracking = Reflect.GetField<bool>(_course, "tracking");
        _time = Reflect.GetField<float>(_course, "currentPathTime");
        _cash = globalStats.currencyLookup[globalStats.Currencies.Cash];
    }

    private void OnTriggerEnter2D(Collider2D collision)
    {
        if (_course == null || collision.GetComponent<Movement>() == null) return;
        if (!_tracking) { Debug.Log("[RechargeMaps] course " + _course.courseNumber + " end reached without a running timer (start gate not entered, or timer reset)"); return; }
        _tracking = false;
        StartCoroutine(After(_course, _time, _cash));
    }

    private System.Collections.IEnumerator After(courseScript course, float run, double cashBefore)
    {
        yield return null;
        if (course == null) yield break;
        Debug.Log("[RechargeMaps] course " + course.courseNumber + " finished in " + run.ToString("0.00") + "s, best " + Reflect.GetField<float>(course, "bestPathTime").ToString("0.00") + "s, reward " + course.reward.ToString("0.##") + " (cash " + (globalStats.currencyLookup[globalStats.Currencies.Cash] - cashBefore).ToString("+0.##;-0.##") + ")" + (VmanScript.isCurrentlyVman ? " (V-man: not recorded)" : ""));
    }
}
