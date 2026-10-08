using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Recharge.ModApi;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// The game's door transition, run by the mod so a map can load behind it.
//
// The game's doors are one full-screen UI Image whose sprite is flipped by an animation
// (CloseGate: 21 frames at 35 fps, loadScene() fires at 0.6 s; "gate" opens a new scene:
// 0.83 s holding shut, then 22 frames at 48 fps). The animator dies with the scene, and
// the map is only built a second or two after the new scene starts, so the open would
// play over a half-built world. Here the same sprites are sampled out of the game's own
// clips and shown on an overlay that survives the scene change:
//   1. freeze: the screen is copied into a still, the cameras go off (closing frames cost
//      almost nothing), the save is handled the way changeScene would;
//   2. close: door frame from real elapsed time, never advanced per frame;
//   3. load: LoadSceneAsync behind the shut doors, then wait for the map (MapManager.MapReady);
//   4. open: again from real time; the new scene's own door animation is put at its end.
// Anything that fails before the scene change falls back to the game's own changeScene.
internal class DoorTransition : MonoBehaviour
{
    private const float TimeoutSeconds = 60f;
    private const int SettleFrames = 3;

    public static bool Busy { get; private set; }
    private static DoorTransition _host;

    private class Plan
    {
        public pauseMenuScript Menu;
        public bool Hard;
        public Action Prepare;
        public Animator Anim;
        public Image Source;
        public string Scene;
        public Sprite[] Close;
        public float CloseSeconds;
        public float CloseRate;
        public Sprite[] Open;
        public float OpenRate;
        public Rect Rect;     // screen pixels; zero size = fill the screen
    }

    // prepare runs exactly once (saves and the pending map), here or on the game's own path.
    public static void Run(pauseMenuScript menu, bool hard, Action prepare)
    {
        if (Busy) return;
        Plan plan = null;
        try { plan = MakePlan(menu, hard, prepare); }
        catch (Exception e) { Debug.LogWarning("[RechargeMaps] doors: can't run the transition itself, using the game's: " + e.Message); }
        if (plan == null) { Fallback(menu, hard, prepare); return; }

        if (_host == null)
        {
            var go = new GameObject("RechargeDoors");
            DontDestroyOnLoad(go);
            _host = go.AddComponent<DoorTransition>();
        }
        Busy = true;
        _host._plan = plan;
        _host._routine = _host.StartCoroutine(_host.Guard(_host.Sequence(plan)));
    }

    private static void Fallback(pauseMenuScript menu, bool hard, Action prepare)
    {
        prepare();
        if (hard) menu.changeSceneHard(); else menu.changeScene();
    }

    // Everything that can be found out before anything is touched.
    private static Plan MakePlan(pauseMenuScript menu, bool hard, Action prepare)
    {
        if (menu == null) return null;
        var anim = Reflect.GetField<Animator>(menu, hard ? "sceneChangeHardAnim" : "sceneChangeAnim");
        if (anim == null || anim.runtimeAnimatorController == null) throw new Exception("no door animator");
        var image = anim.GetComponent<Image>() ?? anim.GetComponentInChildren<Image>(true);
        if (image == null) throw new Exception("no door image");
        var scene = (anim.GetComponent<SceneTransitionObject>() ?? anim.GetComponentInParent<SceneTransitionObject>())?.sceneToLoad;
        if (string.IsNullOrEmpty(scene) || !Application.CanStreamedLevelBeLoaded(scene)) throw new Exception("no scene to load ('" + scene + "')");

        var clips = anim.runtimeAnimatorController.animationClips;
        var close = clips.FirstOrDefault(c => c.events.Any(ev => ev.functionName == "loadScene"));
        if (close == null) throw new Exception("no closing clip");
        var open = clips.Where(c => c != close && c.length > 0.3f).OrderByDescending(c => c.length).FirstOrDefault();
        if (open == null) throw new Exception("no opening clip");

        var original = image.sprite;
        var plan = new Plan { Menu = menu, Hard = hard, Prepare = prepare, Anim = anim, Source = image, Scene = scene };
        try
        {
            plan.CloseSeconds = close.events.First(ev => ev.functionName == "loadScene").time;
            plan.CloseRate = close.frameRate;
            var count = Mathf.Max(1, Mathf.RoundToInt(plan.CloseSeconds * plan.CloseRate));
            plan.Close = Sample(anim.gameObject, image, close, plan.CloseRate, count);
            // The open clip rests on the shut frame first; the opening starts where it first changes.
            plan.OpenRate = open.frameRate;
            var all = Sample(anim.gameObject, image, open, plan.OpenRate, Mathf.RoundToInt(open.length * plan.OpenRate) + 1);
            var from = Array.FindIndex(all, s => s != all[0]);
            if (from < 0) throw new Exception("the opening clip never changes");
            plan.Open = all.Skip(from).ToArray();
        }
        finally { image.sprite = original; }
        if (plan.Close.Distinct().Count() < 4 || plan.Open.Distinct().Count() < 4) throw new Exception("couldn't read the door frames");

        // Where the game's doors sit on screen.
        var canvas = image.canvas != null ? image.canvas.rootCanvas : null;
        if (canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay)
        {
            var c = new Vector3[4];
            image.rectTransform.GetWorldCorners(c);
            var r = new Rect(c[0].x, c[0].y, c[2].x - c[0].x, c[2].y - c[0].y);
            if (r.width >= Screen.width * 0.9f && r.height >= Screen.height * 0.9f) plan.Rect = r;
        }
        return plan;
    }

    private static Sprite[] Sample(GameObject go, Image image, AnimationClip clip, float rate, int count)
    {
        var frames = new Sprite[count];
        for (var i = 0; i < count; i++)
        {
            clip.SampleAnimation(go, Mathf.Min(clip.length, (i + 0.25f) / rate));
            frames[i] = image.sprite;
            if (frames[i] == null) throw new Exception("frame " + i + " of '" + clip.name + "' has no sprite");
        }
        return frames;
    }

    // ---------------------------------------------------------------- running

    private Plan _plan;
    private Coroutine _routine;
    private GameObject _overlay;
    private RawImage _still;
    private Image _doors;
    private Texture2D _stillTexture;
    private readonly List<Camera> _cameras = new List<Camera>();
    private bool _sceneLoaded;
    private float _lastSceneLoad;
    private bool _mapReady;
    private float _hardDeadline;
    private bool _shown;

    private void OnMapReady() => _mapReady = true;
    private void OnSceneLoadedEvent(Scene s, LoadSceneMode m) { _sceneLoaded = true; _lastSceneLoad = Time.realtimeSinceStartup; }

    // A coroutine that throws would just stop, leaving the doors shut for good.
    private IEnumerator Guard(IEnumerator inner)
    {
        while (true)
        {
            bool more;
            try { more = inner.MoveNext(); }
            catch (Exception e)
            {
                Debug.LogError("[RechargeMaps] doors: the transition failed, opening: " + e);
                Teardown();
                yield break;
            }
            if (!more) yield break;
            yield return inner.Current;
        }
    }

    // Backstop if the sequence is killed or stalls outright.
    private void Update()
    {
        if (!Busy || _hardDeadline == 0f || Time.realtimeSinceStartup < _hardDeadline) return;
        Debug.LogWarning("[RechargeMaps] doors: still running " + (TimeoutSeconds + 20f) + " s in, forcing them open");
        if (_routine != null) StopCoroutine(_routine);
        Teardown();
    }

    private IEnumerator Sequence(Plan p)
    {
        var started = Time.realtimeSinceStartup;
        _hardDeadline = started + TimeoutSeconds + 20f;
        float worst = 0f, last = started;
        SceneManager.sceneLoaded += OnSceneLoadedEvent;
        MapManager.MapReady += OnMapReady;

        // 1. freeze. The screen as it is now, taken once the frame has been drawn.
        yield return new WaitForEndOfFrame();
        if (!Freeze(p)) { Teardown(); Fallback(p.Menu, p.Hard, p.Prepare); yield break; }

        // The saves and pending map; slow (file copies), so the door clock starts after.
        p.Prepare();
        var waitForMap = MapManager.Pending;
        ChangeSceneSteps(p);
        yield return null;

        // 2. close, by real time.
        var t0 = Time.realtimeSinceStartup;
        last = t0;
        while (true)
        {
            var now = Time.realtimeSinceStartup;
            worst = Mathf.Max(worst, now - last);
            last = now;
            var el = now - t0;
            _doors.sprite = p.Close[Mathf.Min(p.Close.Length - 1, (int)(el * p.CloseRate))];
            if (el >= p.CloseSeconds) break;
            yield return null;
        }
        _doors.sprite = p.Close[p.Close.Length - 1];
        var closedAt = Time.realtimeSinceStartup;

        // 3. load behind the shut doors.
        var loadStart = closedAt;
        var op = SceneManager.LoadSceneAsync(p.Scene);
        if (op == null) throw new Exception("LoadSceneAsync('" + p.Scene + "') returned nothing");
        while (!op.isDone && Time.realtimeSinceStartup - loadStart < TimeoutSeconds)
        {
            worst = Mathf.Max(worst, Time.realtimeSinceStartup - last);
            last = Time.realtimeSinceStartup;
            yield return null;
        }
        var loadedAt = Time.realtimeSinceStartup;

        // The map is built (or gave up) when LoadMap says so; anything else is ready once its
        // player is up, or the loads have gone quiet.
        var steady = 0f;
        while (Time.realtimeSinceStartup - loadStart < TimeoutSeconds)
        {
            var now = Time.realtimeSinceStartup;
            worst = Mathf.Max(worst, now - last);
            last = now;
            if (waitForMap) { if (_mapReady) break; }
            else if (_sceneLoaded)
            {
                if (PlayerReady()) { steady += Time.unscaledDeltaTime; if (steady > 0.5f) break; } else steady = 0f;
                if (now - _lastSceneLoad > 3f) break;
            }
            yield return null;
        }
        if (Time.realtimeSinceStartup - loadStart >= TimeoutSeconds)
            Debug.LogWarning("[RechargeMaps] doors: not ready after " + TimeoutSeconds + " s (scene loaded: " + _sceneLoaded + ", map ready: " + _mapReady + "), opening anyway");
        var builtAt = Time.realtimeSinceStartup;
        for (var i = 0; i < SettleFrames; i++) yield return null;

        // 4. open, by real time. The picture goes first: the live world is behind it now.
        FinishGameDoors();
        if (_still != null) _still.enabled = false;
        var o0 = Time.realtimeSinceStartup;
        last = o0;
        var openSeconds = p.Open.Length / p.OpenRate;
        while (true)
        {
            var now = Time.realtimeSinceStartup;
            worst = Mathf.Max(worst, now - last);
            last = now;
            var el = now - o0;
            if (el >= openSeconds) break;
            _doors.sprite = p.Open[Mathf.Min(p.Open.Length - 1, (int)(el * p.OpenRate))];
            yield return null;
        }
        var openedAt = Time.realtimeSinceStartup;
        Teardown();
        Debug.Log("[RechargeMaps] doors: closed in " + Ms(t0, closedAt) + " ms, scene loaded in " + Ms(loadStart, loadedAt) +
                  " ms, map built in " + Ms(loadedAt, builtAt) + " ms, opened in " + Ms(o0, openedAt) + " ms (worst frame " + Mathf.RoundToInt(worst * 1000f) + " ms)");
    }

    private static int Ms(float from, float to) => Mathf.RoundToInt((to - from) * 1000f);

    private static bool PlayerReady()
    {
        var m = MapUpgrades.GamePlayer();
        return m != null && !Reflect.GetField<bool>(m, "isOnMainMenu") && !Reflect.GetField<bool>(m, "init");
    }

    // The still, the overlay and the cameras. False: nothing was changed.
    private bool Freeze(Plan p)
    {
        try
        {
            var w = Screen.width;
            var h = Screen.height;
            _stillTexture = new Texture2D(w, h, TextureFormat.RGB24, false);
            _stillTexture.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            _stillTexture.Apply();

            _overlay = new GameObject("DoorOverlay");
            _overlay.transform.SetParent(transform, false);
            var canvas = _overlay.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;
            _overlay.AddComponent<GraphicRaycaster>();

            var stillGo = new GameObject("Still", typeof(RectTransform));
            stillGo.transform.SetParent(_overlay.transform, false);
            Fill(stillGo.GetComponent<RectTransform>(), new Rect());
            _still = stillGo.AddComponent<RawImage>();
            _still.texture = _stillTexture;
            _still.raycastTarget = true;

            var doorGo = new GameObject("Doors", typeof(RectTransform));
            doorGo.transform.SetParent(_overlay.transform, false);
            Fill(doorGo.GetComponent<RectTransform>(), p.Rect);
            _doors = doorGo.AddComponent<Image>();
            _doors.sprite = p.Close[0];
            _doors.type = p.Source.type;
            _doors.preserveAspect = p.Source.preserveAspect;
            _doors.color = p.Source.color;
            if (p.Source.material != null && p.Source.material != Graphic.defaultGraphicMaterial) _doors.material = p.Source.material;
            _doors.raycastTarget = true;
            _shown = true;

            // The live world is not drawn while the doors close and the next scene loads.
            foreach (var cam in Camera.allCameras) { _cameras.Add(cam); cam.enabled = false; }
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[RechargeMaps] doors: can't freeze the screen, using the game's: " + e.Message);
            Teardown();
            return false;
        }
    }

    private static void Fill(RectTransform rt, Rect screen)
    {
        if (screen.width <= 0f)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            return;
        }
        // The overlay canvas is 1 unit per pixel, so the game's own screen rect carries over.
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero; rt.pivot = Vector2.zero;
        rt.anchoredPosition = screen.position;
        rt.sizeDelta = screen.size;
    }

    // What pauseMenuScript.changeScene does besides playing the animation.
    private static void ChangeSceneSteps(Plan p)
    {
        if (SceneManager.GetActiveScene().name != "MainMenu")
        {
            if (Saveloader.Instance != null) Saveloader.Instance.manualSave();
            Reflect.GetField<Movement>(p.Menu, "player")?.gameObject.SetActive(false);
            // The scene is about to be loaded over a minute at worst: no autosave of a player who isn't there.
            MapSaves.HoldGameSaving();
        }
        Time.timeScale = 1;
        Reflect.TrySetField(p.Menu, "changingSceneNow", true);
        globalStats.difficultyLevel = p.Hard ? 1 : 0;
    }

    // The new scene's own doors start shut and open on their own clock; they must end open,
    // not still shut or half way when ours go.
    private static void FinishGameDoors()
    {
        foreach (var t in FindObjectsByType<SceneTransitionObject>(FindObjectsSortMode.None))
        {
            var a = t != null ? t.anim : null;
            if (a == null || !a.isActiveAndEnabled) continue;
            var open = Animator.StringToHash("gate");
            var state = a.HasState(0, open) ? open : a.GetCurrentAnimatorStateInfo(0).fullPathHash;
            a.Play(state, 0, 1f);
            a.Update(0f);
        }
    }

    private void Teardown()
    {
        SceneManager.sceneLoaded -= OnSceneLoadedEvent;
        MapManager.MapReady -= OnMapReady;
        foreach (var cam in _cameras) if (cam != null) cam.enabled = true;
        _cameras.Clear();
        // The scene never changed: the old one carries on, with its player and its menu.
        if (_shown && !_sceneLoaded && _plan != null)
        {
            Reflect.GetField<Movement>(_plan.Menu, "player")?.gameObject.SetActive(true);
            Reflect.TrySetField(_plan.Menu, "changingSceneNow", false);
        }
        if (_overlay != null) Destroy(_overlay);
        if (_stillTexture != null) Destroy(_stillTexture);
        _overlay = null; _still = null; _doors = null; _stillTexture = null;
        _shown = false; _sceneLoaded = false; _mapReady = false; _hardDeadline = 0f; _routine = null; _plan = null;
        Busy = false;
    }
}
