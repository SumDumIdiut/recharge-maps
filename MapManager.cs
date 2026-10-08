using System;
using System.Reflection;
using Recharge.ModApi;
using UnityEngine;
using UnityEngine.SceneManagement;

// The map lifecycle. Playing a map always reloads the gameplay scene with the
// map's save swapped in (MapSaves); once that scene's Player is ready the map
// is built into it (MapWorld + MapObjects + MapCourses) and the player put in
// (MapSpawn). A scene load ends whatever map was loaded.
internal class MapManager : MonoBehaviour
{
    public static MapManager Instance { get; private set; }

    // The map loaded right now, or null in Base Game.
    public static string CurrentMapId => Instance?._world?.MapId;

    // Raised once a map load is over: built, or given up on. The door transition opens on it.
    public static event Action MapReady;
    // A map is chosen and not built yet.
    public static bool Pending => Instance != null && Instance._pendingMapId != null;
    private static void SignalReady()
    {
        try { MapReady?.Invoke(); }
        catch (Exception e) { Debug.LogWarning("[RechargeMaps] map-ready listener failed: " + e.Message); }
    }

    private MapWorld _world;
    private string _pendingMapId;
    private Coroutine _pendingRoutine;

    public static MapManager GetOrCreate()
    {
        if (Instance != null) return Instance;
        var go = new GameObject("MapManager");
        DontDestroyOnLoad(go);
        return go.AddComponent<MapManager>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded += (scene, mode) =>
        {
            MapSaves.OnSceneLoaded(scene, this);
            // The old scene took the map's objects with it; nothing to undo.
            _world = null;
            MapRespawn.Reset();
            ScanScene();
            if (_pendingMapId != null && _pendingRoutine == null) _pendingRoutine = StartCoroutine(LoadWhenPlayerReady());
        };
        ScanScene();
    }

    private void ScanScene()
    {
        RealAssetPalette.ScanCurrentScene();
        if (RealAssetPalette.Get<SpringScript>() != null) StartCoroutine(RealAssetPalette.CaptureSpringAnimation());
    }

    // asNewGame: start the map over (the map maker's test runs).
    public void PlayMap(string mapId, pauseMenuScript menu, bool asNewGame = false)
    {
        if (menu == null) { Debug.LogWarning("[RechargeMaps] no pause menu to change scene with - can't play '" + mapId + "'"); return; }
        var def = MapDefinition.Read(mapId);
        // The doors close, the scene loads and the map is built behind them; the game's own
        // changeScene is the fallback. The save and pending map are set up before the scene goes.
        DoorTransition.Run(menu, false, () =>
        {
            MapSaves.BeginMapScene(mapId, def?.Player, asNewGame);
            _pendingMapId = mapId;
        });
    }

    // Back to Base Game / B-side from a map: the map is saved and released as the doors close.
    public static void LeaveTo(pauseMenuScript menu, bool hard)
    {
        DoorTransition.Run(menu, hard, () => { MapSaves.SaveForSceneChange(); MapSaves.LeaveMap(); });
    }

    // The gameplay Player moves itself to its save's respawn point as it
    // starts (and the title screen has a Player of its own), so wait for it.
    private System.Collections.IEnumerator LoadWhenPlayerReady()
    {
        float waited = 0f;
        // The map this coroutine is loading, fixed now: _pendingMapId is cleared once
        // the save scene has been set up, and that can happen before the Player is ready.
        var want = _pendingMapId;
        while (true)
        {
            // Unity keeps ticking frames while a scene loads, and the incoming Player
            // can be found during that window - before sceneLoaded fires and MapSaves
            // has put the map's save in place. Building then would run the map on
            // Base Game progress: its money, its abilities, and no save of its own.
            var saveInPlace = want != null && MapSaves.ActiveMapId == want;
            var movement = saveInPlace ? MapUpgrades.GamePlayer() : null;
            if (movement != null && !Reflect.GetField<bool>(movement, "isOnMainMenu") && (!Reflect.GetField<bool>(movement, "init") || (MapSaves.StartedFresh && waited > 0.5f))) break;
            // Nothing left to load into: the scene change went somewhere else.
            if (want != null && !saveInPlace && _pendingMapId == null && MapSaves.ActiveMapId == null)
            {
                Debug.LogWarning("[RechargeMaps] stopped waiting for '" + want + "': the scene changed without it");
                _pendingRoutine = null;
                MapSaves.ReleaseSave();
                SignalReady();
                yield break;
            }
            if (waited > 60f)
            {
                Debug.LogWarning("[RechargeMaps] gave up waiting for a gameplay Player to load '" + want + "' into");
                _pendingMapId = null;
                _pendingRoutine = null;
                MapSaves.ReleaseSave();
                SignalReady();
                yield break;
            }
            yield return null;
            waited += Time.unscaledDeltaTime;
        }
        yield return null;
        yield return null;
        _pendingMapId = null;
        _pendingRoutine = null;
        LoadMap(want);
        SignalReady();
    }

    private void LoadMap(string mapId)
    {
        // Everything loading from a save is done: the real save goes back from here on.
        MapSaves.ReleaseSave();
        var def = MapDefinition.Read(mapId);
        if (def == null) return;
        if (def.Groups == null || def.Groups.Count == 0) { Debug.LogError("[RechargeMaps] map has no groups: " + mapId); return; }
        try
        {
            _world?.Unload();
            _world = null;

            MapSaves.Enter(mapId);
            var newGame = MapSaves.StartedFresh && MapSaves.ActiveMapId == mapId;
            if (newGame) MapUpgrades.ApplyStart(mapId, def.Player);
            MapUpgrades.EnsureMapUnlocks(def);

            var w = new MapWorld(mapId, def);
            _world = w;
            // A map with stage edits follows the game's own area-1 state; others show the one they were made in.
            if (w.Overlay && !def.Stages) w.ApplyBaseState(def.BaseState);
            // A rebuilt level: area 1 in the state it was made in, and the area it starts in loaded.
            if (!w.Overlay && def.AreaState == "overgrown" && Singleton<globalStats>.Instance != null) Singleton<globalStats>.Instance.currentA1State = globalStats.area1states.Overgrown;
            var built = MapObjects.Build(w);
            if (!w.Overlay && def.StartZone > 0) MapMedia.LoadArea(def.StartZone, true);
            _zone = -1;
            MapMedia.ShiftBackgrounds(w);
            MapStages.Start(w);
            MapCourses.Build(w);
            MapObjects.DropPrestigeTexts(w);
            MapMedia.Start(w, this);
            // The area the player starts in: its background, lights and music.
            if (!w.Overlay) MapMedia.ApplyArea(w.Group.SpawnArea, true);
            MapSpawn.Place(w, this);
            StartCoroutine(RefreshersNextFrame(w));
            if (newGame && w.Overlay) StartCoroutine(ShowTutorialGlyphs());
            Debug.Log("[RechargeMaps] loaded " + (w.Overlay ? "overlay" : "custom") + " map '" + mapId + "' (" + built + "/" + w.Group.Objects.Count + " objects)" + (newGame ? " as a new game" : ""));
        }
        catch (Exception e)
        {
            Debug.LogError("[RechargeMaps] loading '" + mapId + "' failed: " + e);
        }
    }

    // After the scene's own Start() calls, which hide every refresher's sprites.
    private System.Collections.IEnumerator RefreshersNextFrame(MapWorld w)
    {
        yield return null;
        yield return null;
        if (_world == w) MapUpgrades.RefreshLevelRefreshers(w);
    }

    // A new game shows the game's control glyphs; loading the map can brush
    // the player through the trigger that fades them.
    private System.Collections.IEnumerator ShowTutorialGlyphs()
    {
        yield return new WaitForSecondsRealtime(1.5f);
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var trigger in Resources.FindObjectsOfTypeAll<TutorialTextTrigger>())
        {
            if (trigger == null || !trigger.gameObject.scene.IsValid()) continue;
            (typeof(TutorialTextTrigger).GetField("obj", any)?.GetValue(trigger) as GameObject)?.SetActive(true);
            if (typeof(TutorialTextTrigger).GetField("texts", any)?.GetValue(trigger) is TMPro.TMP_Text[] texts)
                foreach (var t in texts) if (t != null) { var c = t.color; t.color = new Color(c.r, c.g, c.b, 1f); }
        }
    }

    private int _zone = -1;

    private void Update()
    {
        MapSaves.Tick();
        if (_world != null) MapRespawn.Tick(MapUpgrades.GamePlayer());
        if (_world != null) MapStages.Tick(_world);
        if (_world != null) MapSpawn.TickSwitcher(_world, this);
        var zone = Singleton<ZoneLoader>.Instance != null ? Singleton<ZoneLoader>.Instance.activeZone : 0;
        if (_world != null && zone != _zone) { StartCoroutine(RefreshersNextFrame(_world)); _world.ShowZone(zone); }
        _zone = zone;
    }

    // Called by MapSaves' repeating Invoke while a map's save is active.
    public void MapAutosave() => MapSaves.Autosave();

    private void OnApplicationQuit() => MapSaves.Shutdown();
}
