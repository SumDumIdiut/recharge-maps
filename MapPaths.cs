using System.IO;
using UnityEngine;

// RechargeLoaderBootstrap.ModsRoot is internal to Assembly-CSharp and unreachable
// from a mod DLL, so this recomputes the same formula directly (matches maps.rs's
// <GameDir>\Recharge\Mods\recharge.maps\maps expectation exactly).
internal static class MapPaths
{
    public static string ModsRoot => Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Recharge", "Mods");
    public static string MapsDir => Path.Combine(ModsRoot, "recharge.maps", "maps");
    public static string TexturesDir => Path.Combine(ModsRoot, "recharge.maps", "textures");
}
