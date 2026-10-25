using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace cameraTools.Markers
{
    /// <summary>Builds UI pieces from the game's own inventory widgets so the panel looks native.</summary>
    public static class UiKit
    {
        public static readonly Color Orange = new(1f, 0.631f, 0.235f, 1f);
        public static readonly Color Beige = new(0.853f, 0.725f, 0.533f, 1f);
        public static readonly Color Dim = new(0.7f, 0.65f, 0.55f, 1f);

        private static readonly Dictionary<string, Sprite?> SpriteCache = new();
        private static TMP_FontAsset? _font;
        private static Sprite? _cameraIcon;

        public static TMP_FontAsset? Font
        {
            get
            {
                if (_font == null && InventoryGui.instance != null && InventoryGui.instance.m_recipeName != null)
                {
                    _font = InventoryGui.instance.m_recipeName.font;
                }

                return _font;
            }
        }

        public static Sprite? GetSprite(string name)
        {
            if (SpriteCache.TryGetValue(name, out Sprite? cached))
            {
                return cached;
            }

            Sprite? found = Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(s => s.name == name);
            SpriteCache[name] = found;
            return found;
        }

        public static RectTransform Rect(GameObject go) => (RectTransform)go.transform;

        public static GameObject Panel(Transform parent, string name)
        {
            GameObject go = new(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            Image image = go.GetComponent<Image>();
            image.sprite = GetSprite("woodpanel_trophys") ?? GetSprite("woodpanel_settings") ?? GetSprite("woodpanel");
            image.type = Image.Type.Sliced;
            image.color = Color.white;
            image.raycastTarget = true;
            return go;
        }

        public static TextMeshProUGUI Text(Transform parent, string text, float size, Color color, TextAlignmentOptions align = TextAlignmentOptions.Left)
        {
            GameObject go = new("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            TextMeshProUGUI tmp = go.GetComponent<TextMeshProUGUI>();
            if (Font != null) tmp.font = Font;
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = align;
            tmp.enableWordWrapping = true;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.raycastTarget = false;
            return tmp;
        }

        public static Button Button(Transform parent, string text, UnityAction onClick, float width, float height = 32f, float fontSize = 16f)
        {
            Button? template = InventoryGui.instance != null ? InventoryGui.instance.m_craftButton : null;
            GameObject go;
            if (template != null)
            {
                go = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
                go.name = "Button";
                go.SetActive(true);
                Component gamePad = go.GetComponent("UIGamePad");
                if (gamePad != null) UnityEngine.Object.Destroy(gamePad);
                UITooltip? tooltip = go.GetComponent<UITooltip>();
                if (tooltip != null)
                {
                    tooltip.m_text = "";
                    tooltip.m_topic = "";
                }

                foreach (Transform child in go.transform)
                {
                    if (child.GetComponent<TMP_Text>() == null && child.GetComponent<Image>() == null)
                    {
                        child.gameObject.SetActive(false);
                    }
                }
            }
            else
            {
                go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button));
                go.transform.SetParent(parent, false);
                go.GetComponent<Image>().sprite = GetSprite("button");
                go.GetComponent<Image>().type = Image.Type.Sliced;
                Text(go.transform, text, fontSize, Orange, TextAlignmentOptions.Center);
            }

            Button button = go.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(onClick);
            button.interactable = true;

            TMP_Text? label = go.GetComponentInChildren<TMP_Text>(true);
            if (label != null)
            {
                label.text = text;
                label.fontSize = fontSize;
                label.enableAutoSizing = false;
                label.alignment = TextAlignmentOptions.Center;
                label.overflowMode = TextOverflowModes.Ellipsis;
                label.enableWordWrapping = false;
                RectTransform lr = label.rectTransform;
                lr.anchorMin = Vector2.zero;
                lr.anchorMax = Vector2.one;
                lr.offsetMin = new Vector2(6f, 2f);
                lr.offsetMax = new Vector2(-6f, -2f);
            }

            RectTransform rt = Rect(go);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(width, height);
            LayoutElement le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.minWidth = width;
            le.preferredHeight = height;
            le.minHeight = height;
            le.flexibleWidth = 0f;
            return button;
        }

        public static TMP_InputField Input(Transform parent, string placeholder, bool multiline, float height)
        {
            GameObject go = new("Input", typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
            go.transform.SetParent(parent, false);
            Image bg = go.GetComponent<Image>();
            bg.sprite = GetSprite("text_field") ?? GetSprite("button");
            bg.type = Image.Type.Sliced;
            bg.color = Color.white;

            GameObject area = new("Text Area", typeof(RectTransform), typeof(RectMask2D));
            area.transform.SetParent(go.transform, false);
            RectTransform ar = Rect(area);
            ar.anchorMin = Vector2.zero;
            ar.anchorMax = Vector2.one;
            ar.offsetMin = new Vector2(8f, 4f);
            ar.offsetMax = new Vector2(-8f, -4f);

            TextMeshProUGUI ph = Text(area.transform, placeholder, 15f, new Color(0.6f, 0.55f, 0.45f, 0.8f));
            ph.name = "Placeholder";
            ph.fontStyle = FontStyles.Italic;
            Stretch(ph.rectTransform);
            TextMeshProUGUI txt = Text(area.transform, "", 15f, Beige);
            txt.name = "Text";
            txt.overflowMode = TextOverflowModes.Overflow;
            Stretch(txt.rectTransform);

            TMP_InputField field = go.GetComponent<TMP_InputField>();
            field.textViewport = ar;
            field.textComponent = txt;
            field.placeholder = ph;
            field.lineType = multiline ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine;
            field.characterLimit = multiline ? 400 : 60;
            field.caretColor = Orange;
            field.selectionColor = new Color(1f, 0.631f, 0.235f, 0.4f);
            field.customCaretColor = true;

            LayoutElement le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;
            le.flexibleWidth = 1f;
            return field;
        }

        public static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        public static GameObject Row(Transform parent, float height, float spacing = 6f)
        {
            GameObject go = new("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            HorizontalLayoutGroup h = go.GetComponent<HorizontalLayoutGroup>();
            h.spacing = spacing;
            h.childAlignment = TextAnchor.MiddleLeft;
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = false;
            LayoutElement le = go.GetComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;
            return go;
        }

        public static ScrollRect ScrollList(Transform parent, float height, out RectTransform content)
        {
            GameObject go = new("List", typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            Image bg = go.GetComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.25f);
            bg.raycastTarget = true;
            LayoutElement le = go.GetComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;
            le.flexibleWidth = 1f;

            GameObject viewport = new("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewport.transform.SetParent(go.transform, false);
            RectTransform vr = Rect(viewport);
            Stretch(vr);
            vr.offsetMin = new Vector2(4f, 4f);
            vr.offsetMax = new Vector2(-18f, -4f);

            GameObject contentGo = new("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentGo.transform.SetParent(viewport.transform, false);
            content = Rect(contentGo);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;
            VerticalLayoutGroup v = contentGo.GetComponent<VerticalLayoutGroup>();
            v.spacing = 3f;
            v.childControlWidth = true;
            v.childControlHeight = true;
            v.childForceExpandWidth = true;
            v.childForceExpandHeight = false;
            contentGo.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            ScrollRect scroll = go.GetComponent<ScrollRect>();
            scroll.viewport = vr;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;

            Scrollbar? template = InventoryGui.instance != null ? InventoryGui.instance.m_recipeListScroll : null;
            if (template != null)
            {
                GameObject sb = UnityEngine.Object.Instantiate(template.gameObject, go.transform, false);
                sb.name = "Scrollbar";
                sb.SetActive(true);
                RectTransform sr = Rect(sb);
                sr.anchorMin = new Vector2(1f, 0f);
                sr.anchorMax = new Vector2(1f, 1f);
                sr.pivot = new Vector2(1f, 0.5f);
                sr.anchoredPosition = new Vector2(-2f, 0f);
                sr.sizeDelta = new Vector2(12f, -8f);
                Scrollbar bar = sb.GetComponent<Scrollbar>();
                bar.onValueChanged = new Scrollbar.ScrollEvent();
                bar.direction = Scrollbar.Direction.BottomToTop;
                scroll.verticalScrollbar = bar;
                scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            }

            return scroll;
        }

        public static Sprite CameraIcon()
        {
            if (_cameraIcon != null)
            {
                return _cameraIcon;
            }

            const int size = 32;
            Texture2D tex = new(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            Color clear = new(0f, 0f, 0f, 0f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool body = x >= 3 && x <= 28 && y >= 7 && y <= 23;
                    bool bump = x >= 10 && x <= 18 && y >= 23 && y <= 27;
                    float dx = x - 16.5f;
                    float dy = y - 15f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    bool ring = r >= 4.2f && r <= 6.5f;
                    bool lens = r < 2.6f;
                    bool shutter = x >= 23 && x <= 26 && y >= 19 && y <= 21;
                    Color c = clear;
                    if (body || bump) c = Orange;
                    if (ring || lens) c = new Color(0.15f, 0.1f, 0.05f, 1f);
                    if (shutter) c = new Color(0.15f, 0.1f, 0.05f, 1f);
                    tex.SetPixel(x, y, c);
                }
            }

            tex.Apply();
            _cameraIcon = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 32f);
            return _cameraIcon;
        }
    }

    /// <summary>Native-looking status strip under the compass while marker mode is on, plus a crosshair.</summary>
    public class MarkerStatusHud : MonoBehaviour
    {
        private GameObject? _root;
        private TextMeshProUGUI? _line1;
        private TextMeshProUGUI? _line2;
        private GameObject? _crosshair;

        private void Update()
        {
            if (Hud.instance == null || Hud.instance.m_rootObject == null)
            {
                return;
            }

            if (_root == null)
            {
                Build(Hud.instance.m_rootObject.transform);
            }

            bool show = MarkerMode.Active && !InventoryGui.IsVisible();
            if (_root!.activeSelf != show)
            {
                _root.SetActive(show);
            }

            if (_crosshair != null && _crosshair.activeSelf != show)
            {
                _crosshair.SetActive(show);
            }

            if (show)
            {
                _line1!.text = MarkerMode.StatusLine1();
                _line2!.text = MarkerMode.StatusLine2();
            }
        }

        private void Build(Transform parent)
        {
            _root = UiKit.Panel(parent, "MarkerStatus");
            RectTransform rt = UiKit.Rect(_root);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -70f);
            rt.sizeDelta = new Vector2(980f, 64f);
            VerticalLayoutGroup v = _root.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(18, 18, 8, 8);
            v.spacing = 2f;
            v.childControlHeight = true;
            v.childControlWidth = true;
            v.childForceExpandHeight = false;
            _line1 = UiKit.Text(_root.transform, "", 16f, UiKit.Orange, TextAlignmentOptions.Center);
            _line1.enableWordWrapping = false;
            _line2 = UiKit.Text(_root.transform, "", 15f, UiKit.Beige, TextAlignmentOptions.Center);
            _line2.enableWordWrapping = false;

            _crosshair = new GameObject("MarkerCrosshair", typeof(RectTransform), typeof(Image));
            _crosshair.transform.SetParent(parent, false);
            Image img = _crosshair.GetComponent<Image>();
            img.sprite = Hud.instance.m_crosshair != null ? Hud.instance.m_crosshair.sprite : null;
            img.color = new Color(1f, 1f, 1f, 0.8f);
            img.raycastTarget = false;
            RectTransform cr = UiKit.Rect(_crosshair);
            cr.anchorMin = cr.anchorMax = new Vector2(0.5f, 0.5f);
            cr.sizeDelta = new Vector2(28f, 28f);
            _root.SetActive(false);
            _crosshair.SetActive(false);
        }
    }

    /// <summary>The management panel inside the inventory, opened from a camera tab under the crafting panel.</summary>
    public class MarkerPanel : MonoBehaviour
    {
        private GameObject? _tab;
        private Button? _tabButton;
        private GameObject? _panel;
        private bool _open;
        private bool _dirty = true;

        private TextMeshProUGUI? _buildName;
        private readonly Dictionary<MarkerKind, Button> _toolButtons = new();
        private readonly Dictionary<AreaShape, Button> _shapeButtons = new();
        private GameObject? _shapeRow;
        private Button? _modeButton;
        private Button? _visibilityButton;
        private RectTransform? _listContent;
        private readonly List<GameObject> _rows = new();
        private TMP_InputField? _label;
        private TMP_InputField? _note;
        private GameObject? _tagRow;
        private readonly Dictionary<string, Button> _tagButtons = new();
        private TextMeshProUGUI? _selectedInfo;
        private Button? _playButton;
        private TextMeshProUGUI? _footer;
        private Marker? _bound;

        public static bool PanelOpen { get; private set; }

        private void OnEnable()
        {
            MarkerStore.Changed += MarkDirty;
        }

        private void OnDisable()
        {
            MarkerStore.Changed -= MarkDirty;
        }

        private void MarkDirty() => _dirty = true;

        private void Update()
        {
            InventoryGui gui = InventoryGui.instance;
            if (gui == null || gui.m_crafting == null)
            {
                return;
            }

            if (_tab == null)
            {
                BuildTab(gui);
            }

            if (!InventoryGui.IsVisible())
            {
                if (_open)
                {
                    SetOpen(false);
                }

                return;
            }

            if (_open && _panel != null)
            {
                if (_dirty)
                {
                    Refresh();
                }

                RefreshToolButtons();
            }
        }

        private void BuildTab(InventoryGui gui)
        {
            Button button = UiKit.Button(gui.m_crafting, "", () => SetOpen(!_open), 76f, 36f);
            _tab = button.gameObject;
            _tab.name = "MarkersTab";
            _tabButton = button;
            Destroy(_tab.GetComponent<LayoutElement>());

            TMP_Text? text = _tab.GetComponentInChildren<TMP_Text>(true);
            if (text != null)
            {
                text.text = "";
            }

            GameObject icon = new("Icon", typeof(RectTransform), typeof(Image));
            icon.transform.SetParent(_tab.transform, false);
            Image img = icon.GetComponent<Image>();
            img.sprite = UiKit.CameraIcon();
            img.raycastTarget = false;
            img.preserveAspect = true;
            RectTransform ir = UiKit.Rect(icon);
            ir.anchorMin = ir.anchorMax = new Vector2(0.5f, 0.5f);
            ir.sizeDelta = new Vector2(28f, 28f);

            RectTransform rt = UiKit.Rect(_tab);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, 6f);
            rt.sizeDelta = new Vector2(76f, 36f);

            UITooltip tip = _tab.GetComponent<UITooltip>() ?? _tab.AddComponent<UITooltip>();
            tip.m_text = "Markers: areas, points, shots and paths for shot planning";
            tip.m_topic = "Markers";
        }

        private void SetOpen(bool open)
        {
            _open = open;
            PanelOpen = open;
            if (_tabButton != null)
            {
                _tabButton.interactable = !open;
            }

            if (open)
            {
                MarkerStore.EnsureLoaded();
                if (_panel == null)
                {
                    BuildPanel(InventoryGui.instance);
                }

                _panel!.SetActive(true);
                _panel.transform.SetAsLastSibling();
                _dirty = true;
            }
            else if (_panel != null)
            {
                _panel.SetActive(false);
            }
        }

        private void BuildPanel(InventoryGui gui)
        {
            _panel = UiKit.Panel(gui.m_crafting, "MarkersPanel");
            RectTransform rt = UiKit.Rect(_panel);
            UiKit.Stretch(rt);
            VerticalLayoutGroup v = _panel.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(24, 24, 22, 22);
            v.spacing = 6f;
            v.childControlWidth = true;
            v.childControlHeight = true;
            v.childForceExpandWidth = true;
            v.childForceExpandHeight = false;

            // Title and build selector
            GameObject title = UiKit.Row(_panel.transform, 34f);
            TextMeshProUGUI titleText = UiKit.Text(title.transform, "Markers", 26f, UiKit.Orange);
            titleText.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            UiKit.Button(title.transform, "<", () => { MarkerStore.CycleBuild(-1); _dirty = true; }, 30f, 30f);
            _buildName = UiKit.Text(title.transform, "", 17f, UiKit.Beige, TextAlignmentOptions.Center);
            _buildName.gameObject.AddComponent<LayoutElement>().preferredWidth = 150f;
            UiKit.Button(title.transform, ">", () => { MarkerStore.CycleBuild(1); _dirty = true; }, 30f, 30f);
            UiKit.Button(title.transform, "New", NewBuild, 54f, 30f, 14f);
            UiKit.Button(title.transform, "Rename", RenameBuild, 72f, 30f, 14f);

            // Tool row
            GameObject tools = UiKit.Row(_panel.transform, 32f);
            UiKit.Text(tools.transform, "Tool", 15f, UiKit.Dim).gameObject.AddComponent<LayoutElement>().preferredWidth = 48f;
            foreach (MarkerKind kind in new[] { MarkerKind.Area, MarkerKind.Point, MarkerKind.Shot, MarkerKind.Path })
            {
                MarkerKind k = kind;
                _toolButtons[k] = UiKit.Button(tools.transform, k.ToString(), () => { MarkerMode.Tool = k; }, 86f, 30f, 15f);
            }

            _shapeRow = UiKit.Row(_panel.transform, 32f);
            UiKit.Text(_shapeRow.transform, "Shape", 15f, UiKit.Dim).gameObject.AddComponent<LayoutElement>().preferredWidth = 48f;
            foreach (AreaShape shape in new[] { AreaShape.Circle, AreaShape.Rectangle, AreaShape.Polygon })
            {
                AreaShape sh = shape;
                _shapeButtons[sh] = UiKit.Button(_shapeRow.transform, sh.ToString(), () => { MarkerMode.Shape = sh; }, 96f, 30f, 15f);
            }

            GameObject modes = UiKit.Row(_panel.transform, 32f);
            _modeButton = UiKit.Button(modes.transform, "", () => { MarkerMode.SetActive(!MarkerMode.Active); }, 170f, 30f, 15f);
            _visibilityButton = UiKit.Button(modes.transform, "", CycleVisibility, 170f, 30f, 15f);

            // List
            UiKit.ScrollList(_panel.transform, 190f, out _listContent);

            // Editor
            _selectedInfo = UiKit.Text(_panel.transform, "", 14f, UiKit.Dim);
            _selectedInfo.gameObject.AddComponent<LayoutElement>().preferredHeight = 20f;
            _label = UiKit.Input(_panel.transform, "label", false, 30f);
            _label.onEndEdit.AddListener(_ => SaveBound());
            _note = UiKit.Input(_panel.transform, "note: what is interesting here (becomes the call-out)", true, 54f);
            _note.onEndEdit.AddListener(_ => SaveBound());
            _tagRow = UiKit.Row(_panel.transform, 28f, 4f);
            foreach (string tag in MarkerMode.AreaTags)
            {
                string t = tag;
                _tagButtons[t] = UiKit.Button(_tagRow.transform, t, () => { if (_bound != null) { _bound.tag = t; MarkerStore.Save(); } }, 66f, 26f, 13f);
            }

            GameObject actions = UiKit.Row(_panel.transform, 32f);
            UiKit.Button(actions.transform, "Go to", GoToSelected, 84f, 30f, 15f);
            _playButton = UiKit.Button(actions.transform, "Play", PlaySelected, 84f, 30f, 15f);
            UiKit.Button(actions.transform, "Preview", PreviewSelected, 84f, 30f, 15f);
            UiKit.Button(actions.transform, "Delete", DeleteSelected, 84f, 30f, 15f);

            _footer = UiKit.Text(_panel.transform, "", 13f, UiKit.Dim);
            _footer.gameObject.AddComponent<LayoutElement>().preferredHeight = 18f;
        }

        private void Refresh()
        {
            _dirty = false;
            if (_panel == null || _listContent == null)
            {
                return;
            }

            Build build = MarkerStore.ActiveBuild;
            _buildName!.text = $"{build.name}  ({MarkerStore.Builds.IndexOf(build) + 1}/{MarkerStore.Builds.Count})";

            foreach (GameObject row in _rows)
            {
                Destroy(row);
            }

            _rows.Clear();

            List<Marker> markers = MarkerStore.Markers;
            if (MarkerMode.Selected != null && !markers.Contains(MarkerMode.Selected))
            {
                MarkerMode.Selected = null;
            }

            foreach (Marker marker in markers.OrderBy(m => (int)m.Kind).ThenBy(m => m.label))
            {
                Marker m = marker;
                string suffix = m.Kind switch
                {
                    MarkerKind.Path => $"  {m.waypoints.Count} wp",
                    MarkerKind.Area => $"  {m.shape}{(string.IsNullOrEmpty(m.tag) ? "" : " " + m.tag)}",
                    _ => ""
                };
                Button row = UiKit.Button(_listContent, $"{KindTag(m.Kind)}  {m.label}{suffix}", () => Select(m), 10f, 28f, 14f);
                LayoutElement le = row.GetComponent<LayoutElement>();
                le.flexibleWidth = 1f;
                le.minWidth = 0f;
                le.preferredWidth = -1f;
                TMP_Text? text = row.GetComponentInChildren<TMP_Text>(true);
                if (text != null)
                {
                    text.alignment = TextAlignmentOptions.Left;
                    text.color = m == MarkerMode.Selected ? Color.white : MarkerRenderer.ColorFor(m.Kind);
                }

                row.interactable = m != MarkerMode.Selected;
                _rows.Add(row.gameObject);
            }

            Bind(MarkerMode.Selected);
            _footer!.text = $"{markers.Count} markers in this build  |  {MarkerStore.AllMarkers.Count} total  |  {MarkerStore.FilePath}";
        }

        private static string KindTag(MarkerKind kind) => kind switch
        {
            MarkerKind.Area => "[area]",
            MarkerKind.Shot => "[shot]",
            MarkerKind.Path => "[path]",
            _ => "[point]"
        };

        private void RefreshToolButtons()
        {
            foreach (KeyValuePair<MarkerKind, Button> kv in _toolButtons)
            {
                kv.Value.interactable = kv.Key != MarkerMode.Tool;
            }

            bool area = MarkerMode.Tool == MarkerKind.Area;
            if (_shapeRow != null && _shapeRow.activeSelf != area)
            {
                _shapeRow.SetActive(area);
            }

            foreach (KeyValuePair<AreaShape, Button> kv in _shapeButtons)
            {
                kv.Value.interactable = kv.Key != MarkerMode.Shape;
            }

            SetButtonText(_modeButton, MarkerMode.Active ? "Marker mode: ON" : "Marker mode: off");
            SetButtonText(_visibilityButton, $"Overlay: {MarkerVisibility.Mode.ToString().ToLowerInvariant()}");
            if (_playButton != null)
            {
                _playButton.interactable = _bound != null && _bound.Kind == MarkerKind.Path;
            }
        }

        private static void SetButtonText(Button? button, string text)
        {
            TMP_Text? t = button != null ? button.GetComponentInChildren<TMP_Text>(true) : null;
            if (t != null && t.text != text)
            {
                t.text = text;
            }
        }

        private void Select(Marker marker)
        {
            SaveBound();
            MarkerMode.Selected = marker;
            MarkerMode.Highlighted = marker;
            _dirty = true;
        }

        private void Bind(Marker? marker)
        {
            _bound = marker;
            bool has = marker != null;
            _label!.interactable = has;
            _note!.interactable = has;
            _label.SetTextWithoutNotify(has ? marker!.label : "");
            _note.SetTextWithoutNotify(has ? marker!.note : "");
            _selectedInfo!.text = has ? marker!.Describe() : "Select a marker in the list, or place one in the world with F7.";
            bool isArea = has && marker!.Kind == MarkerKind.Area;
            if (_tagRow != null && _tagRow.activeSelf != isArea)
            {
                _tagRow.SetActive(isArea);
            }

            foreach (KeyValuePair<string, Button> kv in _tagButtons)
            {
                kv.Value.interactable = !isArea || marker!.tag != kv.Key;
            }
        }

        private void SaveBound()
        {
            if (_bound == null || _label == null || _note == null)
            {
                return;
            }

            string label = _label.text.Trim();
            string note = _note.text.Trim();
            if (label == _bound.label && note == _bound.note)
            {
                return;
            }

            if (!string.IsNullOrEmpty(label))
            {
                _bound.label = label;
            }

            _bound.note = note;
            MarkerStore.Save();
        }

        private void CycleVisibility()
        {
            MarkerVisibility.Mode = (MarkerVisibilityMode)(((int)MarkerVisibility.Mode + 1) % 3);
        }

        private void NewBuild()
        {
            Build build = MarkerStore.NewBuild("");
            CloseInventory();
            TextInput.instance?.RequestText(new BuildNameReceiver(build), "Build name", 60);
        }

        private void RenameBuild()
        {
            Build build = MarkerStore.ActiveBuild;
            CloseInventory();
            TextInput.instance?.RequestText(new BuildNameReceiver(build), "Rename build", 60);
        }

        private void GoToSelected()
        {
            if (_bound == null) return;
            SaveBound();
            CloseInventory();
            MarkerCommands.GoTo(_bound);
        }

        private void PlaySelected()
        {
            if (_bound == null || _bound.Kind != MarkerKind.Path) return;
            SaveBound();
            CloseInventory();
            try
            {
                PathPlayer.Play(_bound, null, PathLookMode.Waypoints, Vector3.zero);
            }
            catch (Exception ex)
            {
                MarkerMode.Flash(ex.Message);
            }
        }

        private void PreviewSelected()
        {
            if (_bound == null) return;
            SaveBound();
            CloseInventory();
            MarkerVisibility.Mode = MarkerVisibilityMode.Show;
            MarkerMode.Highlighted = _bound;
            MarkerCommands.GoTo(_bound);
        }

        private void DeleteSelected()
        {
            if (_bound == null) return;
            Marker doomed = _bound;
            MarkerMode.Selected = null;
            if (MarkerMode.Highlighted == doomed) MarkerMode.Highlighted = null;
            MarkerStore.Remove(doomed);
            _dirty = true;
        }

        private static void CloseInventory()
        {
            if (InventoryGui.instance != null && InventoryGui.IsVisible())
            {
                InventoryGui.instance.Hide();
            }
        }

        public static void Open()
        {
            if (InventoryGui.instance == null) return;
            if (!InventoryGui.IsVisible())
            {
                InventoryGui.instance.Show(null);
            }

            MarkerPanel? panel = UnityEngine.Object.FindObjectOfType<MarkerPanel>();
            panel?.SetOpen(true);
        }
    }

    public class BuildNameReceiver : TextReceiver
    {
        private readonly Build _build;

        public BuildNameReceiver(Build build)
        {
            _build = build;
        }

        public string GetText() => _build.name;

        public void SetText(string text) => MarkerStore.RenameBuild(_build, text);
    }
}
