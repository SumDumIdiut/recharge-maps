using UnityEngine;

// The overgrown stages' edits: the set for the level's area-1 state is built,
// and swapped when the game changes it (the breaker tripping) - the state
// itself is always the game's own.
internal static class MapStages
{
    private static string _built;
    private static float _next;

    public static string Current()
    {
        var stats = Singleton<globalStats>.Instance;
        return stats != null && stats.currentA1State == globalStats.area1states.Overgrown ? "overgrown" : "start";
    }

    public static void Start(MapWorld w)
    {
        _built = null;
        if (w.StageObjects.Count > 0) Build(w, Current());
    }

    public static void Tick(MapWorld w)
    {
        if (w.StageObjects.Count == 0 || Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 0.5f;
        var now = Current();
        if (now != _built) Build(w, now);
    }

    private static void Build(MapWorld w, string state)
    {
        w.UnloadStage();
        _built = state;
        var n = MapObjects.BuildStage(w, state);
        Debug.Log("[RechargeMaps] overgrown stages: built the " + state + " edits (" + n + ")");
    }
}
