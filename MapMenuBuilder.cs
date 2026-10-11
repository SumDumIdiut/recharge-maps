using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Recharge.ModApi;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Localization;
using UnityEngine.UI;

// "Start Game" (renamed from the real vanilla "Start Demo") and "Delete save
// data" both switch the real main menu panel itself into "picker mode"
// instead of acting directly - StartGame/DeleteSave/Settings' row slots
// become up to 3 individual map entries (Base Game/B-side/every custom map,
// each its own clickable row), a "Page n/m >" row below them swaps between
// groups of 3, and Quit's own row drops below that to act as Back until the
// picker closes, after which it gets its own label and handler back.
internal static class MapMenuBuilder
{
    private const int RowsPerPage = 3;

    public static void Install(pauseMenuScript menu)
    {
        if (PauseMenuHelper.MainBit(menu) == null || PauseMenuHelper.SettingsBit(menu) == null) return;
        if (PauseMenuHelper.MainBit(menu).transform.Find("MapsInstalled") != null) return; // idempotent per instance

        var marker = new GameObject("MapsInstalled");
        marker.transform.SetParent(PauseMenuHelper.MainBit(menu).transform, false);

        // The real B-side/hard-mode row (gated behind BsideEnabler, only
        // visible once snfDemoCompleted is set) now lives inside the map
        // picker instead - hide the original so it doesn't float redundantly.
        var bside = PauseMenuHelper.MainBit(menu).GetComponentInChildren<BsideEnabler>(true);
        if (bside != null) bside.gameObject.SetActive(false);

        // True vertical centering, not a guessed offset: this panel's own
        // anchor (both min and max) already sits at the screen's vertical
        // center (0.5) and its pivot is 0.5 too, so anchoredPosition.y = 0
        // centers it exactly regardless of how tall it ends up once
        // PauseMenuHelper sizes it for however many mods are installed.
        // Applied once, before that sizing runs, so it's baked into every
        // measurement downstream rather than fighting it after the fact.
        var mainRt = PauseMenuHelper.MainBit(menu).GetComponent<RectTransform>();
        if (mainRt != null) mainRt.anchoredPosition = new Vector2(mainRt.anchoredPosition.x, 0f);

        var startGame = PauseMenuHelper.MainBit(menu).transform.Find("StartGame") as RectTransform;
        var deleteSave = PauseMenuHelper.MainBit(menu).transform.Find("DeleteSave") as RectTransform;
        var settings = PauseMenuHelper.MainBit(menu).transform.Find("Settings") as RectTransform;
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
        public GameObject PageButton;
        public RectTransform Quit; // doubles as the picker's Back row
        public Button QuitButton;
        public Button.ButtonClickedEvent QuitClick;
        public string QuitLabel;

        private bool _open;
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

            // The panel is never resized: 3 maps + the page row + Back all sit
            // inside the rows the main menu already has. Maps take the first
            // three rows, the page row the next one, and Back stays on Quit's
            // own row. If Quit sits less than four row-steps below the top
            // (no mods row in the menu), the step shrinks so all five rows
            // still fit between the first row and Quit.
            float topY = StartGame.anchoredPosition.y;
            float slot1Y = DeleteSave != null ? DeleteSave.anchoredPosition.y : topY - 60f;
            float rowSpacing = topY - slot1Y;
            if (rowSpacing == 0f) rowSpacing = 60f;
            // Vanilla step on all five rows; Back sits one step below Quit's old spot.
            // Only if Back (plus half a step) would leave the panel does the step shrink.
            float panelBottom = float.NegativeInfinity;
            try
            {
                var panelRt = PauseMenuHelper.MainBit(Menu).GetComponent<RectTransform>();
                var rowParent = (Quit != null ? Quit.parent : StartGame.parent) as RectTransform;
                if (panelRt != null && rowParent == panelRt) panelBottom = panelRt.rect.yMin;
                else if (panelRt != null && rowParent != null)
                    panelBottom = rowParent.InverseTransformPoint(panelRt.TransformPoint(new Vector3(0f, panelRt.rect.yMin, 0f))).y;
            }
            catch (Exception) { }
            if (rowSpacing < 0f) rowSpacing = -rowSpacing;
            float backY = topY - 4f * rowSpacing;
            if (!float.IsNegativeInfinity(panelBottom) && backY - rowSpacing * 0.5f < panelBottom)
            {
                float fit = (topY - (panelBottom + rowSpacing * 0.5f)) / 4f;
                if (fit > 0f && fit < rowSpacing) { rowSpacing = fit; backY = topY - 4f * rowSpacing; }
            }
            _panelBottom = panelBottom;
            float pagerY = topY - 3f * rowSpacing;

            ((RectTransform)Slots[0].transform).anchoredPosition = new Vector2(0f, topY);
            ((RectTransform)Slots[1].transform).anchoredPosition = new Vector2(0f, topY - rowSpacing);
            ((RectTransform)Slots[2].transform).anchoredPosition = new Vector2(0f, topY - 2f * rowSpacing);
            ((RectTransform)PagerRow.transform).anchoredPosition = new Vector2(0f, pagerY);
            ((RectTransform)PageButton.transform).anchoredPosition = new Vector2(0f, 0f);

            StartGame.gameObject.SetActive(false);
            if (DeleteSave != null) DeleteSave.gameObject.SetActive(false);
            Settings.gameObject.SetActive(false);
            var modsPager = PauseMenuHelper.MainBit(Menu).transform.Find("ModsPager");
            if (modsPager != null) modsPager.gameObject.SetActive(false);

            if (Quit != null)
            {
                Quit.gameObject.SetActive(true);
                if (!_quitMoved) { _quitOrigPos = Quit.anchoredPosition; _quitMoved = true; }
                Quit.anchoredPosition = new Vector2(_quitOrigPos.x, backY);
                PauseMenuHelper.SetButtonLabel(Quit.gameObject, "Back");
                QuitButton.onClick = new Button.ButtonClickedEvent();
                QuitButton.onClick.AddListener(Close);
            }

            RefreshPage();
            _open = true;

            _rowYs = new[] { topY, topY - rowSpacing, topY - 2f * rowSpacing, pagerY, backY };
            ApplyPickerDividers();
            LogLayout(topY, rowSpacing, backY);
        }

        private float[] _rowYs;
        private float _panelBottom;
        private bool _quitMoved;
        private Vector2 _quitOrigPos;
        private readonly List<GameObject> _hiddenDividers = new List<GameObject>();
        private readonly List<GameObject> _pickerDividers = new List<GameObject>();
        private string _vanillaDividerLog = "";

        private static bool IsDivider(Transform t) => t.name.StartsWith("Line") || t.name.StartsWith("ModsPagerDivider");

        private void ApplyPickerDividers()
        {
            try
            {
                RestorePickerDividers(); // never hide twice / leave clones behind
                var main = PauseMenuHelper.MainBit(Menu).transform;
                // stale clones from a previous (rebuilt) menu instance
                foreach (var stale in main.Cast<Transform>().Where(c => c.name.StartsWith("MapsPickerDivider")).ToList())
                    UnityEngine.Object.Destroy(stale.gameObject);

                float hi = _rowYs[0] + 0.5f, lo = _rowYs[4] - 0.5f;
                Transform template = null;
                var log = new System.Text.StringBuilder();
                foreach (Transform child in main)
                {
                    if (!IsDivider(child) || child.name.StartsWith("MapsPickerDivider")) continue;
                    var rt = child as RectTransform;
                    if (rt == null || !child.gameObject.activeSelf) continue;
                    if (log.Length > 0) log.Append(',');
                    log.Append(child.name).Append(':').Append(rt.anchoredPosition.y.ToString("F1"));
                    if (template == null || (!template.name.StartsWith("Line") && child.name.StartsWith("Line"))) template = child;
                }
                _vanillaDividerLog = log.ToString();
                if (template == null) return;

                var templateGo = template.gameObject;
                var parent = template.parent;
                var templateRt = (RectTransform)template;
                for (int i = 0; i < 4; i++)
                {
                    var d = UnityEngine.Object.Instantiate(templateGo, parent);
                    d.name = "MapsPickerDivider" + i;
                    d.SetActive(true);
                    var drt = (RectTransform)d.transform;
                    drt.anchoredPosition = new Vector2(templateRt.anchoredPosition.x, (_rowYs[i] + _rowYs[i + 1]) * 0.5f);
                    _pickerDividers.Add(d);
                }

                foreach (Transform child in parent.Cast<Transform>().ToList())
                {
                    if (!IsDivider(child) || child.name.StartsWith("MapsPickerDivider")) continue;
                    var rt = child as RectTransform;
                    if (rt == null || !child.gameObject.activeSelf) continue;
                    float y = rt.anchoredPosition.y;
                    if (y <= hi && y >= lo)
                    {
                        child.gameObject.SetActive(false);
                        _hiddenDividers.Add(child.gameObject);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Navigator] picker dividers: " + e.Message);
            }
        }

        private void RestorePickerDividers()
        {
            try
            {
                foreach (var d in _pickerDividers) if (d != null) UnityEngine.Object.Destroy(d);
                _pickerDividers.Clear();
                foreach (var d in _hiddenDividers) if (d != null) d.SetActive(true);
                _hiddenDividers.Clear();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Navigator] picker dividers: " + e.Message);
            }
        }

        private void LogLayout(float topY, float step, float quitY)
        {
            try
            {
                var rt = PauseMenuHelper.MainBit(Menu).GetComponent<RectTransform>();
                var pk = string.Join(",", _pickerDividers.Where(d => d != null).Select(d => d.name + ":" + ((RectTransform)d.transform).anchoredPosition.y.ToString("F1")).ToArray());
                Debug.Log("[Navigator] picker layout: topY=" + topY.ToString("F1") + " step=" + step.ToString("F1") + " quitY=" + quitY.ToString("F1")
                    + " rows=[" + string.Join(",", _rowYs.Select(y => y.ToString("F1")).ToArray()) + "]"
                    + " dividers(vanilla)=[" + _vanillaDividerLog + "] dividers(picker)=[" + pk + "]"
                    + " panelBottom=" + _panelBottom.ToString("F1") + " panel=" + rt.sizeDelta + "/" + rt.anchoredPosition);
            }
            catch (Exception e) { Debug.LogWarning("[Navigator] picker dividers: log " + e.Message); }
        }

        // Safety nets: whenever the panel goes away the lines come back; if it
        // returns while the picker is still open, the picker lines return too.
        private void OnDisable() { RestorePickerDividers(); }
        private void OnDestroy() { RestorePickerDividers(); }
        private void OnEnable() { if (_open && _rowYs != null) ApplyPickerDividers(); }

        // Escape backs out of the picker, same as the Back row.
        private void Update()
        {
            if (!_open) return;
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) Close();
        }

        public void Close()
        {
            if (!_open) return;
            _open = false;
            RestorePickerDividers();
            foreach (var slot in Slots) slot.SetActive(false);
            PagerRow.SetActive(false);

            StartGame.gameObject.SetActive(true);
            if (DeleteSave != null) DeleteSave.gameObject.SetActive(true);
            Settings.gameObject.SetActive(true);
            if (Quit != null)
            {
                if (_quitMoved) { Quit.anchoredPosition = _quitOrigPos; _quitMoved = false; }
                if (QuitLabel != null) PauseMenuHelper.SetButtonLabel(Quit.gameObject, QuitLabel);
                if (QuitClick != null) QuitButton.onClick = QuitClick;
            }

            // Re-enabling the pager last lets it re-apply its own layout
            // (page row above Quit); with no pager, the saved spot stands.
            var modsPager = PauseMenuHelper.MainBit(Menu).transform.Find("ModsPager");
            if (modsPager != null)
            {
                modsPager.gameObject.SetActive(true);
                var pageBtn = modsPager.Find("ModsPagerPageButton") as RectTransform;
                if (Quit != null && pageBtn != null && pageBtn.gameObject.activeSelf)
                {
                    float step = Mathf.Abs(_rowYs != null && _rowYs.Length > 1 ? _rowYs[0] - _rowYs[1] : 0f);
                    if (step > 0f && Quit.anchoredPosition.y > pageBtn.anchoredPosition.y - step * 0.5f)
                        Quit.anchoredPosition = new Vector2(Quit.anchoredPosition.x, pageBtn.anchoredPosition.y - step);
                }
            }
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
            bool multi = totalPages > 1;
            var pagerRt = (RectTransform)PagerRow.transform;
            pagerRt.sizeDelta = new Vector2(Mathf.Max(100f, pagerRt.sizeDelta.x), 60f);
            PagerRow.SetActive(multi);
            if (multi) PauseMenuHelper.SetButtonLabel(PageButton, "Page " + (_pageIndex + 1) + "/" + totalPages + "  >");
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
        // on the final click, deletes that map's own save folder (never Base
        // Game's), reloading it live if it's the one currently in the pocket.
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
                    var playing = MapManager.CurrentMapId == page.MapId;
                    MapSaves.Delete(page.MapId);
                    if (playing) MapManager.Instance.PlayMap(page.MapId, Menu);
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
        var state = PauseMenuHelper.MainBit(menu).gameObject.AddComponent<PickerState>();
        state.Menu = menu;
        state.StartGame = startGame;
        state.DeleteSave = PauseMenuHelper.MainBit(menu).transform.Find("DeleteSave") as RectTransform;
        state.Settings = PauseMenuHelper.MainBit(menu).transform.Find("Settings") as RectTransform;

        state.Slots = new GameObject[RowsPerPage];
        for (int i = 0; i < RowsPerPage; i++)
        {
            var slotGo = UnityEngine.Object.Instantiate(startGame.gameObject, PauseMenuHelper.MainBit(menu).transform);
            slotGo.name = "MapsPickerSlot" + i;
            slotGo.SetActive(false);
            state.Slots[i] = slotGo;
        }

        var pagerRow = new GameObject("MapsPickerPager", typeof(RectTransform));
        pagerRow.transform.SetParent(PauseMenuHelper.MainBit(menu).transform, false);
        pagerRow.SetActive(false);
        state.PagerRow = pagerRow;

        var pageGo = UnityEngine.Object.Instantiate(startGame.gameObject, pagerRow.transform);
        pageGo.name = "MapsPickerPageButton";
        ((RectTransform)pageGo.transform).anchoredPosition = new Vector2(0f, 0f);
        PauseMenuHelper.SetButtonLabel(pageGo, "Page 1/1  >");
        var pageBtn = pageGo.GetComponent<Button>();
        pageBtn.onClick = new Button.ButtonClickedEvent();
        pageBtn.onClick.AddListener(state.NextPage);
        state.PageButton = pageGo;

        // Remember the quit row so picker mode can hand it back untouched.
        var quit = PauseMenuHelper.MainBit(menu).transform.Find("QuitToDesktop") as RectTransform;
        if (quit != null)
        {
            state.Quit = quit;
            state.QuitButton = quit.GetComponent<Button>();
            state.QuitClick = state.QuitButton != null ? state.QuitButton.onClick : null;
            var labelT = quit.Find("Text (TMP)");
            state.QuitLabel = labelT != null ? labelT.GetComponent<TMPro.TMP_Text>()?.text : null;
        }

        return state;
    }

    // Base Game is always first; B-side mirrors BsideEnabler's own gate
    // exactly (moving it doesn't unlock anything the player hasn't already
    // earned); every <mapsDir>/<id>/map.json folder follows.
    private static List<MapPage> BuildPageList(pauseMenuScript menu)
    {
        var pages = new List<MapPage>
        {
            new MapPage { Label = "Base Game", Kind = MapPageKind.BaseGame, Play = () => MapManager.LeaveTo(menu, false) }
        };

        if (PlayerPrefs.HasKey("snfDemoCompleted"))
        {
            pages.Add(new MapPage { Label = "B-side", Kind = MapPageKind.BSide, Play = () => MapManager.LeaveTo(menu, true) });
        }

        string[] mapIds;
        try
        {
            var dir = MapPaths.MapsDir;
            System.IO.Directory.CreateDirectory(dir);
            mapIds = System.IO.Directory.GetDirectories(dir)
                .Where(d => System.IO.File.Exists(System.IO.Path.Combine(d, "map.json")))
                // The editor's "Test in game" slot is a copy of a map, not one of its own.
                .Where(d => System.IO.Path.GetFileName(d) != "map-maker-test")
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
            // Hub installs are in folders named by hub id: shown by their Hub name, others by their own.
            var name = MapDefinition.ReadName(id);
            pages.Add(new MapPage { Label = string.IsNullOrWhiteSpace(name) ? id : name.Trim(), Kind = MapPageKind.Custom, MapId = id, Play = () => MapManager.Instance.PlayMap(id, menu) });
        }

        return pages;
    }

    private static void SetButtonLabel(GameObject buttonGo, string text) => PauseMenuHelper.SetButtonLabel(buttonGo, text);

    private static LocalizedString[] GetDeleteMessages(pauseMenuScript menu)
    {
        return Reflect.TryGetField<LocalizedString[]>(menu, "deleteSaveMessages");
    }

    private static string RealDeleteButtonText(pauseMenuScript menu, bool isHard)
    {
        var fieldName = isHard ? "deleteSaveButtonHard" : "deleteSaveButton";
        var tmp = Reflect.TryGetField<TMP_Text>(menu, fieldName);
        return tmp != null ? tmp.text : "Delete Savedata";
    }
}
