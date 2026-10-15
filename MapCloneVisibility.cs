using System.Collections.Generic;
using Recharge.ModApi;
using UnityEngine;

// A course's clone visuals in a map. The game shows them only while clonesScript.onScreen is
// set, which its clonesLoD box (a trigger the player walks in and out of) does; in a map that
// box sits wherever the course's template did, nowhere near the player's path, so the clones
// ran and paid but were never drawn. Here the camera decides instead: the clones are shown
// whenever any point of their path is near what a gameplay camera sees. The box is switched
// off meanwhile so its enter / exit events can't fight that.
internal sealed class MapCloneVisibility : MonoBehaviour
{
    private clonesScript _clones;
    private string _label = "?";
    private float _nextCheck;
    private bool _reported;
    private readonly List<(Collider2D col, bool was)> _boxes = new List<(Collider2D, bool)>();

    public void Configure(clonesScript clones, string label)
    {
        _clones = clones;
        if (!string.IsNullOrEmpty(label)) _label = label;
        foreach (var lod in clones.GetComponentsInChildren<clonesLoD>(true))
            foreach (var col in lod.GetComponents<Collider2D>())
            {
                _boxes.Add((col, col.enabled));
                col.enabled = false;
            }
    }

    private void OnDestroy()
    {
        foreach (var (col, was) in _boxes) if (col != null) col.enabled = was;
        _boxes.Clear();
    }

    private const float CheckInterval = 0.1f;   // visibility checks per second = 1 / this
    private const float ShowMargin = 0.25f;     // a clone drawn just off-screen is cheap, one missing at the edge is not
    private const float HideMargin = 0.4f;      // hysteresis: once shown, hidden only when this far out

    // One view per gameplay camera, shared by every course and refreshed at most once per frame.
    private struct View { public Camera Cam; public bool Plain; public Vector2 Centre; public Vector2 Half; }
    private static readonly List<View> Views = new List<View>();
    private static readonly List<Camera> Scratch = new List<Camera>();
    private static int _viewFrame = -1;

    private static void RefreshViews()
    {
        if (_viewFrame == Time.frameCount) return;
        _viewFrame = Time.frameCount;
        Views.Clear();
        Scratch.Clear();
        var main = Camera.main;
        if (main != null && main.isActiveAndEnabled) Scratch.Add(main);
        else
            foreach (var cam in Camera.allCameras)
                if (cam != null && cam.isActiveAndEnabled && cam.orthographic && cam.targetTexture == null) Scratch.Add(cam);
        foreach (var cam in Scratch)
        {
            var t = cam.transform;
            // An unrotated orthographic camera sees an axis-aligned world rectangle; anything else takes the exact viewport route.
            var plain = cam.orthographic && Mathf.Abs(Vector3.Dot(t.right, Vector3.right)) > 0.9999f && Mathf.Abs(Vector3.Dot(t.up, Vector3.up)) > 0.9999f && t.right.x > 0f && t.up.y > 0f;
            var half = new Vector2(cam.orthographicSize * cam.aspect, cam.orthographicSize);
            Views.Add(new View { Cam = cam, Plain = plain, Centre = new Vector2(t.position.x, t.position.y), Half = half });
        }
    }

    // The path in world space and its bounds, built once and again only if the path or the host moves.
    private Vector2[] _source;
    private int _sourceLength;
    private Vector2[] _world = new Vector2[0];
    private Vector4 _hostKey;
    private Vector4 _hostKey2;
    private Vector2 _min, _max;
    private bool _hasBounds;
    private Vector2 _lastCentre, _lastHalf;
    private bool _lastPlain, _haveLast, _lastResult;

    private static Vector4 Key(Transform t) { var m = t.localToWorldMatrix; return new Vector4(m.m00, m.m01, m.m10, m.m11); }
    private static Vector4 Key2(Transform t) { var p = t.position; return new Vector4(p.x, p.y, p.z, 0f); }

    private void RefreshPath()
    {
        var path = _clones.clonePath;
        var host = _clones.transform;
        var k1 = Key(host);
        var k2 = Key2(host);
        if (ReferenceEquals(path, _source) && (path == null || path.Length == _sourceLength) && k1 == _hostKey && k2 == _hostKey2) return;
        _source = path; _sourceLength = path != null ? path.Length : 0; _hostKey = k1; _hostKey2 = k2;
        _haveLast = false;
        if (_world.Length != _sourceLength) _world = new Vector2[_sourceLength];
        _hasBounds = false;
        for (int i = 0; i < _sourceLength; i++)
        {
            var w = host.TransformPoint(path[i]);
            var p = new Vector2(w.x, w.y);
            _world[i] = p;
            if (!_hasBounds) { _min = _max = p; _hasBounds = true; }
            else { _min = Vector2.Min(_min, p); _max = Vector2.Max(_max, p); }
        }
    }

    public string BoundsText()
    {
        RefreshPath();
        return _hasBounds ? "bounds=(" + _min.x.ToString("0") + "," + _min.y.ToString("0") + ")-(" + _max.x.ToString("0") + "," + _max.y.ToString("0") + ")" : "bounds=none";
    }

    private void LateUpdate()
    {
        if (_clones == null || Time.unscaledTime < _nextCheck) return;
        _nextCheck = Time.unscaledTime + CheckInterval;
        RefreshViews();
        if (Views.Count == 0) return;
        RefreshPath();

        var visible = false;
        if (_hasBounds)
        {
            var margin = _clones.onScreen ? HideMargin : ShowMargin;
            for (int v = 0; v < Views.Count && !visible; v++)
            {
                var view = Views[v];
                if (view.Plain)
                {
                    if (_haveLast && _lastPlain && view.Centre == _lastCentre && view.Half == _lastHalf && _lastResult == _clones.onScreen)
                    { visible = _lastResult; break; }
                    // Half is the half extent; the viewport spans 0..1, so a margin m adds m of the full width each side.
                    var hx = view.Half.x * (1f + 2f * margin);
                    var hy = view.Half.y * (1f + 2f * margin);
                    var minX = view.Centre.x - hx; var maxX = view.Centre.x + hx;
                    var minY = view.Centre.y - hy; var maxY = view.Centre.y + hy;
                    if (_max.x < minX || _min.x > maxX || _max.y < minY || _min.y > maxY) continue;   // path bounds miss the view entirely
                    for (int i = 0; i < _world.Length; i++)
                    {
                        var p = _world[i];
                        if (p.x >= minX && p.x <= maxX && p.y >= minY && p.y <= maxY) { visible = true; break; }
                    }
                }
                else
                {
                    for (int i = 0; i < _world.Length && !visible; i++)
                    {
                        var vp = view.Cam.WorldToViewportPoint(new Vector3(_world[i].x, _world[i].y, _clones.transform.position.z));
                        if (vp.z > -1000f && vp.x >= -margin && vp.x <= 1f + margin && vp.y >= -margin && vp.y <= 1f + margin) visible = true;
                    }
                }
            }
            if (Views.Count == 1 && Views[0].Plain) { _lastCentre = Views[0].Centre; _lastHalf = Views[0].Half; _lastPlain = true; _haveLast = true; _lastResult = visible; }
            else _haveLast = false;
        }

        if (_clones.onScreen != visible)
        {
            _clones.isOnScreen(visible);
            Debug.Log("[Navigator] course " + _label + ": clone visuals " + (visible ? "shown" : "hidden") + " by camera (" + Describe(_clones) + ")");
        }
        else if (visible && !_reported) ReportActive();
    }

    // Once clone visuals really exist, where and how they are drawn: the line to read if they still don't show.
    private void ReportActive()
    {
        var active = Reflect.TryGetField<List<GameObject>>(_clones, "activeClones");
        if (active == null || active.Count == 0) return;
        _reported = true;
        var go = active[0];
        var sr = go != null ? go.GetComponent<SpriteRenderer>() : null;
        var cam = Views.Count > 0 ? Views[0].Cam : null;
        var at = cam != null && go != null ? cam.WorldToViewportPoint(go.transform.position) : Vector3.zero;
        Debug.Log("[Navigator] course " + _label + ": " + active.Count + " clone visuals live; first activeSelf=" + (go != null && go.activeSelf) +
                  " inHierarchy=" + (go != null && go.activeInHierarchy) + " layer=" + (go != null ? LayerMask.LayerToName(go.layer) : "?") +
                  " sprite=" + (sr != null && sr.sprite != null ? sr.sprite.name : "none") + " renderer=" + (sr != null && sr.enabled) +
                  " sorting=" + (sr != null ? sr.sortingLayerName + "/" + sr.sortingOrder : "?") + " alpha=" + (sr != null ? sr.color.a.ToString("0.##") : "?") +
                  " viewport=" + at + " camera=" + (cam != null ? cam.name + " mask=" + cam.cullingMask : "none"));
    }

    private static string Describe(clonesScript clones) =>
        "clones=" + clones.cloneCount + " path=" + (clones.clonePath != null ? clones.clonePath.Length : 0);

    // The one line per course at map load: its clone count, whether its visuals are shown, and
    // the layer / sorting its clone sprite is drawn with.
    public static void LogCourse(string id, clonesScript clones)
    {
        if (clones == null) { Debug.Log("[Navigator] course " + id + ": clones=none visible=n/a layer/sorting=n/a (no clone object)"); return; }
        var template = Reflect.TryGetField<GameObject>(clones, "clone");
        var sr = template != null ? template.GetComponentInChildren<SpriteRenderer>(true) : null;
        var lod = clones.GetComponentInChildren<clonesLoD>(true);
        var vis = clones.GetComponent<MapCloneVisibility>();
        Debug.Log("[Navigator] course " + id + ": clones=" + clones.cloneCount + " visible=" + (clones.onScreen ? "on" : "off") +
                  (clones.gameObject.activeInHierarchy ? "" : " (clone object inactive)") + " layer/sorting=" +
                  (template != null ? LayerMask.LayerToName(template.layer) : "?") + "/" + (sr != null ? sr.sortingLayerName + "/" + sr.sortingOrder : "?") +
                  " path=" + (clones.clonePath != null ? clones.clonePath.Length : 0) + " lod=" + (lod != null ? "yes" : "no") +
                  (vis != null ? " " + vis.BoundsText() + " checks/s=" + (1f / CheckInterval).ToString("0") + " (camera rect vs path bounds, exact scan only on overlap)" : " no camera visibility"));
    }
}
