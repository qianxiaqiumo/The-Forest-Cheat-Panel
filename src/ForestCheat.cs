// ForestCheat.cs —— 《The Forest》单机修改器（BepInEx 5 / Mono）
// 全部通过反射访问游戏类型，游戏小版本更新不会导致加载失败。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace ForestCheat
{
    [BepInPlugin("com.local.forest.cheat", "Forest Cheat", "0.1.0")]
    public class CheatPlugin : BaseUnityPlugin
    {
        // ---------------- 开关状态
        private bool showMenu = false;
        private bool godMode = false;
        private bool lockStamina = true;
        private bool lockFood = true;
        private bool lockBattery = true;
        private bool moveBoost = false;
        private float speedMul = 2f;
        private float jumpMul = 1.5f;
        private bool showCoords = true;
        private bool pauseOnMenu = true;
        private float _savedTimeScale = 1f;

        private string tpX = "", tpY = "", tpZ = "";
        private Vector2 scroll;
        private Vector2 itemScroll;
        private Vector2 wpScroll;
        private string probeLog = "";

        // ---------------- 坐标收藏夹
        private class Waypoint
        {
            public string Name;
            public float X, Y, Z;
        }

        // 数据文件统一放在 BepInEx\config\ 下
        private static string ConfigPath(string fileName)
        {
            try
            {
                string root = Paths.BepInExRootPath;
                if (string.IsNullOrEmpty(root)) root = ".";
                string dir = Path.Combine(root, "config");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, fileName);
            }
            catch { return fileName; }
        }
        private readonly List<Waypoint> _wps = new List<Waypoint>();
        private bool _wpsLoaded;
        private string wpName = "";
        private string wpStatus = "";

        // 物品
        private string itemName = "Log";
        private string itemAmount = "10";
        private string itemSearch = "";
        private string itemStatus = "";
        private readonly List<string> _itemNames = new List<string>();
        private readonly List<int> _itemIds = new List<int>();
        private readonly Dictionary<string, string> _zh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _zhWord = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool _zhLoaded;
        private bool _showZh = true;
        private Vector2 menuScroll;
        private float _lastAutoRefresh;

        // ---------------- 反射缓存
        private Type _localPlayer;
        private Type _itemDbType;
        private Type _itemPropsType;
        private object _stats;              // PlayerStats 实例
        private object _fp;                 // FirstPersonCharacter 实例
        private object _inv;                // PlayerInventory 实例
        private bool _resolved;

        // 移动速度原始值（只缓存一次，关闭后恢复）
        private bool _speedCaptured;
        private object _lastFp;
        private float _baseWalk, _baseRun, _baseCrouch, _baseStrafe, _baseSwim, _baseJump;
        private bool _diagUpdate, _diagOnGui;
        private GUIStyle _panelStyle;
        private object _camRotator;

        private GUIStyle PanelStyle()
        {
            if (_panelStyle == null)
            {
                _panelStyle = new GUIStyle(GUI.skin.box);
                var tex = new Texture2D(1, 1);
                tex.SetPixel(0, 0, new Color(0.94f, 0.95f, 0.97f, 0.95f));
                tex.Apply();
                _panelStyle.normal.background = tex;
                _panelStyle.padding = new RectOffset(12, 12, 10, 10);
                _panelStyle.border = new RectOffset(1, 1, 1, 1);
            }
            return _panelStyle;
        }

        static CheatPlugin()
        {
            Diag("静态构造（类型已加载）");
        }

        public CheatPlugin()
        {
            Diag("实例构造（AddComponent 成功）");
        }

        private void Awake()
        {
            Diag("Awake");
            try
            {
                Application.runInBackground = true;
            }
            catch { }
        }

        private static void Diag(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine;
            try
            {
                if (string.IsNullOrEmpty(Paths.BepInExRootPath)) return;
                File.AppendAllText(Path.Combine(Paths.BepInExRootPath, "forestcheat_diag.txt"), line);
            }
            catch { }
        }

        private void Update()
        {
            try
            {
                UpdateCore();
            }
            catch (Exception e)
            {
                Diag("Update 异常: " + e);
            }
        }

        private void UpdateCore()
        {
            if (!_diagUpdate)
            {
                _diagUpdate = true;
                Diag("Update 首次运行 frame=" + Time.frameCount + " 焦点=" + Application.isFocused);
            }

            if (Input.GetKeyDown(KeyCode.F7)) ToggleMenu();
            if (Input.GetKeyDown(KeyCode.F8)) ToggleMenu();

            if (showMenu)
            {
                // 游戏自己的 Update 会反复把鼠标锁回去，这里只是提前释放一下，
                // 真正的强制解锁在 OnGUI 里每帧执行
                try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch { }
                // 暂停游戏（持续强制，防止被游戏改回去）
                if (pauseOnMenu && Time.timeScale > 0.0001f)
                {
                    _savedTimeScale = Time.timeScale;
                    Time.timeScale = 0f;
                }
            }

            if (!_resolved)
            {
                ResolveGameTypes();
            }

            if (_stats != null)
            {
                ApplyStats();
            }

            if (_fp != null)
            {
                ApplyMovement();
            }

            // 进存档后自动试一次物品列表（只试一次，失败由按钮手动重试）
            if (_resolved && _itemNames.Count == 0 && itemStatus.Length == 0 && Time.time - _lastAutoRefresh > 3f)
            {
                _lastAutoRefresh = Time.time;
                RefreshItemList();
            }
        }

        private void ToggleMenu()
        {
            showMenu = !showMenu;
            if (showMenu)
            {
                if (Time.timeScale > 0.0001f) _savedTimeScale = Time.timeScale;
                if (pauseOnMenu) Time.timeScale = 0f;
                SetLookActive(false);
                try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch { }
            }
            else
            {
                float restore = _savedTimeScale > 0.0001f ? _savedTimeScale : 1f;
                Time.timeScale = restore;
                SetLookActive(true);
                SetViewLock(true);
                try { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; } catch { }
            }
        }

        // ---------------------------------------------------------------- 类型解析
        private void ResolveGameTypes()
        {
            _localPlayer = FindType("TheForest.Utils.LocalPlayer");
            if (_localPlayer == null)
            {
                return;     // 还没进游戏（主菜单），下一帧再试
            }

            // LocalPlayer 的成员是 public static **字段**（不是属性）
            _stats = StaticGet(_localPlayer, "Stats");
            _fp = StaticGet(_localPlayer, "FpCharacter");
            _inv = StaticGet(_localPlayer, "Inventory");
            _itemDbType = FindType("TheForest.Items.ItemDatabase");
            _itemPropsType = FindType("TheForest.Items.Inventory.ItemProperties");

            _resolved = (_stats != null || _fp != null);
            if (_resolved)
            {
                Logger.LogInfo("已连接到游戏：Stats=" + (_stats != null) +
                               " FpCharacter=" + (_fp != null) +
                               " Inventory=" + (_inv != null) +
                               " ItemDatabase=" + (_itemDbType != null));
            }
        }

        private static Type FindType(string fullName)
        {
            var t = Type.GetType(fullName + ", Assembly-CSharp");
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    t = asm.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static object StaticGet(Type t, string member)
        {
            if (t == null) return null;
            try
            {
                var p = t.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (p != null) return p.GetValue(null, null);
                var f = t.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f != null) return f.GetValue(null);
            }
            catch { }
            return null;
        }
        // ---------------------------------------------------------------- 属性应用
        private void ApplyStats()
        {
            if (godMode)
            {
                Set("Health", 100f);
                Set("Armor", 400);
                Set("IsBloody", false);
                Set("IsCold", false);
                Set("FireWarmth", true);
                Set("SunWarmth", true);
            }

            if (lockStamina)
            {
                Set("Stamina", 100f);
                Set("Energy", 100f);
            }

            if (lockFood)
            {
                // 不同版本 Fullness 单位不同：0~1 或 0~100，按当前值自动判断
                float cur = GetFloat("Fullness", 1f);
                Set("Fullness", cur <= 1.5f ? 1f : 100f);
                Set("Thirst", cur <= 1.5f ? 0f : 0f);
                Set("Starvation", 0f);
            }

            if (lockBattery)
            {
                Set("BatteryCharge", 100f);
            }

            // 理智值：Sanity 是独立对象，字段名 CurrentSanity
            try
            {
                var sanityProp = _stats.GetType().GetProperty("Sanity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (sanityProp != null)
                {
                    var sanity = sanityProp.GetValue(_stats, null);
                    if (sanity != null)
                    {
                        var f = sanity.GetType().GetField("CurrentSanity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (f != null) f.SetValue(sanity, 100f);
                    }
                }
            }
            catch { }
        }

        private void ApplyMovement()
        {
            if (!ReferenceEquals(_fp, _lastFp))
            {
                _lastFp = _fp;
                _speedCaptured = false;
            }

            if (!_speedCaptured)
            {
                float probeRun = GetFloat(_fp, "runSpeed", 0f);
                if (probeRun <= 0.01f)
                {
                    return;     // 角色还没初始化完，先不写
                }
                _baseWalk = GetFloat(_fp, "walkSpeed", 4f);
                _baseRun = probeRun;
                _baseCrouch = GetFloat(_fp, "crouchSpeed", 2f);
                _baseStrafe = GetFloat(_fp, "strafeSpeed", 4f);
                _baseSwim = GetFloat(_fp, "swimmingSpeed", 3f);
                _baseJump = GetFloat(_fp, "jumpHeight", 5f);
                _speedCaptured = true;
            }

            float mul = moveBoost ? speedMul : 1f;
            SetField(_fp, "runSpeed", _baseRun * mul);
            SetField(_fp, "walkSpeed", _baseWalk * mul);
            SetField(_fp, "crouchSpeed", _baseCrouch * mul);
            SetField(_fp, "strafeSpeed", _baseStrafe * mul);
            SetField(_fp, "swimmingSpeed", _baseSwim * mul);
            SetField(_fp, "jumpHeight", _baseJump * (moveBoost ? jumpMul : 1f));

            if (godMode)
            {
                SetField(_fp, "allowFallDamage", false);
            }
        }

        // ---------------------------------------------------------------- 反射读写
        private FieldInfo FieldOf(object obj, string name)
        {
            return obj == null ? null : obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        private void Set(string field, object value)
        {
            try
            {
                var f = FieldOf(_stats, field);
                if (f == null) return;
                object v = value;
                if (f.FieldType != value.GetType())
                {
                    v = Convert.ChangeType(value, f.FieldType);
                }
                f.SetValue(_stats, v);
            }
            catch { }
        }

        private float GetFloat(string field, float fallback)
        {
            try
            {
                var f = FieldOf(_stats, field);
                if (f == null) return fallback;
                object v = f.GetValue(_stats);
                return v == null ? fallback : Convert.ToSingle(v);
            }
            catch { return fallback; }
        }

        private static float GetFloat(object obj, string field, float fallback)
        {
            try
            {
                var f = obj.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null) return fallback;
                object v = f.GetValue(obj);
                return v == null ? fallback : Convert.ToSingle(v);
            }
            catch { return fallback; }
        }

        private static void SetField(object obj, string field, object value)
        {
            try
            {
                var f = obj.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null) return;
                f.SetValue(obj, Convert.ChangeType(value, f.FieldType));
            }
            catch { }
        }

        // ---------------------------------------------------------------- 传送 / 坐标
        private void SetViewLock(bool locked)
        {
            try
            {
                if (_fp == null) return;
                var m = _fp.GetType().GetMethod(locked ? "LockView" : "UnLockView");
                if (m != null) m.Invoke(_fp, null);
            }
            catch { }
        }

        // 菜单打开时暂时关掉相机旋转组件，这样移动鼠标点按钮不会带着视角乱转
        private void SetLookActive(bool active)
        {
            try
            {
                if (_localPlayer == null) return;
                if (_camRotator == null) _camRotator = StaticGet(_localPlayer, "CamRotator");
                var c = _camRotator as Behaviour;
                if (c != null) c.enabled = active;
            }
            catch { }
        }

        private Transform PlayerTransform()        {
            try
            {
                if (_localPlayer == null) return null;
                object t = StaticGet(_localPlayer, "Transform");
                return t as Transform;
            }
            catch { return null; }
        }

        private void Teleport()
        {
            var tr = PlayerTransform();
            if (tr == null) return;
            float x, y, z;
            if (float.TryParse(tpX, out x) && float.TryParse(tpY, out y) && float.TryParse(tpZ, out z))
            {
                try
                {
                    var rb = tr.GetComponent<Rigidbody>();
                    if (rb != null) rb.velocity = Vector3.zero;
                }
                catch { }
                tr.position = new Vector3(x, y, z);
            }
        }

        // ---------------------------------------------------------------- 物品 API 探测
        private void ProbeItemApi()
        {
            var lines = new List<string>();
            lines.Add("=== 物品相关类型 ===");
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    if (!(t.Name.Contains("ItemHolder") || t.Name.Contains("Inventory") ||
                          t.Name.Contains("ItemDatabase") || t.Name.Contains("ItemSpawner")))
                    {
                        continue;
                    }
                    lines.Add("[" + t.FullName + "]");
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                    {
                        var n = m.Name.ToLowerInvariant();
                        if (n.Contains("add") || n.Contains("spawn") || n.Contains("give") || n.Contains("create"))
                        {
                            lines.Add("   " + m.ReturnType.Name + " " + m.Name + "(" +
                                      string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name).ToArray()) + ")");
                        }
                    }
                    if (lines.Count > 120)
                    {
                        lines.Add("…（已截断）");
                        break;
                    }
                }
                if (lines.Count > 120) break;
            }
            probeLog = string.Join("\n", lines.ToArray());
            Logger.LogInfo("\n" + probeLog);
        }

        // ---------------------------------------------------------------- 物品生成
        private void RefreshItemList()
        {
            _itemNames.Clear();
            _itemIds.Clear();
            if (_itemDbType == null) { itemStatus = "物品数据库类型未找到"; return; }
            try
            {
                var itemsProp = _itemDbType.GetProperty("Items", BindingFlags.Public | BindingFlags.Static);
                object raw = itemsProp != null ? itemsProp.GetValue(null, null) : null;
                if (raw == null)
                {
                    var itemsField = _itemDbType.GetField("_items", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (itemsField != null) raw = itemsField.GetValue(null);
                }
                var arr = raw as Array;
                if (arr == null) { itemStatus = "Items 为空（先进存档再点刷新）"; return; }

                var pairs = new List<KeyValuePair<string, int>>();
                foreach (var it in arr)
                {
                    if (it == null) continue;
                    var tp = it.GetType();
                    var nf = tp.GetField("_name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var idf = tp.GetField("_id", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    string n = nf != null ? nf.GetValue(it) as string : null;
                    if (string.IsNullOrEmpty(n)) continue;
                    int id = 0;
                    if (idf != null)
                    {
                        try { id = Convert.ToInt32(idf.GetValue(it)); } catch { }
                    }
                    pairs.Add(new KeyValuePair<string, int>(n, id));
                }
                pairs.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
                for (int i = 0; i < pairs.Count; i++)
                {
                    _itemNames.Add(pairs[i].Key);
                    _itemIds.Add(pairs[i].Value);
                }
                itemStatus = "共 " + _itemNames.Count + " 种物品";
                DumpItemList();
            }
            catch (Exception e) { itemStatus = "读取失败：" + e.Message; }
        }

        private void DumpItemList()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# The Forest 物品清单（ID <- 名称）");
                sb.AppendLine("# 生成时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                for (int i = 0; i < _itemNames.Count; i++)
                {
                    sb.Append(_itemIds[i]).Append('\t').AppendLine(_itemNames[i]);
                }
                string text = sb.ToString();
                try { File.WriteAllText(ConfigPath("物品清单.txt"), text); } catch { }
                try
                {
                    if (!string.IsNullOrEmpty(Paths.BepInExRootPath))
                    {
                        File.WriteAllText(Path.Combine(Paths.BepInExRootPath, "物品清单.txt"), text);
                    }
                }
                catch { }
            }
            catch { }
        }

        // ---------------------------------------------------------------- 物品名中文对照
        private void AddZh(string en, string zh)
        {
            if (!string.IsNullOrEmpty(en)) _zh[en.Trim()] = zh;
        }

        private void AddWord(string en, string zh)
        {
            if (!string.IsNullOrEmpty(en)) _zhWord[en.Trim()] = zh;
        }

        private void EnsureZh()
        {
            if (_zhLoaded) return;
            _zhLoaded = true;

            // ===== 整名校对（优先级最高） =====
            AddZh("Log", "原木"); AddZh("Stick", "树枝"); AddZh("Rock", "石头"); AddZh("Small Rock", "小石头");
            AddZh("Cloth", "布"); AddZh("Rope", "绳子"); AddZh("Leaf", "树叶"); AddZh("Leaves", "树叶");
            AddZh("Feather", "羽毛"); AddZh("Bone", "骨头"); AddZh("Teeth", "牙齿"); AddZh("Sap", "树脂");
            AddZh("Resin", "树脂"); AddZh("Coal", "煤炭"); AddZh("Cash", "现金"); AddZh("Battery", "电池");
            AddZh("Tape", "磁带"); AddZh("Cassette Tape", "磁带"); AddZh("Walkman", "随身听");
            AddZh("Map", "地图"); AddZh("Compass", "指南针"); AddZh("Camera", "相机");
            AddZh("Keycard", "门禁卡"); AddZh("Wrist Watch", "手表");

            AddZh("Meat", "生肉"); AddZh("Cooked Meat", "熟肉"); AddZh("Dried Meat", "风干肉");
            AddZh("Fish", "鱼"); AddZh("Cooked Fish", "熟鱼"); AddZh("Oyster", "牡蛎");
            AddZh("Berries", "浆果"); AddZh("Blueberries", "蓝莓");
            AddZh("Meds", "药物"); AddZh("Medicines", "药品"); AddZh("Herbal Medicine", "草药");
            AddZh("Energy Mix", "能量饮料"); AddZh("Alcohol", "酒精"); AddZh("Water", "水");

            AddZh("Axe", "斧头"); AddZh("Old Axe", "旧斧头"); AddZh("Rusty Axe", "锈蚀的斧头");
            AddZh("Modern Axe", "现代斧头"); AddZh("Crafted Axe", "自制斧头");
            AddZh("Climbing Axe", "攀爬斧"); AddZh("Katana", "武士刀"); AddZh("Machete", "砍刀");
            AddZh("Spear", "长矛"); AddZh("Weak Spear", "简易长矛"); AddZh("Upgraded Spear", "强化长矛");
            AddZh("Bow", "弓"); AddZh("Crafted Bow", "自制弓"); AddZh("Modern Bow", "现代弓");
            AddZh("Slingshot", "弹弓"); AddZh("Arrow", "箭"); AddZh("Bone Arrow", "骨箭");
            AddZh("Fire Arrow", "火箭"); AddZh("Poison Arrow", "毒箭");
            AddZh("Dynamite", "炸药"); AddZh("Flare", "信号棒"); AddZh("Bomb", "炸弹");
            AddZh("Flashlight", "手电筒"); AddZh("Rebreather", "呼吸器"); AddZh("Repair Tool", "修理工具");

            // ===== 词根，用于拼装未知名称（有未识别词则退回英文） =====
            AddWord("Old", "旧"); AddWord("Rusty", "锈蚀"); AddWord("Modern", "现代");
            AddWord("Crafted", "自制"); AddWord("Upgraded", "强化"); AddWord("Weak", "简易");
            AddWord("Small", "小"); AddWord("Large", "大"); AddWord("Big", "大");
            AddWord("Axe", "斧"); AddWord("Bow", "弓"); AddWord("Arrow", "箭"); AddWord("Spear", "矛");
            AddWord("Bomb", "炸弹"); AddWord("Trap", "陷阱"); AddWord("Armor", "护甲");
            AddWord("Skin", "皮"); AddWord("Fur", "毛皮"); AddWord("Hide", "兽皮");
            AddWord("Meat", "肉"); AddWord("Fish", "鱼"); AddWord("Water", "水"); AddWord("Fire", "火");
            AddWord("Torch", "火把"); AddWord("Wall", "墙"); AddWord("Floor", "地板");
            AddWord("Foundation", "地基"); AddWord("Door", "门"); AddWord("Gate", "大门");
            AddWord("Roof", "屋顶"); AddWord("Stairs", "楼梯"); AddWord("Ladder", "梯子");
            AddWord("Platform", "平台"); AddWord("Rack", "架子"); AddWord("Storage", "储存点");
            AddWord("Holder", "放置架"); AddWord("Shelter", "庇护所"); AddWord("Tree", "树");
            AddWord("House", "小屋"); AddWord("Garden", "菜园"); AddWord("Effigy", "木偶");
            AddWord("Rock", "石头"); AddWord("Stone", "石头"); AddWord("Stick", "树枝");
            AddWord("Log", "原木"); AddWord("Cloth", "布"); AddWord("Rope", "绳"); AddWord("Leaf", "树叶");
            AddWord("Feather", "羽毛"); AddWord("Bone", "骨"); AddWord("Teeth", "牙"); AddWord("Sap", "树脂");
            AddWord("Poison", "毒"); AddWord("Explosive", "爆炸"); AddWord("Sticky", "黏性");
            AddWord("Flaming", "燃烧"); AddWord("Bag", "袋子"); AddWord("Pouch", "小袋");
            AddWord("Head", "头颅"); AddWord("Trophy", "战利品"); AddWord("Paddle", "船桨");
            AddWord("Rebreather", "呼吸器"); AddWord("Flashlight", "手电筒"); AddWord("Torch", "火把");

            // ===== 外部对照表：物品中文对照.txt（每行：英文<TAB>中文） =====
            try
            {
                string p = ConfigPath("物品中文对照.txt");
                if (File.Exists(p))
                {
                    foreach (var line in File.ReadAllLines(p))
                    {
                        if (string.IsNullOrEmpty(line) || line.TrimStart().StartsWith("#")) continue;
                        var parts = line.Split('\t');
                        if (parts.Length >= 2 && parts[0].Trim().Length > 0 && parts[1].Trim().Length > 0)
                        {
                            AddZh(parts[0].Trim(), parts[1].Trim());
                        }
                    }
                }
            }
            catch { }
        }

        private string Zh(string en)
        {
            if (string.IsNullOrEmpty(en)) return null;
            EnsureZh();
            string v;
            if (_zh.TryGetValue(en, out v)) return v;

            var words = en.Split(new char[] { ' ', '-', '(', ')', '_', '/', '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return null;
            var sb = new System.Text.StringBuilder();
            foreach (var w in words)
            {
                string t;
                if (_zhWord.TryGetValue(w, out t)) sb.Append(t);
                else if (_zh.TryGetValue(w, out t)) sb.Append(t);
                else return null;       // 有未识别词，整体退回英文
            }
            return sb.ToString();
        }

        private string ZhToEn(string zh)
        {
            if (string.IsNullOrEmpty(zh)) return null;
            EnsureZh();
            foreach (var kv in _zh)
            {
                if (string.Equals(kv.Value, zh, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            }
            foreach (var kv in _zh)
            {
                if (kv.Value.StartsWith(zh)) return kv.Key;
            }
            return null;
        }

        private string ItemLabel(int i)
        {
            string en = _itemNames[i];
            if (!_showZh) return _itemIds[i] + "   " + en;
            string zh = Zh(en);
            return _itemIds[i] + "   " + (zh != null ? zh + "  (" + en + ")" : en);
        }

        private int ResolveItemId(string name)
        {
            if (_itemDbType == null || string.IsNullOrEmpty(name)) return 0;
            try
            {
                var m = _itemDbType.GetMethod("ItemIdByName", BindingFlags.Public | BindingFlags.Static,
                                              null, new Type[] { typeof(string) }, null);
                if (m == null) return 0;
                object r = m.Invoke(null, new object[] { name });
                return r == null ? 0 : Convert.ToInt32(r);
            }
            catch { return 0; }
        }

        private int ParseAmount()
        {
            int a;
            if (!int.TryParse((itemAmount ?? "").Trim(), out a) || a <= 0) a = 1;
            if (a > 9999) a = 9999;
            return a;
        }

        // 自动匹配 AddItem 的各种重载签名
        private bool AddItemById(int id, int amount)
        {
            if (_inv == null) { itemStatus = "背包未就绪（先进存档）"; return false; }
            var t = _inv.GetType();
            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var m in methods)
            {
                if (m.Name != "AddItem" && m.Name != "AddItemNF") continue;
                var ps = m.GetParameters();
                if (ps.Length < 2) continue;
                if (ps[0].ParameterType != typeof(int) || ps[1].ParameterType != typeof(int)) continue;

                object[] args = new object[ps.Length];
                args[0] = id;
                args[1] = amount;
                for (int i = 2; i < ps.Length; i++)
                {
                    var pt = ps[i].ParameterType;
                    if (pt == typeof(bool))
                    {
                        // 第一个 bool 通常是 preventAutoEquip：必须为 true，
                        // 否则地图/头灯等物品会被自动拿在手上并锁死鼠标
                        string pn = (ps[i].Name ?? "").ToLowerInvariant();
                        args[i] = pn.Contains("prevent");
                    }
                    else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                    else
                    {
                        object inst = null;
                        try { inst = Activator.CreateInstance(pt); } catch { }
                        args[i] = inst;
                    }
                }
                try
                {
                    object r = m.Invoke(_inv, args);
                    if (r == null) return true;
                    if (r is bool) return (bool)r;
                    return true;
                }
                catch (Exception e)
                {
                    itemStatus = "调用 " + m.Name + " 出错：" +
                                  (e.InnerException != null ? e.InnerException.Message : e.Message);
                }
            }
            return false;
        }

        private void GiveItem(string name, int amount)
        {
            if (amount <= 0) amount = 1;
            int id = ResolveItemId(name);
            if (id <= 0)
            {
                // 允许直接输中文
                string en = ZhToEn(name);
                if (en != null) { name = en; id = ResolveItemId(en); }
            }
            if (id <= 0) { itemStatus = "找不到物品：" + name; return; }
            bool ok = AddItemById(id, amount);
            itemStatus = (ok ? "已添加 " : "添加失败 ") + name + " ×" + amount;
        }

        private void GiveAllItems()
        {
            if (_itemNames.Count == 0) RefreshItemList();
            if (_itemNames.Count == 0) { itemStatus = "物品列表为空，先进存档再刷新"; return; }
            int amount = ParseAmount();
            int okCount = 0, failCount = 0;
            for (int i = 0; i < _itemNames.Count; i++)
            {
                if (AddItemById(_itemIds[i], amount)) okCount++;
                else failCount++;
            }
            itemStatus = "全部给予完成：成功 " + okCount + " 项，未成功 " + failCount + " 项";
        }

        // ---------------------------------------------------------------- 坐标收藏夹
        private void EnsureWaypoints()
        {
            if (_wpsLoaded) return;
            _wpsLoaded = true;
            try
            {
                string wpPath = ConfigPath("坐标收藏.txt");
                if (!File.Exists(wpPath)) return;
                foreach (var line in File.ReadAllLines(wpPath))
                {
                    if (string.IsNullOrEmpty(line) || line.TrimStart().StartsWith("#")) continue;
                    var p = line.Split('\t');
                    if (p.Length < 4) continue;
                    float x, y, z;
                    if (!float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x)) continue;
                    if (!float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y)) continue;
                    if (!float.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z)) continue;
                    var w = new Waypoint();
                    w.Name = p[0].Trim();
                    w.X = x; w.Y = y; w.Z = z;
                    _wps.Add(w);
                }
            }
            catch { }
        }

        private void SaveWaypoints()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# The Forest 坐标收藏（名称 <TAB> X <TAB> Y <TAB> Z）");
                sb.AppendLine("# 可直接编辑此文件，重启游戏生效");
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                foreach (var w in _wps)
                {
                    sb.Append(w.Name.Replace("\t", " ")).Append('\t')
                      .Append(w.X.ToString("0.00", ci)).Append('\t')
                      .Append(w.Y.ToString("0.00", ci)).Append('\t')
                      .Append(w.Z.ToString("0.00", ci)).AppendLine();
                }
                File.WriteAllText(ConfigPath("坐标收藏.txt"), sb.ToString());
            }
            catch { }
        }

        private void AddWaypoint()
        {
            EnsureWaypoints();
            var tr = PlayerTransform();
            if (tr == null) { wpStatus = "还没进存档，无法读取位置"; return; }
            var p = tr.position;
            string n = (wpName ?? "").Trim();
            if (n.Length == 0) n = "位置 " + (_wps.Count + 1);
            var w = new Waypoint();
            w.Name = n;
            w.X = p.x; w.Y = p.y; w.Z = p.z;
            _wps.Add(w);
            wpName = "";
            SaveWaypoints();
            wpStatus = "已收藏：" + n + "（共 " + _wps.Count + " 个）";
        }

        private void TeleportTo(Waypoint w)
        {
            if (w == null) return;
            var tr = PlayerTransform();
            if (tr == null) { wpStatus = "还没进存档"; return; }
            try
            {
                var rb = tr.GetComponent<Rigidbody>();
                if (rb != null) rb.velocity = Vector3.zero;
            }
            catch { }
            tr.position = new Vector3(w.X, w.Y, w.Z);
            wpStatus = "已传送到：" + w.Name;
        }

        // ---------------------------------------------------------------- 界面
        private void OnGUI()
        {
            if (!_diagOnGui)
            {
                _diagOnGui = true;
                Diag("OnGUI 首次运行");
            }

            if (!showMenu) return;

            // 每帧强制解锁鼠标（OnGUI 在所有 Update 之后执行，游戏抢不回去）
            try
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            catch { }

            GUI.skin.label.fontSize = 13;
            bool closeRequested = false;
            var origLabelColor = GUI.skin.label.normal.textColor;            var origBoxColor = GUI.skin.box.normal.textColor;
            GUI.skin.label.normal.textColor = new Color(0.08f, 0.08f, 0.10f);
            GUI.skin.box.normal.textColor = new Color(0.08f, 0.08f, 0.10f);

            float w = Mathf.Min(430f, Screen.width - 40f);
            float h = Mathf.Min(780f, Screen.height - 40f);
            // 右上角显示
            GUILayout.BeginArea(new Rect(Screen.width - w - 20f, 20f, w, h), PanelStyle());
            menuScroll = GUILayout.BeginScrollView(menuScroll);
            GUILayout.Label("<b>The Forest 修改器 v0.1</b>   F7 开关菜单");
            GUILayout.BeginHorizontal();
            pauseOnMenu = GUILayout.Toggle(pauseOnMenu, " 打开菜单时暂停游戏");
            if (GUILayout.Button("关闭菜单", GUILayout.Width(90))) closeRequested = true;
            GUILayout.EndHorizontal();
            GUILayout.Space(4);

            if (_stats == null && _fp == null)
            {
                GUILayout.Label("状态：等待进入存档……");
            }
            else
            {
                GUILayout.Label("状态：已连接（Stats=" + (_stats != null) + "）");
            }

            GUILayout.Space(6);
            godMode = GUILayout.Toggle(godMode, " 上帝模式（血量/护甲/不流血/保暖）");
            lockStamina = GUILayout.Toggle(lockStamina, " 锁定体力 + 精力");
            lockFood = GUILayout.Toggle(lockFood, " 锁定饱食 + 不渴 + 不饿");
            lockBattery = GUILayout.Toggle(lockBattery, " 锁定电池电量");

            GUILayout.Space(6);
            moveBoost = GUILayout.Toggle(moveBoost, " 移动加速");
            GUILayout.BeginHorizontal();
            GUILayout.Label("速度倍数 " + speedMul.ToString("0.0"), GUILayout.Width(90));
            speedMul = GUILayout.HorizontalSlider(speedMul, 1f, 5f);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("跳跃倍数 " + jumpMul.ToString("0.0"), GUILayout.Width(90));
            jumpMul = GUILayout.HorizontalSlider(jumpMul, 1f, 4f);
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            showCoords = GUILayout.Toggle(showCoords, " 显示坐标");
            var tr = PlayerTransform();
            if (showCoords && tr != null)
            {
                var p = tr.position;
                GUILayout.Label("当前位置：" + p.x.ToString("0.0") + ", " + p.y.ToString("0.0") + ", " + p.z.ToString("0.0"));
                if (GUILayout.Button("把当前位置填入传送框"))
                {
                    tpX = p.x.ToString("0.0");
                    tpY = p.y.ToString("0.0");
                    tpZ = p.z.ToString("0.0");
                }
            }

            GUILayout.Space(4);
            GUILayout.Label("传送坐标（X / Y / Z）：");
            GUILayout.BeginHorizontal();
            tpX = GUILayout.TextField(tpX, GUILayout.Width(90));
            tpY = GUILayout.TextField(tpY, GUILayout.Width(90));
            tpZ = GUILayout.TextField(tpZ, GUILayout.Width(90));
            GUILayout.EndHorizontal();
            if (GUILayout.Button("传送到该坐标"))
            {
                Teleport();
            }

            GUILayout.Space(8);
            GUILayout.Label("<b>坐标收藏夹</b>（数量不限，自动保存到 坐标收藏.txt）");
            GUILayout.BeginHorizontal();
            wpName = GUILayout.TextField(wpName == null ? "" : wpName, GUILayout.Width(160));
            if (GUILayout.Button("收藏当前位置")) AddWaypoint();
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(wpStatus)) GUILayout.Label(wpStatus);

            EnsureWaypoints();
            if (_wps.Count > 0)
            {
                wpScroll = GUILayout.BeginScrollView(wpScroll, GUILayout.Height(160));
                int delIndex = -1;
                for (int i = 0; i < _wps.Count; i++)
                {
                    var wp = _wps[i];
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button(wp.Name + "   [" + wp.X.ToString("0") + ", " + wp.Z.ToString("0") + "]", GUILayout.Width(215)))
                    {
                        TeleportTo(wp);
                    }
                    if (GUILayout.Button("填坐标", GUILayout.Width(66)))
                    {
                        tpX = wp.X.ToString("0.0");
                        tpY = wp.Y.ToString("0.0");
                        tpZ = wp.Z.ToString("0.0");
                        wpStatus = "已填入：" + wp.Name;
                    }
                    if (GUILayout.Button("删", GUILayout.Width(32)))
                    {
                        delIndex = i;
                    }
                    GUILayout.EndHorizontal();
                }
                if (delIndex >= 0)
                {
                    wpStatus = "已删除：" + _wps[delIndex].Name;
                    _wps.RemoveAt(delIndex);
                    SaveWaypoints();
                }
                GUILayout.EndScrollView();
            }

            GUILayout.Space(8);
            GUILayout.Label("<b>物品生成</b>（刷新后点名字即可给予）");            GUILayout.BeginHorizontal();
            GUILayout.Label("数量", GUILayout.Width(32));
            itemAmount = GUILayout.TextField(itemAmount, GUILayout.Width(50));
            itemSearch = GUILayout.TextField(itemSearch, GUILayout.Width(160));
            if (GUILayout.Button("清空搜索", GUILayout.Width(70))) itemSearch = "";
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("中文名", GUILayout.Width(48));
            _showZh = GUILayout.Toggle(_showZh, " 物品名显示中文（取消则只显示英文）");
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("木头")) GiveItem("Log", ParseAmount());
            if (GUILayout.Button("树枝")) GiveItem("Stick", ParseAmount());
            if (GUILayout.Button("石头")) GiveItem("Rock", ParseAmount());
            if (GUILayout.Button("布")) GiveItem("Cloth", ParseAmount());
            if (GUILayout.Button("绳子")) GiveItem("Rope", ParseAmount());
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            itemName = GUILayout.TextField(itemName, GUILayout.Width(160));
            if (GUILayout.Button("给予")) GiveItem(itemName, ParseAmount());
            if (GUILayout.Button("刷新列表")) RefreshItemList();
            GUILayout.EndHorizontal();

            if (GUILayout.Button("◆ 全部物品各给 " + ParseAmount() + " 个（逐个尝试）")) GiveAllItems();
            if (!string.IsNullOrEmpty(itemStatus)) GUILayout.Label(itemStatus);

            if (GUILayout.Button("解除鼠标锁定 / 关闭地图界面"))
            {
                try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch { }
                itemStatus = "已尝试解锁鼠标（再按 F7 关菜单可恢复视角）";
            }

            if (_itemNames.Count > 0)
            {
                itemScroll = GUILayout.BeginScrollView(itemScroll, GUILayout.Height(240));
                string kw = (itemSearch ?? "").Trim();
                int shown = 0;
                for (int i = 0; i < _itemNames.Count; i++)
                {
                    string label = ItemLabel(i);
                    if (kw.Length > 0 &&
                        _itemNames[i].IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0 &&
                        label.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    shown++;
                    if (shown > 400) { GUILayout.Label("…还有更多，请输入关键字缩小范围"); break; }
                    if (GUILayout.Button(label))
                    {
                        itemName = _itemNames[i];
                        GiveItem(itemName, ParseAmount());
                    }
                }
                if (shown == 0) GUILayout.Label("没有匹配的物品");
                GUILayout.EndScrollView();
            }

            GUILayout.Space(8);
            if (GUILayout.Button("探测物品生成 API（写入日志）"))
            {
                ProbeItemApi();
            }
            if (!string.IsNullOrEmpty(probeLog))
            {
                scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(120));
                GUILayout.TextArea(probeLog, GUILayout.ExpandHeight(true));
                GUILayout.EndScrollView();
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();

            GUI.skin.label.normal.textColor = origLabelColor;
            GUI.skin.box.normal.textColor = origBoxColor;

            if (closeRequested) ToggleMenu();
        }
    }
}
