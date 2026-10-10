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
        public GameObject[] PagerParts; // [0] "<" prev, [1] "n / m" centre (next), [2] ">" next
        public RectTransform Quit; // doubles as the picker's Back row
        public Button QuitButton;
        public Button.ButtonClickedEvent QuitClick;
        public string QuitLabel;
        public RectTransform Background;
        public float PanelTopY;
        public float BottomMargin;

        private bool _open;
        private bool _deleteMode;
        private int _pageIndex;
        private List<MapPage> _pages;
        private Vector2 _panelPos;
        private Vector2 _panelSize;
        private float _quitY;
        private float _topY;
        private float _rowSpacing;
        private float _topPad;
        private float _baseFont;
        private float _rowWidth;
        private Transform _divTemplate;
        private readonly List<GameObject> _myDividers = new List<GameObject>();
        private readonly List<KeyValuePair<GameObject, bool>> _hiddenDividers = new List<KeyValuePair<GameObject, bool>>();
        private TMP_Text _quitTmp;
        private bool _qAuto, _qWrap;
        private float _qSize;
        private TextOverflowModes _qOverflow;
        private TextAlignmentOptions _qAlign;
        private readonly Dictionary<string, int> _deleteCounters = new Dictionary<string, int>();

        private static TMP_Text LabelOf(GameObject go)
        {
            var t = go != null ? go.transform.Find("Text (TMP)") : null;
            return t != null ? t.GetComponent<TMP_Text>() : null;
        }

        private static void StyleFixed(TMP_Text tmp, float size)
        {
            if (tmp == null) return;
            tmp.enableAutoSizing = false;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.fontSize = size;
        }

        public void Open(bool deleteMode)
        {
            _deleteMode = deleteMode;
            _pageIndex = 0;
            _deleteCounters.Clear();
            _pages = BuildPageList(Menu);

            _topY = StartGame.anchoredPosition.y;
            float slot1Y = DeleteSave != null ? DeleteSave.anchoredPosition.y : _topY - 60f;
            _rowSpacing = _topY - slot1Y;
            if (_rowSpacing == 0f) _rowSpacing = 60f;

            // Base font: the vanilla row's own (auto-sized) size, slightly reduced.
            var vanilla = LabelOf(Settings.gameObject) ?? LabelOf(StartGame.gameObject);
            float vsize = vanilla != null ? vanilla.fontSize : 36f;
            if (vanilla != null && vanilla.enableAutoSizing) vsize = Mathf.Min(vsize, vanilla.fontSizeMax);
            _baseFont = Mathf.Max(8f, vsize * 0.9f);
            _rowWidth = StartGame.rect.width;
            if (_rowWidth < 10f) _rowWidth = 300f;

            if (Background != null) { _panelPos = Background.anchoredPosition; _panelSize = Background.sizeDelta; }
            float rowTop = _topY + (1f - StartGame.pivot.y) * StartGame.rect.height;
            _topPad = Mathf.Clamp(PanelTopY - rowTop, 10f, 80f);

            // Dividers: remember and hide every vanilla/pager divider, show our own.
            _hiddenDividers.Clear();
            var mainT = PauseMenuHelper.MainBit(Menu).transform;
            _divTemplate = null;
            foreach (Transform child in mainT)
            {
                if (child.name.StartsWith("MapsPickerDivider")) continue;
                if (!child.name.StartsWith("Line") && !child.name.StartsWith("ModsPagerDivider")) continue;
                if (_divTemplate == null && child.name.StartsWith("Line")) _divTemplate = child;
                _hiddenDividers.Add(new KeyValuePair<GameObject, bool>(child.gameObject, child.gameObject.activeSelf));
            }
            if (_myDividers.Count == 0 && _divTemplate != null)
            {
                for (int i = 0; i < RowsPerPage + 1; i++)
                {
                    var d = UnityEngine.Object.Instantiate(_divTemplate.gameObject, mainT);
                    d.name = "MapsPickerDivider" + i;
                    d.SetActive(false);
                    _myDividers.Add(d);
                }
            }
            foreach (var kv in _hiddenDividers) kv.Key.SetActive(false);

            StartGame.gameObject.SetActive(false);
            if (DeleteSave != null) DeleteSave.gameObject.SetActive(false);
            Settings.gameObject.SetActive(false);
            var modsPager = PauseMenuHelper.MainBit(Menu).transform.Find("ModsPager");
            if (modsPager != null) modsPager.gameObject.SetActive(false);

            if (Quit != null)
            {
                _quitY = Quit.anchoredPosition.y;
                Quit.gameObject.SetActive(true);
                PauseMenuHelper.SetButtonLabel(Quit.gameObject, "Back");
                _quitTmp = LabelOf(Quit.gameObject);
                if (_quitTmp != null)
                {
                    _qAuto = _quitTmp.enableAutoSizing; _qSize = _quitTmp.fontSize; _qWrap = _quitTmp.enableWordWrapping;
                    _qOverflow = _quitTmp.overflowMode; _qAlign = _quitTmp.alignment;
                }
                QuitButton.onClick = new Button.ButtonClickedEvent();
                QuitButton.onClick.AddListener(Close);
            }

            RefreshPage();
            _open = true;
        }

        // Escape backs out of the picker, same as the Back row.
        private void Update()
        {
            if (!_open) return;
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) Close();
        }

        // Panel height follows the Back row, with the same padding at the bottom as at the top.
        private void ResizePanel(float backY)
        {
            if (Background == null) return;
            float backHalf = Quit != null ? Quit.pivot.y * Quit.rect.height : 0f;
            float bottomEdge = backY - backHalf - _topPad;
            Background.sizeDelta = new Vector2(Background.sizeDelta.x, PanelTopY - bottomEdge);
            Background.anchoredPosition = new Vector2(Background.anchoredPosition.x, bottomEdge + Background.pivot.y * Background.sizeDelta.y);
        }

        public void Close()
        {
            if (!_open) return;
            _open = false;
            foreach (var slot in Slots) slot.SetActive(false);
            foreach (var part in PagerParts) part.SetActive(false);
            foreach (var d in _myDividers) d.SetActive(false);
            foreach (var kv in _hiddenDividers) if (kv.Key != null) kv.Key.SetActive(kv.Value);
            _hiddenDividers.Clear();

            StartGame.gameObject.SetActive(true);
            if (DeleteSave != null) DeleteSave.gameObject.SetActive(true);
            Settings.gameObject.SetActive(true);
            var modsPager = PauseMenuHelper.MainBit(Menu).transform.Find("ModsPager");
            if (modsPager != null) modsPager.gameObject.SetActive(true);

            if (Quit != null)
            {
                Quit.anchoredPosition = new Vector2(Quit.anchoredPosition.x, _quitY);
                if (QuitLabel != null) PauseMenuHelper.SetButtonLabel(Quit.gameObject, QuitLabel);
                if (_quitTmp != null)
                {
                    _quitTmp.enableAutoSizing = _qAuto; _quitTmp.fontSize = _qSize; _quitTmp.enableWordWrapping = _qWrap;
                    _quitTmp.overflowMode = _qOverflow; _quitTmp.alignment = _qAlign;
                }
                if (QuitClick != null) QuitButton.onClick = QuitClick;
            }

            if (Background != null)
            {
                Background.sizeDelta = _panelSize;
                Background.anchoredPosition = _panelPos;
            }
        }

        private int TotalPages => Math.Max(1, (int)Math.Ceiling(_pages.Count / (double)RowsPerPage));

        public void NextPage()
        {
            _pageIndex = (_pageIndex + 1) % TotalPages;
            RefreshPage();
        }

        public void PrevPage()
        {
            _pageIndex = (_pageIndex + TotalPages - 1) % TotalPages;
            RefreshPage();
        }

        private float RowY(int index) => _topY - index * _rowSpacing;

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
                    ((RectTransform)slot.transform).anchoredPosition = new Vector2(0f, RowY(i));
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

            int totalPages = TotalPages;
            bool multi = totalPages > 1;
            int pagerIdx = count;
            int backIdx = count + (multi ? 1 : 0);

            if (multi)
            {
                float y = RowY(pagerIdx);
                float h = StartGame.sizeDelta.y;
                float[] xs = { -_rowWidth * 0.375f, 0f, _rowWidth * 0.375f };
                float[] ws = { _rowWidth * 0.25f, _rowWidth * 0.5f, _rowWidth * 0.25f };
                string[] texts = { "<", (_pageIndex + 1) + " / " + totalPages, ">" };
                for (int i = 0; i < 3; i++)
                {
                    var part = PagerParts[i];
                    var rt = (RectTransform)part.transform;
                    rt.anchorMin = new Vector2(0.5f, StartGame.anchorMin.y);
                    rt.anchorMax = new Vector2(0.5f, StartGame.anchorMax.y);
                    rt.pivot = new Vector2(0.5f, StartGame.pivot.y);
                    rt.sizeDelta = new Vector2(ws[i], h);
                    rt.anchoredPosition = new Vector2(xs[i], y);
                    part.SetActive(true);
                    PauseMenuHelper.SetButtonLabel(part, texts[i]);
                    var tmp = LabelOf(part);
                    if (tmp != null) tmp.alignment = TextAlignmentOptions.Center;
                }
            }
            else
            {
                foreach (var part in PagerParts) part.SetActive(false);
            }

            float backY = RowY(backIdx);
            if (Quit != null) Quit.anchoredPosition = new Vector2(Quit.anchoredPosition.x, backY);
            ResizePanel(backY);

            // Dividers between every pair of consecutive visible rows (incl. above Back).
            for (int j = 0; j < _myDividers.Count; j++)
            {
                var d = _myDividers[j];
                bool show = j < backIdx;
                d.SetActive(show);
                if (!show) continue;
                var drt = (RectTransform)d.transform;
                drt.anchoredPosition = new Vector2(drt.anchoredPosition.x, _topY - (j + 0.5f) * _rowSpacing);
            }

            ApplyFit();
        }

        // One font size for every row on the page: the base size, or smaller for
        // all of them when the longest label would not fit; the rest ellipsises.
        private void ApplyFit()
        {
            var tmps = new List<TMP_Text>();
            foreach (var slot in Slots) if (slot.activeSelf) { var t = LabelOf(slot); if (t != null) tmps.Add(t); }

            float size = _baseFont;
            float floor = _baseFont * 0.6f;
            foreach (var t in tmps)
            {
                StyleFixed(t, _baseFont);
                var rect = t.rectTransform.rect;
                float avail = rect.width - t.margin.x - t.margin.z;
                if (avail < 10f) avail = _rowWidth * 0.85f;
                float need = t.GetPreferredValues(t.text, 100000f, 0f).x;
                if (need > avail && need > 0f) size = Mathf.Min(size, _baseFont * avail / need);
            }
            size = Mathf.Max(floor, size);
            foreach (var t in tmps) StyleFixed(t, size);
            foreach (var part in PagerParts) if (part.activeSelf) StyleFixed(LabelOf(part), size);
            if (_quitTmp != null) { StyleFixed(_quitTmp, size); _quitTmp.alignment = _qAlign; }
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
                ApplyFit();
            }
            else
            {
                bool isHard = page.Kind == MapPageKind.BSide;
                if (isHard) Menu.DeleteSavePressedHard();
                else Menu.DeleteSavePressed();
                PauseMenuHelper.SetButtonLabel(slot, RealDeleteButtonText(Menu, isHard));
                ApplyFit();
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

        state.PagerParts = new GameObject[3];
        string[] partNames = { "Prev", "Mid", "Next" };
        for (int i = 0; i < 3; i++)
        {
            var part = UnityEngine.Object.Instantiate(startGame.gameObject, PauseMenuHelper.MainBit(menu).transform);
            part.name = "MapsPickerPage" + partNames[i];
            part.SetActive(false);
            var pb = part.GetComponent<Button>();
            pb.onClick = new Button.ButtonClickedEvent();
            if (i == 0) pb.onClick.AddListener(state.PrevPage);
            else pb.onClick.AddListener(state.NextPage);
            state.PagerParts[i] = part;
        }

        // Remember the quit row so picker mode can hand it back untouched.
        var quit = PauseMenuHelper.MainBit(menu).transform.Find("QuitToDesktop") as RectTransform;
        if (quit != null)
        {
            state.Quit = quit;
            state.QuitButton = quit.GetComponent<Button>();
            state.QuitClick = state.QuitButton != null ? state.QuitButton.onClick : null;
            var labelT = quit.Find("Text (TMP)");
            state.QuitLabel = labelT != null ? labelT.GetComponent<TMPro.TMP_Text>()?.text : null;

            var background = PauseMenuHelper.MainBit(menu).GetComponent<RectTransform>();
            if (background != null)
            {
                var fitter = background.GetComponent<ContentSizeFitter>();
                if (fitter != null) fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
                state.Background = background;
                state.PanelTopY = background.anchoredPosition.y + (1f - background.pivot.y) * background.sizeDelta.y;
                state.BottomMargin = quit.anchoredPosition.y - (background.anchoredPosition.y - background.pivot.y * background.sizeDelta.y) + 20f;
            }
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
