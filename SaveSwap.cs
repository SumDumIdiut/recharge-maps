using System;
using System.IO;
using UnityEngine;

// The file side of map saves. The game only ever loads /Savedata (and its
// /Savedatabackup fallback), and only while a scene starts - so a map's save
// is loaded the game's own way: the real folders are set aside, the map's save
// is copied in their place, the scene loads it, and the real folders go back.
//
// Crash-safe with no bookkeeping: a set-aside folder is always "<name>.navigator-hold",
// and anything copied in carries a Marker file, so SwapOut can always tell
// what's real and what's a throwaway copy.
internal static class SaveSwap
{
    public const string Game = "/Savedata";
    public const string GameBackup = "/Savedatabackup";
    private const string HoldSuffix = ".navigator-hold";
    private const string Marker = "/navigator-swapped";

    // Older versions set these aside too; SwapOut still returns them.
    private static readonly string[] LegacyHeld = { "/Savedatahard", "/Savedatahardbackup" };

    public static string Full(string folder) => Application.persistentDataPath + folder;

    public static bool HasSave(string folder) => File.Exists(Full(folder) + "/playerdata.txt");

    public static bool IsSwapped => Directory.Exists(Full(Game) + HoldSuffix) || File.Exists(Full(Game) + Marker);

    // Puts the map save in `folder` (and folder+"backup") where the game loads
    // from. `folder` null or empty means a brand-new game.
    public static void SwapIn(string folder)
    {
        SwapOut();
        Hold(Game);
        Hold(GameBackup);
        Place(folder, Game);
        Place(folder != null ? folder + "backup" : null, GameBackup);
        Debug.Log("[RechargeMaps] save swapped in: " + (folder != null && HasSave(folder) ? folder : "a new game"));
    }

    // Puts the real save back, whatever state a swap (or a crash mid-swap) left.
    public static void SwapOut()
    {
        var any = false;
        foreach (var name in new[] { Game, GameBackup })
        {
            var dir = Full(name);
            try
            {
                var held = Directory.Exists(dir + HoldSuffix);
                if (Directory.Exists(dir) && (held || File.Exists(dir + Marker))) { Directory.Delete(dir, true); any = true; }
                if (held) { Directory.Move(dir + HoldSuffix, dir); any = true; }
            }
            catch (Exception e) { Debug.LogError("[RechargeMaps] couldn't put " + name + " back: " + e.Message); }
        }
        foreach (var name in LegacyHeld)
        {
            var dir = Full(name);
            try
            {
                if (!Directory.Exists(dir + HoldSuffix)) continue;
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.Move(dir + HoldSuffix, dir);
                any = true;
            }
            catch (Exception e) { Debug.LogError("[RechargeMaps] couldn't put " + name + " back: " + e.Message); }
        }
        if (any) Debug.Log("[RechargeMaps] real save back in place");
    }

    public static void CopyFolder(string from, string to)
    {
        var src = Full(from);
        if (!Directory.Exists(src)) return;
        var dst = Full(to);
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
        {
            var name = Path.GetFileName(file);
            if ("/" + name == Marker) continue;
            File.Copy(file, Path.Combine(dst, name), true);
        }
    }

    public static void DeleteFolder(string folder)
    {
        try { if (Directory.Exists(Full(folder))) Directory.Delete(Full(folder), true); }
        catch (Exception e) { Debug.LogWarning("[RechargeMaps] couldn't delete " + folder + ": " + e.Message); }
    }

    private static void Hold(string name)
    {
        var dir = Full(name);
        if (Directory.Exists(dir)) Directory.Move(dir, dir + HoldSuffix);
    }

    private static void Place(string from, string name)
    {
        CopyFolder(from, name);
        Directory.CreateDirectory(Full(name));
        File.WriteAllText(Full(name) + Marker, "");
    }
}
