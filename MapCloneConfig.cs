using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

// The map editor's settings on a copied level object.
internal static class MapCloneConfig
{
    // The map editor's settings for a cloned object: rotation (degrees), scale
    // [x, y] (negative flips), script fields {"Type.field": value}, and a zip
    // mover's track {end: [x, y], time, backTime, width}.
    // The editor's opacity, over whatever the object's own colours are.
    public static void Fade(GameObject go, float alpha)
    {
        alpha = Mathf.Clamp01(alpha);
        foreach (var sr in go.GetComponentsInChildren<SpriteRenderer>(true)) { var c = sr.color; c.a *= alpha; sr.color = c; }
        foreach (var tm in go.GetComponentsInChildren<UnityEngine.Tilemaps.Tilemap>(true)) { var c = tm.color; c.a *= alpha; tm.color = c; }
        foreach (var tx in go.GetComponentsInChildren<TMPro.TMP_Text>(true)) tx.alpha *= alpha;
    }

    // A checkpoint's trigger box as the editor sized it, in world units around the object.
    private static void SizeCheckpoint(GameObject clone, JObject trig)
    {
        // A checkpoint's box, or a long-fall zone's.
        Component owner = clone.GetComponentInChildren<checkpointScript>(true);
        if (owner == null) owner = clone.GetComponentInChildren<longFallColliderController>(true);
        var box = owner != null ? owner.GetComponent<BoxCollider2D>() : null;
        if (box == null) { Debug.LogWarning("[RechargeMaps] no box trigger to size on " + clone.name); return; }
        var s = box.transform.lossyScale;
        if (Mathf.Abs(s.x) < 1e-4f || Mathf.Abs(s.y) < 1e-4f) return;
        var at = (Vector2)(clone.transform.position - box.transform.position);
        box.size = new Vector2((trig["w"]?.Value<float>() ?? 100f) / Mathf.Abs(s.x), (trig["h"]?.Value<float>() ?? 100f) / Mathf.Abs(s.y));
        box.offset = new Vector2((at.x + (trig["dx"]?.Value<float>() ?? 0f)) / s.x, (at.y + (trig["dy"]?.Value<float>() ?? 0f)) / s.y);
    }

    public static void Apply(GameObject clone, JObject obj)
    {
        var t = clone.transform;
        if (obj["absolute"]?.Value<bool>() == true)
        {
            // Decorations: an exact world rotation and scale, whatever the copied object had.
            t.rotation = Quaternion.Euler(0f, 0f, obj["rotation"]?.Value<float>() ?? 0f);
            if (obj["scale"] is JArray abs && abs.Count == 2)
            {
                var parentScale = t.parent != null ? t.parent.lossyScale : Vector3.one;
                t.localScale = new Vector3(abs[0].Value<float>() / parentScale.x, abs[1].Value<float>() / parentScale.y, t.localScale.z);
            }
        }
        else
        {
            if (obj["rotation"] != null) t.rotation = Quaternion.Euler(0f, 0f, obj["rotation"].Value<float>()) * t.rotation;
            if (obj["scale"] is JArray sc && sc.Count == 2)
                t.localScale = new Vector3(t.localScale.x * sc[0].Value<float>(), t.localScale.y * sc[1].Value<float>(), t.localScale.z);
        }
        if (obj["tint"] is JArray tint && tint.Count >= 3)
        {
            var sr = clone.GetComponent<SpriteRenderer>();
            if (sr != null) sr.color = new Color(tint[0].Value<float>(), tint[1].Value<float>(), tint[2].Value<float>(), sr.color.a);
        }
        if (obj["alpha"] != null) Fade(clone, obj["alpha"].Value<float>());
        if (obj["trigger"] is JObject trig) SizeCheckpoint(clone, trig);
        if (obj["width"] != null)
        {
            // Stretch a tiled sprite (a wide spring) - sprite and collider together.
            var width = obj["width"].Value<float>();
            var sr = clone.GetComponent<SpriteRenderer>();
            var box = clone.GetComponent<BoxCollider2D>();
            if (sr != null)
            {
                var simple = sr.drawMode == SpriteDrawMode.Simple && sr.sprite != null;
                var old = simple ? sr.sprite.rect.width / sr.sprite.pixelsPerUnit : sr.size.x;
                var height = simple ? sr.sprite.rect.height / sr.sprite.pixelsPerUnit : sr.size.y;
                sr.drawMode = SpriteDrawMode.Tiled;
                sr.size = new Vector2(width, height);
                if (box != null && old > 0f) box.size = new Vector2(box.size.x * width / old, box.size.y);
            }
        }
        if (obj["fields"] is JObject fields)
        {
            foreach (var prop in fields.Properties())
            {
                var dot = prop.Name.LastIndexOf('.');
                if (dot <= 0) continue;
                var typeName = prop.Name.Substring(0, dot);
                var fieldName = prop.Name.Substring(dot + 1);
                foreach (var comp in clone.GetComponentsInChildren<Component>(true))
                {
                    if (comp == null || comp.GetType().Name != typeName) continue;
                    var f = comp.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null) f.SetValue(comp, Convert.ChangeType(prop.Value.Value<double>(), f.FieldType));
                }
            }
        }
        if (obj["zip"] is JObject zip) ApplyZip(clone, zip);
        if (obj["upgrade"] is JObject upgrade) MapUpgrades.ApplyBox(clone, upgrade);
        MapUpgrades.PrepareRefresher(clone);
    }

    private static void BuildPlatform(PlatformMover mover, JArray rects)
    {
        var sr = mover.GetComponent<SpriteRenderer>();
        var box = mover.GetComponent<BoxCollider2D>();
        if (sr == null) return;
        var scale = mover.transform.lossyScale;
        // The game's collider sits a little inside the sprite; keep that margin.
        var fit = box != null && sr.size.x > 0f && sr.size.y > 0f ? new Vector2(box.size.x / sr.size.x, box.size.y / sr.size.y) : Vector2.one;
        int n = 0;
        foreach (var token in rects)
        {
            if (!(token is JArray r) || r.Count != 4) continue;
            var size = new Vector2(r[2].Value<float>() / scale.x, r[3].Value<float>() / scale.y);
            var piece = new GameObject("PlatformTiles_" + n++);
            piece.layer = mover.gameObject.layer;
            piece.tag = mover.gameObject.tag;
            piece.transform.SetParent(mover.transform, false);
            piece.transform.localPosition = new Vector3(r[0].Value<float>() / scale.x, r[1].Value<float>() / scale.y, 0f);
            var art = piece.AddComponent<SpriteRenderer>();
            art.sprite = sr.sprite;
            art.sharedMaterial = sr.sharedMaterial;
            art.drawMode = sr.drawMode == SpriteDrawMode.Simple ? SpriteDrawMode.Sliced : sr.drawMode;
            art.size = size;
            art.color = sr.color;
            art.sortingLayerID = sr.sortingLayerID;
            art.sortingOrder = sr.sortingOrder;
            var col = piece.AddComponent<BoxCollider2D>();
            col.size = Vector2.Scale(size, fit);
        }
        sr.enabled = false;
        if (box != null) box.enabled = false;
    }

    private static void ApplyZip(GameObject clone, JObject zip)
    {
        var mover = clone.GetComponentInChildren<PlatformMover>(true);
        if (mover == null) return;
        var endArr = zip["end"] as JArray;
        var end = endArr != null && endArr.Count == 2 ? new Vector2(endArr[0].Value<float>(), endArr[1].Value<float>()) : Vector2.zero;
        // Positions is an array of a private struct: edit boxed copies, write them back.
        var posField = typeof(PlatformMover).GetField("Positions", BindingFlags.Instance | BindingFlags.NonPublic);
        if (posField?.GetValue(mover) is Array positions && positions.Length >= 2)
        {
            var elemType = positions.GetType().GetElementType();
            var position = elemType.GetField("position");
            var time = elemType.GetField("timeToReachFromPrevious");
            var last = positions.GetValue(positions.Length - 1);
            position.SetValue(last, end);
            if (zip["time"] != null) time.SetValue(last, zip["time"].Value<float>());
            positions.SetValue(last, positions.Length - 1);
            var first = positions.GetValue(0);
            if (zip["backTime"] != null) time.SetValue(first, zip["backTime"].Value<float>());
            positions.SetValue(first, 0);
            if (zip["auto"]?.Value<bool>() == true)
            {
                // Moving on its own: every phase starts the next once its pause is
                // over, and touching it no longer matters.
                var wait = elemType.GetField("waitOnPhaseEnd");
                for (int i = 0; i < positions.Length; i++)
                {
                    var phase = positions.GetValue(i);
                    elemType.GetField("autoStartNextPhase").SetValue(phase, true);
                    elemType.GetField("nextPhaseOnEnter").SetValue(phase, false);
                    elemType.GetField("nextPhaseOnExit").SetValue(phase, false);
                    if (i == positions.Length - 1) wait.SetValue(phase, zip["pauseReturn"]?.Value<float>() ?? 0.5f);
                    if (i == 0) wait.SetValue(phase, zip["pauseMove"]?.Value<float>() ?? 1f);
                    positions.SetValue(phase, i);
                }
                mover.gameObject.AddComponent<MapZipAutoStart>().FirstPause = zip["pauseMove"]?.Value<float>() ?? 1f;
            }
            posField.SetValue(mover, positions);
        }
        // The track and its end node follow the new path.
        var len = end.magnitude;
        var dir = len > 0.001f ? end / len : Vector2.up;
        foreach (var tr in clone.GetComponentsInChildren<Transform>(true))
        {
            if (tr.name == "ZipTrack")
            {
                tr.localPosition = end / 2f + dir * 5f;
                tr.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
                var sr = tr.GetComponent<SpriteRenderer>();
                if (sr != null) sr.size = new Vector2(len + 30f, sr.size.y);
            }
            else if (tr.name == "ZipNode (1)") tr.localPosition = end - dir * 5f;
        }
        // A platform built from grate tiles: rectangles of the game's own grate
        // hung off the moving part, each with its collider. Colliders on a child
        // belong to the moving part's body, so the game carries the player on them.
        if (zip["rects"] is JArray rects && rects.Count > 0)
        {
            BuildPlatform(mover, rects);
            return;
        }
        // The platform's size: the game shapes it to its track (tall across a
        // level one, flat across an upright or diagonal one) rather than turning it.
        if (zip["size"] is JArray sizeArr && sizeArr.Count == 2)
        {
            var size = new Vector2(sizeArr[0].Value<float>(), sizeArr[1].Value<float>());
            var sr = mover.GetComponent<SpriteRenderer>();
            var box = mover.GetComponent<BoxCollider2D>();
            if (sr != null)
            {
                var old = sr.size;
                sr.size = size;
                if (box != null && old.x > 0f && old.y > 0f) box.size = new Vector2(box.size.x * size.x / old.x, box.size.y * size.y / old.y);
            }
        }
        else if (zip["width"] != null)
        {
            var width = zip["width"].Value<float>();
            var sr = mover.GetComponent<SpriteRenderer>();
            var box = mover.GetComponent<BoxCollider2D>();
            if (sr != null)
            {
                var old = sr.size.x;
                sr.size = new Vector2(width, sr.size.y);
                if (box != null && old > 0f) box.size = new Vector2(box.size.x * width / old, box.size.y);
            }
        }
    }
}

// Starts a self-moving zip mover's loop. The game only ever starts a zip
// mover's first move from a touch, and resets it whenever its zone is
// switched off and on again, so this starts it on every enable.
internal class MapZipAutoStart : MonoBehaviour
{
    public float FirstPause = 1f;
    private static readonly MethodInfo Advance = typeof(PlatformMover).GetMethod("IE_AdvanceToNextState", BindingFlags.Instance | BindingFlags.NonPublic);

    private void OnEnable() => StartCoroutine(Kick());

    private System.Collections.IEnumerator Kick()
    {
        // PlatformMover.OnEnable jumps back to its start over a couple of physics steps.
        for (int i = 0; i < 3; i++) yield return new WaitForFixedUpdate();
        if (FirstPause > 0f) yield return new WaitForSeconds(FirstPause);
        var mover = GetComponent<PlatformMover>();
        if (mover == null || Advance == null || Recharge.ModApi.Reflect.GetField<bool>(mover, "isPhaseChangeInProgess")) yield break;
        mover.StartCoroutine((System.Collections.IEnumerator)Advance.Invoke(mover, null));
    }
}
