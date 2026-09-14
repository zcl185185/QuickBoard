using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using Application = System.Windows.Application;

namespace QuickBoard;

    public partial class MainWindow : Window
    {
        // 第二实例通过这个信号让第一实例把窗口召出来
        public static readonly EventWaitHandle ShowSignal =
            new EventWaitHandle(false, EventResetMode.AutoReset, "QuickBoard.ShowSignal");

    // ---------- 常量 ----------
    const int HOTKEY_ID = 0xB00B;
    const uint WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---------- 路径 ----------
    string AppDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickBoard");
    string DataFile => Path.Combine(AppDir, "data.json");
    string RunKeyPath => @"Software\Microsoft\Windows\CurrentVersion\Run";

    // ---------- 状态 ----------
    string curHotkey = "!q";
    bool onTop = true, hideOnBlur = true, dialogOpen = false, dataReady = false, forceExit = false, started = false;
    IntPtr Hwnd;
    HwndSource src;
    Forms.NotifyIcon tray;
    BookmarkSyncService bmSync;

    JsonObject cfg = new()
    {
        ["hotkey"] = "!q",
        ["onTop"] = true,
        ["blur"] = true,
        ["fold"] = new JsonObject(),
        ["window"] = new JsonObject()
    };

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(AppDir);
        try
        {
            // 从 dist 里的 app-icon.png 设置窗口/任务栏图标（PNG 解码稳定，任务栏实时跟随）
            string pngPath = Path.Combine(AppContext.BaseDirectory, "app-icon.png");
            if (File.Exists(pngPath))
            {
                var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    new Uri(pngPath), System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                if (dec.Frames.Count > 0) Icon = dec.Frames[0];
            }
        }
        catch { }
        LoadCfgIntoWindowBounds();

        SourceInitialized += (s, e) =>
        {
            Hwnd = new WindowInteropHelper(this).Handle;
            src = HwndSource.FromHwnd(Hwnd);
            src.AddHook(WndProc);
            RegisterHot(curHotkey);
        };
        Loaded += async (s, e) => await InitWeb();
        Deactivated += (s, e) => { if (hideOnBlur && IsVisible && !dialogOpen) SavePosAndHide(); };
        Closing += (s, e) =>
        {
            if (forceExit)
            {
                // 托盘菜单「退出」：真正结束程序
                try { SavePos(); tray.Visible = false; TryUnregHotkey(curHotkey); } catch { }
                return;
            }
            // 点 ✕ / Alt+F4：不退出，收起到托盘后台待命
            e.Cancel = true;
            SavePosAndHide();
        };

        // 监听“二次启动”信号：双击桌面图标 = 召出窗口
        System.Threading.Tasks.Task.Run(() =>
        {
            while (ShowSignal.WaitOne())
                Dispatcher.Invoke(ShowAndActivate);
        });
    }

    // ================= WebView2 =================
    async System.Threading.Tasks.Task InitWeb()
    {
        try
        {
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                null, Path.Combine(AppDir, "WebView2"));
            await Web.EnsureCoreWebView2Async(env);
            var wv = Web.CoreWebView2;
            string root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            wv.SetVirtualHostNameToFolderMapping("appassets.local", root,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            wv.WebMessageReceived += OnMessage;

            // 页面里所有网址一律转交系统默认浏览器打开（带用户登录态的正常浏览器），
            // 程序窗口本身永远停留在速查板，绝不内部跳转
            wv.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                SavePosAndHide();     // 先收起速查板，别挡住浏览器
                OpenInBrowser(e.Uri);
            };
            wv.NavigationStarting += (s, e) =>
            {
                var u = e.Uri ?? "";
                if (u.StartsWith("http") && !u.Contains("appassets.local"))
                {
                    e.Cancel = true;
                    SavePosAndHide();
                    OpenInBrowser(u);
                }
            };
            wv.NavigationCompleted += (s, e) =>
            {
                if (!started)
                {
                    started = true;
                    InitTray();      // 页面加载完成后创建托盘图标
                    SyncCfgToPage(); // 把当前配置同步给页面
                }
            };
            Web.Source = new Uri("https://appassets.local/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show("初始化失败：" + ex.Message, "速查板", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
        }
    }

    void PostToPage(string json)
    {
        if (Web?.CoreWebView2 != null)
            Dispatcher.Invoke(() => Web.CoreWebView2.PostWebMessageAsJson(json));
    }

    // ================= 页面消息 =================
    void OpenInBrowser(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    void OnMessage(object sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            // 页面 postMessage 的是字符串：WebMessageAsJson 会再包一层引号，需先解一层
            string raw = e.WebMessageAsJson;
            if (raw.StartsWith("\""))
                raw = JsonSerializer.Deserialize<string>(raw);
            var msg = JsonNode.Parse(raw) as JsonObject;
            string type = msg?["type"]?.ToString();
            switch (type)
            {
                case "load": SendInit(); break;
                case "save": SaveData(msg["payload"] as JsonObject); break;
                case "hide": SavePosAndHide(); break;
                case "export": DoExport(); break;
                case "import-request": DoImport(); break;
                case "sync-now": EnsureBmSync(); _ = bmSync.SyncNowAsync(true); break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("msg error: " + ex.Message);
        }
    }

    void SendInit()
    {
        JsonObject root = ReadData();
        var init = new JsonObject
        {
            ["type"] = "init",
            ["data"] = root,   // 为 null 时页面使用默认示例
        };
        PostToPage(init.ToJsonString());
        if (root != null)
        {
            if (root["cfg"]?["hotkey"] is JsonNode hk) { string s = hk.ToString(); if (s != curHotkey) TryApplyHotkey(s); }
            if (root["cfg"]?["onTop"] is JsonNode ot) onTop = AsBool(ot);
            if (root["cfg"]?["blur"] is JsonNode bl) hideOnBlur = AsBool(bl);
            ApplyOnTop(); UpdateTrayChecks(); SyncCfgToPage();
        }
        dataReady = true;
        EnsureBmSync();   // 页面数据就绪后再启动书签同步（data.json 首启时由页面创建）
    }

    void EnsureBmSync()
    {
        if (bmSync != null) return;
        bmSync = new BookmarkSyncService(DataFile, Dispatcher, PostToPage);
        bmSync.Start();
    }

    // JSON 里 true / 1 都当作“开”
    static bool AsBool(JsonNode n)
    {
        if (n == null) return false;
        if (n is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<int>(out var i)) return i != 0;
        }
        return false;
    }

    JsonObject ReadData()
    {
        if (!File.Exists(DataFile)) return null;
        try { return JsonNode.Parse(File.ReadAllText(DataFile)) as JsonObject; }
        catch { return null; }
    }

    void SaveData(JsonObject payload)
    {
        if (payload == null) return;
        var root = ReadData() ?? new JsonObject();
        var prevCfg = root["cfg"] as JsonObject;
        root["state"] = payload["state"]?.DeepClone() ?? new JsonObject();
        root["cfg"] = payload["cfg"]?.DeepClone() ?? (prevCfg?.DeepClone() ?? cfg.DeepClone());
        // 页面拿到的还是旧数据时（同步刚发生、data-updated 尚未送达），别把同步元数据丢掉
        if (root["cfg"]?["bmSync"] == null && prevCfg?["bmSync"] != null)
            root["cfg"]["bmSync"] = prevCfg["bmSync"].DeepClone();
        WriteData(root);
        if (root["cfg"] is JsonObject c) SyncCfg(c);
    }

    void WriteData(JsonObject root)
    {
        string tmp = DataFile + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, DataFile, true);
    }

    // 把 C# 侧 cfg 同步为文件里的最新值，并应用到窗口行为
    void SyncCfg(JsonObject c)
    {
        if (c["hotkey"] is JsonNode hk)
        {
            string s = hk.ToString();
            if (s != curHotkey && TryApplyHotkey(s))
                TrayTip("快捷键已更新：" + PrettyHotkey(curHotkey));
            else if (s != curHotkey)
            {
                // 注册失败：回写真实值并通知页面
                c["hotkey"] = curHotkey;
                SyncCfgToPage();
            }
        }
        if (c["onTop"] is JsonNode ot) { onTop = AsBool(ot); ApplyOnTop(); }
        if (c["blur"] is JsonNode bl) hideOnBlur = AsBool(bl);
        // 书签同步开关（缺省视为开启）
        if (c["bmSync"] is JsonObject b) bmSync?.ApplyEnabled(b["enabled"] == null || AsBool(b["enabled"]));
        UpdateTrayChecks();
    }

    void SyncCfgToPage()
    {
        PostToPage(new JsonObject
        {
            ["type"] = "cfg-sync",
            ["cfg"] = new JsonObject
            {
                ["hotkey"] = curHotkey,
                ["onTop"] = onTop,
                ["blur"] = hideOnBlur,
            }
        }.ToJsonString());
    }

    // ================= 数据读写 =================
    void SavePos()
    {
        try
        {
            var root = ReadData() ?? new JsonObject();
            if (root["cfg"] is not JsonObject c) { c = new JsonObject(); root["cfg"] = c; }
            c["window"] = new JsonObject
            {
                ["x"] = (int)Left, ["y"] = (int)Top, ["w"] = (int)Width, ["h"] = (int)Height
            };
            WriteData(root);
        }
        catch { }
    }

    void LoadCfgIntoWindowBounds()
    {
        try
        {
            var root = ReadData();
            var w = root?["cfg"]?["window"];
            if (w != null)
            {
                Left = w["x"]?.GetValue<int>() ?? 80;
                Top = w["y"]?.GetValue<int>() ?? 60;
                Width = w["w"]?.GetValue<int>() ?? 1200;
                Height = w["h"]?.GetValue<int>() ?? 680;
            }
        }
        catch { }
    }

    void SavePosAndHide() { SavePos(); Hide(); }

    void ToggleBoard()
    {
        if (IsVisible) SavePosAndHide();
        else
        {
            Topmost = onTop;
            Show();
            Activate();
        }
    }

    void ApplyOnTop() { if (IsVisible) Topmost = onTop; }

    // ================= 全局热键 =================
    void RegisterHot(string hk) { if (TryParseHotkey(hk, out var mods, out var vk)) RegisterHotKey(Hwnd, HOTKEY_ID, mods, vk); }
    void TryUnregHotkey(string hk) { try { UnregisterHotKey(Hwnd, HOTKEY_ID); } catch { } }

    bool TryApplyHotkey(string hk)
    {
        if (string.IsNullOrWhiteSpace(hk) || hk == curHotkey) return hk == curHotkey;
        if (!TryParseHotkey(hk, out var mods, out var vk)) return false;
        TryUnregHotkey(curHotkey);
        if (RegisterHotKey(Hwnd, HOTKEY_ID, mods, vk)) { curHotkey = hk; AIconTip(); return true; }
        RegisterHot(curHotkey);   // 注册回旧键
        return false;
    }

    void AIconTip() { if (tray != null) tray.Text = "速查板 — 按 " + PrettyHotkey(curHotkey) + " 呼出"; }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID) { ToggleBoard(); handled = true; }
        return IntPtr.Zero;
    }

    static string PrettyHotkey(string hk)
    {
        var map = new System.Collections.Generic.Dictionary<char, string>
        {
            ['^'] = "Ctrl+", ['!'] = "Alt+", ['+'] = "Shift+", ['#'] = "Win+"
        };
        var outp = new System.Text.StringBuilder();
        foreach (var ch in hk)
            outp.Append(map.TryGetValue(ch, out var v) ? v : (ch == 'S' && hk.EndsWith("Space") ? "" : ch.ToString()));
        var r = outp.ToString();
        if (r.EndsWith("Space")) r = r.Substring(0, r.Length - 5) + "空格";
        return r;
    }

    static bool TryParseHotkey(string s, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '^') mods |= MOD_CONTROL;
            else if (c == '!') mods |= MOD_ALT;
            else if (c == '+') mods |= MOD_SHIFT;
            else if (c == '#') mods |= MOD_WIN;
            else break;
            i++;
        }
        string key = s.Substring(i).Trim();
        if (key.Length == 0 || mods == 0) return false;
        vk = VkOf(key);
        return vk != 0;
    }

    static uint VkOf(string k)
    {
        k = k.ToUpperInvariant();
        if (k.Length == 1)
        {
            char c = k[0];
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return c;
        }
        if (k.Length is 2 or 3 && k[0] == 'F' && int.TryParse(k[1..], out int fn) && fn is >= 1 and <= 24)
            return (uint)(0x70 + fn - 1);
        return k switch
        {
            "SPACE" => 0x20, "ENTER" => 0x0D, "TAB" => 0x09, "BACKSPACE" => 0x08, "ESC" => 0x1B,
            "UP" => 0x26, "DOWN" => 0x28, "LEFT" => 0x25, "RIGHT" => 0x27,
            "HOME" => 0x24, "END" => 0x23, "PGUP" => 0x21, "PGDN" => 0x22,
            "INS" => 0x2D, "DEL" => 0x2E,
            ";" => 0xBA, "=" => 0xBB, "," => 0xBC, "-" => 0xBD, "." => 0xBE, "/" => 0xBF,
            "`" => 0xC0, "[" => 0xDB, "\\" => 0xDC, "]" => 0xDD, "'" => 0xDE,
            _ => 0
        };
    }

    // ================= 托盘 =================
    void InitTray()
    {
        try
        {
            if (tray != null) return;

            // 图标三重兜底：dist 里的 app.ico → 从 exe 提取 → 系统默认图标
            Drawing.Icon ico = null;
            try
            {
                string icoPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
                if (File.Exists(icoPath)) ico = new Drawing.Icon(icoPath);
            }
            catch { }
            if (ico == null)
            {
                try { ico = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }
            }
            if (ico == null) ico = Drawing.SystemIcons.Application;

            tray = new Forms.NotifyIcon
            {
                Icon = ico,
                Text = "速查板",
                Visible = true
            };
            tray.ShowBalloonTip(2500, "速查板已在托盘运行", "按 " + PrettyHotkey(curHotkey) + " 呼出窗口；点 ✕ 只会收到托盘，不会退出。", Forms.ToolTipIcon.None);
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("呼出 / 隐藏", null, (s, e) => ToggleBoard());
            menu.Items.Add(new Forms.ToolStripSeparator());
            var miSet = menu.Items.Add("设置快捷键 / 设置面板");
            miSet.Click += (s, e) => { ShowAndActivate(); PostToPage("{\"type\":\"open-settings\"}"); };
            var miAuto = new Forms.ToolStripMenuItem("开机自启") { Checked = File.Exists(AutostartLnk()) };
            miAuto.Click += (s, e) => ToggleAutostart();
            menu.Items.Add(miAuto);
            var miTop = new Forms.ToolStripMenuItem("窗口置顶") { Checked = onTop };
            miTop.Click += (s, e) => { onTop = !onTop; ApplyOnTop(); PersistFlag("onTop", onTop); UpdateTrayChecks(); };
            menu.Items.Add(miTop);
            var miBlur = new Forms.ToolStripMenuItem("失焦自动隐藏") { Checked = hideOnBlur };
            miBlur.Click += (s, e) => { hideOnBlur = !hideOnBlur; PersistFlag("blur", hideOnBlur); UpdateTrayChecks(); };
            menu.Items.Add(miBlur);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => { forceExit = true; Close(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => ToggleBoard();
            AIconTip();
            File.WriteAllText(Path.Combine(AppDir, "tray-log.txt"), DateTime.Now + " 托盘图标创建成功");
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(AppDir, "tray-log.txt"), DateTime.Now + " 托盘创建失败: " + ex.Message); } catch { }
        }
    }

    string AutostartLnk() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "速查板.lnk");

    void ToggleAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key.GetValue("QuickBoard") != null)
            {
                key.DeleteValue("QuickBoard");
                TrayTip("已取消开机自启");
            }
            else
            {
                key.SetValue("QuickBoard", "\"" + Environment.ProcessPath + "\"");
                TrayTip("已开启开机自启");
            }
            UpdateTrayChecks();
        }
        catch { TrayTip("设置失败"); }

        void UpdateTrayChecks()
        {
            if (tray?.ContextMenuStrip == null) return;
            foreach (Forms.ToolStripItem it in tray.ContextMenuStrip.Items)
                if (it is Forms.ToolStripMenuItem m && m.Text == "开机自启")
                    m.Checked = Registry.CurrentUser.OpenSubKey(RunKeyPath)?.GetValue("QuickBoard") != null;
        }
    }

    void UpdateTrayChecks()
    {
        if (tray?.ContextMenuStrip == null) return;
        foreach (Forms.ToolStripItem it in tray.ContextMenuStrip.Items)
        {
            if (it is not Forms.ToolStripMenuItem m) continue;
            if (m.Text == "窗口置顶") m.Checked = onTop;
            if (m.Text == "失焦自动隐藏") m.Checked = hideOnBlur;
            if (m.Text == "开机自启")
                m.Checked = Registry.CurrentUser.OpenSubKey(RunKeyPath)?.GetValue("QuickBoard") != null;
        }
    }

    void TrayTip(string text) { tray?.ShowBalloonTip(1200, "速查板", text, Forms.ToolTipIcon.None); }

    void ShowAndActivate()
    {
        Show();
        if (onTop) Topmost = true;
        Activate();
    }

    void PersistFlag(string name, bool v)
    {
        try
        {
            var root = ReadData() ?? new JsonObject();
            if (root["cfg"] is not JsonObject c) { c = new JsonObject(); root["cfg"] = c; }
            c[name] = v;
            WriteData(root);
            SyncCfgToPage();
        }
        catch { }
    }

    // ================= 导出 / 导入 =================
    void DoExport()
    {
        dialogOpen = true;
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出速查板备份",
                FileName = "速查板备份-" + DateTime.Now.ToString("yyyy-MM-dd") + ".json",
                Filter = "JSON 备份文件|*.json"
            };
            if (dlg.ShowDialog(this) == true)
            {
                var root = ReadData() ?? new JsonObject();
                File.WriteAllText(dlg.FileName, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                PostToPage("{\"type\":\"exported\",\"ok\":true}");
            }
            else PostToPage("{\"type\":\"exported\",\"ok\":false}");
        }
        catch (Exception ex) { MessageBox.Show(this, "导出失败：" + ex.Message); }
        finally { dialogOpen = false; }
    }

    void DoImport()
    {
        dialogOpen = true;
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "导入速查板备份",
                Filter = "JSON 备份文件|*.json"
            };
            if (dlg.ShowDialog(this) == true)
            {
                // 只负责读文件，格式解析交给页面（兼容 v1/v2/v3 三代备份格式）
                string text = File.ReadAllText(dlg.FileName);
                PostToPage(new JsonObject { ["type"] = "import-file", ["text"] = text }.ToJsonString());
            }
        }
        catch (Exception ex) { MessageBox.Show(this, "读取失败：" + ex.Message); }
        finally { dialogOpen = false; }
    }
}
