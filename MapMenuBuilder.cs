using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Recharge.ModApi;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UI;

// "Start Game" (renamed from the real vanilla "Start Demo") and "Delete save
// data" both switch the real main menu panel itself into "picker mode"
// instead of acting directly - StartGame/DeleteSave/Settings' row slots
// become up to 3 individual map entries (Base Game/B-side/every custom map,
// each its own clickable row - no single "current" selection to page
// through), a "< i/N >" row below them pages between groups of 3 when there
// are more maps than that, and Quit's own slot is taken over by a Back row
// for as long as picker mode is active.
internal static class MapMenuBuilder
{
    private static readonly BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private const int RowsPerPage = 3;

    public static void Install(pauseMenuScript menu)
    {
        if (menu.mainBitPublic == null || menu.settingsBitPublic == null) return;
        if (menu.mainBitPublic.transform.Find("MapsInstalled") != null) return; // idempotent per instance

        var marker = new GameObject("MapsInstalled");
        marker.transform.SetParent(menu.mainBitPublic.transform, false);

        // The real B-side/hard-mode row (gated behind BsideEnabler, only
        // visible once snfDemoCompleted is set) now lives inside the map
        // picker instead - hide the original so it doesn't float redundantly.
        var bside = menu.mainBitPublic.GetComponentInChildren<BsideEnabler>(true);
        if (bside != null) bside.gameObject.SetActive(false);

        // True vertical centering, not a guessed offset: this panel's own
        // anchor (both min and max) already sits at the screen's vertical
        // center (0.5) and its pivot is 0.5 too, so anchoredPosition.y = 0
        // centers it exactly regardless of how tall it ends up once
        // PauseMenuHelper sizes it for however many mods are installed.
        // Applied once, before that sizing runs, so it's baked into every
        // measurement downstream rather than fighting it after the fact.
        var mainRt = menu.mainBitPublic.GetComponent<RectTransform>();
        if (mainRt != null) mainRt.anchoredPosition = new Vector2(mainRt.anchoredPosition.x, 0f);

        var startGame = menu.mainBitPublic.transform.Find("StartGame") as RectTransform;
        var deleteSave = menu.mainBitPublic.transform.Find("DeleteSave") as RectTransform;
        var settings = menu.mainBitPublic.transform.Find("Settings") as RectTransform;
        if (startGame == null || settings == null) return;

        var picker = BuildPicker(menu, startGame);

        PauseMenuHelper.SetButtonLabel(startGame.gameObject, "Start Game");
        var startBtn = startGame.GetComponent<Button>();
        startBtn.onClick = new Button.ButtonClickedEvent();
        startBtn.onClick.AddListener(() => picker.Open(deleteMode: false));

        if (deleteSave != null)
        {
            var deleteBtn = deleteSave.GetComponent<Button>();
            deleteBtn.onClick = new Button.ButtonClickedEvent();
            deleteBtn.onClick.AddListener(() => picker.Open(deleteMode: true));
        }
    }

    private enum MapPageKind { BaseGame, BSide, Custom }

    private struct MapPage
    {
        public string Label;
        public MapPageKind Kind;
        public string MapId; // only meaningful for Custom
        public Action Play;
    }

    private class PickerState : MonoBehaviour
    {
        public pauseMenuScript Menu;
        public RectTransform StartGame;
        public RectTransform DeleteSave;
        public RectTransform Settings;
        public GameObject[] Slots; // up to RowsPerPage map rows, reused across picker pages
        public GameObject PagerRow;
        public GameObject BackRow;

        private bool _deleteMode;
        private int _pageIndex;
        private List<MapPage> _pages;
        private readonly Dictionary<string, int> _deleteCounters = new Dictionary<string, int>();

        public void Open(bool deleteMode)
        {
            _deleteMode = deleteMode;
            _pageIndex = 0;
            _deleteCounters.Clear();
            _pages = BuildPageList(Menu);

            float topY = StartGame.anchoredPosition.y;
            float slot1Y = DeleteSave != null ? DeleteSave.anchoredPosition.y : topY - 60f;
            float slot2Y = Settings.anchoredPosition.y;
            float rowSpacing = topY - slot1Y;
            if (rowSpacing == 0f) rowSpacing = 60f;
            float pagerY = slot2Y - rowSpacing;

            var quit = Menu.mainBitPublic.transform.Find("QuitToDesktop") as RectTransform;
            float backY = quit != null ? quit.anchoredPosition.y : pagerY - rowSpacing;

            ((RectTransform)Slots[0].transform).anchoredPosition = new Vector2(0f, topY);
            ((RectTransform)Slots[1].transform).anchoredPosition = new Vector2(0f, slot1Y);
            ((RectTransform)Slots[2].transform).anchoredPosition = new Vector2(0f, slot2Y);
            ((RectTransform)PagerRow.transform).anchoredPosition = new Vector2(0f, pagerY);
            ((RectTransform)BackRow.transform).anchoredPosition = new Vector2(0f, backY);

            StartGame.gameObject.SetActive(false);
            if (DeleteSave != null) DeleteSave.gameObject.SetActive(false);
            Settings.gameObject.SetActive(false);
            var modsPager = Menu.mainBitPublic.transform.Find("ModsPager");
            if (modsPager != null) modsPager.gameObject.SetActive(false);
            if (quit != null) quit.gameObject.SetActive(false);

            BackRow.SetActive(true);
            RefreshPage();
        }

        public void Close()
        {
            foreach (var slot in Slots) slot.SetActive(false);
            PagerRow.SetActive(false);
            BackRow.SetActive(false);

            StartGame.gameObject.SetActive(true);
            if (DeleteSave != null) DeleteSave.gameObject.SetActive(true);
            Settings.gameObject.SetActive(true);
            var modsPager = Menu.mainBitPublic.transform.Find("ModsPager");
            if (modsPager != null) modsPager.gameObject.SetActive(true);
            var quit = Menu.mainBitPublic.transform.Find("QuitToDesktop");
            if (quit != null) quit.gameObject.SetActive(true);
        }

        public void PrevPage()
        {
            int totalPages = Math.Max(1, (int)Math.Ceiling(_pages.Count / (double)RowsPerPage));
            _pageIndex = (_pageIndex - 1 + totalPages) % totalPages;
            RefreshPage();
        }

        public void NextPage()
        {
            int totalPages = Math.Max(1, (int)Math.Ceiling(_pages.Count / (double)RowsPerPage));
            _pageIndex = (_pageIndex + 1) % totalPages;
            RefreshPage();
        }

        private void RefreshPage()
        {
            int startIdx = _pageIndex * RowsPerPage;
            int count = Math.Max(0, Math.Min(RowsPerPage, _pages.Count - startIdx));
            for (int i = 0; i < RowsPerPage; i++)
            {
                var slot = Slots[i];
                if (i < count)
                {
                    var page = _pages[startIdx + i];
                    slot.SetActive(true);
                    PauseMenuHelper.SetButtonLabel(slot, RowLabel(page));
                    var btn = slot.GetComponent<Button>();
                    btn.onClick = new Button.ButtonClickedEvent();
                    btn.onClick.AddListener(() => ClickRow(slot, page));
                }
                else
                {
                    slot.SetActive(false);
                }
            }

            int totalPages = Math.Max(1, (int)Math.Ceiling(_pages.Count / (double)RowsPerPage));
            var counter = PagerRow.transform.Find("Counter");
            if (counter != null) PauseMenuHelper.SetButtonLabel(counter.gameObject, (_pageIndex + 1) + "/" + totalPages);
            bool multi = totalPages > 1;
            var prev = PagerRow.transform.Find("Prev");
            var next = PagerRow.transform.Find("Next");
            if (prev != null) prev.gameObject.SetActive(multi);
            if (next != null) next.gameObject.SetActive(multi);
            PagerRow.SetActive(multi);
        }

        private string RowLabel(MapPage page) => _deleteMode ? "Delete " + page.Label : page.Label;

        private void ClickRow(GameObject slot, MapPage page)
        {
            if (_deleteMode) ClickDelete(slot, page);
            else ClickPlay(page);
        }

        private void ClickPlay(MapPage page)
        {
            page.Play();
            if (Menu.menuOpen) Menu.menuButtonPressed();
        }

        // Base Game/B-side: forwards every click straight to the real
        // vanilla DeleteSavePressed()/DeleteSavePressedHard() (same 4-click
        // escalating confirm, same whole-folder + shared currency/upgrade
        // wipe) so this can never drift from real behavior - then mirrors
        // the real button's own resulting text. Both always target their own
        // real folder regardless of which difficulty is currently active, so
        // this is correct for whichever row is clicked.
        // Custom map: no vanilla equivalent exists, so this drives its own
        // escalating confirm (reusing the same real localized messages,
        // tracked per row since several maps can be on screen at once) and,
        // on the final click, deletes only that map's own course-progress
        // file, reloading it live if it's the one currently in the pocket.
        private void ClickDelete(GameObject slot, MapPage page)
        {
            if (page.Kind == MapPageKind.Custom)
            {
                var messages = GetDeleteMessages(Menu);
                _deleteCounters.TryGetValue(page.MapId, out var counter);
                counter++;
                _deleteCounters[page.MapId] = counter;
                if (messages == null || counter >= 4)
                {
                    MapManager.DeleteMapSave(page.MapId);
                    if (MapManager.CurrentMapId == page.MapId) MapManager.Instance.LoadMap(page.MapId);
                    _deleteCounters[page.MapId] = 0;
                    PauseMenuHelper.SetButtonLabel(slot, RowLabel(page));
                }
                else
                {
                    PauseMenuHelper.SetButtonLabel(slot, messages[counter].GetLocalizedString());
                }
            }
            else
            {
                bool isHard = page.Kind == MapPageKind.BSide;
                if (isHard) Menu.DeleteSavePressedHard();
                else Menu.DeleteSavePressed();
                PauseMenuHelper.SetButtonLabel(slot, RealDeleteButtonText(Menu, isHard));
            }
        }
    }

    private static PickerState BuildPicker(pauseMenuScript menu, RectTransform startGame)
    {
        var state = menu.mainBitPublic.gameObject.AddComponent<PickerState>();
        state.Menu = menu;
        state.StartGame = startGame;
        state.DeleteSave = menu.mainBitPublic.transform.Find("DeleteSave") as RectTransform;
        state.Settings = menu.mainBitPublic.transform.Find("Settings") as RectTransform;

        state.Slots = new GameObject[RowsPerPage];
        for (int i = 0; i < RowsPerPage; i++)
        {
            var slotGo = UnityEngine.Object.Instantiate(startGame.gameObject, menu.mainBitPublic.transform);
            slotGo.name = "MapsPickerSlot" + i;
            slotGo.SetActive(false);
            state.Slots[i] = slotGo;
        }

        var pagerRow = new GameObject("MapsPickerPager", typeof(RectTransform));
        pagerRow.transform.SetParent(menu.mainBitPublic.transform, false);
        pagerRow.SetActive(false);
        state.PagerRow = pagerRow;

        var prevGo = UnityEngine.Object.Instantiate(startGame.gameObject, pagerRow.transform);
        prevGo.name = "Prev";
        var prevRt = (RectTransform)prevGo.transform;
        prevRt.anchoredPosition = new Vector2(-110f, 0f);
        prevRt.sizeDelta = new Vector2(60f, prevRt.sizeDelta.y);
        SetButtonLabel(prevGo, "<");
        PauseMenuHelper.ScaleButtonFontSize(prevGo, 1.6f);
        var prevBtn = prevGo.GetComponent<Button>();
        prevBtn.onClick = new Button.ButtonClickedEvent();
        prevBtn.onClick.AddListener(state.PrevPage);

        var nextGo = UnityEngine.Object.Instantiate(startGame.gameObject, pagerRow.transform);
        nextGo.name = "Next";
        var nextRt = (RectTransform)nextGo.transform;
        nextRt.anchoredPosition = new Vector2(110f, 0f);
        nextRt.sizeDelta = new Vector2(60f, nextRt.sizeDelta.y);
        SetButtonLabel(nextGo, ">");
        PauseMenuHelper.ScaleButtonFontSize(nextGo, 1.6f);
        var nextBtn = nextGo.GetComponent<Button>();
        nextBtn.onClick = new Button.ButtonClickedEvent();
        nextBtn.onClick.AddListener(state.NextPage);

        var counterGo = UnityEngine.Object.Instantiate(startGame.gameObject, pagerRow.transform);
        counterGo.name = "Counter";
        var counterRt = (RectTransform)counterGo.transform;
        counterRt.anchoredPosition = Vector2.zero;
        counterRt.sizeDelta = new Vector2(100f, counterRt.sizeDelta.y);
        // Left with its Button intact but disabled, rather than destroyed -
        // the real rows' orange comes from the Button's own Normal color
        // state, not a fixed text color, and disabling (not just clearing
        // onClick) freezes it there instead of leaving it selectable via
        // keyboard/gamepad nav or mouse hover (which would flip it to the
        // Highlighted color, a stray green, since it's still a real Selectable).
        var counterBtn = counterGo.GetComponent<Button>();
        if (counterBtn != null) counterBtn.enabled = false;

        var backGo = UnityEngine.Object.Instantiate(startGame.gameObject, menu.mainBitPublic.transform);
        backGo.name = "MapsPickerBack";
        SetButtonLabel(backGo, "Back");
        var backBtn = backGo.GetComponent<Button>();
        backBtn.onClick = new Button.ButtonClickedEvent();
        backBtn.onClick.AddListener(state.Close);
        backGo.SetActive(false);
        state.BackRow = backGo;

        return state;
    }

    // Base Game is always first; B-side mirrors BsideEnabler's own gate
    // exactly (moving it doesn't unlock anything the player hasn't already
    // earned); every <mapsDir>/<id>/map.json folder follows.
    private static List<MapPage> BuildPageList(pauseMenuScript menu)
    {
        var pages = new List<MapPage>
        {
            new MapPage { Label = "Base Game", Kind = MapPageKind.BaseGame, Play = () => menu.changeScene() }
        };

        if (PlayerPrefs.HasKey("snfDemoCompleted"))
        {
            pages.Add(new MapPage { Label = "B-side", Kind = MapPageKind.BSide, Play = () => menu.changeSceneHard() });
        }

        string[] mapIds;
        try
        {
            var dir = MapPaths.MapsDir;
            System.IO.Directory.CreateDirectory(dir);
            mapIds = System.IO.Directory.GetDirectories(dir)
                .Where(d => System.IO.File.Exists(System.IO.Path.Combine(d, "map.json")))
                .Select(d => System.IO.Path.GetFileName(d))
                .ToArray();
        }
        catch (Exception e)
        {
            mapIds = Array.Empty<string>();
            Debug.LogError("[RechargeMaps] failed to list maps dir: " + e);
        }

        foreach (var mapId in mapIds)
        {
            var id = mapId; // local copy for the closure
            pages.Add(new MapPage { Label = id, Kind = MapPageKind.Custom, MapId = id, Play = () => MapManager.Instance.PlayMap(id, menu) });
        }

        return pages;
    }

    private static void SetButtonLabel(GameObject buttonGo, string text) => PauseMenuHelper.SetButtonLabel(buttonGo, text);

    private static LocalizedString[] GetDeleteMessages(pauseMenuScript menu)
    {
        var field = typeof(pauseMenuScript).GetField("deleteSaveMessages", NonPublicInstance);
        return field?.GetValue(menu) as LocalizedString[];
    }

    private static string RealDeleteButtonText(pauseMenuScript menu, bool isHard)
    {
        var fieldName = isHard ? "deleteSaveButtonHard" : "deleteSaveButton";
        var field = typeof(pauseMenuScript).GetField(fieldName, NonPublicInstance);
        var tmp = field?.GetValue(menu) as TMP_Text;
        return tmp != null ? tmp.text : "Delete Savedata";
    }
}
