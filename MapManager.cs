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
        MapSaves.BeginMapScene(mapId, def?.Player, asNewGame);
        _pendingMapId = mapId;
        menu.changeScene();
    }

    // The gameplay Player moves itself to its save's respawn point as it
    // starts (and the title screen has a Player of its own), so wait for it.
    private System.Collections.IEnumerator LoadWhenPlayerReady()
    {
        float waited = 0f;
        while (true)
        {
            var movement = MapUpgrades.GamePlayer();
            if (movement != null && !Reflect.GetField<bool>(movement, "isOnMainMenu") && (!Reflect.GetField<bool>(movement, "init") || (MapSaves.StartedFresh && waited > 0.5f))) break;
            if (waited > 60f)
            {
                Debug.LogWarning("[RechargeMaps] gave up waiting for a gameplay Player to load '" + _pendingMapId + "' into");
                _pendingMapId = null;
                _pendingRoutine = null;
                yield break;
            }
            yield return null;
            waited += Time.unscaledDeltaTime;
        }
        yield return null;
        yield return null;
        var mapId = _pendingMapId;
        _pendingMapId = null;
        _pendingRoutine = null;
        LoadMap(mapId);
    }

    private void LoadMap(string mapId)
    {
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
            if (w.Overlay) w.ApplyBaseState(def.BaseState);
            var built = MapObjects.Build(w);
            MapCourses.Build(w);
            MapRespawn.ConvertLevelCourseCheckpoints(w);
            MapSpawn.Place(w, this);
            if (newGame && w.Overlay) StartCoroutine(ShowTutorialGlyphs());
            Debug.Log("[RechargeMaps] loaded " + (w.Overlay ? "overlay" : "custom") + " map '" + mapId + "' (" + built + "/" + w.Group.Objects.Count + " objects)" + (newGame ? " as a new game" : ""));
        }
        catch (Exception e)
        {
            Debug.LogError("[RechargeMaps] loading '" + mapId + "' failed: " + e);
        }
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

    private void Update()
    {
        MapSaves.Tick();
        if (_world != null) MapRespawn.Tick(MapUpgrades.GamePlayer());
    }

    // Called by MapSaves' repeating Invoke while a map's save is active.
    public void MapAutosave() => MapSaves.Autosave();

    private void OnApplicationQuit() => MapSaves.Shutdown();
}
