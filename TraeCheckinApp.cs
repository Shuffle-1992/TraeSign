using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TraeSign
{
    // ============ 入口 ============
    internal static class Program
    {
        private const string MutexName = "Local\\TraeCheckin_6aa8f93d";
        private const string ShowEventName = "TraeCheckin_ShowWindow";

        [STAThread]
        private static int Main(string[] args)
        {
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length > 0 && (args[0] == "--selftest" || args[0] == "-t"))
                return SelfTest.Run();

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    try { EventWaitHandle.OpenExisting(ShowEventName).Set(); }
                    catch { }
                    return 0;
                }
                using (var showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                {
                    var sync = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        while (true)
                        {
                            if (!showEvt.WaitOne(60000)) continue;
                            try { sync.Post(delegate { TrayApp.ShowMainWindow(); }, null); }
                            catch { }
                        }
                    });
                    Application.Run(new TrayApp());
                }
            }
            return 0;
        }
    }

    // ============ 账号信息 ============
    internal class AccountInfo
    {
        public string Brand;
        public string Username;
        public string ExpiredAt;
        public bool HasToken;

        public string Display
        {
            get { return (string.IsNullOrEmpty(Username) ? "(未知账号)" : Username) + "（" + Brand + "）"; }
        }

        public override string ToString() { return Display; }
    }

    // ============ 运行日志（诊断用，超 1MB 轮转保留一份旧文件） ============
    internal static class RuntimeLog
    {
        private static readonly object Gate = new object();
        public static string LogPath { get { return Path.Combine(HistoryStore.DataDir, "runtime.log"); } }

        public static void Write(string line)
        {
            try
            {
                lock (Gate)
                {
                    if (!Directory.Exists(HistoryStore.DataDir)) Directory.CreateDirectory(HistoryStore.DataDir);
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024)
                        File.Replace(LogPath, LogPath + ".old", null);
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("MM-dd HH:mm:ss") + " " + line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }

    // ============ 平台槽位（Trae / WorkBuddy / ZCode 各一份） ============
    internal class PlatformSlot
    {
        public string Platform;
        public List<AccountInfo> Accounts = new List<AccountInfo>();
        public string ActiveBrand;
        public DailyScheduler Scheduler;
        public CheckinResult LastResult;
        public bool Busy;

        public AccountInfo ActiveAccount()
        {
            foreach (var a in Accounts)
                if (a.Brand == ActiveBrand) return a;
            return Accounts.Count > 0 ? Accounts[0] : null;
        }
    }

    // ============ 托盘应用上下文（三平台） ============
    internal class TrayApp : ApplicationContext
    {
        private NotifyIcon _tray;
        private MainForm _form;
        private static TrayApp _instance;

        private readonly List<PlatformSlot> _slots = new List<PlatformSlot>();
        private PlatformSlot _trae;
        private PlatformSlot _wb;
        private PlatformSlot _zc;

        // ZCode 活动套餐轮询（区别于每日签到：套餐随机投放，周期检查、发现即领）
        private System.Windows.Forms.Timer _zcodeTimer;
        private DateTime _zcodeNextCheckAt;
        internal const int ZcodePollMinutes = 10;

        public TrayApp()
        {
            _instance = this;
            _form = null;

            _trae = new PlatformSlot();
            _trae.Platform = "Trae";
            _wb = new PlatformSlot();
            _wb.Platform = "WorkBuddy";
            _zc = new PlatformSlot();
            _zc.Platform = "ZCode";   // 活动套餐模型，无每日调度器
            _slots.Add(_trae);
            _slots.Add(_wb);
            _slots.Add(_zc);

            _tray = new NotifyIcon();
            _tray.Visible = true;
            _tray.Icon = IconFactory.Create(TrayState.Unknown);
            _tray.Text = "TraeSign - 加载中...";
            _tray.DoubleClick += delegate { ShowMainWindow(); };

            var menu = new ContextMenuStrip();
            menu.Items.Add("立即签到（全部平台）", null, delegate
            {
                RunCheckinAsync(false, "Trae", null);
                RunCheckinAsync(false, "WorkBuddy", null);
                RunCheckinAsync(false, "ZCode", null);
            });
            menu.Items.Add("打开主窗口", null, delegate { ShowMainWindow(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitApp(); });
            _tray.ContextMenuStrip = menu;

            foreach (var s in _slots)
            {
                if (s.Platform == "ZCode") continue;
                s.Scheduler = new DailyScheduler(this, s.Platform);
                s.Scheduler.Start();
            }
            StartZcodePoller();

            // 加载三平台账号列表（后台），完成后刷新状态
            LoadAccountsAsync();
        }

        // ZCode 轮询器：启动后 15 秒首查（避开启动批量查询高峰），此后每 10 分钟
        private void StartZcodePoller()
        {
            _zcodeNextCheckAt = DateTime.Now.AddSeconds(15);
            _zcodeTimer = new System.Windows.Forms.Timer();
            _zcodeTimer.Interval = 30000;
            _zcodeTimer.Tick += delegate
            {
                if (DateTime.Now < _zcodeNextCheckAt) return;
                _zcodeNextCheckAt = DateTime.Now.AddMinutes(ZcodePollMinutes);
                var slot = Slot("ZCode");
                if (slot == null || slot.Busy) return;
                RunCheckinAsync(true, "ZCode", null);
            };
            _zcodeTimer.Start();
        }

        // 按 ZCode 结果差异化安排下次检查时间
        private void ScheduleZcodeNext(CheckinResult r)
        {
            if (r == null) return;
            if (r.Code == 1005 && r.RetryAfterAt > 0)
                _zcodeNextCheckAt = DateTimeOffset.FromUnixTimeSeconds(r.RetryAfterAt).LocalDateTime.AddMinutes(1);
            else if (r.Code == 1005)
                _zcodeNextCheckAt = DateTime.Now.AddMinutes(30);
            else if (!r.Success && !r.Already && r.Code != 0 && r.Code != 1003)
                _zcodeNextCheckAt = DateTime.Now.AddMinutes(5);
            else
                _zcodeNextCheckAt = DateTime.Now.AddMinutes(ZcodePollMinutes);
        }

        public NotifyIcon Tray { get { return _tray; } }
        public static TrayApp Instance { get { return _instance; } }
        public IList<PlatformSlot> Slots { get { return _slots; } }

        public PlatformSlot Slot(string platform)
        {
            foreach (var s in _slots)
                if (s.Platform == platform) return s;
            return null;
        }

        public static void ShowMainWindow()
        {
            if (_instance == null) return;
            if (_instance._form == null || _instance._form.IsDisposed)
                _instance._form = new MainForm(_instance);
            _instance._form.RefreshAccounts();
            _instance._form.RefreshView();
            _instance._form.Show();
            _instance._form.Activate();
        }

        public void SetTray(TrayState state, string text)
        {
            if (_tray == null) return;
            var old = _tray.Icon;
            _tray.Icon = IconFactory.Create(state);
            _tray.Text = text;
            if (old != null) { old.Dispose(); }
        }

        // 单平台信号灯：绿=已完成（已签到/已领取/无活动）、红=需处理（未找到登录态/失败/待授权）、灰=进行中
        public static readonly Color LightGreen = Color.FromArgb(76, 175, 80);
        public static readonly Color LightRed = Color.FromArgb(244, 67, 54);
        public static readonly Color LightGray = Color.FromArgb(158, 158, 158);

        public static void GetSlotLight(PlatformSlot s, out Color color, out string state)
        {
            color = LightGray;
            state = "待查询";
            if (s == null) return;
            if (s.Accounts.Count == 0)
            {
                color = LightRed;
                state = s.Platform == "WorkBuddy" ? "待授权" : "未找到登录态";
                return;
            }
            if (s.Busy)
            {
                state = s.Platform == "ZCode" ? "检查中" : "签到中";
                return;
            }
            var r = s.LastResult;
            if (r != null && (r.Success || r.CheckedIn))
            {
                color = LightGreen;
                state = s.Platform == "ZCode" ? "已领取" : "已签到";
                return;
            }
            if (r != null && r.Already)
            {
                color = LightGreen;
                state = s.Platform == "ZCode" ? "无活动" : "已签到";
                return;
            }
            if (r != null && r.HasClaimable)
            {
                state = "发现可领套餐";
                return;
            }
            if (r != null && r.Code == -1 && s.Platform == "WorkBuddy")
            {
                color = LightRed;
                state = "待授权";
                return;
            }
            if (r != null)
            {
                color = LightRed;
                state = s.Platform == "ZCode" ? "检查异常" : "未签到";
            }
        }

        // 聚合托盘状态：任一平台红灯→红；全部绿灯→绿；否则灰
        public TrayState ComputeAggregateState(out string tip)
        {
            bool allOk = true;
            bool anyFail = false;
            var parts = new List<string>();
            foreach (var s in _slots)
            {
                Color c;
                string state;
                GetSlotLight(s, out c, out state);
                parts.Add(s.Platform + ":" + state);
                if (c == LightRed) anyFail = true;
                if (c != LightGreen) allOk = false;
            }
            tip = "TraeSign - " + string.Join(" | ", parts.ToArray());
            if (anyFail) return TrayState.Failed;
            if (allOk) return TrayState.CheckedIn;
            return TrayState.Unknown;
        }

        public void UpdateTray()
        {
            string tip;
            TrayState st = ComputeAggregateState(out tip);
            SetTray(st, tip);
        }

        public void RefreshView()
        {
            if (_form != null && !_form.IsDisposed) _form.RefreshView();
        }

        // 后台加载三平台账号列表
        public void LoadAccountsAsync()
        {
            Task.Run(delegate { return LoadBothLists(); })
                .ContinueWith(t =>
                {
                    TwoLists both;
                    try { both = t.Result; }
                    catch { both = null; }
                    if (both != null) OnAccountsLoaded(both.Trae, both.Wb, both.Zc);
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static TwoLists LoadBothLists()
        {
            var r = new TwoLists();
            r.Trae = CheckinRunner.ListAccounts();
            r.Wb = WorkbuddyAuth.ListAccounts();
            r.Zc = ZcodeAuth.ListAccounts();
            return r;
        }

        // 手动刷新：重新拉取三平台账号列表 + 当前账号状态
        public void RefreshAccountsAndStatus()
        {
            LoadAccountsAsync();
        }

        private void OnAccountsLoaded(List<AccountInfo> trae, List<AccountInfo> wb, List<AccountInfo> zc)
        {
            _trae.Accounts = trae ?? new List<AccountInfo>();
            string saved = HistoryStore.GetActiveBrand();
            if (!string.IsNullOrEmpty(saved) && HasBrand(_trae, saved))
                _trae.ActiveBrand = saved;
            else if (_trae.Accounts.Count > 0)
                _trae.ActiveBrand = _trae.Accounts[0].Brand;
            else
                _trae.ActiveBrand = null;
            if (_trae.ActiveBrand != null) HistoryStore.SetActiveBrand(_trae.ActiveBrand);

            _wb.Accounts = wb ?? new List<AccountInfo>();
            _wb.ActiveBrand = _wb.Accounts.Count > 0 ? WorkbuddyAuth.Platform : null;

            _zc.Accounts = zc ?? new List<AccountInfo>();
            _zc.ActiveBrand = _zc.Accounts.Count > 0 ? ZcodeAuth.Platform : null;

            if (_form != null && !_form.IsDisposed)
            {
                _form.RefreshAccounts();
                // 账号装载后立即刷新状态区，不等查询返回（避免"有账号但显示未找到"的中间态）
                _form.RefreshView();
            }

            if (_trae.Accounts.Count == 0 && _wb.Accounts.Count == 0 && _zc.Accounts.Count == 0)
            {
                UpdateTray();
                return;
            }
            if (_trae.ActiveBrand != null) _trae.Scheduler.ResetForBrand(_trae.ActiveBrand);
            if (_wb.ActiveBrand != null) _wb.Scheduler.ResetForBrand(_wb.ActiveBrand);
            // 三平台并行查询状态（只查询，不签到；ZCode 发现可领套餐会触发自动领取）
            RunCheckinAsync(true, "Trae", null);
            RunCheckinAsync(true, "WorkBuddy", null);
            RunCheckinAsync(true, "ZCode", null);
        }

        private static bool HasBrand(PlatformSlot slot, string brand)
        {
            foreach (var a in slot.Accounts)
                if (a.Brand == brand) return true;
            return false;
        }

        public void SetActiveBrand(string platform, string brand)
        {
            var slot = Slot(platform);
            if (slot == null || brand == slot.ActiveBrand) return;
            if (!HasBrand(slot, brand)) return;
            slot.ActiveBrand = brand;
            if (platform == "Trae") HistoryStore.SetActiveBrand(brand);
            slot.Scheduler.ResetForBrand(brand);
            RefreshView();
        }

        public void RunCheckinAsync(bool queryOnly, string platform, string brand)
        {
            var slot = Slot(platform);
            if (slot == null) return;
            if (slot.Busy) return;
            if (string.IsNullOrEmpty(brand)) brand = slot.ActiveBrand;
            slot.Busy = true;
            UpdateTray();
            string reqPlatform = platform;
            string reqBrand = brand;

            Task.Run(delegate { return RunPlatform(reqPlatform, queryOnly, reqBrand); })
                .ContinueWith(t =>
                {
                    CheckinResult r;
                    try { r = t.Result; }
                    catch (Exception ex) { r = CheckinResult.Fail(-6, "调用签到程序异常: " + ex.Message, reqBrand); }
                    if (r != null) r.Platform = reqPlatform;
                    OnCheckinDone(slot, r, queryOnly, reqBrand);
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static CheckinResult RunPlatform(string platform, bool queryOnly, string brand)
        {
            if (platform == WorkbuddyAuth.Platform) return WorkbuddyRunner.Run(queryOnly);
            if (platform == ZcodeAuth.Platform) return ZcodeRunner.Run(queryOnly);
            return CheckinRunner.Run(queryOnly, brand);
        }

        private void OnCheckinDone(PlatformSlot slot, CheckinResult r, bool queryOnly, string requestedBrand)
        {
            slot.Busy = false;
            slot.LastResult = r;
            if (r == null) { UpdateTray(); return; }
            string brand = r.Brand ?? requestedBrand;
            bool ok = r.Success || r.CheckedIn;
            RuntimeLog.Write(slot.Platform + (queryOnly ? " query" : " run") + " code=" + r.Code + " ok=" + ok + (r.HasClaimable ? " claimable" : "") + " msg=" + (r.DisplayText ?? r.Message));

            string userName = r.Username;
            if (string.IsNullOrEmpty(userName))
            {
                var acc = slot.ActiveAccount();
                if (acc != null) userName = acc.Username;
            }
            string who = string.IsNullOrEmpty(userName) ? brand : userName + "（" + brand + "）";

            string statusText;
            if (!string.IsNullOrEmpty(r.DisplayText))
            {
                statusText = who + " " + r.DisplayText;
            }
            else if (ok)
            {
                statusText = who + " 今日已签到";
                if (r.Base.HasValue || r.Extra.HasValue)
                    statusText = who + " 今日已签到 +" + (r.Base.GetValueOrDefault(0) + r.Extra.GetValueOrDefault(0)) + "（基础" + r.Base + " 额外" + r.Extra + "）";
            }
            else if (r.Code == 9095)
            {
                statusText = who + " 本机今日已被另一账号签到";
            }
            else if (r.Code == -6)
            {
                statusText = who + " " + (string.IsNullOrEmpty(r.Message) ? "账号状态异常（token 可能已过期）" : r.Message);
            }
            else
            {
                statusText = who + " " + (string.IsNullOrEmpty(r.Message) ? "签到失败：未知原因" : r.Message);
            }
            UpdateTray();

            if (!string.IsNullOrEmpty(r.Username)) HistoryStore.SetUser(brand, r.Username);

            // 查询模式只在"已签到"时落库；签到模式无论成败都落库（ZCode 无活动查询不落库）
            bool zcNoop = (slot.Platform == "ZCode" && r.Already);
            if ((!queryOnly || ok) && !zcNoop)
                HistoryStore.Upsert(new HistoryEntry
                {
                    brand = brand,
                    date = DateTime.Now.ToString("yyyy-MM-dd"),
                    success = ok,
                    checked_in = r.CheckedIn,
                    occupied = (r.Code == 9095),
                    code = r.Code,
                    baseCredits = r.Base,
                    extra = r.Extra,
                    ts = DateTimeOffset.Now.ToUnixTimeSeconds()
                });

            // 调度状态：只有真实签到尝试才回填（查询不改调度；ZCode 用轮询器）
            if (!queryOnly && slot.Scheduler != null) slot.Scheduler.NotifyResult(r);
            if (slot.Platform == "ZCode") ScheduleZcodeNext(r);

            // ZCode 轮询/启动查询发现可领套餐 → 自动领取（已确认全自动策略）
            if (queryOnly && slot.Platform == "ZCode" && r.HasClaimable)
            {
                RunCheckinAsync(false, "ZCode", null);
            }

            if (!queryOnly)
            {
                string tip;
                ToolTipIcon ticon;
                if (r.Already)
                {
                    tip = string.IsNullOrEmpty(r.DisplayText) ? "今天已签到，无需重复签到" : r.DisplayText;
                    ticon = ToolTipIcon.Info;
                }
                else if (ok)
                {
                    tip = "签到成功：" + statusText;
                    ticon = ToolTipIcon.Info;
                }
                else
                {
                    tip = statusText;
                    ticon = ToolTipIcon.Warning;
                }
                _tray.ShowBalloonTip(5000, "TraeSign", tip, ticon);
            }

            RefreshView();
        }

        // WorkBuddy 首次授权：打开浏览器 → 轮询 token → 落库 → 重新加载
        public void StartWorkbuddyAuthAsync()
        {
            if (_wb.Busy) return;
            _wb.Busy = true;
            UpdateTray();
            _tray.ShowBalloonTip(5000, "TraeSign", "WorkBuddy：已打开浏览器，请在页面完成授权登录（3 分钟内有效）", ToolTipIcon.Info);
            string authError = null;
            Task.Run(delegate { return WorkbuddyAuth.StartOAuth(); })
                .ContinueWith(t =>
                {
                    _wb.Busy = false;
                    WbAuthData r = null;
                    try { r = t.Result; }
                    catch (Exception ex) { authError = ex.Message; }
                    if (r != null)
                    {
                        _wb.LastResult = null;
                        _tray.ShowBalloonTip(5000, "TraeSign", "WorkBuddy：授权成功，开始查询签到状态", ToolTipIcon.Info);
                        LoadAccountsAsync();
                    }
                    else
                    {
                        string msg = string.IsNullOrEmpty(authError) ? "授权未完成或超时" : authError;
                        _wb.LastResult = CheckinResult.Fail(-1, "授权失败：" + msg, WorkbuddyAuth.Platform);
                        _tray.ShowBalloonTip(5000, "TraeSign", "WorkBuddy：授权失败 - " + msg, ToolTipIcon.Warning);
                        UpdateTray();
                        RefreshView();
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public void ExitApp()
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
            ExitThread();
        }
    }

    // 三平台账号列表装载结果
    internal class TwoLists
    {
        public List<AccountInfo> Trae;
        public List<AccountInfo> Wb;
        public List<AccountInfo> Zc;
    }

    // ============ 签到引擎（内置，无外部依赖） ============
    internal class CheckinResult
    {
        public bool Success;
        public int Code;
        public bool CheckedIn;
        public bool Already;         // 今日已签到（未执行 claim，无需重复签到）
        public string Username;
        public int? Base;
        public int? Extra;
        public string Message;
        public string Brand;
        public string Platform;      // 所属平台："Trae" / "WorkBuddy" / "ZCode"
        public string DisplayText;   // 引擎自定义状态文案（设置后替代默认组合）
        public bool HasClaimable;    // ZCode：发现可领套餐
        public long RetryAfterAt;    // ZCode：1005 补货时间（unix 秒；0=未知）

        public static CheckinResult Fail(int code, string message, string brand)
        {
            return new CheckinResult { Success = false, Code = code, CheckedIn = false, Username = null, Base = null, Extra = null, Message = message, Brand = brand };
        }
    }

    internal class AuthData
    {
        public string Brand;
        public string Enc;
        public string DevId;
        public string Token;
        public string Username;
        public string ExpiredAt;
        public string Region;
    }

    // AES-128-CBC 解密（与官方客户端派生逻辑一致，从 Node 版原样移植）
    internal static class TraeAuth
    {
        public const string StatusUrl = "https://api.trae.cn/trae/api/v2/ug/checkin_credits/status";
        public const string ClaimUrl = "https://api.trae.cn/trae/api/v2/ug/checkin_credits/claim";
        private static readonly string[] Brands = { "TRAE SOLO CN", "Trae CN", "TraeWork" };
        private static readonly byte[] Ure = new byte[] { 82,9,106,213,48,54,165,56,191,64,163,158,129,243,215,251,124,227,57,130,155,47,255,135,52,142,67,68,196,222,233,203,84,123,148,50,166,194,35,61,238,76,149,11,66,250,195,78,8,46,161,102,40,217,36,178,118,91,162,73,109,139,209,37 };
        private static readonly byte[] Dre = new byte[] { 31,221,168,51,136,7,199,49,177,18,16,89,39,128,236,95,96,81,127,169,25,181,74,13,45,229,122,159,147,201,156,239,160,224,59,77,174,42,245,176,200,235,187,60,131,83,153,97,23,43,4,126,186,119,214,38,225,105,20,99,85,33,12,125 };

        public static string Decrypt(string enc)
        {
            byte[] t = Convert.FromBase64String(enc);
            byte[] key = new byte[32];
            Buffer.BlockCopy(t, 6, key, 0, 32);
            byte[] sha;
            using (var h = System.Security.Cryptography.SHA512.Create()) sha = h.ComputeHash(key);
            byte[] xor = new byte[64];
            for (int i = 0; i < 64; i++) xor[i] = (byte)(Ure[i] ^ Dre[i]);
            byte[] comb = new byte[128];
            Buffer.BlockCopy(sha, 0, comb, 0, 64);
            Buffer.BlockCopy(xor, 0, comb, 64, 64);
            byte[] hash;
            using (var h2 = System.Security.Cryptography.SHA512.Create()) hash = h2.ComputeHash(comb);
            byte[] aesKey = new byte[16];
            Buffer.BlockCopy(hash, 0, aesKey, 0, 16);
            byte[] iv = new byte[16];
            Buffer.BlockCopy(hash, 16, iv, 0, 16);
            byte[] ct = new byte[t.Length - 38];
            Buffer.BlockCopy(t, 38, ct, 0, ct.Length);
            byte[] dec;
            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
                aes.KeySize = 128;
                aes.Key = aesKey;
                aes.IV = iv;
                using (var d = aes.CreateDecryptor()) dec = d.TransformFinalBlock(ct, 0, ct.Length);
            }
            // 先按字节跳过前 64 字节再 UTF8 解码（与 Node decrypt(dec.slice(64)) 一致）
            return Encoding.UTF8.GetString(dec, 64, dec.Length - 64);
        }

        public static AuthData FindAuth(string brandFilter)
        {
            foreach (var a in FindAllAuths())
                if (string.IsNullOrEmpty(brandFilter) || a.Brand == brandFilter) return a;
            return null;
        }

        public static List<AuthData> FindAllAuths()
        {
            var list = new List<AuthData>();
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var b in Brands)
            {
                var a = ReadAuth(b, Path.Combine(root, b, "User", "globalStorage", "storage.json"));
                if (a != null) list.Add(a);
            }
            return list;
        }

        private static AuthData ReadAuth(string brand, string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                string text = File.ReadAllText(file, Encoding.UTF8);
                var s = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
                if (s == null) return null;
                string dcId = "";
                foreach (string k in s.Keys)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(k, "^iCubeAuthInfo://icube-dc:(\\d+)");
                    if (m.Success) { dcId = m.Groups[1].Value; break; }
                }
                string enc = null;
                foreach (string k in s.Keys)
                    if (k.StartsWith("iCubeAuthInfo://icube.cloudide")) { enc = s[k] as string; break; }
                if (string.IsNullOrEmpty(enc)) return null;
                if (string.IsNullOrEmpty(dcId))
                {
                    object tv;
                    if (s.TryGetValue("telemetry.devDeviceId", out tv)) dcId = tv as string;
                    if (string.IsNullOrEmpty(dcId) && s.TryGetValue("telemetry.machineId", out tv)) dcId = tv as string;
                }
                var auth = new AuthData { Brand = brand, Enc = enc, DevId = dcId ?? "" };
                var parsed = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Decrypt(enc));
                if (parsed != null)
                {
                    object v;
                    if (parsed.TryGetValue("token", out v)) auth.Token = v as string;
                    if (parsed.TryGetValue("expiredAt", out v)) auth.ExpiredAt = v as string;
                    object accObj;
                    if (parsed.TryGetValue("account", out accObj))
                    {
                        var acc = accObj as Dictionary<string, object>;
                        if (acc != null && acc.TryGetValue("username", out v)) auth.Username = v as string;
                    }
                    object urObj;
                    if (parsed.TryGetValue("userRegion", out urObj))
                    {
                        var ur = urObj as Dictionary<string, object>;
                        if (ur != null && ur.TryGetValue("region", out v)) auth.Region = v as string;
                    }
                }
                return auth;
            }
            catch { return null; }
        }
    }

    // HTTP 响应（含状态码，供 WorkBuddy 引擎区分 401 与业务错误）
    internal class ApiResp
    {
        public int Status;      // HTTP 状态码；0=无响应（网络异常）
        public string Body;
        public string Error;

        public bool Ok { get { return Status >= 200 && Status < 300; } }
    }

    // HTTP POST/GET JSON（直连，证书失败降级重试一次）
    internal static class HttpApi
    {
        // User-Agent/Accept 等是 HttpWebRequest 受限头，走 Headers[] 会抛 ArgumentException
        public static void ApplyHeaders(HttpWebRequest req, Dictionary<string, string> headers)
        {
            if (headers == null) return;
            foreach (var kv in headers)
            {
                if (string.Equals(kv.Key, "User-Agent", StringComparison.OrdinalIgnoreCase)) req.UserAgent = kv.Value;
                else if (string.Equals(kv.Key, "Accept", StringComparison.OrdinalIgnoreCase)) req.Accept = kv.Value;
                else if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) req.ContentType = kv.Value;
                else if (string.Equals(kv.Key, "Referer", StringComparison.OrdinalIgnoreCase)) req.Referer = kv.Value;
                else req.Headers[kv.Key] = kv.Value;
            }
        }

        public static string PostJson(string url, Dictionary<string, string> headers, string bodyJson)
        {
            ApiResp r = SendCore(url, "POST", headers, bodyJson, false);
            if (r.Status == 0) throw new WebException(r.Error ?? "网络异常");
            return r.Body;
        }

        public static ApiResp PostWithStatus(string url, Dictionary<string, string> headers, string bodyJson)
        {
            return SendCore(url, "POST", headers, bodyJson, false);
        }

        public static ApiResp GetWithStatus(string url, Dictionary<string, string> headers)
        {
            return SendCore(url, "GET", headers, null, false);
        }

        private static ApiResp SendCore(string url, string method, Dictionary<string, string> headers, string bodyJson, bool insecure)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = 60000;
            req.ReadWriteTimeout = 60000;
            req.Proxy = null;   // 直连，与 Node https 行为一致
            if (insecure) req.ServerCertificateValidationCallback = delegate { return true; };
            ApplyHeaders(req, headers);
            if (method == "POST")
            {
                req.ContentType = "application/json";
                byte[] payload = Encoding.UTF8.GetBytes(bodyJson ?? "{}");
                req.ContentLength = payload.Length;
                using (var s = req.GetRequestStream()) s.Write(payload, 0, payload.Length);
            }
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rs = resp.GetResponseStream())
                using (var sr = new StreamReader(rs, Encoding.UTF8))
                {
                    var ok = new ApiResp();
                    ok.Status = (int)resp.StatusCode;
                    ok.Body = sr.ReadToEnd();
                    return ok;
                }
            }
            catch (WebException ex)
            {
                if (ex.Response != null)
                {
                    try
                    {
                        var hr = (HttpWebResponse)ex.Response;
                        var fail = new ApiResp();
                        fail.Status = (int)hr.StatusCode;
                        using (var rs = hr.GetResponseStream())
                        using (var sr = new StreamReader(rs, Encoding.UTF8)) fail.Body = sr.ReadToEnd();
                        return fail;
                    }
                    catch { }
                }
                if (!insecure && ex.Status == WebExceptionStatus.SecureChannelFailure)
                    return SendCore(url, method, headers, bodyJson, true);
                var err = new ApiResp();
                err.Status = 0;
                err.Error = ex.Message;
                return err;
            }
        }
    }

    internal static class CheckinRunner
    {
        public static CheckinResult Run(bool queryOnly, string brand)
        {
            AuthData auth = TraeAuth.FindAuth(brand);
            if (auth == null)
                return CheckinResult.Fail(-1, "未找到登录态，请先运行 Trae 桌面端登录", brand);
            if (string.IsNullOrEmpty(auth.Token))
                return CheckinResult.Fail(-2, "登录态无 token", auth.Brand);
            if (string.IsNullOrEmpty(brand)) brand = auth.Brand;

            var headers = new Dictionary<string, string>();
            headers["Authorization"] = "Cloud-IDE-JWT " + auth.Token;
            headers["x-device-id"] = auth.DevId;
            if (!string.IsNullOrEmpty(auth.Region)) headers["X-User-Region"] = auth.Region;

            try
            {
                var status = Parse(HttpApi.PostJson(TraeAuth.StatusUrl, headers, "{}"));
                int? baseV = NullInt(status, "credits");
                int? extraV = NullInt(status, "extra_credits");
                if (baseV == null && extraV == null)
                    return CheckinResult.Fail(-6, "账号状态异常（token 可能已过期），请重新登录该软件", auth.Brand);

                bool checkedIn = GetBool(status, "checked_in");
                if (queryOnly)
                    return new CheckinResult { Success = checkedIn, Code = 0, CheckedIn = checkedIn, Already = checkedIn, Username = auth.Username, Base = baseV, Extra = extraV, Message = checkedIn ? "今日已签到" : "今日未签到", Brand = auth.Brand };
                if (checkedIn)
                    return new CheckinResult { Success = true, Code = 0, CheckedIn = true, Already = true, Username = auth.Username, Base = baseV, Extra = extraV, Message = "今日已签到", Brand = auth.Brand };
                if (!GetBool(status, "enable"))
                {
                    int sc = GetInt(status, "code");
                    return CheckinResult.Fail(sc > 0 ? sc : -4, "签到未开启", auth.Brand);
                }

                // claim + 二次确认（防 token 过期假成功）
                var claim = Parse(HttpApi.PostJson(TraeAuth.ClaimUrl, headers, "{\"req_source\":1}"));
                int claimCode = GetInt(claim, "code");
                if (claimCode == 0 || GetBool(claim, "checked_in"))
                {
                    var confirm = Parse(HttpApi.PostJson(TraeAuth.StatusUrl, headers, "{}"));
                    if (GetBool(confirm, "checked_in"))
                    {
                        int? cB = NullInt(confirm, "credits");
                        int? cE = NullInt(confirm, "extra_credits");
                        return new CheckinResult { Success = true, Code = 0, CheckedIn = true, Already = false, Username = auth.Username, Base = cB ?? baseV, Extra = cE ?? extraV, Message = "签到成功", Brand = auth.Brand };
                    }
                    return CheckinResult.Fail(-6, "签到结果异常（token 可能已过期），请重新登录该软件", auth.Brand);
                }
                if (claimCode == 9095)
                    return CheckinResult.Fail(9095, "本设备今日已有账号签到", auth.Brand);
                return CheckinResult.Fail(claimCode, GetString(claim, "message") ?? "签到失败", auth.Brand);
            }
            catch (Exception ex)
            {
                return CheckinResult.Fail(-3, "网络异常: " + ex.Message, auth.Brand);
            }
        }

        public static List<AccountInfo> ListAccounts()
        {
            var result = new List<AccountInfo>();
            foreach (var a in TraeAuth.FindAllAuths())
                result.Add(new AccountInfo { Brand = a.Brand, Username = a.Username, ExpiredAt = a.ExpiredAt, HasToken = !string.IsNullOrEmpty(a.Token) });
            return result;
        }

        private static Dictionary<string, object> Parse(string json)
        {
            try { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); }
            catch { return new Dictionary<string, object>(); }
        }

        private static bool GetBool(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                if (v is bool) return (bool)v;
                try { return Convert.ToBoolean(v); } catch { }
            }
            return false;
        }

        private static int GetInt(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToInt32(v); } catch { }
            }
            return -1;
        }

        private static int? NullInt(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null && !(v is string))
            {
                try { return Convert.ToInt32(v); } catch { }
            }
            return null;
        }

        private static string GetString(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return v.ToString();
            return null;
        }
    }

    // ============ JSON 工具（WorkBuddy 引擎用，兼容 camelCase/snake_case） ============
    internal static class JsonUtil
    {
        public static Dictionary<string, object> ParseJson(string json)
        {
            try { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); }
            catch { return new Dictionary<string, object>(); }
        }

        public static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v)) return v as Dictionary<string, object>;
            return null;
        }

        public static string GetString(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return v.ToString();
            return null;
        }

        public static string GetStringAny(Dictionary<string, object> d, params string[] keys)
        {
            if (d == null) return null;
            foreach (string k in keys)
            {
                string v = GetString(d, k);
                if (v != null) return v;
            }
            return null;
        }

        public static int GetInt(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToInt32(v); } catch { }
            }
            return -1;
        }

        public static int? NullInt(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null && !(v is string))
            {
                try { return Convert.ToInt32(v); } catch { }
            }
            return null;
        }

        public static int? GetIntAny(Dictionary<string, object> d, params string[] keys)
        {
            if (d == null) return null;
            foreach (string k in keys)
            {
                int? v = NullInt(d, k);
                if (v.HasValue) return v;
            }
            return null;
        }

        public static long GetLong(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToInt64(v); } catch { }
            }
            return 0;
        }

        public static bool GetBool(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                if (v is bool) return (bool)v;
                try { return Convert.ToBoolean(v); } catch { }
            }
            return false;
        }

        public static bool GetBoolAny(Dictionary<string, object> d, params string[] keys)
        {
            if (d == null) return false;
            foreach (string k in keys)
                if (d.ContainsKey(k)) return GetBool(d, k);
            return false;
        }
    }

    // ============ WorkBuddy 凭证（客户端文件明文兼容 + OAuth 插件授权流 + DPAPI token 库） ============
    internal class WbAuthData
    {
        public string Uid;              // account.uid（明文）或 JWT sub
        public string AccessToken;      // 明文 JWT；加密信封时为 null
        public string RefreshToken;     // 仅程序自有 token 库提供
        public long ExpiresAt;          // ms epoch；0=未知
        public long RefreshExpiresAt;   // ms epoch；0=未知
        public string Domain;           // 如 www.workbuddy.cn
        public bool FromStore;          // 是否来自程序自有 token 库
    }

    internal static class WorkbuddyAuth
    {
        public const string Platform = "WorkBuddy";
        public const string DefaultDomain = "https://www.workbuddy.cn";
        public static readonly string[] Domains = { "https://www.workbuddy.cn", "https://www.codebuddy.cn" };
        public const string StatePath = "/v2/plugin/auth/state?platform=workbuddy";
        public const string TokenPath = "/v2/plugin/auth/token?state=";
        public const string RefreshUrl = "https://copilot.tencent.com/v2/plugin/auth/token/refresh";
        private static readonly string[] AuthFiles = { "workbuddy-desktop.info", "workbuddy-desktop-ai.info", "workbuddy-desktop-dev.info" };

        public static string TokenStorePath { get { return Path.Combine(HistoryStore.DataDir, "workbuddy_token.json"); } }

        // ---------- 本机客户端登录态 ----------
        public static WbAuthData ReadClientFile()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodeBuddyExtension", "Data", "Public", "auth");
            foreach (string name in AuthFiles)
            {
                var a = ReadAuthFile(Path.Combine(dir, name));
                if (a != null) return a;
            }
            return null;
        }

        private static WbAuthData ReadAuthFile(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                string text = File.ReadAllText(file, Encoding.UTF8);
                var s = JsonUtil.ParseJson(text);
                if (s == null || s.Count == 0) return null;
                var auth = new WbAuthData();
                object v;
                object accObj;
                if (s.TryGetValue("account", out accObj))
                {
                    var acc = accObj as Dictionary<string, object>;
                    if (acc != null && acc.TryGetValue("uid", out v)) auth.Uid = v as string;
                }
                if (!s.TryGetValue("auth", out accObj)) return string.IsNullOrEmpty(auth.Uid) ? null : auth;
                var ad = accObj as Dictionary<string, object>;
                if (ad == null) return string.IsNullOrEmpty(auth.Uid) ? null : auth;

                object at;
                if (ad.TryGetValue("accessToken", out at) && at is string)
                    auth.AccessToken = (string)at;   // 明文 JWT；$wbEncrypted 信封时保持 null（密钥在客户端进程内，无法离线解密）
                // 注意：不读取客户端文件的 refreshToken——滚动轮换会让客户端持有的旧 token 失效导致其掉线；
                // 程序的自动续期只使用自有 token 库（OAuth 授权所得）的 refreshToken
                if (ad.TryGetValue("domain", out v) && v is string && !string.IsNullOrEmpty((string)v))
                    auth.Domain = (string)v;
                auth.ExpiresAt = JsonUtil.GetLong(ad, "expiresAt");
                auth.RefreshExpiresAt = JsonUtil.GetLong(ad, "refreshExpiresAt");
                return auth;
            }
            catch { return null; }
        }

        // $wbEncrypted 加密信封判定
        public static bool IsEncryptedEnvelope(object v)
        {
            var d = v as Dictionary<string, object>;
            if (d == null) return false;
            object flag;
            if (!d.TryGetValue("$wbEncrypted", out flag) || flag == null) return false;
            try { return Convert.ToInt32(flag) == 1; } catch { return false; }
        }

        // ---------- JWT / Base64Url ----------
        public static string JwtSub(string jwt)
        {
            try
            {
                if (string.IsNullOrEmpty(jwt)) return null;
                string[] parts = jwt.Split('.');
                if (parts.Length < 2) return null;
                string json = Base64UrlDecode(parts[1]);
                var d = JsonUtil.ParseJson(json);
                if (d == null) return null;
                object v;
                if (d.TryGetValue("sub", out v)) return v == null ? null : v.ToString();
                return null;
            }
            catch { return null; }
        }

        public static string Base64UrlDecode(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = s.Replace('-', '+').Replace('_', '/');
            int pad = (4 - t.Length % 4) % 4;
            if (pad > 0) t = t + new string('=', pad);
            return Encoding.UTF8.GetString(Convert.FromBase64String(t));
        }

        public static string Base64UrlEncode(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static bool IsExpired(long expiresAtMs)
        {
            if (expiresAtMs <= 0) return false;
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= expiresAtMs;
        }

        // ---------- 程序自有 token 库（DPAPI CurrentUser 加密落盘） ----------
        internal class WbTokenStoreDto
        {
            public int v;
            public string uid;
            public string at;   // DPAPI base64(accessToken)
            public string rt;   // DPAPI base64(refreshToken)
            public long expiresAt;
            public long refreshExpiresAt;
            public long savedAt;
        }

        public static WbAuthData LoadStore()
        {
            try
            {
                if (!File.Exists(TokenStorePath)) return null;
                var dto = new JavaScriptSerializer().Deserialize<WbTokenStoreDto>(File.ReadAllText(TokenStorePath, Encoding.UTF8));
                if (dto == null || string.IsNullOrEmpty(dto.at)) return null;
                var a = new WbAuthData();
                a.FromStore = true;
                a.Uid = dto.uid;
                a.AccessToken = DpapiUnprotect(dto.at);
                a.RefreshToken = string.IsNullOrEmpty(dto.rt) ? null : DpapiUnprotect(dto.rt);
                a.ExpiresAt = dto.expiresAt;
                a.RefreshExpiresAt = dto.refreshExpiresAt;
                if (string.IsNullOrEmpty(a.AccessToken)) return null;
                return a;
            }
            catch { return null; }
        }

        public static void SaveStore(WbAuthData a)
        {
            if (a == null || string.IsNullOrEmpty(a.AccessToken)) return;
            try
            {
                var dto = new WbTokenStoreDto();
                dto.v = 1;
                dto.uid = a.Uid;
                dto.at = DpapiProtect(a.AccessToken);
                dto.rt = string.IsNullOrEmpty(a.RefreshToken) ? null : DpapiProtect(a.RefreshToken);
                dto.expiresAt = a.ExpiresAt;
                dto.refreshExpiresAt = a.RefreshExpiresAt;
                dto.savedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                string json = new JavaScriptSerializer().Serialize(dto);
                if (!Directory.Exists(HistoryStore.DataDir)) Directory.CreateDirectory(HistoryStore.DataDir);
                string tmp = TokenStorePath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(TokenStorePath)) File.Replace(tmp, TokenStorePath, null);
                else File.Move(tmp, TokenStorePath);
            }
            catch { }
        }

        private static string DpapiProtect(string plain)
        {
            byte[] enc = System.Security.Cryptography.ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain ?? ""), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
        }

        private static string DpapiUnprotect(string b64)
        {
            try
            {
                byte[] dec = System.Security.Cryptography.ProtectedData.Unprotect(
                    Convert.FromBase64String(b64 ?? ""), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch { return null; }
        }

        // ---------- 取可用 token：客户端明文 → token 库（过期自动续期） ----------
        public static WbAuthData GetWorkingToken()
        {
            WbAuthData file = ReadClientFile();
            if (file != null && !string.IsNullOrEmpty(file.AccessToken) && !IsExpired(file.ExpiresAt)) return file;

            WbAuthData store = LoadStore();
            if (store != null && !string.IsNullOrEmpty(store.AccessToken))
            {
                if (!IsExpired(store.ExpiresAt)) return store;
                if (!string.IsNullOrEmpty(store.RefreshToken) && !IsExpired(store.RefreshExpiresAt))
                {
                    WbAuthData nr = TryRefresh(store);
                    if (nr != null) return nr;
                }
            }
            return null;
        }

        // refreshToken 滚动轮换：刷新成功立即回写落盘
        public static WbAuthData TryRefresh(WbAuthData current)
        {
            if (current == null || string.IsNullOrEmpty(current.RefreshToken)) return null;
            try
            {
                var headers = new Dictionary<string, string>();
                headers["X-Refresh-Token"] = current.RefreshToken;
                headers["X-Auth-Refresh-Source"] = "plugin";
                if (!string.IsNullOrEmpty(current.AccessToken)) headers["Authorization"] = "Bearer " + current.AccessToken;
                if (!string.IsNullOrEmpty(current.Uid)) headers["X-User-Id"] = current.Uid;
                ApiResp resp = HttpApi.PostWithStatus(RefreshUrl, headers, "{}");
                if (resp.Status == 0) return null;
                var d = JsonUtil.ParseJson(resp.Body);
                if (JsonUtil.GetInt(d, "code") != 0) return null;
                var data = JsonUtil.GetDict(d, "data");
                if (data == null) return null;
                string at = JsonUtil.GetStringAny(data, "accessToken", "access_token");
                if (string.IsNullOrEmpty(at)) return null;
                string rt = JsonUtil.GetStringAny(data, "refreshToken", "refresh_token");

                var nr = new WbAuthData();
                nr.FromStore = true;
                nr.Uid = current.Uid ?? JwtSub(at);
                nr.AccessToken = at;
                nr.RefreshToken = string.IsNullOrEmpty(rt) ? current.RefreshToken : rt;
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long expiresIn = JsonUtil.GetLong(data, "expiresIn");
                long refreshExpiresIn = JsonUtil.GetLong(data, "refreshExpiresIn");
                nr.ExpiresAt = expiresIn > 0 ? now + expiresIn * 1000 : 0;
                nr.RefreshExpiresAt = refreshExpiresIn > 0 ? now + refreshExpiresIn * 1000 : current.RefreshExpiresAt;
                SaveStore(nr);
                return nr;
            }
            catch { return null; }
        }

        // ---------- OAuth 插件授权流（一次性，浏览器确认） ----------
        public static WbAuthData StartOAuth()
        {
            // 1. 申请 state
            ApiResp resp = HttpApi.PostWithStatus(DefaultDomain + StatePath, null, "{}");
            if (resp.Status == 0) throw new Exception("网络异常：" + (resp.Error ?? ""));
            var d = JsonUtil.ParseJson(resp.Body);
            var data = JsonUtil.GetDict(d, "data");
            string state = data != null ? JsonUtil.GetString(data, "state") : null;
            string authUrl = data != null ? JsonUtil.GetString(data, "authUrl") : null;
            if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(authUrl))
                throw new Exception("授权服务响应异常（HTTP " + resp.Status + "）");

            // 2. 打开浏览器（失败不中断，用户可手动复制链接）
            try { Process.Start(authUrl); } catch { }

            // 3. 轮询换 token（最长约 3 分钟）
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(3000);
                ApiResp tr = HttpApi.GetWithStatus(DefaultDomain + TokenPath + Uri.EscapeDataString(state), null);
                if (tr.Status == 0) continue;
                var td = JsonUtil.ParseJson(tr.Body);
                int code = JsonUtil.GetInt(td, "code");
                if (code == 11217) continue;   // 等待用户在浏览器确认
                if (code != 0)
                    throw new Exception("授权失败(" + code + ")：" + (JsonUtil.GetStringAny(td, "msg", "message") ?? ""));
                var tdata = JsonUtil.GetDict(td, "data");
                if (tdata == null) continue;
                string at = JsonUtil.GetStringAny(tdata, "accessToken", "access_token");
                if (string.IsNullOrEmpty(at))
                {
                    var nested = JsonUtil.GetDict(tdata, "auth");
                    if (nested == null) nested = JsonUtil.GetDict(tdata, "token");
                    if (nested != null) at = JsonUtil.GetStringAny(nested, "accessToken", "access_token");
                }
                if (string.IsNullOrEmpty(at)) continue;

                string rt = JsonUtil.GetStringAny(tdata, "refreshToken", "refresh_token");
                if (string.IsNullOrEmpty(rt))
                {
                    var nested = JsonUtil.GetDict(tdata, "auth");
                    if (nested != null) rt = JsonUtil.GetStringAny(nested, "refreshToken", "refresh_token");
                }
                var a = new WbAuthData();
                a.FromStore = true;
                a.AccessToken = at;
                a.RefreshToken = rt;
                a.Uid = JsonUtil.GetStringAny(tdata, "uid", "userId") ?? JwtSub(at);
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long expiresIn = JsonUtil.GetLong(tdata, "expiresIn");
                long refreshExpiresIn = JsonUtil.GetLong(tdata, "refreshExpiresIn");
                a.ExpiresAt = expiresIn > 0 ? now + expiresIn * 1000 : 0;
                a.RefreshExpiresAt = refreshExpiresIn > 0 ? now + refreshExpiresIn * 1000 : 0;
                SaveStore(a);
                return a;
            }
            throw new Exception("授权超时：未在 3 分钟内完成浏览器确认");
        }

        // ---------- 账号列表（供 UI/调度） ----------
        public static List<AccountInfo> ListAccounts()
        {
            var list = new List<AccountInfo>();
            WbAuthData file = ReadClientFile();
            WbAuthData store = LoadStore();
            string uid = null;
            long exp = 0;
            if (file != null && !string.IsNullOrEmpty(file.Uid)) { uid = file.Uid; exp = file.ExpiresAt; }
            if (uid == null && store != null && !string.IsNullOrEmpty(store.Uid)) { uid = store.Uid; exp = store.ExpiresAt; }
            bool hasToken = GetWorkingToken() != null;
            if (uid == null && !hasToken) return list;
            var info = new AccountInfo();
            info.Brand = Platform;
            info.Username = string.IsNullOrEmpty(uid) ? null : uid;
            info.ExpiredAt = exp > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(exp).LocalDateTime.ToString("yyyy-MM-dd HH:mm") : null;
            info.HasToken = hasToken;
            list.Add(info);
            return list;
        }
    }

    // ============ WorkBuddy 签到引擎 ============
    internal static class WorkbuddyRunner
    {
        private const string StatusPath = "/v2/billing/meter/checkin-activity-status";
        private const string ClaimPath = "/v2/billing/meter/daily-checkin";

        public static CheckinResult Run(bool queryOnly)
        {
            WbAuthData auth = WorkbuddyAuth.GetWorkingToken();
            if (auth == null || string.IsNullOrEmpty(auth.AccessToken))
                return CheckinResult.Fail(-1, "未授权：请在 WorkBuddy 页签点击“登录授权”完成一次浏览器登录", WorkbuddyAuth.Platform);

            try
            {
                // 1. 状态查询（401/过期 → 自动续期重试一次）
                ApiResp st = PostWithAuthRefresh(auth, StatusPath);
                if (st == null)
                    return CheckinResult.Fail(-3, "网络异常：主备域名均不可达", WorkbuddyAuth.Platform);
                var status = JsonUtil.ParseJson(st.Body);
                int code = JsonUtil.GetInt(status, "code");
                if (st.Status == 401 || IsAuthCode(code))
                    return CheckinResult.Fail(-6, "登录已失效且自动续期失败，请重新授权", WorkbuddyAuth.Platform);
                if (code != 0)
                    return CheckinResult.Fail(code, JsonUtil.GetStringAny(status, "msg", "message") ?? "状态查询失败", WorkbuddyAuth.Platform);
                var data = JsonUtil.GetDict(status, "data");
                if (data == null)
                    return CheckinResult.Fail(-6, "状态响应异常（可能未授权或接口已变化）", WorkbuddyAuth.Platform);

                bool checkedIn = JsonUtil.GetBoolAny(data, "todayCheckedIn", "today_checked_in");
                int? todayCredit = JsonUtil.GetIntAny(data, "todayCredit", "today_credit", "dailyCredit", "daily_credit");

                if (queryOnly)
                    return new CheckinResult
                    {
                        Success = checkedIn,
                        Code = 0,
                        CheckedIn = checkedIn,
                        Already = checkedIn,
                        Username = auth.Uid,
                        Base = todayCredit,
                        Extra = null,
                        Message = checkedIn ? "今日已签到" : "今日未签到",
                        Brand = WorkbuddyAuth.Platform
                    };

                if (checkedIn)
                    return new CheckinResult
                    {
                        Success = true,
                        Code = 0,
                        CheckedIn = true,
                        Already = true,
                        Username = auth.Uid,
                        Base = todayCredit,
                        Extra = null,
                        Message = "今日已签到",
                        Brand = WorkbuddyAuth.Platform
                    };

                // 2. 领取
                ApiResp cl = PostWithAuthRefresh(auth, ClaimPath);
                if (cl == null)
                    return CheckinResult.Fail(-3, "网络异常：主备域名均不可达", WorkbuddyAuth.Platform);
                var claim = JsonUtil.ParseJson(cl.Body);
                int ccode = JsonUtil.GetInt(claim, "code");
                string cmsg = JsonUtil.GetStringAny(claim, "msg", "message");
                if (cl.Status == 401 || IsAuthCode(ccode))
                    return CheckinResult.Fail(-6, "登录已失效且自动续期失败，请重新授权", WorkbuddyAuth.Platform);

                if (ccode == 0 || ccode == 10001 || (cmsg != null && cmsg.Contains("已签到")))
                {
                    // 3. 二次确认（防假成功，与 Trae 同口径）
                    var cdata = JsonUtil.GetDict(claim, "data");
                    int? credit = cdata != null ? JsonUtil.GetIntAny(cdata, "credit", "todayCredit", "today_credit") : null;
                    ApiResp cf = PostAny(BuildHeaders(auth), StatusPath);
                    var confirm = cf != null && cf.Ok ? JsonUtil.ParseJson(cf.Body) : null;
                    var cdata2 = confirm != null && JsonUtil.GetInt(confirm, "code") == 0 ? JsonUtil.GetDict(confirm, "data") : null;
                    bool confirmed = cdata2 != null && JsonUtil.GetBoolAny(cdata2, "todayCheckedIn", "today_checked_in");
                    if (!confirmed && ccode != 10001)
                        return CheckinResult.Fail(-6, "签到结果异常（未确认到已签到状态）", WorkbuddyAuth.Platform);
                    int? todayCredit2 = cdata2 != null ? JsonUtil.GetIntAny(cdata2, "todayCredit", "today_credit", "dailyCredit", "daily_credit") : null;
                    int? finalCredit = credit.HasValue ? credit : (todayCredit2.HasValue ? todayCredit2 : todayCredit);
                    bool already = ccode == 10001;
                    return new CheckinResult
                    {
                        Success = true,
                        Code = 0,
                        CheckedIn = true,
                        Already = already,
                        Username = auth.Uid,
                        Base = finalCredit,
                        Extra = null,
                        Message = already ? "今日已签到" : "签到成功",
                        Brand = WorkbuddyAuth.Platform
                    };
                }
                if (ccode == 41000)
                    return CheckinResult.Fail(41000, "签到活动未开始或已结束", WorkbuddyAuth.Platform);
                return CheckinResult.Fail(ccode, cmsg ?? "签到失败", WorkbuddyAuth.Platform);
            }
            catch (Exception ex)
            {
                return CheckinResult.Fail(-3, "网络异常: " + ex.Message, WorkbuddyAuth.Platform);
            }
        }

        private static Dictionary<string, string> BuildHeaders(WbAuthData auth)
        {
            var headers = new Dictionary<string, string>();
            headers["Authorization"] = "Bearer " + auth.AccessToken;
            if (!string.IsNullOrEmpty(auth.Uid)) headers["X-User-Id"] = auth.Uid;
            return headers;
        }

        // 主备域名回退：主域名无响应时切备用
        private static ApiResp PostAny(Dictionary<string, string> headers, string path)
        {
            ApiResp last = null;
            foreach (string dom in WorkbuddyAuth.Domains)
            {
                var h = new Dictionary<string, string>(headers);
                h["X-Domain"] = dom.Replace("https://", "");
                ApiResp r = HttpApi.PostWithStatus(dom + path, h, "{}");
                if (r.Status != 0) return r;
                last = r;
            }
            return last;
        }

        // 401/token 失效 → 续期一次后重试（结果回写 token 库）
        private static ApiResp PostWithAuthRefresh(WbAuthData auth, string path)
        {
            ApiResp r = PostAny(BuildHeaders(auth), path);
            if (r != null && r.Status != 401 && !BodyHasAuthCode(r)) return r;

            WbAuthData nr = null;
            if (!string.IsNullOrEmpty(auth.RefreshToken)) nr = WorkbuddyAuth.TryRefresh(auth);
            if (nr == null)
            {
                // 客户端文件里的明文 token 可能已被客户端自己刷新 → 重读一次
                var file = WorkbuddyAuth.ReadClientFile();
                if (file == null || string.IsNullOrEmpty(file.AccessToken) || file.AccessToken == auth.AccessToken) return r;
                auth.AccessToken = file.AccessToken;
            }
            else
            {
                auth.AccessToken = nr.AccessToken;
                auth.RefreshToken = nr.RefreshToken;
            }
            return PostAny(BuildHeaders(auth), path);
        }

        private static bool BodyHasAuthCode(ApiResp r)
        {
            if (r == null || string.IsNullOrEmpty(r.Body)) return false;
            int code = JsonUtil.GetInt(JsonUtil.ParseJson(r.Body), "code");
            return IsAuthCode(code);
        }

        private static bool IsAuthCode(int code)
        {
            return code == 401 || code == 40100 || code == 12153;
        }
    }

    // ============ AES-256-GCM 纯托管实现（NIST SP 800-38D；.NET 4.8 无 AesGcm，
    // 而 CNG P/Invoke 在部分环境（沙箱/安全软件钩 GCM 路径）稳定返回 C000000D，故全托管） ============
    internal static class ZcodeGcm
    {
        // 解密失败（tag 校验不符/长度非法）返回 null
        public static byte[] Decrypt(byte[] key, byte[] iv, byte[] tag, byte[] cipherText)
        {
            if (key == null || iv == null || tag == null || cipherText == null) return null;
            if (key.Length != 32 || iv.Length != 12 || tag.Length != 16) return null;

            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Mode = System.Security.Cryptography.CipherMode.ECB;
                aes.Padding = System.Security.Cryptography.PaddingMode.None;
                aes.KeySize = 256;
                aes.Key = key;
                using (var enc = aes.CreateEncryptor())
                {
                    // H = E(K, 0^128)
                    byte[] h = new byte[16];
                    enc.TransformBlock(h, 0, 16, h, 0);

                    // J0 = IV || 0x00000001（96 位 IV 专属）
                    byte[] j0 = new byte[16];
                    Buffer.BlockCopy(iv, 0, j0, 0, 12);
                    j0[15] = 1;

                    // 明文 = CTR(K, inc32(J0), C)
                    byte[] plain = new byte[cipherText.Length];
                    CtrXor(enc, j0, cipherText, plain);

                    // S = GHASH(AAD=空, C, len)
                    byte[] s = Ghash(h, null, cipherText);

                    // 期望 tag = E(K, J0) XOR S
                    byte[] ek0 = new byte[16];
                    enc.TransformBlock(j0, 0, 16, ek0, 0);
                    Xor(ek0, s);
                    for (int i = 0; i < 16; i++)
                        if (ek0[i] != tag[i]) return null;
                    return plain;
                }
            }
        }

        private static void CtrXor(System.Security.Cryptography.ICryptoTransform enc, byte[] j0, byte[] input, byte[] output)
        {
            // GCM 规范：密文流计数器从 inc32(J0) 开始（J0 本身只用于 tag 的 E(K,J0)）
            byte[] counter = (byte[])j0.Clone();
            byte[] stream = new byte[16];
            for (int off = 0; off < input.Length; off += 16)
            {
                Inc32(counter);
                enc.TransformBlock(counter, 0, 16, stream, 0);
                int n = Math.Min(16, input.Length - off);
                for (int i = 0; i < n; i++) output[off + i] = (byte)(input[off + i] ^ stream[i]);
            }
        }

        private static void Inc32(byte[] block)
        {
            for (int i = 15; i >= 12; i--)
            {
                block[i] = (byte)(block[i] + 1);
                if (block[i] != 0) break;
            }
        }

        // GHASH_H(AAD, C)：Y=0，逐块 Y=(Y^B)·H，末尾补长度块（AAD 位长 || C 位长，各 64 位大端）
        private static byte[] Ghash(byte[] h, byte[] aad, byte[] cipher)
        {
            byte[] y = new byte[16];
            if (aad != null && aad.Length > 0)
            {
                byte[] ab = new byte[16];
                int off = 0;
                while (off < aad.Length)
                {
                    int n = Math.Min(16, aad.Length - off);
                    Array.Clear(ab, 0, 16);
                    Buffer.BlockCopy(aad, off, ab, 0, n);
                    Xor(y, ab);
                    byte[] m = GfMul(y, h);
                    Buffer.BlockCopy(m, 0, y, 0, 16);
                    off += 16;
                }
            }
            byte[] cb = new byte[16];
            int co = 0;
            while (co < cipher.Length)
            {
                int n = Math.Min(16, cipher.Length - co);
                Array.Clear(cb, 0, 16);
                Buffer.BlockCopy(cipher, co, cb, 0, n);
                Xor(y, cb);
                byte[] m = GfMul(y, h);
                Buffer.BlockCopy(m, 0, y, 0, 16);
                co += 16;
            }
            byte[] lenBlock = new byte[16];
            WriteULongBE(lenBlock, 0, aad != null ? (ulong)aad.Length * 8 : 0);
            WriteULongBE(lenBlock, 8, (ulong)cipher.Length * 8);
            Xor(y, lenBlock);
            return GfMul(y, h);
        }

        // GF(2^128) 乘法（NIST 反射位序），Z·V 迭代
        private static byte[] GfMul(byte[] x, byte[] h)
        {
            byte[] z = new byte[16];
            byte[] v = new byte[16];
            Buffer.BlockCopy(h, 0, v, 0, 16);
            for (int i = 0; i < 128; i++)
            {
                if ((x[i >> 3] & (byte)(0x80 >> (i & 7))) != 0) Xor(z, v);
                int lsb = v[15] & 1;
                RightShift1(v);
                if (lsb != 0) v[0] ^= 0xE1;
            }
            return z;
        }

        private static void RightShift1(byte[] b)
        {
            for (int i = 15; i > 0; i--)
                b[i] = (byte)(((b[i] & 0xFE) >> 1) | ((b[i - 1] & 1) << 7));
            b[0] = (byte)(b[0] >> 1);
        }

        private static void Xor(byte[] dst, byte[] src)
        {
            for (int i = 0; i < 16; i++) dst[i] ^= src[i];
        }

        private static void WriteULongBE(byte[] b, int off, ulong v)
        {
            for (int i = 0; i < 8; i++) b[off + i] = (byte)(v >> (56 - i * 8));
        }
    }


    // ============ ZCode 凭证（解密 ~/.zcode/v2/credentials.json 的 enc:v1 JWT） ============
    internal static class ZcodeAuth
    {
        public const string Platform = "ZCode";
        public const string BaseUrl = "https://zcode.z.ai/api/v1";
        public const string AppVersion = "3.14.3";   // 与官方桌面端现行版一致；preview 按 app_version 过滤活动可见性
        public const string EncPrefix = "enc:v1:";

        public static string CredentialsPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zcode", "v2", "credentials.json"); }
        }

        // 与 zcode CLI 一致的密钥种子：优先环境变量，否则机器信息回退
        public static string ResolveSecret()
        {
            string env = Environment.GetEnvironmentVariable("ZCODE_CREDENTIAL_SECRET");
            if (!string.IsNullOrEmpty(env)) return env.Trim();
            string user = "unknown";
            try { user = Environment.UserName; } catch { }
            if (string.IsNullOrEmpty(user)) user = "unknown";
            return "zcode-credential-fallback:win32:" + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + ":" + user;
        }

        public static byte[] DeriveKey(string secret)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return sha.ComputeHash(Encoding.UTF8.GetBytes(secret ?? ""));
        }

        // 解密 enc:v1:<iv b64url>.<tag b64url>.<ct b64url>；失败返回 null
        public static string DecryptWithSecret(string enc, string secret)
        {
            try
            {
                if (string.IsNullOrEmpty(enc) || !enc.StartsWith(EncPrefix)) return null;
                string[] parts = enc.Substring(EncPrefix.Length).Split('.');
                if (parts.Length != 3) return null;
                byte[] iv = Base64UrlDecodeBytes(parts[0]);
                byte[] tag = Base64UrlDecodeBytes(parts[1]);
                byte[] ct = Base64UrlDecodeBytes(parts[2]);
                if (iv == null || tag == null || ct == null) return null;
                if (iv.Length != 12 || tag.Length != 16 || ct.Length == 0) return null;
                byte[] plain = ZcodeGcm.Decrypt(DeriveKey(secret), iv, tag, ct);
                return plain == null ? null : Encoding.UTF8.GetString(plain);
            }
            catch { return null; }
        }

        public static byte[] Base64UrlDecodeBytes(string s)
        {
            try
            {
                if (string.IsNullOrEmpty(s)) return null;
                string t = s.Replace('-', '+').Replace('_', '/');
                int pad = (4 - t.Length % 4) % 4;
                if (pad > 0) t = t + new string('=', pad);
                return Convert.FromBase64String(t);
            }
            catch { return null; }
        }

        public static string Base64UrlDecode(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = s.Replace('-', '+').Replace('_', '/');
            int pad = (4 - t.Length % 4) % 4;
            if (pad > 0) t = t + new string('=', pad);
            return Encoding.UTF8.GetString(Convert.FromBase64String(t));
        }

        // 取 JWT（解密失败/未登录返回 null）；JWT 仅在内存使用
        public static string GetJwt()
        {
            try
            {
                if (!File.Exists(CredentialsPath)) return null;
                var s = JsonUtil.ParseJson(File.ReadAllText(CredentialsPath, Encoding.UTF8));
                if (s == null) return null;
                object v;
                if (!s.TryGetValue("zcodejwttoken", out v)) return null;
                string enc = v as string;
                if (string.IsNullOrEmpty(enc) || !enc.StartsWith(EncPrefix)) return null;
                return DecryptWithSecret(enc, ResolveSecret());
            }
            catch { return null; }
        }

        public static string JwtUserId(string jwt)
        {
            try
            {
                if (string.IsNullOrEmpty(jwt)) return null;
                string[] parts = jwt.Split('.');
                if (parts.Length < 2) return null;
                var d = JsonUtil.ParseJson(Base64UrlDecode(parts[1]));
                if (d == null) return null;
                object v;
                if (d.TryGetValue("user_id", out v) && v != null) return v.ToString();
                if (d.TryGetValue("sub", out v) && v != null) return v.ToString();
                return null;
            }
            catch { return null; }
        }

        public static List<AccountInfo> ListAccounts()
        {
            var list = new List<AccountInfo>();
            string jwt = GetJwt();
            if (string.IsNullOrEmpty(jwt)) return list;
            var info = new AccountInfo();
            info.Brand = Platform;
            info.Username = JwtUserId(jwt);
            info.HasToken = true;
            list.Add(info);
            return list;
        }
    }

    // ============ ZCode 本地状态（device_mid + 已领套餐去重） ============
    internal class ZcodeClaimedDto
    {
        public string plan_id;
        public string name;
        public long units;
        public long ts;
    }

    internal class ZcodeStateDto
    {
        public string device_mid;
        public List<ZcodeClaimedDto> claimed = new List<ZcodeClaimedDto>();
    }

    internal static class ZcodeState
    {
        public static string StatePath { get { return Path.Combine(HistoryStore.DataDir, "zcode_state.json"); } }

        public static ZcodeStateDto Load()
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    var dto = new JavaScriptSerializer().Deserialize<ZcodeStateDto>(File.ReadAllText(StatePath, Encoding.UTF8));
                    if (dto != null)
                    {
                        if (dto.claimed == null) dto.claimed = new List<ZcodeClaimedDto>();
                        return dto;
                    }
                }
            }
            catch { }
            return new ZcodeStateDto();
        }

        public static void Save(ZcodeStateDto state)
        {
            try
            {
                if (!Directory.Exists(HistoryStore.DataDir)) Directory.CreateDirectory(HistoryStore.DataDir);
                string json = new JavaScriptSerializer().Serialize(state);
                string tmp = StatePath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(StatePath)) File.Replace(tmp, StatePath, null);
                else File.Move(tmp, StatePath);
            }
            catch { }
        }

        public static string GetDeviceMid()
        {
            var st = Load();
            if (!string.IsNullOrEmpty(st.device_mid)) return st.device_mid;
            string mid = Guid.NewGuid().ToString();
            st.device_mid = mid;
            Save(st);
            return mid;
        }

        public static bool IsClaimed(string planId)
        {
            if (string.IsNullOrEmpty(planId)) return false;
            var st = Load();
            foreach (var c in st.claimed)
                if (c != null && c.plan_id == planId) return true;
            return false;
        }

        public static void MarkClaimed(string planId, string name, long units)
        {
            if (string.IsNullOrEmpty(planId)) return;
            var st = Load();
            foreach (var c in st.claimed)
                if (c != null && c.plan_id == planId) return;
            st.claimed.Add(new ZcodeClaimedDto { plan_id = planId, name = name, units = units, ts = DateTimeOffset.Now.ToUnixTimeSeconds() });
            if (st.claimed.Count > 200) st.claimed.RemoveRange(0, st.claimed.Count - 200);
            Save(st);
        }
    }

    // ============ ZCode 阿里云无痕验证码（真实 Edge + CDP，零外部依赖） ============
    internal static class ZcodeCaptcha
    {
        private const int SolveTimeoutMs = 90000;
        private const int PollIntervalMs = 1000;
        private const int EdgeReadyTimeoutMs = 35000;

        public static string FindEdgePath()
        {
            string[] candidates = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
            };
            foreach (string p in candidates)
                if (File.Exists(p)) return p;
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue(null);
                        if (v != null)
                        {
                            string p = Environment.ExpandEnvironmentVariables(v.ToString());
                            if (File.Exists(p)) return p;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        // sceneId/region/prefix 来自 client/configs（失败用社区实测默认值）
        public static string Solve()
        {
            string scene = "11xygtvd", region = "sgp", prefix = "no8xfe";
            try
            {
                ApiResp r = HttpApi.GetWithStatus(ZcodeAuth.BaseUrl + "/client/configs?app_version=" + ZcodeAuth.AppVersion, null);
                if (r != null && r.Ok && !string.IsNullOrEmpty(r.Body))
                {
                    var cfg = JsonUtil.GetDict(JsonUtil.GetDict(JsonUtil.GetDict(JsonUtil.ParseJson(r.Body), "data"), "configs"), "captcha");
                    if (cfg != null)
                    {
                        string s = JsonUtil.GetString(cfg, "sceneId");
                        string rg = JsonUtil.GetString(cfg, "region");
                        string pf = JsonUtil.GetString(cfg, "prefix");
                        if (!string.IsNullOrEmpty(s)) scene = s;
                        if (!string.IsNullOrEmpty(rg)) region = rg;
                        if (!string.IsNullOrEmpty(pf)) prefix = pf;
                    }
                }
            }
            catch { }
            return SolveWithScene(scene, region, prefix);
        }

        private static string SolveWithScene(string scene, string region, string prefix)
        {
            string edge = FindEdgePath();
            if (string.IsNullOrEmpty(edge)) throw new Exception("未找到 Edge 浏览器，无法完成验证码");
            string html = BuildCaptchaHtml(scene, region, prefix);
            string tmpDir = Path.Combine(Path.GetTempPath(), "TraeSign_cdp_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            string htmlPath = Path.Combine(tmpDir, "captcha.html");
            File.WriteAllText(htmlPath, html, new UTF8Encoding(false));
            string fileUrl = "file:///" + htmlPath.Replace('\\', '/');

            // 端口传 0：Edge 自选可用端口并写入 DevToolsActivePort 文件，彻底避开
            // "程序预选端口被 Edge 绑定失败"这类环境问题
            var psi = new ProcessStartInfo();
            psi.FileName = edge;
            psi.Arguments = "--remote-debugging-port=0 --user-data-dir=\"" + Path.Combine(tmpDir, "profile") + "\" --no-first-run --no-default-browser-check --window-size=420,300 --app=" + fileUrl;
            psi.UseShellExecute = false;
            psi.RedirectStandardError = true;
            RuntimeLog.Write("captcha: launching edge (port=0, devtools-file mode)");
            Process proc = Process.Start(psi);
            try { proc.ErrorDataReceived += OnEdgeStderr; proc.BeginErrorReadLine(); } catch { }
            try
            {
                string wsUrl = WaitForCdpReady(Path.Combine(tmpDir, "profile"), proc, EdgeReadyTimeoutMs);
                RuntimeLog.Write("captcha: cdp connected");
                string param = EvalUntilParam(wsUrl, SolveTimeoutMs);
                RuntimeLog.Write("captcha: solved len=" + (param ?? "").Length);
                return param;
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("captcha: FAILED " + ex.Message);
                throw;
            }
            finally
            {
                try { if (proc != null && !proc.HasExited) Process.Start("taskkill", "/PID " + proc.Id + " /T /F"); } catch { }
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        private static void OnEdgeStderr(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data)) RuntimeLog.Write("edge: " + e.Data);
        }

        // Chromium 就绪后会在 user-data-dir 写 DevToolsActivePort（首行=端口）。
        // 等该文件出现 → 读端口 → HTTP 确认 → 返回 captcha 页的 ws 地址
        private static string WaitForCdpReady(string profileDir, Process proc, int timeoutMs)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            string devFile = Path.Combine(profileDir, "DevToolsActivePort");
            bool fileSeen = false;
            int port = 0;
            while (DateTime.Now < deadline)
            {
                Thread.Sleep(600);
                if (!fileSeen && File.Exists(devFile))
                {
                    fileSeen = true;
                    try
                    {
                        string[] lines = File.ReadAllLines(devFile);
                        if (lines.Length > 0) int.TryParse(lines[0].Trim(), out port);
                        RuntimeLog.Write("captcha: devtools-file seen port=" + port + " at " + (long)(DateTime.Now - deadline.AddMilliseconds(-timeoutMs)).TotalMilliseconds + "ms");
                    }
                    catch { }
                }
                if (!fileSeen)
                {
                    bool alive = false;
                    try { alive = !proc.HasExited; } catch { alive = true; }
                    if (!alive) throw new Exception("Edge 启动后立即退出（可能被安全软件拦截），exit=" + SafeExitCode(proc));
                    continue;
                }
                if (port > 0)
                {
                    string wsUrl = FindPageWs(port);
                    if (wsUrl != null) return wsUrl;
                }
            }
            throw new Exception("Edge 调试端口未就绪（devFile=" + (fileSeen ? "已出现但页面未就绪" : "35 秒未出现") + "），请重试一次");
        }

        private static string SafeExitCode(Process proc)
        {
            try { return proc.ExitCode.ToString(); } catch { return "?"; }
        }

        private static string FindPageWs(int port)
        {
            ApiResp r = HttpGetLocal("http://127.0.0.1:" + port + "/json/list");
            if (r.Status != 200 || string.IsNullOrEmpty(r.Body)) return null;
            var s = new JavaScriptSerializer();
            object arr = null;
            try { arr = s.DeserializeObject(r.Body); } catch { }
            var list = arr as System.Collections.ArrayList;
            if (list == null) return null;
            foreach (object item in list)
            {
                var d = item as Dictionary<string, object>;
                if (d == null) continue;
                object type, ws;
                if (d.TryGetValue("type", out type) && (type as string) == "page" && d.TryGetValue("webSocketDebuggerUrl", out ws))
                    return ws as string;
            }
            return null;
        }

        private static string BuildCaptchaHtml(string scene, string region, string prefix)
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>TraeSign</title></head><body>");
            sb.Append("<div id=\"cap\"></div><button id=\"btn\">go</button>");
            sb.Append("<script src=\"https://o.alicdn.com/captcha-frontend/aliyunCaptcha/AliyunCaptcha.js\"></script><script>");
            sb.Append("window.__P=null;window.__E=null;");
            sb.Append("(function t(){if(typeof initAliyunCaptcha!==\"function\"){setTimeout(t,200);return}");
            sb.Append("initAliyunCaptcha({SceneId:").Append(JsonQuote(scene));
            sb.Append(",mode:\"popup\",region:").Append(JsonQuote(region));
            sb.Append(",prefix:").Append(JsonQuote(prefix));
            sb.Append(",language:\"zh-cn\",element:\"#cap\",button:\"#btn\"");
            sb.Append(",getInstance:function(i){(i.startTracelessVerification||i.show).call(i)}");
            // 实测：无痕验证成功时 success 回调参数本身就是 verifyParam 字符串
            sb.Append(",success:function(r){window.__P=(typeof r===\"string\")?r:((r&&r.captchaVerifyParam)||JSON.stringify(r))}");
            sb.Append(",fail:function(e){window.__E=\"fail:\"+JSON.stringify(e)}");
            sb.Append(",onError:function(e){window.__E=\"onError:\"+JSON.stringify(e)}});})();");
            sb.Append("</script></body></html>");
            return sb.ToString();
        }

        private static string JsonQuote(string s)
        {
            return new JavaScriptSerializer().Serialize(s ?? "");
        }

        private static int GetFreePort()
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static ApiResp HttpGetLocal(string url)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 3000;
                req.ReadWriteTimeout = 3000;
                req.Proxy = null;
                using (var resp = req.GetResponse())
                using (var rs = resp.GetResponseStream())
                using (var sr = new StreamReader(rs, Encoding.UTF8))
                    return new ApiResp { Status = 200, Body = sr.ReadToEnd() };
            }
            catch (WebException ex)
            {
                var r = new ApiResp();
                if (ex.Response != null)
                {
                    try
                    {
                        using (var rs = ex.Response.GetResponseStream())
                        using (var sr = new StreamReader(rs, Encoding.UTF8)) r.Body = sr.ReadToEnd();
                        r.Status = (int)((HttpWebResponse)ex.Response).StatusCode;
                    }
                    catch { }
                }
                return r;
            }
            catch { return new ApiResp(); }
        }

        // 连 CDP，轮询 window.__P/__E 直到出参/报错/超时
        private static string EvalUntilParam(string wsUrl, int timeoutMs)
        {
            using (var ws = new System.Net.WebSockets.ClientWebSocket())
            {
                ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None).Wait(10000);
                if (ws.State != System.Net.WebSockets.WebSocketState.Open) throw new Exception("无法连接 Edge 调试通道");
                DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
                int msgId = 0;
                while (DateTime.Now < deadline)
                {
                    Thread.Sleep(PollIntervalMs);
                    string expr = "JSON.stringify({p:window.__P,e:window.__E})";
                    msgId++;
                    string req = "{\"id\":" + msgId + ",\"method\":\"Runtime.evaluate\",\"params\":{\"expression\":" + JsonQuote(expr) + ",\"returnByValue\":true}}";
                    string resp = WsRoundTrip(ws, req, msgId);
                    if (resp == null) throw new Exception("与 Edge 的调试连接中断");
                    var root = JsonUtil.ParseJson(resp);
                    var result = JsonUtil.GetDict(root, "result");
                    var resultInner = JsonUtil.GetDict(result, "result");
                    string value = JsonUtil.GetString(resultInner, "value");
                    if (string.IsNullOrEmpty(value)) continue;
                    var state = JsonUtil.ParseJson(value);
                    string p = JsonUtil.GetString(state, "p");
                    string e = JsonUtil.GetString(state, "e");
                    if (!string.IsNullOrEmpty(p)) return p;
                    if (!string.IsNullOrEmpty(e)) throw new Exception("验证码失败：" + e);
                }
                throw new Exception("验证码超时（可能触发了人工滑动验证），请稍后重试或在 ZCode 客户端手动领取");
            }
        }

        private static string WsRoundTrip(System.Net.WebSockets.ClientWebSocket ws, string req, int msgId)
        {
            try
            {
                byte[] payload = Encoding.UTF8.GetBytes(req);
                ws.SendAsync(new ArraySegment<byte>(payload), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None).Wait(10000);
                var buf = new byte[1 << 16];
                var sb = new StringBuilder();
                var cts = new CancellationTokenSource(30000);
                while (true)
                {
                    var seg = new ArraySegment<byte>(buf);
                    var tr = ws.ReceiveAsync(seg, cts.Token);
                    if (!tr.Wait(30000)) return null;
                    var r = tr.Result;
                    if (r.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) return null;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                    if (r.EndOfMessage)
                    {
                        string text = sb.ToString();
                        sb.Length = 0;
                        // CDP 会推送事件（无 id），只取匹配的响应
                        var d = JsonUtil.ParseJson(text);
                        if (JsonUtil.GetInt(d, "id") == msgId) return text;
                    }
                }
            }
            catch { return null; }
        }
    }

    // ============ ZCode 活动套餐领取引擎 ============
    internal static class ZcodeRunner
    {
        internal class ZcodePlan
        {
            public string PlanId;
            public string Name;
            public long Units;
            public string ShowName;
        }

        // preview 响应 → 可领套餐列表（静态纯函数，供测试）
        public static List<ZcodePlan> ParsePreviewPlans(string json)
        {
            var list = new List<ZcodePlan>();
            var root = JsonUtil.ParseJson(json);
            if (JsonUtil.GetInt(root, "code") != 0) return list;
            var data = JsonUtil.GetDict(root, "data");
            if (data == null) return list;
            object plansObj;
            if (!data.TryGetValue("plans", out plansObj)) return list;
            var plans = plansObj as System.Collections.ArrayList;   // JavaScriptSerializer 数组即 ArrayList（踩坑 #13）
            if (plans == null) return list;
            foreach (object item in plans)
            {
                var p = item as Dictionary<string, object>;
                if (p == null) continue;
                var plan = new ZcodePlan();
                plan.PlanId = JsonUtil.GetString(p, "plan_id");
                plan.Name = JsonUtil.GetString(p, "name");
                object ents;
                if (p.TryGetValue("entitlements", out ents))
                {
                    var entList = ents as System.Collections.ArrayList;
                    if (entList != null && entList.Count > 0)
                    {
                        var e0 = entList[0] as Dictionary<string, object>;
                        if (e0 != null)
                        {
                            object u;
                            if (e0.TryGetValue("grant_units", out u) && u != null)
                            {
                                try { plan.Units = Convert.ToInt64(u); } catch { }
                            }
                            plan.ShowName = JsonUtil.GetString(e0, "show_name");
                        }
                    }
                }
                if (!string.IsNullOrEmpty(plan.PlanId)) list.Add(plan);
            }
            return list;
        }

        public static CheckinResult Run(bool queryOnly)
        {
            string jwt = ZcodeAuth.GetJwt();
            if (string.IsNullOrEmpty(jwt))
                return CheckinResult.Fail(-1, "未找到 ZCode 登录态，请先登录 ZCode 客户端或 CLI", ZcodeAuth.Platform);
            string userId = ZcodeAuth.JwtUserId(jwt);
            string deviceMid = ZcodeState.GetDeviceMid();

            try
            {
                ReportActivation(deviceMid);   // 激活事件（best effort，疑似投放资格信号）

                ApiResp pv = Preview(deviceMid, jwt);
                if (pv == null)
                    return CheckinResult.Fail(-3, "网络异常：ZCode 服务不可达", ZcodeAuth.Platform);
                int code = JsonUtil.GetInt(JsonUtil.ParseJson(pv.Body), "code");
                if (pv.Status == 401 || code == 401)
                    return CheckinResult.Fail(-6, "ZCode 登录已失效，请重新登录 ZCode 客户端", ZcodeAuth.Platform);
                if (code != 0)
                    return CheckinResult.Fail(code, "ZCode 服务返回错误(" + code + ")", ZcodeAuth.Platform);

                List<ZcodePlan> plans = ParsePreviewPlans(pv.Body);
                ZcodePlan target = null;
                foreach (var p in plans)
                    if (!ZcodeState.IsClaimed(p.PlanId)) { target = p; break; }

                if (queryOnly)
                {
                    if (target == null)
                        return new CheckinResult
                        {
                            Success = false, Code = 0, CheckedIn = false, Already = true,
                            Username = userId, Base = null, Extra = null,
                            DisplayText = plans.Count > 0 ? "暂无可领新套餐" : "暂无可领套餐",
                            Brand = ZcodeAuth.Platform
                        };
                    return new CheckinResult
                    {
                        Success = false, Code = 0, CheckedIn = false, Already = false,
                        Username = userId, HasClaimable = true,
                        DisplayText = "发现可领套餐：" + (target.Name ?? target.PlanId) + "（" + FormatUnits(target.Units) + "）",
                        Brand = ZcodeAuth.Platform
                    };
                }

                if (target == null)
                    return new CheckinResult
                    {
                        Success = false, Code = 0, CheckedIn = false, Already = true,
                        Username = userId, DisplayText = "当前无可领新套餐，无需领取",
                        Brand = ZcodeAuth.Platform
                    };

                return ClaimPlan(deviceMid, jwt, userId, target);
            }
            catch (Exception ex)
            {
                return CheckinResult.Fail(-3, "网络异常: " + ex.Message, ZcodeAuth.Platform);
            }
        }

        private static CheckinResult ClaimPlan(string deviceMid, string jwt, string userId, ZcodePlan plan)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string vp;
                try { vp = ZcodeCaptcha.Solve(); }
                catch (Exception ex)
                {
                    if (attempt == 1) return CheckinResult.Fail(-4, "验证码未通过：" + ex.Message, ZcodeAuth.Platform);
                    continue;
                }

                var headers = BuildHeaders(deviceMid, jwt);
                headers["X-Aliyun-Captcha-Verify-Param"] = vp;
                var body = new Dictionary<string, object>();
                body["plan_id"] = plan.PlanId;
                string bodyJson = new JavaScriptSerializer().Serialize(body);
                ApiResp r = HttpApi.PostWithStatus(ZcodeAuth.BaseUrl + "/zcode-plan/billing/claim", headers, bodyJson);
                if (r == null || r.Status == 0)
                    return CheckinResult.Fail(-3, "网络异常：领取请求不可达", ZcodeAuth.Platform);

                var resp = JsonUtil.ParseJson(r.Body);
                int code = JsonUtil.GetInt(resp, "code");
                string msg = JsonUtil.GetStringAny(resp, "msg", "message");

                if (r.Status == 401 || code == 401)
                    return CheckinResult.Fail(-6, "ZCode 登录已失效，请重新登录 ZCode 客户端", ZcodeAuth.Platform);
                if (code == 0)
                {
                    ZcodeState.MarkClaimed(plan.PlanId, plan.Name, plan.Units);
                    return new CheckinResult
                    {
                        Success = true, Code = 0, CheckedIn = true,
                        Username = userId, Base = (int)Math.Min(plan.Units, int.MaxValue), Extra = null,
                        DisplayText = "领取成功：" + (plan.Name ?? plan.PlanId) + " +" + FormatUnits(plan.Units) + " token",
                        Brand = ZcodeAuth.Platform
                    };
                }
                if (code == 3007)
                {
                    if (attempt == 1)
                        return CheckinResult.Fail(3007, "验证码校验失败（已重试一次）", ZcodeAuth.Platform);
                    continue;   // 换新验证码重试一次
                }
                if (code == 1003)
                {
                    ZcodeState.MarkClaimed(plan.PlanId, plan.Name, plan.Units);
                    return new CheckinResult
                    {
                        Success = false, Code = 1003, CheckedIn = false, Already = true,
                        Username = userId, DisplayText = "该套餐已领取过", Brand = ZcodeAuth.Platform
                    };
                }
                if (code == 1005)
                {
                    var data = JsonUtil.GetDict(resp, "data");
                    var planData = JsonUtil.GetDict(data, "plan");
                    long endsAt = 0;
                    if (planData != null) endsAt = JsonUtil.GetLong(planData, "ends_at");
                    return new CheckinResult
                    {
                        Success = false, Code = 1005, CheckedIn = false,
                        Username = userId, RetryAfterAt = endsAt,
                        DisplayText = "名额已领完" + (endsAt > 0 ? "，服务端将于 " + DateTimeOffset.FromUnixTimeSeconds(endsAt).LocalDateTime.ToString("HH:mm") + " 前后补货" : ""),
                        Brand = ZcodeAuth.Platform
                    };
                }
                if (code == 1002)
                {
                    ZcodeState.MarkClaimed(plan.PlanId, plan.Name, plan.Units);   // 活动已结束，标记避免反复尝试
                    return CheckinResult.Fail(1002, "该活动已结束", ZcodeAuth.Platform);
                }
                if (code == 1004)
                {
                    ZcodeState.MarkClaimed(plan.PlanId, plan.Name, plan.Units);   // 不符合条件，标记避免反复尝试
                    return CheckinResult.Fail(1004, "不符合领取条件", ZcodeAuth.Platform);
                }
                return CheckinResult.Fail(code, msg ?? "领取失败", ZcodeAuth.Platform);
            }
            return CheckinResult.Fail(-4, "验证码未通过", ZcodeAuth.Platform);
        }

        private static Dictionary<string, string> BuildHeaders(string deviceMid, string jwt)
        {
            var h = new Dictionary<string, string>();
            h["Authorization"] = "Bearer " + jwt;
            h["User-Agent"] = "ZCode/" + ZcodeAuth.AppVersion;
            h["X-Title"] = "Z Code@electron";
            h["X-ZCode-App-Version"] = ZcodeAuth.AppVersion;
            h["X-Platform"] = "win32-x64";
            h["X-Release-Channel"] = "stable";
            h["X-Client-Language"] = "zh-CN";
            h["X-Client-Timezone"] = "Asia/Shanghai";
            h["X-Os-Category"] = "windows";
            h["X-Os-Version"] = Environment.OSVersion.Version.ToString();
            // billing 全家桶必需 X-Device-Mid，缺失报 3001
            h["X-Device-Mid"] = deviceMid;
            h["x-request-id"] = Guid.NewGuid().ToString();
            return h;
        }

        private static void ReportActivation(string deviceMid)
        {
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var body = new Dictionary<string, object>();
                var events = new List<object>();
                var e1 = new Dictionary<string, object>();
                e1["event"] = "app_launch";
                e1["ts"] = now;
                events.Add(e1);
                var e2 = new Dictionary<string, object>();
                e2["event"] = "app_daily_active";
                e2["ts"] = now;
                events.Add(e2);
                body["events"] = events;
                string json = new JavaScriptSerializer().Serialize(body);
                var h = new Dictionary<string, string>();
                h["X-Device-Mid"] = deviceMid;
                h["x-request-id"] = Guid.NewGuid().ToString();
                HttpApi.PostWithStatus(ZcodeAuth.BaseUrl + "/event/report", h, json);
            }
            catch { }
        }

        private static ApiResp Preview(string deviceMid, string jwt)
        {
            string platform = Environment.Is64BitOperatingSystem ? "win32-x64" : "win32-x86";
            string url = ZcodeAuth.BaseUrl + "/zcode-plan/billing/preview?app_version=" + ZcodeAuth.AppVersion + "&platform=" + platform;
            return HttpGetBilling(url, BuildHeaders(deviceMid, jwt));
        }

        private static ApiResp HttpGetBilling(string url, Dictionary<string, string> headers)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 60000;
                req.ReadWriteTimeout = 60000;
                req.Proxy = null;
                HttpApi.ApplyHeaders(req, headers);
                using (var resp = req.GetResponse())
                using (var rs = resp.GetResponseStream())
                using (var sr = new StreamReader(rs, Encoding.UTF8))
                    return new ApiResp { Status = (int)((HttpWebResponse)resp).StatusCode, Body = sr.ReadToEnd() };
            }
            catch (WebException ex)
            {
                var r = new ApiResp();
                if (ex.Response != null)
                {
                    try
                    {
                        var hr = (HttpWebResponse)ex.Response;
                        r.Status = (int)hr.StatusCode;
                        using (var rs = hr.GetResponseStream())
                        using (var sr = new StreamReader(rs, Encoding.UTF8)) r.Body = sr.ReadToEnd();
                    }
                    catch { }
                }
                return r;
            }
            catch { return null; }
        }

        private static string FormatUnits(long units)
        {
            if (units >= 100000000L && units % 100000000L == 0) return (units / 100000000L) + " 亿";
            if (units >= 10000L && units % 10000L == 0) return (units / 10000L) + " 万";
            return units.ToString();
        }
    }

    // ============ 历史记录 ============
    internal class HistoryEntry
    {
        public string brand;
        public string date;          // yyyy-MM-dd
        public bool success;
        public bool checked_in;
        public bool occupied;        // 9095: 本机已被另一账号占用
        public int code;             // 结果码：0=成功 9095=占用 -6=token 异常
        public int? baseCredits;
        public int? extra;
        public long ts;
    }

    internal class HistoryData
    {
        [ScriptIgnore]
        public string user;   // 旧格式兼容读取（迁移后置空，序列化忽略）
        public string activeBrand;
        public Dictionary<string, string> users = new Dictionary<string, string>();
        public List<HistoryEntry> history = new List<HistoryEntry>();
    }

    internal static class HistoryStore
    {
        public const string FallbackBrand = "TRAE SOLO CN";
        public static string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TraeCheckin");
        public static string HistoryPath { get { return Path.Combine(DataDir, "history.json"); } }

        public static HistoryData Load()
        {
            try
            {
                if (File.Exists(HistoryPath))
                {
                    var js = new JavaScriptSerializer();
                    return js.Deserialize<HistoryData>(File.ReadAllText(HistoryPath, Encoding.UTF8));
                }
            }
            catch { }
            return new HistoryData();
        }

        // 加载并迁移旧格式：user→users[默认品牌]、历史条目补 brand、补 activeBrand
        public static HistoryData LoadMigrated()
        {
            var data = Load();
            bool dirty = false;
            string fb = data.activeBrand;
            if (string.IsNullOrEmpty(fb)) { fb = FallbackBrand; data.activeBrand = fb; dirty = true; }
            if (!string.IsNullOrEmpty(data.user) && data.users.Count == 0)
            {
                data.users[fb] = data.user;
                data.user = null;
                dirty = true;
            }
            for (int i = 0; i < data.history.Count; i++)
            {
                var e = data.history[i];
                if (e != null && string.IsNullOrEmpty(e.brand))
                {
                    e.brand = fb;
                    dirty = true;
                }
            }
            if (dirty) Save(data);
            return data;
        }

        public static string GetActiveBrand()
        {
            try { return LoadMigrated().activeBrand; }
            catch { return null; }
        }

        public static void SetActiveBrand(string brand)
        {
            if (string.IsNullOrEmpty(brand)) return;
            var data = LoadMigrated();
            if (data.activeBrand != brand)
            {
                data.activeBrand = brand;
                Save(data);
            }
        }

        public static string GetUser(string brand)
        {
            try
            {
                var data = LoadMigrated();
                string u;
                if (data.users.TryGetValue(brand, out u)) return u;
            }
            catch { }
            return null;
        }

        public static void SetUser(string brand, string username)
        {
            if (string.IsNullOrEmpty(brand) || string.IsNullOrEmpty(username)) return;
            var data = LoadMigrated();
            string old;
            if (!data.users.TryGetValue(brand, out old) || old != username)
            {
                data.users[brand] = username;
                Save(data);
            }
        }

        public static void Upsert(HistoryEntry entry)
        {
            if (string.IsNullOrEmpty(entry.brand)) entry.brand = FallbackBrand;
            var data = LoadMigrated();
            data.history.RemoveAll(e => e != null && e.brand == entry.brand && e.date == entry.date);
            data.history.Add(entry);
            data.history.Sort(delegate(HistoryEntry a, HistoryEntry b)
            {
                int c = string.CompareOrdinal(a.brand, b.brand);
                return c != 0 ? c : string.CompareOrdinal(a.date, b.date);
            });
            Save(data);
        }

        public static Dictionary<string, bool> SuccessMap(int year, int month, string brand)
        {
            var map = new Dictionary<string, bool>();
            var data = LoadMigrated();
            string prefix = year.ToString("0000") + "-" + month.ToString("00") + "-";
            foreach (var e in data.history)
            {
                if (e == null || e.date == null) continue;
                if (e.brand != brand) continue;
                if (e.date.Length >= prefix.Length && e.date.StartsWith(prefix))
                    map[e.date] = e.success;
            }
            return map;
        }

        public static bool TodaySuccess(string brand)
        {
            return TodayEntry(brand) != null;
        }

        public static HistoryEntry TodayEntry(string brand)
        {
            var data = LoadMigrated();
            string key = DateTime.Now.ToString("yyyy-MM-dd");
            foreach (var e in data.history)
                if (e != null && e.brand == brand && e.date == key) return e;
            return null;
        }

        public static bool BrandHasTodayAttempt(string brand)
        {
            return TodayEntry(brand) != null;
        }

        private static void Save(HistoryData data)
        {
            try
            {
                if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);
                var js = new JavaScriptSerializer();
                string json = js.Serialize(data);
                string tmp = HistoryPath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(HistoryPath))
                    File.Replace(tmp, HistoryPath, null);
                else
                    File.Move(tmp, HistoryPath);
            }
            catch { }
        }
    }

    // ============ 每日调度（每平台独立一份） ============
    internal class DailyScheduler
    {
        private static readonly TimeSpan Target = new TimeSpan(0, 5, 0);
        private static readonly int[] BackoffMinutes = { 5, 15, 30, 60, 120 };

        private System.Windows.Forms.Timer _timer;
        private TrayApp _app;
        private readonly string _platform;
        private DateTime? _lastAttemptDate;
        private DateTime? _nextRetryAt;
        private int _retryCount;

        public DailyScheduler(TrayApp app, string platform)
        {
            _app = app;
            _platform = platform;
            _lastAttemptDate = null;
            _nextRetryAt = null;
            _retryCount = 0;
        }

        public DateTime? NextRetryAt { get { return _nextRetryAt; } }

        public void Start()
        {
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 30000;
            _timer.Tick += OnTick;
            _timer.Start();
        }

        public void MarkManualDone()
        {
            _lastAttemptDate = DateTime.Now.Date;
            _nextRetryAt = null;
            _retryCount = 0;
        }

        // 该账号今日已尝试过（成功或占用）则当天不再触发
        public static bool ShouldStopToday(CheckinResult r)
        {
            return r.Success || r.CheckedIn || r.Code == 9095;
        }

        // 签到尝试结果回填：成功/占用→当天停止；其他失败→退避重试
        public void NotifyResult(CheckinResult r)
        {
            if (ShouldStopToday(r))
            {
                MarkManualDone();
            }
            else
            {
                _nextRetryAt = DateTime.Now.AddMinutes(BackoffMinutes[Math.Min(_retryCount, BackoffMinutes.Length - 1)]);
                if (_retryCount < BackoffMinutes.Length - 1) _retryCount++;
            }
        }

        // 切换账号时重置：新账号今日未尝试→立即补签；已尝试→按记录跳过
        public void ResetForBrand(string brand)
        {
            if (HistoryStore.BrandHasTodayAttempt(brand))
            {
                _lastAttemptDate = DateTime.Now.Date;
                _nextRetryAt = null;
                _retryCount = 0;
            }
            else
            {
                _lastAttemptDate = null;
                _nextRetryAt = null;
                _retryCount = 0;
            }
        }

        // 纯逻辑（供测试）: now 时刻是否应该触发签到
        public static bool ShouldCheckinNow(DateTime now, DateTime? lastAttemptDate, DateTime? nextRetryAt, TimeSpan target)
        {
            if (lastAttemptDate == now.Date && nextRetryAt == null) return false;
            if (nextRetryAt == null && now.TimeOfDay < target) return false;
            if (nextRetryAt != null && now < nextRetryAt.Value) return false;
            return true;
        }

        private void OnTick(object sender, EventArgs e)
        {
            var now = DateTime.Now;
            if (now.Hour >= 23) return;                     // 当天放弃
            if (!ShouldCheckinNow(now, _lastAttemptDate, _nextRetryAt, Target)) return;
            _app.RunCheckinAsync(false, _platform, null);
        }
    }

    // ============ 单平台面板（Trae / WorkBuddy 复用） ============
    internal class PlatformPanel : UserControl
    {
        private readonly TrayApp _app;
        public readonly string Platform;

        private ComboBox _accountCombo;
        private Button _refreshBtn;
        private Button _authBtn;
        private Label _accountDetailLabel;
        private Label _todayStatusLabel;
        private Label _creditsLabel;
        private Label _occupiedLabel;
        private Label _autoNoteLabel;
        private Label _retryLabel;
        private Button _checkinBtn;
        private GroupBox _calendarGroup;
        private CalendarControl _calendar;
        private bool _changingCombo;
        private DateTime _calShownDate;   // 日历当前渲染对应的"今天"，用于跨天自动翻月

        public PlatformPanel(TrayApp app, string platform)
        {
            _app = app;
            Platform = platform;
            _changingCombo = false;
            _calShownDate = DateTime.MinValue;
            Dock = DockStyle.Fill;
            Font = new Font("Microsoft YaHei UI", 9f);

            bool isWb = (platform == "WorkBuddy");
            bool isZc = (platform == "ZCode");

            // ---- 账号区 ----
            var accGroup = new GroupBox();
            accGroup.Dock = DockStyle.Top;
            accGroup.Height = 104;
            accGroup.Text = "账号";
            accGroup.Padding = new Padding(10, 6, 10, 4);

            _accountCombo = new ComboBox();
            _accountCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _accountCombo.Location = new Point(14, 22);
            _accountCombo.Width = 310;
            _accountCombo.SelectedIndexChanged += delegate { OnAccountChanged(); };

            _refreshBtn = new Button();
            _refreshBtn.Text = isWb || isZc ? "刷新状态" : "刷新账号状态";
            _refreshBtn.Location = new Point(330, 21);
            _refreshBtn.Size = new Size(84, 25);
            _refreshBtn.Click += delegate { _app.RefreshAccountsAndStatus(); };

            _authBtn = new Button();
            _authBtn.Text = "登录授权";
            _authBtn.Location = new Point(330, 50);
            _authBtn.Size = new Size(84, 25);
            _authBtn.Visible = isWb;
            _authBtn.Click += delegate { _app.StartWorkbuddyAuthAsync(); };

            _accountDetailLabel = new Label();
            _accountDetailLabel.Location = new Point(14, 52);
            _accountDetailLabel.AutoSize = false;
            _accountDetailLabel.Height = 40;
            _accountDetailLabel.Width = (isWb || isZc) ? 310 : 400;
            _accountDetailLabel.Text = isZc
                ? "未找到 ZCode 登录态，请先登录 ZCode 客户端或 CLI"
                : (isWb ? "未授权：点击“登录授权”完成一次浏览器登录" : "未找到登录态，请先运行 Trae 桌面端登录");

            accGroup.Controls.Add(_accountCombo);
            accGroup.Controls.Add(_refreshBtn);
            accGroup.Controls.Add(_authBtn);
            accGroup.Controls.Add(_accountDetailLabel);

            // ---- 今日签到区 / 套餐领取区 ----
            var todayGroup = new GroupBox();
            todayGroup.Dock = DockStyle.Top;
            todayGroup.Height = 88;
            todayGroup.Text = isZc ? "套餐领取" : "今日签到";
            todayGroup.Padding = new Padding(10, 6, 10, 4);

            _todayStatusLabel = new Label();
            _todayStatusLabel.Location = new Point(14, 24);
            _todayStatusLabel.AutoSize = false;
            _todayStatusLabel.Width = 400;
            _todayStatusLabel.Font = new Font(this.Font.FontFamily, 10f, FontStyle.Bold);
            _todayStatusLabel.Text = isZc ? "套餐：--" : "今日：--";

            _creditsLabel = new Label();
            _creditsLabel.Location = new Point(14, 50);
            _creditsLabel.AutoSize = false;
            _creditsLabel.Width = 400;
            _creditsLabel.ForeColor = Color.FromArgb(76, 175, 80);
            _creditsLabel.Text = isZc ? "最近领取：--" : "积分：--";

            _occupiedLabel = new Label();
            _occupiedLabel.Location = new Point(14, 64);
            _occupiedLabel.AutoSize = false;
            _occupiedLabel.Width = 400;
            _occupiedLabel.ForeColor = Color.FromArgb(244, 67, 54);
            _occupiedLabel.Visible = false;
            _occupiedLabel.Text = "本机今日已被另一账号签到，本账号今日无法再签";

            todayGroup.Controls.Add(_todayStatusLabel);
            todayGroup.Controls.Add(_creditsLabel);
            todayGroup.Controls.Add(_occupiedLabel);

            // ---- 自动签到区 ----
            var autoGroup = new GroupBox();
            autoGroup.Dock = DockStyle.Top;
            autoGroup.Height = 100;
            autoGroup.Text = isZc ? "自动领取" : "自动签到";
            autoGroup.Padding = new Padding(10, 6, 10, 6);

            _autoNoteLabel = new Label();
            _autoNoteLabel.Location = new Point(14, 24);
            _autoNoteLabel.AutoSize = false;
            _autoNoteLabel.Height = 20;
            _autoNoteLabel.Width = 400;
            _autoNoteLabel.Text = isZc
                ? "每 10 分钟自动检查新活动套餐，发现即自动领取"
                : "每日 00:05 自动签到（" + platform + "）";

            _retryLabel = new Label();
            _retryLabel.Location = new Point(14, 52);
            _retryLabel.AutoSize = false;
            _retryLabel.Height = 20;
            _retryLabel.Width = 260;
            _retryLabel.ForeColor = Color.FromArgb(140, 140, 140);
            _retryLabel.Text = "";

            _checkinBtn = new Button();
            _checkinBtn.Text = isZc ? "检查并领取" : "立即签到（手动）";
            _checkinBtn.Location = new Point(282, 48);
            _checkinBtn.Size = new Size(140, 32);
            _checkinBtn.Click += delegate { _app.RunCheckinAsync(false, Platform, null); };

            autoGroup.Controls.Add(_autoNoteLabel);
            autoGroup.Controls.Add(_retryLabel);
            autoGroup.Controls.Add(_checkinBtn);

            // ---- 日历区 ----
            _calendarGroup = new GroupBox();
            _calendarGroup.Dock = DockStyle.Fill;
            _calendarGroup.Text = "签到日历";
            _calendarGroup.Padding = new Padding(10, 6, 10, 10);

            _calendar = new CalendarControl();
            _calendar.Dock = DockStyle.Fill;
            _calendar.MonthChanged += delegate { RefreshCalendar(); };

            _calendarGroup.Controls.Add(_calendar);

            Controls.Add(_calendarGroup);
            Controls.Add(autoGroup);
            Controls.Add(todayGroup);
            Controls.Add(accGroup);

            RefreshAccounts();
        }

        public void RefreshAccounts()
        {
            var slot = _app.Slot(Platform);
            if (slot == null) return;
            _changingCombo = true;
            try
            {
                _accountCombo.Items.Clear();
                string active = slot.ActiveBrand;
                int idx = -1;
                foreach (var a in slot.Accounts)
                {
                    _accountCombo.Items.Add(a);
                    if (a.Brand == active) idx = _accountCombo.Items.Count - 1;
                }
                if (idx >= 0) _accountCombo.SelectedIndex = idx;
                else if (_accountCombo.Items.Count > 0) _accountCombo.SelectedIndex = 0;
                _accountCombo.Enabled = _accountCombo.Items.Count > 1;
                _authBtn.Visible = (Platform == "WorkBuddy");
            }
            finally { _changingCombo = false; }
        }

        private void OnAccountChanged()
        {
            if (_changingCombo) return;
            var sel = _accountCombo.SelectedItem as AccountInfo;
            if (sel != null)
            {
                var slot = _app.Slot(Platform);
                if (slot != null && slot.ActiveBrand != sel.Brand)
                    _app.SetActiveBrand(Platform, sel.Brand);
            }
            RefreshView();
        }

        public void RefreshView()
        {
            // 单面板刷新异常要显式可见（写入详情区），避免静默停留旧状态且拖垮其他面板
            try
            {
                RefreshViewCore();
                _accountDetailLabel.ForeColor = Color.FromArgb(55, 55, 55);
            }
            catch (Exception ex)
            {
                try
                {
                    _accountDetailLabel.ForeColor = Color.FromArgb(244, 67, 54);
                    _accountDetailLabel.Text = "界面刷新异常：" + ex.Message;
                }
                catch { }
            }
        }

        private void RefreshViewCore()
        {
            var slot = _app.Slot(Platform);
            if (slot == null) return;
            // 自愈兜底：列表为空但本机凭证实际存在（装载竞态残留）→ 实时重读并回填
            if (slot.Accounts.Count == 0 && (Platform == "ZCode" || Platform == "Trae"))
            {
                try
                {
                    List<AccountInfo> live = Platform == "ZCode" ? ZcodeAuth.ListAccounts() : CheckinRunner.ListAccounts();
                    if (live.Count > 0)
                    {
                        slot.Accounts = live;
                        slot.ActiveBrand = live[0].Brand;
                        _app.RunCheckinAsync(true, Platform, null);
                    }
                }
                catch { }
            }
            string active = slot.ActiveBrand;
            AccountInfo acc = slot.ActiveAccount();
            bool isZc = (Platform == "ZCode");
            string todayWord = isZc ? "套餐" : "今日";
            string creditWord = isZc ? "最近领取" : "积分";
            _todayStatusLabel.ForeColor = Color.FromArgb(30, 30, 30);

            if (acc == null)
            {
                _accountDetailLabel.Text = isZc
                    ? "未找到 ZCode 登录态；请确认已登录 ZCode 客户端/CLI，或点“刷新状态”重试"
                    : (Platform == "WorkBuddy"
                        ? "未找到 WorkBuddy 客户端登录态；可点击“登录授权”直接授权本程序"
                        : "未找到登录态，请先运行 Trae 桌面端登录");
                _todayStatusLabel.Text = todayWord + "：--";
                _creditsLabel.Text = creditWord + "：--";
                _occupiedLabel.Visible = false;
                _calendarGroup.Text = "签到日历";
                _calendar.SetData(new Dictionary<string, bool>());
                return;
            }

            bool wbNoToken = (Platform == "WorkBuddy" && !acc.HasToken);
            if (wbNoToken)
            {
                _accountDetailLabel.Text = "未授权：点击“登录授权”完成一次浏览器登录，之后自动续期";
                _todayStatusLabel.Text = "今日：待授权";
                _creditsLabel.Text = "积分：--";
                _occupiedLabel.Visible = false;
            }
            else
            {
                _accountDetailLabel.Text = "软件：" + acc.Brand + "   登录名：" + (string.IsNullOrEmpty(acc.Username) ? "--" : acc.Username);
                if (!string.IsNullOrEmpty(acc.ExpiredAt))
                    _accountDetailLabel.Text += "   Token 过期：" + FormatExpiry(acc.ExpiredAt);

                var entry = HistoryStore.TodayEntry(active);
                bool ok = entry != null && entry.success;
                var last = slot.LastResult;
                if (ok)
                {
                    _todayStatusLabel.Text = todayWord + "：" + (isZc ? "已领取" : "已签到");
                    if (isZc)
                        _creditsLabel.Text = creditWord + "：+" + (entry.baseCredits.HasValue ? entry.baseCredits.GetValueOrDefault(0).ToString("N0") : "--") + " token";
                    else if (entry.baseCredits.HasValue || entry.extra.HasValue)
                        _creditsLabel.Text = creditWord + "：+" + (entry.baseCredits.GetValueOrDefault(0) + entry.extra.GetValueOrDefault(0)) + "（基础" + entry.baseCredits + " 额外" + entry.extra + "）";
                    else
                        _creditsLabel.Text = creditWord + "：已领取";
                    _occupiedLabel.Visible = false;
                }
                else if (last != null && last.HasClaimable)
                {
                    _todayStatusLabel.Text = todayWord + "：" + last.DisplayText;
                    _creditsLabel.Text = creditWord + "：--";
                    _occupiedLabel.Visible = false;
                }
                else if (last != null && last.Already && isZc)
                {
                    _todayStatusLabel.Text = todayWord + "：暂无可领新套餐";
                    _creditsLabel.Text = creditWord + "：--";
                    _occupiedLabel.Visible = false;
                }
                else if (entry != null && entry.occupied)
                {
                    _todayStatusLabel.Text = "今日：未签到（本机已被另一账号签到）";
                    _creditsLabel.Text = creditWord + "：--";
                    _occupiedLabel.Visible = true;
                }
                else if (entry != null && entry.code == -6)
                {
                    _todayStatusLabel.Text = "今日：" + (isZc ? "登录已失效，请重新登录 ZCode" : (Platform == "WorkBuddy" ? "登录已失效，请重新授权" : "账号状态异常（token 可能已过期）"));
                    _creditsLabel.Text = creditWord + "：--";
                    _occupiedLabel.Visible = false;
                }
                else if (last != null && !string.IsNullOrEmpty(last.DisplayText) && !ok)
                {
                    _todayStatusLabel.Text = todayWord + "：" + last.DisplayText;
                    _creditsLabel.Text = creditWord + "：--";
                    _occupiedLabel.Visible = false;
                }
                else if (last != null && !ok && !string.IsNullOrEmpty(last.Message))
                {
                    _todayStatusLabel.Text = todayWord + "：" + (isZc ? "检查失败：" : "签到失败：") + last.Message;
                    _todayStatusLabel.ForeColor = Color.FromArgb(244, 67, 54);
                    _creditsLabel.Text = creditWord + "：--";
                    _occupiedLabel.Visible = false;
                }
                else
                {
                    _todayStatusLabel.Text = todayWord + "：" + (isZc ? "待检查" : "未签到");
                    _creditsLabel.Text = creditWord + "：--";
                    _occupiedLabel.Visible = false;
                }
            }

            _autoNoteLabel.Text = isZc
                ? "每 10 分钟自动检查新活动套餐（登录名：" + (string.IsNullOrEmpty(acc.Username) ? "--" : acc.Username) + "）"
                : "每日 00:05 自动签到（" + (string.IsNullOrEmpty(acc.Username) ? acc.Brand : acc.Username) + "）";
            var retry = slot.Scheduler != null ? slot.Scheduler.NextRetryAt : null;
            _retryLabel.Text = isZc
                ? "发现新套餐会自动领取并气泡通知"
                : (retry.HasValue
                    ? "上次失败，下次重试：" + retry.Value.ToString("HH:mm")
                    : "自动签到已开启，失败后自动重试");

            _calendarGroup.Text = isZc
                ? "领取日历（登录名：" + (string.IsNullOrEmpty(acc.Username) ? acc.Brand : acc.Username) + "）"
                : "签到日历（账号：" + (string.IsNullOrEmpty(acc.Username) ? acc.Brand : acc.Username) + "）";
            RefreshCalendar();
        }

        private static string FormatExpiry(string iso)
        {
            DateTime dt;
            if (DateTime.TryParse(iso, out dt))
            {
                if (dt < DateTime.Now) return "已过期，请重新登录";
                return dt.ToString("yyyy-MM-dd");
            }
            return iso;
        }

        // 跨天判定（纯函数，供测试）：日期变化后日历应翻到新月
        public static bool ShouldSnapMonth(DateTime shownDate, DateTime now)
        {
            return shownDate.Date != now.Date;
        }

        private void RefreshCalendar()
        {
            // 修复：应用跨天运行后日历仍停在旧月份（如 10/1 签到但日历显示 9 月）
            // 检测到日期变化即自动翻到当前月；同一天内手动 ◀▶ 翻月不受影响
            DateTime now = DateTime.Now;
            if (ShouldSnapMonth(_calShownDate, now))
            {
                _calShownDate = now.Date;
                _calendar.SetMonth(now);
            }
            var slot = _app.Slot(Platform);
            string brand = (slot != null && !string.IsNullOrEmpty(slot.ActiveBrand)) ? slot.ActiveBrand : Platform;
            _calendar.SetData(HistoryStore.SuccessMap(_calendar.CurrentMonth.Year, _calendar.CurrentMonth.Month, brand));
        }
    }

    // ============ 主窗口（标题行 + 三平台 Tab） ============
    internal class MainForm : Form
    {
        private readonly TrayApp _app;
        private Label _titleStatus;
        private TabControl _tabs;
        private PlatformPanel _traePanel;
        private PlatformPanel _wbPanel;
        private PlatformPanel _zcPanel;

        public MainForm(TrayApp app)
        {
            _app = app;
            Text = "TraeSign - 每日签到";
            Width = 460;
            Height = 726;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            Font = new Font("Microsoft YaHei UI", 9f);
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ---- 标题行 ----
            var titlePanel = new Panel();
            titlePanel.Dock = DockStyle.Top;
            titlePanel.Height = 46;
            titlePanel.Padding = new Padding(12, 10, 12, 4);

            var titleLabel = new Label();
            titleLabel.Text = "TraeSign";
            titleLabel.Font = new Font(this.Font.FontFamily, 13f, FontStyle.Bold);
            titleLabel.AutoSize = true;
            titleLabel.Location = new Point(12, 12);

            _titleStatus = new Label();
            _titleStatus.Text = "状态：--";
            _titleStatus.AutoSize = true;
            _titleStatus.Location = new Point(320, 16);
            _titleStatus.ForeColor = Color.Gray;

            titlePanel.Controls.Add(titleLabel);
            titlePanel.Controls.Add(_titleStatus);

            // ---- 三平台 Tab（自绘：页签带红绿灯状态点） ----
            _tabs = new TabControl();
            _tabs.Dock = DockStyle.Fill;
            _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            _tabs.DrawItem += OnTabDrawItem;

            var tpTrae = new TabPage("Trae");
            _traePanel = new PlatformPanel(app, "Trae");
            tpTrae.Controls.Add(_traePanel);

            var tpWb = new TabPage("WorkBuddy");
            _wbPanel = new PlatformPanel(app, "WorkBuddy");
            tpWb.Controls.Add(_wbPanel);

            var tpZc = new TabPage("ZCode");
            _zcPanel = new PlatformPanel(app, "ZCode");
            tpZc.Controls.Add(_zcPanel);

            _tabs.TabPages.Add(tpTrae);
            _tabs.TabPages.Add(tpWb);
            _tabs.TabPages.Add(tpZc);

            // OwnerDrawFixed 的页签尺寸须在页签加入后设置才生效
            _tabs.SizeMode = TabSizeMode.Fixed;
            _tabs.ItemSize = new Size(118, 30);

            Controls.Add(_tabs);
            Controls.Add(titlePanel);
        }

        // 页签自绘：状态灯（绿/红/灰）+ 平台名
        private void OnTabDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _tabs.TabPages.Count) return;
            var slot = _app.Slot(_tabs.TabPages[e.Index].Text);
            Color c;
            string state;
            TrayApp.GetSlotLight(slot, out c, out state);
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

            using (var b = new SolidBrush(selected ? Color.White : SystemColors.Control))
                e.Graphics.FillRectangle(b, e.Bounds);
            if (selected)
                using (var p = new Pen(TrayApp.LightGreen, 2f))
                    e.Graphics.DrawLine(p, e.Bounds.X + 2, e.Bounds.Bottom - 2, e.Bounds.Right - 2, e.Bounds.Bottom - 2);

            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(c))
                e.Graphics.FillEllipse(b, e.Bounds.X + 13, e.Bounds.Y + e.Bounds.Height / 2 - 5, 10, 10);

            string text = _tabs.TabPages[e.Index].Text;
            using (var f = new Font(this.Font, selected ? FontStyle.Bold : FontStyle.Regular))
                TextRenderer.DrawText(e.Graphics, text, f,
                    new Rectangle(e.Bounds.X + 28, e.Bounds.Y, e.Bounds.Width - 30, e.Bounds.Height),
                    selected ? Color.FromArgb(30, 30, 30) : Color.FromArgb(90, 90, 90),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            // 悬停提示带具体状态
            _tabs.TabPages[e.Index].ToolTipText = state;
        }

        public void RefreshAccounts()
        {
            _traePanel.RefreshAccounts();
            _wbPanel.RefreshAccounts();
            _zcPanel.RefreshAccounts();
        }

        public void RefreshView()
        {
            string tip;
            TrayState st = _app.ComputeAggregateState(out tip);
            if (st == TrayState.CheckedIn)
            {
                _titleStatus.Text = "状态：已签到";
                _titleStatus.ForeColor = Color.FromArgb(76, 175, 80);
            }
            else if (st == TrayState.Failed)
            {
                _titleStatus.Text = "状态：有平台异常";
                _titleStatus.ForeColor = Color.FromArgb(244, 67, 54);
            }
            else
            {
                _titleStatus.Text = "状态：进行中";
                _titleStatus.ForeColor = Color.Gray;
            }
            _titleStatus.Tag = tip;
            _traePanel.RefreshView();
            _wbPanel.RefreshView();
            _zcPanel.RefreshView();
            _tabs.Invalidate();   // 状态灯重绘
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        }
    }

    // ============ 托盘图标 ============
    internal enum TrayState { Unknown, CheckedIn, Failed }

    internal static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }

    internal static class IconFactory
    {
        public static Icon Create(TrayState state)
        {
            using (var bmp = new Bitmap(16, 16))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    Color c = state == TrayState.CheckedIn ? Color.FromArgb(76, 175, 80)
                           : state == TrayState.Failed ? Color.FromArgb(244, 67, 54)
                           : Color.Gray;
                    using (var b = new SolidBrush(c)) g.FillEllipse(b, 1, 1, 14, 14);
                    if (state == TrayState.CheckedIn)
                    {
                        using (var p = new Pen(Color.White, 2f))
                            g.DrawLines(p, new PointF[] { new PointF(4f, 8.5f), new PointF(6.5f, 11f), new PointF(12f, 5f) });
                    }
                    IntPtr h = bmp.GetHicon();
                    try { return (Icon)Icon.FromHandle(h).Clone(); }
                    finally { NativeMethods.DestroyIcon(h); }
                }
            }
        }
    }

    // ============ 自检 ============
    internal static class SelfTest
    {
        public static int Run()
        {
            var sb = new StringBuilder();
            int[] fails = new int[1];
            string logPath = Path.Combine(HistoryStore.DataDir, "selfcheck.log");
            var Check = new Action<string, bool>(delegate(string name, bool ok)
            {
                sb.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name);
                if (!ok) fails[0]++;
            });

            string realDataDir = HistoryStore.DataDir;
            string tmpDataDir = Path.Combine(Path.GetTempPath(), "TraeCheckinSelftest_" + Guid.NewGuid().ToString("N"));
            HistoryStore.DataDir = tmpDataDir;

            try
            {
                // 1. BuildCells 断言
                var cells = CalendarControl.BuildCells(new DateTime(2026, 9, 1));
                Check("BuildCells 数量=42", cells.Count == 42);
                bool firstInMonthIsDay1 = false;
                for (int i = 0; i < cells.Count; i++)
                {
                    if (cells[i].InMonth && cells[i].Date.Day == 1) { firstInMonthIsDay1 = true; break; }
                }
                Check("当月1号在网格内", firstInMonthIsDay1);
                Check("首列=周一", ((int)cells[0].Date.DayOfWeek) == 1);

                // 2. 调度纯函数断言
                DateTime d1 = new DateTime(2026, 9, 15, 1, 0, 0);
                DateTime early = new DateTime(2026, 9, 15, 0, 1, 0);
                DateTime? none = null;
                var target = new TimeSpan(0, 5, 0);
                Check("未到点不触发", DailyScheduler.ShouldCheckinNow(early, none, none, target) == false);
                Check("过点触发", DailyScheduler.ShouldCheckinNow(d1, none, none, target) == true);
                Check("今日已完成不触发", DailyScheduler.ShouldCheckinNow(d1, d1.Date, none, target) == false);
                Check("退避中不触发", DailyScheduler.ShouldCheckinNow(d1, none, d1.AddMinutes(30), target) == false);
                Check("退避结束触发", DailyScheduler.ShouldCheckinNow(d1.AddHours(1), none, d1.AddMinutes(30), target) == true);

                // 3. 9095 停止判定
                var r9095 = CheckinResult.Fail(9095, "x", "Trae CN");
                var r9074 = CheckinResult.Fail(9074, "x", "Trae CN");
                Check("9095 当天停止", DailyScheduler.ShouldStopToday(r9095) == true);
                Check("9074 继续重试", DailyScheduler.ShouldStopToday(r9074) == false);

                // 4. 真实账号列表 + 解密一致性
                var accounts = CheckinRunner.ListAccounts();
                Check("账号列表>=2(实际=" + accounts.Count + ")", accounts.Count >= 2);
                bool allNamed = true;
                foreach (var a in accounts)
                    if (string.IsNullOrEmpty(a.Username)) { allNamed = false; break; }
                Check("解密一致(账号名全部非空)", allNamed);

                // 5. 旧格式迁移
                HistoryStore.SetActiveBrand(HistoryStore.FallbackBrand);
                HistoryStore.SetUser(HistoryStore.FallbackBrand, "旧用户A");
                HistoryStore.Upsert(new HistoryEntry { brand = HistoryStore.FallbackBrand, date = "2026-09-14", success = true, checked_in = true, ts = 1 });
                Check("按品牌落盘", HistoryStore.TodaySuccess(HistoryStore.FallbackBrand) == false);
                Check("昨日记录存在", HistoryStore.SuccessMap(2026, 9, HistoryStore.FallbackBrand).ContainsKey("2026-09-14"));
                Check("其他品牌日历隔离", HistoryStore.SuccessMap(2026, 9, "Trae CN").Count == 0);

                // 6. (brand,date) 双键：同日后不同品牌不互删
                HistoryStore.Upsert(new HistoryEntry { brand = "Trae CN", date = "2026-09-14", success = false, occupied = true, ts = 2 });
                Check("Trae CN 同日记录独立", HistoryStore.SuccessMap(2026, 9, "Trae CN").ContainsKey("2026-09-14"));
                Check("SOLO 记录未被误删", HistoryStore.SuccessMap(2026, 9, HistoryStore.FallbackBrand).ContainsKey("2026-09-14"));

                // 7. 真实查询与签到（幂等安全，内置引擎）
                string firstBrand = accounts.Count > 0 ? accounts[0].Brand : HistoryStore.FallbackBrand;
                var q = CheckinRunner.Run(true, firstBrand);
                Check("查询成功(checked_in=" + q.CheckedIn + ")", q.Code == 0 && q.Brand == firstBrand && q.Username != null);
                if (!string.IsNullOrEmpty(q.Username)) HistoryStore.SetUser(firstBrand, q.Username);
                var c = CheckinRunner.Run(false, firstBrand);
                Check("签到调用成功(code=" + c.Code + ", msg=" + c.Message + ")", c.Success || c.CheckedIn || c.Code == 9095 || c.Code == 9074);

                // 8. WorkBuddy：信封判定 / JWT / 字段兼容 / token 库 DPAPI 往返
                var encObj = JsonUtil.ParseJson("{\"$wbEncrypted\":1,\"envelope\":\"x\"}");
                Check("WB 加密信封判定", WorkbuddyAuth.IsEncryptedEnvelope(encObj) == true);
                Check("WB 明文非信封判定", WorkbuddyAuth.IsEncryptedEnvelope("eyJabc") == false);
                string fakeJwt = WorkbuddyAuth.Base64UrlEncode("{\"alg\":\"none\"}") + "." + WorkbuddyAuth.Base64UrlEncode("{\"sub\":\"uid-12345\"}") + ".sig";
                Check("WB JWT sub 解析", WorkbuddyAuth.JwtSub(fakeJwt) == "uid-12345");
                var wbFields = JsonUtil.ParseJson("{\"todayCheckedIn\":true,\"streak_days\":3,\"todayCredit\":150}");
                Check("WB camel/snake 兼容取值", JsonUtil.GetBoolAny(wbFields, "todayCheckedIn", "today_checked_in") == true
                    && JsonUtil.GetIntAny(wbFields, "streakDays", "streak_days") == 3
                    && JsonUtil.GetIntAny(wbFields, "today_credit", "todayCredit") == 150);
                var tok = new WbAuthData();
                tok.Uid = "t-uid"; tok.AccessToken = "at-selftest-token"; tok.RefreshToken = "rt-selftest"; tok.ExpiresAt = 0; tok.RefreshExpiresAt = 0;
                WorkbuddyAuth.SaveStore(tok);
                var tok2 = WorkbuddyAuth.LoadStore();
                Check("WB token 库 DPAPI 往返", tok2 != null && tok2.AccessToken == "at-selftest-token" && tok2.RefreshToken == "rt-selftest" && tok2.Uid == "t-uid");

                // 9. WorkBuddy：账号列表 + 真实查询（仅已有 token 时；未授权跳过网络测试）
                var wbAccounts = WorkbuddyAuth.ListAccounts();
                bool wbBrandOk = true;
                foreach (var a in wbAccounts)
                    if (a.Brand != WorkbuddyAuth.Platform) wbBrandOk = false;
                Check("WB 账号列表合法(实际=" + wbAccounts.Count + ")", wbBrandOk);
                if (WorkbuddyAuth.GetWorkingToken() != null)
                {
                    var wq = WorkbuddyRunner.Run(true);
                    Check("WB 查询成功(code=" + wq.Code + ", msg=" + wq.Message + ")", wq.Code == 0 || wq.Code == -6);
                }
                else
                {
                    sb.AppendLine("[SKIP] WB 真实查询（未授权，跳过；完成一次浏览器授权后自测将覆盖）");
                }

                // 10. ZCode：AES-GCM 向量 / Base64Url / preview 解析 / Edge 探测 / 日历跨天
                string vec = "enc:v1:Stbd72p7d7tsa9Hy.SwrZebP7w3M_I3_9gMETiQ.vaYnsBO0RkDFeSpTEkNnm-qOrrGMra6c9jaiWuJh5Jk";
                Check("ZC AES-GCM 向量解密", ZcodeAuth.DecryptWithSecret(vec, "selftest-vector-secret") == "TraeSign-ZCode-vector-2026-10-01");
                Check("ZC 错误密钥解密失败(null)", ZcodeAuth.DecryptWithSecret(vec, "wrong-secret") == null);
                // NIST GCM Test Case 13（K=0^32, IV=0^12, 空 C, T=530f...738b）：算法正确性外证
                byte[] nistK = new byte[32], nistIv = new byte[12], nistTag = HexToBytes("530f8afbc74536b9a963b4f1c4cb738b");
                byte[] nistPlain = ZcodeGcm.Decrypt(nistK, nistIv, nistTag, new byte[0]);
                Check("ZC NIST GCM 用例13", nistPlain != null && nistPlain.Length == 0);
                Check("ZC Base64Url 解码", ZcodeAuth.Base64UrlDecode("eyJhIjoxfQ") == "{\"a\":1}");
                string pvJson = "{\"code\":0,\"data\":{\"plans\":[{\"plan_id\":\"p1\",\"name\":\"Trust\",\"entitlements\":[{\"show_name\":\"GLM-5.3-Flash\",\"grant_units\":100000000}]}]}}";
                var zplans = ZcodeRunner.ParsePreviewPlans(pvJson);
                Check("ZC preview 解析(ArrayList)", zplans.Count == 1 && zplans[0].PlanId == "p1" && zplans[0].Units == 100000000L && zplans[0].ShowName == "GLM-5.3-Flash");
                Check("ZC 空 plans 解析", ZcodeRunner.ParsePreviewPlans("{\"code\":0,\"data\":{\"plans\":[]}}").Count == 0);
                Check("ZC 非0 code 解析", ZcodeRunner.ParsePreviewPlans("{\"code\":401,\"msg\":\"x\"}").Count == 0);
                Check("ZC Edge 路径探测", !string.IsNullOrEmpty(ZcodeCaptcha.FindEdgePath()));
                Check("ZC 日历跨天判定", PlatformPanel.ShouldSnapMonth(new DateTime(2026, 9, 30), new DateTime(2026, 10, 1))
                    && !PlatformPanel.ShouldSnapMonth(new DateTime(2026, 10, 1), new DateTime(2026, 10, 1)));

                // 11. ZCode：真实查询（只读 preview，不领取）
                if (!string.IsNullOrEmpty(ZcodeAuth.GetJwt()))
                {
                    var zq = ZcodeRunner.Run(true);
                    Check("ZC 真实查询(code=" + zq.Code + ")", zq.Code == 0 || zq.Code == -6 || zq.Code == -3);
                }
                else
                {
                    sb.AppendLine("[SKIP] ZC 真实查询（无 ZCode 登录态，跳过）");
                }
            }
            catch (Exception ex)
            {
                fails[0]++;
                sb.AppendLine("[EXCEPTION] " + ex.ToString());
            }

            HistoryStore.DataDir = realDataDir;
            try { if (Directory.Exists(tmpDataDir)) Directory.Delete(tmpDataDir, true); }
            catch { }

            sb.AppendLine("RESULT: " + (fails[0] == 0 ? "ALL PASS" : fails[0] + " FAILED"));
            try
            {
                if (!Directory.Exists(realDataDir)) Directory.CreateDirectory(realDataDir);
                File.WriteAllText(logPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
            return fails[0] == 0 ? 0 : 1;
        }

        private static byte[] HexToBytes(string hex)
        {
            byte[] b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }
    }
}
