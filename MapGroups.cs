using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

// Group ids from the editor: every object with the same "group" is shown,
// hidden, toggled or moved together by group triggers. Grouped tiles are
// painted on their own tilemap per group, so the group can switch them off.
internal static class MapGroups
{
    public static string Of(JObject obj)
    {
        var g = obj["group"]?.Value<string>();
        return string.IsNullOrEmpty(g) ? null : g;
    }

    public static void Add(MapWorld w, JObject obj, GameObject go)
    {
        var g = Of(obj);
        if (g == null || go == null) return;
        if (!w.Groups.TryGetValue(g, out var list)) w.Groups[g] = list = new List<GameObject>();
        if (!list.Contains(go)) list.Add(go);
    }

    // The groups the editor marked "starts hidden", once each thing is built.
    public static void HideStarting(MapWorld w)
    {
        foreach (var g in w.Def.HiddenGroups ?? new List<string>())
        {
            if (!w.Groups.TryGetValue(g, out var list)) continue;
            foreach (var go in list)
                if (go != null && w.StartedHidden.Add(go)) go.SetActive(false);
        }
    }

    public static IEnumerable<GameObject> Members(MapWorld w, string g) =>
        g != null && w.Groups.TryGetValue(g, out var list) ? list.Where(go => go != null) : Enumerable.Empty<GameObject>();
}

// A trigger zone acting when the player walks in: on a group (show, hide,
// toggle, move) or on the player (teleport, respawn point, camera zoom, a message).
internal class MapGroupTrigger : MonoBehaviour
{
    public MapWorld World;
    public string Kind;
    public string Group;
    public Vector2 Offset;
    public float Seconds = 1f;
    public bool Back;
    public bool Once;
    public Vector2 Target;
    public float Zoom = 1f;
    public string Text;

    private bool _used;
    private bool _moved;
    private Coroutine _moving;

    private void OnTriggerEnter2D(Collider2D collision)
    {
        if (!collision.CompareTag("Player") || World == null) return;
        var mv = collision.GetComponent<Movement>();
        if (Kind == "zoom") { SetZoom(mv, Zoom); return; }
        if (Once && _used) return;
        _used = true;
        switch (Kind)
        {
            case "show": SetGroup(_ => true); break;
            case "hide": SetGroup(_ => false); break;
            case "toggle": SetGroup(go => !go.activeSelf); break;
            case "move": if (!_moved) MoveGroup(Offset); break;
            case "teleport": Teleport(mv); break;
            case "respawn": if (mv != null) mv.respawnPoint = Target + new Vector2(0f, 12f); break;
            case "message": MapMessage.Show(Text, Seconds); break;
        }
    }

    private void OnTriggerExit2D(Collider2D collision)
    {
        if (!collision.CompareTag("Player") || World == null) return;
        if (Kind == "zoom") SetZoom(collision.GetComponent<Movement>(), 1f);
        if (Kind == "move" && Back && _moved) MoveGroup(-Offset);
    }

    private void SetGroup(System.Func<GameObject, bool> active)
    {
        foreach (var go in MapGroups.Members(World, Group).ToList()) go.SetActive(active(go));
    }

    private void MoveGroup(Vector2 by)
    {
        _moved = by == Offset;
        if (_moving != null) StopCoroutine(_moving);
        _moving = StartCoroutine(Slide(MapGroups.Members(World, Group).Select(go => go.transform).ToList(), by, Seconds));
    }

    // Moved by the step each frame rather than to fixed points, so a floating-origin shift mid-move is fine.
    private static IEnumerator Slide(List<Transform> members, Vector2 by, float seconds)
    {
        float done = 0f;
        for (float t = 0f; done < 1f;)
        {
            t += Time.deltaTime;
            var next = seconds <= 0f ? 1f : Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / seconds));
            var step = (Vector3)(by * (next - done));
            foreach (var m in members) if (m != null) m.position += step;
            done = next;
            if (done < 1f) yield return null;
        }
    }

    private void Teleport(Movement mv)
    {
        if (mv == null) return;
        var pos = MapWorld.Live(Target);
        mv.transform.position = pos;
        var body = mv.GetComponent<Rigidbody2D>();
        if (body != null) { body.position = pos; body.linearVelocity = Vector2.zero; }
        if (mv.cam != null) mv.cam.setup(pos, mv.cam.camSize);
    }

    private void SetZoom(Movement mv, float zoom)
    {
        if (mv == null || mv.cam == null) return;
        var normal = World.Def.CameraSize > 0 ? World.Def.CameraSize.Value : MapSpawn.DefaultCamSize;
        mv.cam.newCamSize(normal * zoom);
    }
}

// A line of text on screen for a few seconds, in the game's own font.
internal static class MapMessage
{
    private static GameObject _shown;

    public static void Show(string text, float seconds)
    {
        if (_shown != null) Object.Destroy(_shown);
        var go = new GameObject("MapMessage");
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 500;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        var label = new GameObject("Text");
        label.transform.SetParent(go.transform, false);
        var tmp = label.AddComponent<TMPro.TextMeshProUGUI>();
        var font = Object.FindObjectsByType<TMPro.TMP_Text>(FindObjectsSortMode.None).Select(t => t.font).FirstOrDefault(f => f != null);
        if (font != null) tmp.font = font;
        tmp.text = text ?? "";
        tmp.fontSize = 56f;
        tmp.alignment = TMPro.TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.outlineWidth = 0.2f;
        tmp.outlineColor = new Color32(0, 0, 0, 255);
        var rect = (RectTransform)label.transform;
        rect.anchorMin = new Vector2(0.1f, 0.12f);
        rect.anchorMax = new Vector2(0.9f, 0.3f);
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        _shown = go;
        Object.Destroy(go, Mathf.Max(0.5f, seconds));
    }
}

// When an end-credits trigger fires: the area layer its credits play on comes on,
// even outside the area it belongs to (MapWorld.ZoneOnly would keep it off there).
internal class MapCreditsShow : MonoBehaviour
{
    public MapWorld World;

    private void OnTriggerEnter2D(Collider2D collision)
    {
        if (World == null || collision.GetComponent<Movement>() == null) return;
        var credits = GetComponentInChildren<EndCreditsTrigger>(true)?.credits;
        for (var t = credits != null ? credits.transform : null; t != null; t = t.parent)
            foreach (var (go, _) in World.ZoneOnly)
                if (go != null && go.transform == t) go.SetActive(true);
    }
}
