using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Windows.Threading;

namespace QuickBoard;

// 浏览器书签同步引擎：监视 Edge / Chrome 的本地书签文件（Bookmarks，JSON 格式），
// 变化后解析书签树并按 URL 合并进 data.json 的「常用网址」栏（增 / 改 / 删），
// 再把最新数据全量推给页面刷新。
// 合并固定在 UI 线程执行，与页面 save 走同一条消息队列，天然串行，不会并发写坏数据文件。
public class BookmarkSyncService
{
    readonly string dataFile;
    readonly Dispatcher ui;
    readonly Action<string> postToPage;

    readonly List<FileSystemWatcher> watchers = new();
    System.Threading.Timer debounce;   // 书签保存时浏览器会连续写多次盘，防抖后统一同步一次
    int initialTries;                  // 首次启动时 data.json 可能还没被页面创建，允许重试几次

    const int DebounceMs = 1500;

    public BookmarkSyncService(string dataFile, Dispatcher ui, Action<string> postToPage)
    {
        this.dataFile = dataFile;
        this.ui = ui;
        this.postToPage = postToPage;
    }

    class BmItem { public string Title, Folder, Url; }

    // ================= 生命周期 =================
    public void Start()
    {
        Log("Start: enabled=" + Enabled(ReadData()));
        if (Enabled(ReadData())) StartWatching();
        lock (watchers) Log("watching files: " + watchers.Count);
        Schedule(2000);   // 启动后先做一次初始同步，补上程序没运行期间的书签变化
    }

    // 设置面板开关：关 → 停止监听；开 → 恢复监听并立即补一次同步
    public void ApplyEnabled(bool on)
    {
        if (on) { StartWatching(); Schedule(500); }
        else
        {
            lock (watchers)
            {
                foreach (var w in watchers) { try { w.Dispose(); } catch { } }
                watchers.Clear();
            }
            debounce?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    // ================= 监听 =================
    void StartWatching()
    {
        lock (watchers)
        {
            var known = new HashSet<string>(watchers.Select(w => w.Path));
            foreach (var file in LocateBookmarkFiles())
            {
                string dir = Path.GetDirectoryName(file);
                if (dir == null || known.Contains(dir)) continue;
                try
                {
                    var fsw = new FileSystemWatcher(dir, "Bookmarks")
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                        EnableRaisingEvents = true
                    };
                    fsw.Changed += (s, e) => Schedule(DebounceMs);
                    fsw.Created += (s, e) => Schedule(DebounceMs);
                    fsw.Renamed += (s, e) => Schedule(DebounceMs);
                    // 内部缓冲区溢出等异常：丢弃重建，保证监听不静默失效
                    fsw.Error += (s, e) =>
                    {
                        lock (watchers) { try { fsw.Dispose(); watchers.Remove(fsw); } catch { } }
                        StartWatching();
                    };
                    watchers.Add(fsw);
                }
                catch { }
            }
        }
    }

    void Schedule(int dueMs)
    {
        if (debounce == null)
            debounce = new System.Threading.Timer(
                _ => ui.BeginInvoke(async () => await SyncNowAsync(false)),
                null, dueMs, Timeout.Infinite);
        else
            debounce.Change(dueMs, Timeout.Infinite);
    }

    // ================= 同步 =================
    // force=false：由书签文件变化触发，尊重设置里的开关；force=true：设置面板「立即同步」
    public async System.Threading.Tasks.Task SyncNowAsync(bool force)
    {
        try
        {
            var root = ReadData();
            if (root?["state"]?["cards"] is not JsonArray)
            {
                Log("sync: data not ready, tries=" + initialTries);
                if (++initialTries <= 5) Schedule(3000);   // 等页面首启把数据文件建好
                return;
            }
            bool includeOther = AsBool(root["cfg"]?["bmSync"]?["includeOther"]);
            if (!force && !Enabled(root)) { Log("sync: disabled, skip"); return; }

            List<BmItem> items = null;
            for (int attempt = 0; attempt < 3 && items == null; attempt++)
            {
                try { items = ReadBrowserBookmarks(includeOther); }
                catch (Exception ex) { Log("read fail: " + ex.Message); if (attempt < 2) await System.Threading.Tasks.Task.Delay(600); }   // 浏览器正在写盘，稍后重读
            }
            if (items == null) { Log("sync: give up after retries"); return; }   // 多次都读到半截文件，等下一次变化事件

            Merge(root, items);
        }
        catch (Exception ex) { Log("sync error: " + ex); }
    }

    List<BmItem> ReadBrowserBookmarks(bool includeOther)
    {
        var list = new List<BmItem>();
        var seen = new HashSet<string>();
        foreach (var file in LocateBookmarkFiles())
        {
            var roots = JsonNode.Parse(File.ReadAllText(file))?["roots"] as JsonObject;
            CollectChildren(roots?["bookmark_bar"], "", list, seen);   // 根（书签栏）本身不算分组，其下的直接书签不分组
            if (includeOther) CollectChildren(roots?["other"], "", list, seen);
        }
        return list;
    }

    // 展开同步根的直接子节点（不把根文件夹名计入分组路径）
    void CollectChildren(JsonNode rootFolder, string path, List<BmItem> list, HashSet<string> seen)
    {
        if (rootFolder is JsonObject f && f["children"] is JsonArray kids)
            foreach (var k in kids) Collect(k, path, list, seen);
    }

    // 递归展开书签树：子文件夹路径用 / 连接作为分组名，直接放在同步根下的不分组
    void Collect(JsonNode node, string path, List<BmItem> list, HashSet<string> seen)
    {
        if (node is not JsonObject o) return;
        string type = o["type"]?.ToString();
        if (type == "url")
        {
            string url = (o["url"]?.ToString() ?? "").Trim();
            if (!url.StartsWith("http://") && !url.StartsWith("https://")) return;
            if (!seen.Add(url)) return;
            string title = (o["name"]?.ToString() ?? "").Trim();
            list.Add(new BmItem { Title = title.Length > 0 ? title : url, Folder = path, Url = url });
        }
        else if (type == "folder")
        {
            string name = o["name"]?.ToString() ?? "";
            string sub = path.Length == 0 ? name : path + "/" + name;
            if (o["children"] is JsonArray kids)
                foreach (var k in kids) Collect(k, sub, list, seen);
        }
    }

    void Merge(JsonObject root, List<BmItem> items)
    {
        Log("merge: items=" + items.Count);
        var cards = root["state"]?["cards"] as JsonArray;
        var cols = root["state"]?["cols"] as JsonArray;
        if (cards == null || cols == null) return;

        // 「常用网址」栏被删过则重建，保证同步有落点
        if (!cols.Any(n => n is JsonObject c && c["id"]?.ToString() == "url"))
            cols.Add(new JsonObject { ["id"] = "url", ["name"] = "常用网址" });

        var cfg = root["cfg"] as JsonObject ?? new JsonObject();
        var bm = cfg["bmSync"] as JsonObject ?? new JsonObject();
        var oldTracked = new HashSet<string>();
        if (bm["urls"] is JsonArray ua)
            foreach (var u in ua) oldTracked.Add(u?.ToString() ?? "");
        oldTracked.Remove("");

        var urlSet = new HashSet<string>(items.Select(i => i.Url));
        var byUrl = new Dictionary<string, JsonObject>();
        foreach (var c in cards.OfType<JsonObject>())
            if (c["colId"]?.ToString() == "url")
                byUrl.TryAdd((c["body"]?.ToString() ?? "").Trim(), c);

        int added = 0, updated = 0, removed = 0;
        var tracked = new HashSet<string>();

        foreach (var it in items)
        {
            if (byUrl.TryGetValue(it.Url, out var card))
            {
                if (!oldTracked.Contains(it.Url)) continue;   // 手动建的卡：不动、也不纳入跟踪
                bool changed = false;
                if ((card["title"]?.ToString() ?? "") != it.Title) { card["title"] = it.Title; changed = true; }
                if ((card["folder"]?.ToString() ?? "") != it.Folder) { card["folder"] = it.Folder; changed = true; }
                if (changed) updated++;
            }
            else
            {
                card = new JsonObject
                {
                    ["id"] = Guid.NewGuid().ToString(),
                    ["colId"] = "url",
                    ["folder"] = it.Folder,
                    ["title"] = it.Title,
                    ["body"] = it.Url
                };
                cards.Add(card);
                byUrl[it.Url] = card;
                added++;
            }
            tracked.Add(it.Url);
        }

        // 浏览器里已删除的跟踪网址 → 一并移除对应卡片
        foreach (var u in oldTracked)
        {
            if (urlSet.Contains(u)) { tracked.Add(u); continue; }
            for (int i = cards.Count - 1; i >= 0; i--)
                if (cards[i] is JsonObject c && c["colId"]?.ToString() == "url"
                    && (c["body"]?.ToString() ?? "").Trim() == u)
                { cards.RemoveAt(i); removed++; }
        }

        var arr = new JsonArray();
        foreach (var u in tracked) arr.Add(u);
        bm["urls"] = arr;
        bm["lastSync"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        bm["count"] = tracked.Count;
        cfg["bmSync"] = bm;
        root["cfg"] = cfg;

        WriteData(root);
        Log("merge done: +" + added + " ~" + updated + " -" + removed + ", tracked=" + tracked.Count);
        if (added + updated + removed > 0)
            postToPage(new JsonObject
            {
                ["type"] = "data-updated",
                ["data"] = root,
                ["summary"] = "书签已同步：新增" + added + "，更新" + updated + "，删除" + removed
            }.ToJsonString());
        else
            postToPage(new JsonObject
            {
                ["type"] = "bm-status",
                ["lastSync"] = bm["lastSync"]?.ToString(),
                ["count"] = tracked.Count
            }.ToJsonString());
    }

    // ================= 书签文件定位 =================
    IEnumerable<string> LocateBookmarkFiles()
    {
        string lac = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] userDataRoots =
        {
            Path.Combine(lac, "Microsoft", "Edge", "User Data"),
            Path.Combine(lac, "Google", "Chrome", "User Data")
        };
        foreach (var ud in userDataRoots)
        {
            if (!Directory.Exists(ud)) continue;
            string[] dirs = Array.Empty<string>();
            try { dirs = Directory.GetDirectories(ud); } catch { }
            foreach (var d in dirs)
            {
                string name = Path.GetFileName(d);
                if (name is "System Profile" or "Guest Profile" or "Crashpad") continue;
                string bf = Path.Combine(d, "Bookmarks");
                if (File.Exists(bf)) yield return bf;
            }
        }
    }

    // ================= 工具 =================
    static bool Enabled(JsonObject root)
    {
        var b = root?["cfg"]?["bmSync"] as JsonObject;
        return b == null || b["enabled"] == null || AsBool(b["enabled"]);   // 缺省视为开启
    }

    // JSON 里 true / 1 都当作「开」
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
        try
        {
            if (!File.Exists(dataFile)) return null;
            return JsonNode.Parse(File.ReadAllText(dataFile)) as JsonObject;
        }
        catch { return null; }
    }

    // 调试日志：与 tray-log.txt 同目录，便于排查同步链路问题
    static void Log(string msg)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickBoard");
            File.AppendAllText(Path.Combine(dir, "bm-log.txt"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine);
        }
        catch { }
    }

    void WriteData(JsonObject root)
    {
        // TypeInfoResolver 必须显式指定：树里经隐式转换生成的 JsonValue 节点序列化时需要它
        var opts = new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        string tmp = dataFile + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(opts));
        File.Move(tmp, dataFile, true);
    }
}
