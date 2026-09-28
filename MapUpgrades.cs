using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

internal static class MapUpgrades
{
    private const string BoxesFile = "/navigator-boxes.json";
    private const string StartFile = "/navigator-start.json";
    private static Dictionary<string, int> _bought = new Dictionary<string, int>();

    private static string Full(string folder) => Application.persistentDataPath + folder;

    public static int Bought(string id) => id != null && _bought.TryGetValue(id, out var n) ? n : 0;

    public static void Reset() => _bought = new Dictionary<string, int>();

    public static void Load(string folder)
    {
        Reset();
        try
        {
            var path = Full(folder) + BoxesFile;
            if (File.Exists(path)) _bought = JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(path)) ?? new Dictionary<string, int>();
        }
        catch (Exception e) { Debug.LogWarning("[RechargeMaps] reading upgrade boxes failed: " + e.Message); }
    }

    public static void Save(string folder)
    {
        foreach (var box in UnityEngine.Object.FindObjectsByType<MapUpgradeBox>(FindObjectsSortMode.None))
            if (box.Id != null) _bought[box.Id] = box.Bought;
        try { File.WriteAllText(Full(folder) + BoxesFile, JsonConvert.SerializeObject(_bought)); }
        catch (Exception e) { Debug.LogWarning("[RechargeMaps] saving upgrade boxes failed: " + e.Message); }
    }

    public static void ApplyStart(JObject player, string folder)
    {
        if (player == null) return;
        var json = player.ToString(Formatting.None);
        var marker = Full(folder) + StartFile;
        try { if (File.Exists(marker) && File.ReadAllText(marker) == json) return; } catch { }
        var mv = UnityEngine.Object.FindFirstObjectByType<Movement>();
        if (mv == null) return;
        int dashes = Math.Max(0, player["dashes"]?.Value<int>() ?? 1);
        int jumps = Math.Max(0, player["airJumps"]?.Value<int>() ?? 1);
        mv.dashUnlocked = dashes > 0;
        mv.maxAirDashes = dashes;
        mv.airDashesLeft = dashes;
        mv.doubleJumpUnlocked = jumps > 0;
        mv.maxAirJumps = jumps;
        mv.airJumpsLeft = jumps;
        mv.wallJumpUnlocked = player["wallJump"]?.Value<bool>() ?? true;
        mv.blockSwapUnlocked = player["blockSwap"]?.Value<bool>() ?? false;
        mv.omniDashUnlocked = player["omniDash"]?.Value<bool>() ?? false;
        globalStats.globalUpgradeDict[globalStats.globalUpgradeSet.zipMoversUnlocked] = (player["zipMovers"]?.Value<bool>() ?? true) ? 1.0 : 0.0;
        globalStats.globalUpgradeDict[globalStats.globalUpgradeSet.unlockJiggleDrops] = (player["refreshers"]?.Value<bool>() ?? true) ? 1.0 : 0.0;
        globalStats.currencyLookup[globalStats.Currencies.Cash] = Math.Max(0.0, player["cash"]?.Value<double>() ?? 0.0);
        Reset();
        try
        {
            Directory.CreateDirectory(Full(folder));
            File.WriteAllText(marker, json);
            if (File.Exists(Full(folder) + BoxesFile)) File.Delete(Full(folder) + BoxesFile);
        }
        catch (Exception e) { Debug.LogWarning("[RechargeMaps] saving the map's start failed: " + e.Message); }
    }

    public static void EnsureMapUnlocks(MapDefinition def)
    {
        bool zips = false, refreshers = false;
        foreach (var group in def?.Groups ?? new List<MapGroup>())
            foreach (var obj in group.Objects ?? new List<JObject>())
            {
                if (obj["type"]?.Value<string>() != "clone") continue;
                var path = obj["path"]?.Value<string>() ?? "";
                if (obj["zip"] != null || path.Contains("ZipMover")) zips = true;
                if (path.Contains("Resetter")) refreshers = true;
            }
        var player = def?.Player;
        if (zips && (player?["zipMovers"]?.Value<bool>() ?? true)) Unlock(globalStats.globalUpgradeSet.zipMoversUnlocked);
        if (refreshers && (player?["refreshers"]?.Value<bool>() ?? true)) Unlock(globalStats.globalUpgradeSet.unlockJiggleDrops);
    }

    private static void Unlock(globalStats.globalUpgradeSet upgrade)
    {
        if (!globalStats.globalUpgradeDict.TryGetValue(upgrade, out var v) || v < 1.0) globalStats.globalUpgradeDict[upgrade] = 1.0;
    }

    public static void PrepareRefresher(GameObject clone)
    {
        var drop = clone.GetComponentInChildren<JiggleDropScript>(true);
        if (drop == null) return;
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        typeof(JiggleDropScript).GetField("ActivationSequence", any)?.SetValue(drop, 0);
        typeof(JiggleDropScript).GetField("visibleAtSequence0", any)?.SetValue(drop, true);
        clone.AddComponent<MapRefresherStarter>().Drop = drop;
    }

    // The level's own upgrade box, re-priced: its behaviour stays the game's,
    // only its currency, prices, max buys and label change. Undone on unload.
    public static Action OverrideBox(GameObject go, JObject cfg)
    {
        var box = go.GetComponentInChildren<upgradeBox>(true);
        if (box == null) return null;
        var host = box.gameObject;
        var old = host.GetComponent<MapUpgradeBox>();
        if (old != null) UnityEngine.Object.DestroyImmediate(old);
        var cost = box.upgradeCost;
        var cap = box.Cap;
        var mult = box.baseCapMult;
        var currencyField = typeof(upgradeBox).GetField("upgradeCurrency", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var currency = currencyField?.GetValue(box);
        var comp = host.AddComponent<MapUpgradeBox>();
        comp.SetupPricing(box, cfg);
        return () =>
        {
            if (comp != null) UnityEngine.Object.Destroy(comp);
            if (box == null) return;
            box.upgradeCost = cost;
            box.Cap = cap;
            box.baseCapMult = mult;
            if (currency != null) currencyField.SetValue(box, currency);
        };
    }

    public static void ApplyBox(GameObject clone, JObject cfg)
    {
        var box = clone.GetComponentInChildren<upgradeBox>(true);
        if (box == null) return;
        clone.AddComponent<MapUpgradeBox>().Setup(box, cfg);
    }
}

internal class MapUpgradeBox : MonoBehaviour
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly string[] Detached = { "course", "clones", "upgradeTree", "gateAnim", "tripBreakerScript", "atomColliderScript", "statuePrestigeText", "ObjectReference", "doorToOpen", "courseToExemptReference", "localUpgradeScript" };
    private static readonly string[] DetachedArrays = { "oldCourses", "boxesToActivate", "boxesToDeactivate", "coursesToReset", "SegmentsToTrigger" };

    public string Id;
    public int Bought => _box != null ? _box.TimesUsed : 0;

    private upgradeBox _box;
    private double[] _prices = { 10 };
    private double _scale = 1.5, _add, _power = 1;
    private int _max = 1;
    private string _label;
    private int _seen = -1;
    private TMP_Text _name;

    public void Setup(upgradeBox box, JObject cfg)
    {
        _box = box;
        Id = cfg["id"]?.Value<string>();
        _label = cfg["label"]?.Value<string>();
        _prices = (cfg["prices"] as JArray)?.Select(p => p.Value<double>()).ToArray() ?? new double[0];
        if (_prices.Length == 0) _prices = new[] { 10.0 };
        _scale = cfg["scale"]?.Value<double>() ?? 1.5;
        _add = cfg["add"]?.Value<double>() ?? 0;
        _power = cfg["power"]?.Value<double>() ?? 1;
        _max = Math.Max(1, cfg["max"]?.Value<int>() ?? 1);

        foreach (var name in Detached)
        {
            var f = typeof(upgradeBox).GetField(name, Any);
            if (f == null) continue;
            var target = f.GetValue(box);
            var t = (target as Component)?.transform ?? (target as GameObject)?.transform;
            if (t == null || !t.IsChildOf(transform)) f.SetValue(box, null);
        }
        foreach (var name in DetachedArrays)
        {
            var f = typeof(upgradeBox).GetField(name, Any);
            if (f != null && f.FieldType.IsArray) f.SetValue(box, Array.CreateInstance(f.FieldType.GetElementType(), 0));
        }

        switch (cfg["kind"]?.Value<string>())
        {
            case "doubleJump": Movement(upgradeBox.movementUpgrades.doubleJump); break;
            case "wallJump": Movement(upgradeBox.movementUpgrades.wallJump); break;
            case "blockSwap": Movement(upgradeBox.movementUpgrades.unlockBlockSwap); break;
            case "zipMovers": Global(globalStats.globalUpgradeSet.zipMoversUnlocked); break;
            case "omniDash": Global(globalStats.globalUpgradeSet.vmanTime); _omni = true; break;
            case "refreshers": Global(globalStats.globalUpgradeSet.unlockJiggleDrops); break;
            case "clones": Local(localUpgrades.localUpgradeSet.cloneCount); break;
            case "baseReward": Local(localUpgrades.localUpgradeSet.cashPerLoop); break;
            case "cloneMult": Local(localUpgrades.localUpgradeSet.cloneMult); break;
            case "fastClone": Local(localUpgrades.localUpgradeSet.fastCloneChance); break;
            case "bigClone": Local(localUpgrades.localUpgradeSet.bigCloneChance); break;
            default: Movement(upgradeBox.movementUpgrades.dash); break;
        }
        if (Enum.TryParse<globalStats.Currencies>(cfg["currency"]?.Value<string>() ?? "Cash", out var currency))
            typeof(upgradeBox).GetField("upgradeCurrency", Any)?.SetValue(box, currency);
        typeof(upgradeBox).GetField("effectMult", Any)?.SetValue(box, 1.0);
        typeof(upgradeBox).GetField("TierMod", Any)?.SetValue(box, 0.0);
        _name = typeof(upgradeBox).GetField("upgradeNameDisplay", Any)?.GetValue(box) as TMP_Text;

        box.reactivateAndReset();
        box.buyMax = false;
        box.neverBuyMax = true;
        box.visible = true;
        box.init = true;
        box.baseCapMult = 1.0;
        box.Cap = _max;
        box.TimesUsed = Math.Min(MapUpgrades.Bought(Id), _max);
        box.upgradeCost = PriceAt(box.TimesUsed);
        box.isActive = box.TimesUsed < _max;
    }

    public void SetupPricing(upgradeBox box, JObject cfg)
    {
        _box = box;
        Id = cfg["id"]?.Value<string>();
        _label = cfg["label"]?.Value<string>();
        _prices = (cfg["prices"] as JArray)?.Select(p => p.Value<double>()).ToArray() ?? new double[0];
        if (_prices.Length == 0) _prices = new[] { box.upgradeCost };
        _scale = cfg["scale"]?.Value<double>() ?? 1.5;
        _add = cfg["add"]?.Value<double>() ?? 0;
        _power = cfg["power"]?.Value<double>() ?? 1;
        _max = Math.Max(1, cfg["max"]?.Value<int>() ?? box.Cap);
        if (Enum.TryParse<globalStats.Currencies>(cfg["currency"]?.Value<string>() ?? "Cash", out var currency))
            typeof(upgradeBox).GetField("upgradeCurrency", Any)?.SetValue(box, currency);
        _name = typeof(upgradeBox).GetField("upgradeNameDisplay", Any)?.GetValue(box) as TMP_Text;
        box.baseCapMult = 1.0;
        box.Cap = _max;
        _pricingOnly = true;
    }

    private bool _pricingOnly;

    private void Movement(upgradeBox.movementUpgrades kind)
    {
        _box.upgrade = localUpgrades.localUpgradeSet.Movement;
        _box.movementUpgrade = kind;
    }

    private bool _local, _linked, _omni;

    // The game's own omni dash box also turns the world overgrown; a map's
    // box gives just the ability, and takes the midair jump it says it does.
    private static void GiveOmniDash()
    {
        var mv = UnityEngine.Object.FindFirstObjectByType<Movement>();
        if (mv == null) return;
        mv.omniDashUnlocked = true;
        mv.maxAirJumps = Math.Max(0, mv.maxAirJumps - 1);
        mv.airJumpsLeft = Math.Min(mv.airJumpsLeft, mv.maxAirJumps);
    }

    private void Local(localUpgrades.localUpgradeSet kind)
    {
        _box.upgrade = kind;
        _local = true;
    }

    // A box linked to one of the map's courses: it becomes that course's own
    // upgrade (under its localUpgrades, with its course and clones wired in).
    public void LinkCourse(courseScript course)
    {
        var local = course.GetComponentInChildren<localUpgrades>(true);
        if (local == null) return;
        transform.SetParent(local.transform, true);
        typeof(upgradeBox).GetField("course", Any)?.SetValue(_box, course);
        typeof(upgradeBox).GetField("clones", Any)?.SetValue(_box, course.GetComponentInChildren<clonesScript>(true));
        typeof(upgradeBox).GetField("localUpgradeScript", Any)?.SetValue(_box, local);
        if (typeof(localUpgrades).GetField("ChildBoxes", Any)?.GetValue(local) is List<upgradeBox> boxes && !boxes.Contains(_box)) boxes.Add(_box);
        _linked = true;
    }

    private void Global(globalStats.globalUpgradeSet kind)
    {
        _box.upgrade = localUpgrades.localUpgradeSet.GLOBAL;
        _box.globalUpgrade = kind;
    }

    private double PriceAt(int n)
    {
        if (n < _prices.Length) return _prices[n];
        var c = _prices[_prices.Length - 1];
        for (int i = _prices.Length; i <= n; i++) c = Math.Ceiling(Math.Pow(c + _add, _power) * _scale);
        return c;
    }

    private void Call(string method) => typeof(upgradeBox).GetMethod(method, Any, null, Type.EmptyTypes, null)?.Invoke(_box, null);

    private void LateUpdate()
    {
        if (_box == null) return;
        if (_local && !_linked)
        {
            if (_box.isActive) Call("deactivate");
            if (_name != null && _name.text != "Needs a course") _name.text = "Needs a course";
            return;
        }
        if (_box.TimesUsed != _seen)
        {
            if (_omni && _seen >= 0 && _box.TimesUsed > _seen) GiveOmniDash();
            _seen = _box.TimesUsed;
            if (_seen < _max)
            {
                if (!_box.isActive && (!_pricingOnly || _max > 1))
                {
                    _box.reactivateAndReset();
                    _box.TimesUsed = _seen;
                }
                _box.upgradeCost = PriceAt(_seen);
                Call("updateCostTextDisplay");
            }
            else if (_box.isActive) Call("deactivate");
        }
        if (_name != null && !string.IsNullOrEmpty(_label) && _name.text != _label) _name.text = _label;
    }
}

internal class MapRefresherStarter : MonoBehaviour
{
    public JiggleDropScript Drop;
    private int _frames;

    private void Update()
    {
        if (Drop == null) { Destroy(this); return; }
        if (++_frames < 2) return;
        typeof(JiggleDropScript).GetMethod("SetActiveVisualState", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(Drop, new object[] { false });
        Destroy(this);
    }
}
