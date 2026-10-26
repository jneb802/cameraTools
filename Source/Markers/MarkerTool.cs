using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace cameraTools.Markers
{
    /// <summary>
    /// The marker tool is a cloned Hammer whose piece table holds one "piece" per marker type, so marking
    /// uses the game's own build flow: equip the tool, right click to pick the mark, left click to place,
    /// middle click to remove. Nothing is ever built; the placement is intercepted and stored as a marker.
    /// </summary>
    public static class MarkerTool
    {
        public const string ItemPrefabName = "CameraToolsMarkerTool";
        public const string ItemName = "Marker tool";
        public const string TablePrefabName = "_CameraToolsMarkerPieceTable";

        public const string AreaPiece = "ct_marker_area";
        public const string CirclePiece = "ct_marker_circle";
        public const string PointPiece = "ct_marker_point";
        public const string ShotPiece = "ct_marker_shot";
        public const string PathPiece = "ct_marker_path";

        private static readonly Dictionary<string, (MarkerKind kind, AreaShape shape)> ToolByPiece = new()
        {
            [AreaPiece] = (MarkerKind.Area, AreaShape.Polygon),
            [CirclePiece] = (MarkerKind.Area, AreaShape.Circle),
            [PointPiece] = (MarkerKind.Point, AreaShape.Circle),
            [ShotPiece] = (MarkerKind.Shot, AreaShape.Circle),
            [PathPiece] = (MarkerKind.Path, AreaShape.Circle),
        };

        private static GameObject? _container;
        private static GameObject? _item;
        private static PieceTable? _table;

        public static bool IsEquipped(Player? player)
        {
            if (player == null)
            {
                return false;
            }

            ItemDrop.ItemData? right = player.GetRightItem();
            return right != null && right.m_shared.m_name == ItemName;
        }

        public static bool IsMarkerPiece(Piece? piece) => piece != null && ToolByPiece.ContainsKey(piece.gameObject.name);

        public static bool TryGetTool(Piece? piece, out MarkerKind kind, out AreaShape shape)
        {
            if (piece != null && ToolByPiece.TryGetValue(piece.gameObject.name, out (MarkerKind kind, AreaShape shape) tool))
            {
                kind = tool.kind;
                shape = tool.shape;
                return true;
            }

            kind = MarkerKind.Point;
            shape = AreaShape.Circle;
            return false;
        }

        public static Piece? SelectedPiece(Player player) => player.m_buildPieces != null ? player.m_buildPieces.GetSelectedPiece() : null;

        /// <summary>Puts a marker tool in the player's inventory and equips it.</summary>
        public static string GiveTool(Player player)
        {
            if (_item == null)
            {
                return "ERROR: the marker tool item is not registered (ObjectDB not ready)";
            }

            Inventory inventory = player.GetInventory();
            ItemDrop.ItemData? existing = inventory.GetAllItems().FirstOrDefault(i => i.m_shared.m_name == ItemName);
            if (existing == null)
            {
                existing = inventory.AddItem(ItemPrefabName, 1, 1, 0, 0L, "", false);
                if (existing == null)
                {
                    return "ERROR: inventory is full";
                }
            }

            player.EquipItem(existing);
            return $"OK: {ItemName} equipped; right click picks the mark, left click places, middle click removes";
        }

        /// <summary>Creates the item once and makes sure the given ObjectDB knows it and its recipe.</summary>
        public static void EnsureItem(ObjectDB db)
        {
            if (_item == null)
            {
                GameObject? hammer = db.GetItemPrefab("Hammer");
                if (hammer == null)
                {
                    cameraToolsPlugin.TemplateLogger.LogWarning("Marker tool: Hammer prefab not found, tool not created");
                    return;
                }

                _container ??= CreateContainer();
                _table = CreateTable();
                _item = Object.Instantiate(hammer, _container.transform);
                _item.name = ItemPrefabName;
                ItemDrop drop = _item.GetComponent<ItemDrop>();
                ItemDrop.ItemData.SharedData shared = drop.m_itemData.m_shared;
                shared.m_name = ItemName;
                shared.m_description = "Marks the parts of a build that matter for a camera tour: areas, points of interest, framed shots and walking paths. Nothing is built; the marks are saved to cameraTools' own file and only show while this tool is in your hand.\n\nRight click: choose the mark. Left click: place. Middle click: remove the mark you look at. E: rename it. F8: return the camera to your character.";
                shared.m_buildPieces = _table;
                shared.m_useDurability = false;
                shared.m_maxStackSize = 1;
                shared.m_icons = new[] { MarkerIcons.Tool() };
            }

            if (!db.m_items.Contains(_item))
            {
                db.m_items.Add(_item);
            }

            ItemDrop itemDrop = _item.GetComponent<ItemDrop>();
            if (!db.m_recipes.Any(r => r != null && r.m_item == itemDrop))
            {
                Recipe recipe = ScriptableObject.CreateInstance<Recipe>();
                recipe.name = "Recipe_" + ItemPrefabName;
                recipe.m_item = itemDrop;
                recipe.m_amount = 1;
                recipe.m_enabled = true;
                recipe.m_resources = Array.Empty<Piece.Requirement>();
                db.m_recipes.Add(recipe);
            }

            db.UpdateRegisters();
        }

        /// <summary>Registers the item with the network scene and builds the marker pieces (needs in-game materials).</summary>
        public static void EnsurePrefabs(ZNetScene scene)
        {
            if (_item == null || _table == null)
            {
                return;
            }

            int hash = ItemPrefabName.GetStableHashCode();
            if (!scene.m_namedPrefabs.ContainsKey(hash))
            {
                scene.m_prefabs.Add(_item);
                scene.m_namedPrefabs.Add(hash, _item);
            }

            if (_table.m_pieces.Count > 0)
            {
                return;
            }

            Material? material = FindPieceMaterial(scene);
            _table.m_pieces.Add(CreatePiece(AreaPiece, "Area",
                "An area of the build worth a section of the tour. Click each corner in order; click the first corner again to close it. Delete removes the last corner.",
                MarkerIcons.Area(), material));
            _table.m_pieces.Add(CreatePiece(CirclePiece, "Round area",
                "A round area. Click its center, then click again at its edge to set the radius.",
                MarkerIcons.Circle(), material));
            _table.m_pieces.Add(CreatePiece(PointPiece, "Point of interest",
                "One thing worth a close-up. Click it; the name you type becomes the call-out.",
                MarkerIcons.Point(), material));
            _table.m_pieces.Add(CreatePiece(ShotPiece, "Shot",
                "Saves the camera's current position and direction as a framed shot. Works from the free camera too.",
                MarkerIcons.Shot(), material));
            _table.m_pieces.Add(CreatePiece(PathPiece, "Path",
                "A natural walking route for the camera. Click to drop a waypoint where you stand, looking where you look; click again without moving to finish. G records waypoints while you walk. E on a waypoint sets a pause.",
                MarkerIcons.Path(), material));
        }

        private static GameObject CreateContainer()
        {
            GameObject container = new("cameraTools_prefabs");
            container.SetActive(false);
            Object.DontDestroyOnLoad(container);
            return container;
        }

        private static PieceTable CreateTable()
        {
            GameObject go = new(TablePrefabName);
            go.transform.SetParent(_container!.transform, false);
            PieceTable table = go.AddComponent<PieceTable>();
            table.m_canRemovePieces = true;
            table.m_canRemoveFeasts = false;
            table.m_hideAdvancedMenu = true;
            table.m_skill = Skills.SkillType.None;
            return table;
        }

        private static Material? FindPieceMaterial(ZNetScene scene)
        {
            foreach (string name in new[] { "wood_pole", "wood_beam", "wood_wall" })
            {
                GameObject? prefab = scene.GetPrefab(name);
                MeshRenderer? renderer = prefab != null ? prefab.GetComponentInChildren<MeshRenderer>() : null;
                if (renderer != null && renderer.sharedMaterial != null)
                {
                    return renderer.sharedMaterial;
                }
            }

            return null;
        }

        private static GameObject CreatePiece(string name, string label, string description, Sprite icon, Material? material)
        {
            GameObject go = new(name);
            go.transform.SetParent(_container!.transform, false);
            go.layer = LayerMask.NameToLayer("piece_nonsolid");

            Piece piece = go.AddComponent<Piece>();
            piece.m_name = label;
            piece.m_description = description;
            piece.m_icon = icon;
            piece.m_category = Piece.PieceCategory.Misc;
            piece.m_canRotate = false;
            piece.m_clipEverything = true;
            piece.m_allowedInDungeons = true;
            piece.m_allowedInDeepSnow = true;
            piece.m_canBeRemoved = false;
            piece.m_targetNonPlayerBuilt = false;
            piece.m_resources = Array.Empty<Piece.Requirement>();
            piece.m_placeEffect = new EffectList();

            // The ghost needs a renderer; a short stake marks the aim point like a surveyor's peg.
            GameObject stake = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            stake.name = "stake";
            Collider collider = stake.GetComponent<Collider>();
            if (collider != null)
            {
                Object.DestroyImmediate(collider);
            }

            stake.transform.SetParent(go.transform, false);
            stake.transform.localPosition = new Vector3(0f, 0.45f, 0f);
            stake.transform.localScale = new Vector3(0.07f, 0.45f, 0.07f);
            stake.layer = go.layer;
            if (material != null)
            {
                stake.GetComponent<MeshRenderer>().sharedMaterial = material;
            }

            return go;
        }
    }

    /// <summary>Flat gold pictograms in the style of the inventory icons, drawn in code so no asset bundle is needed.</summary>
    public static class MarkerIcons
    {
        private static readonly Dictionary<string, Sprite> Cache = new();
        private static readonly Color Gold = new(0.78f, 0.49f, 0.18f);
        private static readonly Color Light = new(1f, 0.9f, 0.65f);
        private static readonly Color Dark = new(0.32f, 0.16f, 0.07f);

        public static Sprite Tool() => Get("tool", 128, Camera);
        public static Sprite Shot() => Get("shot", 128, Camera);
        public static Sprite Area() => Get("area", 128, Polygon);
        public static Sprite Circle() => Get("circle", 128, Ring);
        public static Sprite Point() => Get("point", 128, Pin);
        public static Sprite Path() => Get("path", 128, Route);

        private static Sprite Get(string key, int size, Func<float, float, Color?> paint)
        {
            if (Cache.TryGetValue(key, out Sprite cached))
            {
                return cached;
            }

            Texture2D texture = new(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Color? color = paint(x + 0.5f, y + 0.5f);
                    if (color.HasValue)
                    {
                        pixels[y * size + x] = color.Value;
                    }
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(false, true);
            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
            Cache[key] = sprite;
            return sprite;
        }

        private static Color? Camera(float fx, float fy)
        {
            bool body = RoundedRect(fx, fy, 14f, 30f, 114f, 94f, 10f);
            bool bodyInner = RoundedRect(fx, fy, 19f, 35f, 109f, 89f, 7f);
            bool bump = RoundedRect(fx, fy, 44f, 88f, 84f, 106f, 6f);
            bool bumpInner = RoundedRect(fx, fy, 49f, 92f, 79f, 101f, 4f);
            float dx = fx - 64f;
            float dy = fy - 60f;
            float r = Mathf.Sqrt(dx * dx + dy * dy);
            bool shutter = RoundedRect(fx, fy, 92f, 72f, 106f, 84f, 3f);
            Color? color = null;
            if (body || bump) color = Dark;
            if (bodyInner || bumpInner) color = Gold;
            if (shutter) color = Dark;
            if (r <= 24f) color = Dark;
            if (r <= 19f) color = Light;
            if (r <= 13f) color = Dark;
            if (r <= 8f) color = Gold;
            if (dx > -9f && dx < -3f && dy > 3f && dy < 9f) color = Light;
            return color;
        }

        private static Color? Polygon(float fx, float fy)
        {
            Vector2[] pts = { new(22f, 36f), new(58f, 18f), new(108f, 44f), new(96f, 104f), new(34f, 96f) };
            return Outline(fx, fy, pts, true);
        }

        private static Color? Ring(float fx, float fy)
        {
            float dx = fx - 64f;
            float dy = fy - 64f;
            float r = Mathf.Sqrt(dx * dx + dy * dy);
            if (r <= 50f && r >= 32f) return Dark;
            if (r <= 46f && r >= 36f) return Gold;
            if (r <= 7f) return Dark;
            if (r <= 4f) return Light;
            return null;
        }

        private static Color? Pin(float fx, float fy)
        {
            // Diamond head on a short post.
            float dx = Mathf.Abs(fx - 64f);
            float dy = Mathf.Abs(fy - 78f);
            bool head = dx / 30f + dy / 34f <= 1f;
            bool headInner = dx / 23f + dy / 26f <= 1f;
            bool post = fx > 59f && fx < 69f && fy > 14f && fy < 50f;
            bool postInner = fx > 62f && fx < 66f && fy > 17f && fy < 48f;
            bool foot = RoundedRect(fx, fy, 44f, 10f, 84f, 20f, 4f);
            Color? color = null;
            if (head || post || foot) color = Dark;
            if (headInner || postInner) color = Gold;
            if (dx < 7f && Mathf.Abs(fy - 78f) < 7f) color = Light;
            return color;
        }

        private static Color? Route(float fx, float fy)
        {
            Vector2[] pts = { new(18f, 26f), new(48f, 60f), new(78f, 44f), new(110f, 100f) };
            Color? line = Outline(fx, fy, pts, false);
            Color? color = line;
            foreach (Vector2 p in pts)
            {
                float d = Vector2.Distance(new Vector2(fx, fy), p);
                if (d <= 11f) color = Dark;
                if (d <= 7f) color = Light;
            }

            return color;
        }

        private static Color? Outline(float fx, float fy, Vector2[] pts, bool closed)
        {
            float best = float.MaxValue;
            int count = closed ? pts.Length : pts.Length - 1;
            for (int i = 0; i < count; i++)
            {
                Vector2 a = pts[i];
                Vector2 b = pts[(i + 1) % pts.Length];
                best = Mathf.Min(best, DistanceToSegment(new Vector2(fx, fy), a, b));
            }

            if (best <= 9f) return Dark;
            return null;
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(0.0001f, ab.sqrMagnitude));
            return Vector2.Distance(p, a + ab * t);
        }

        private static bool RoundedRect(float x, float y, float x0, float y0, float x1, float y1, float radius)
        {
            if (x < x0 || x > x1 || y < y0 || y > y1) return false;
            float cx = Mathf.Clamp(x, x0 + radius, x1 - radius);
            float cy = Mathf.Clamp(y, y0 + radius, y1 - radius);
            float dx = x - cx;
            float dy = y - cy;
            return dx * dx + dy * dy <= radius * radius;
        }
    }
}
