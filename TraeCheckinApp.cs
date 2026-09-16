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

    // ============ 托盘应用上下文 ============
    internal class TrayApp : ApplicationContext
    {
        private NotifyIcon _tray;
        private MainForm _form;
        private DailyScheduler _sched;
        private bool _busy;
        private static TrayApp _instance;

        private List<AccountInfo> _accounts;
        private string _activeBrand;

        public TrayApp()
        {
            _instance = this;
            _busy = false;
            _form = null;
            _accounts = new List<AccountInfo>();

            _tray = new NotifyIcon();
            _tray.Visible = true;
            _tray.Icon = IconFactory.Create(TrayState.Unknown);
            _tray.Text = "TraeSign - 加载中...";
            _tray.DoubleClick += delegate { ShowMainWindow(); };

            var menu = new ContextMenuStrip();
            menu.Items.Add("立即签到", null, delegate { RunCheckinAsync(false, _activeBrand); });
            menu.Items.Add("打开日历", null, delegate { ShowMainWindow(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitApp(); });
            _tray.ContextMenuStrip = menu;

            _sched = new DailyScheduler(this);
            _sched.Start();

            // 加载账号列表（后台），完成后刷新状态
            LoadAccountsAsync();
        }

        public NotifyIcon Tray { get { return _tray; } }
        public bool Busy { get { return _busy; } }
        public static TrayApp Instance { get { return _instance; } }
        public List<AccountInfo> Accounts { get { return _accounts; } }
        public DailyScheduler Scheduler { get { return _sched; } }
        public string ActiveBrand { get { return _activeBrand; } }


        public AccountInfo ActiveAccount()
        {
            foreach (var a in _accounts)
                if (a.Brand == _activeBrand) return a;
            return _accounts.Count > 0 ? _accounts[0] : null;
        }

        public static void ShowMainWindow()
        {
            if (_instance == null) return;
            if (_instance._form == null || _instance._form.IsDisposed)
                _instance._form = new MainForm(_instance);
            _instance._form.RefreshAccounts(_instance._accounts);
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

        public void RefreshView()
        {
            if (_form != null && !_form.IsDisposed) _form.RefreshView();
        }

        // 后台加载账号列表
        public void LoadAccountsAsync()
        {
            Task.Run(delegate { return CheckinRunner.ListAccounts(); })
                .ContinueWith(t =>
                {
                    List<AccountInfo> list;
                    try { list = t.Result; }
                    catch { list = null; }
                    if (list != null) OnAccountsLoaded(list);
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // 手动刷新：重新拉取账号列表 + 当前账号状态
        public void RefreshAccountsAndStatus()
        {
            LoadAccountsAsync();
        }

        private void OnAccountsLoaded(List<AccountInfo> list)
        {
            _accounts = list;
            // 校验/回退 activeBrand
            string saved = HistoryStore.GetActiveBrand();
            if (!string.IsNullOrEmpty(saved) && HasBrand(saved))
                _activeBrand = saved;
            else if (_accounts.Count > 0)
                _activeBrand = _accounts[0].Brand;
            else
                _activeBrand = null;

            if (_activeBrand != null) HistoryStore.SetActiveBrand(_activeBrand);
            if (_form != null && !_form.IsDisposed) _form.RefreshAccounts(_accounts);

            if (_accounts.Count == 0)
            {
                SetTray(TrayState.Failed, "TraeSign - 未找到登录态");
                return;
            }
            // 调度按当前账号初始化
            _sched.ResetForBrand(_activeBrand);
            // 刷新状态（只查询，不签到）
            RunCheckinAsync(true, _activeBrand);
        }

        private bool HasBrand(string brand)
        {
            foreach (var a in _accounts)
                if (a.Brand == brand) return true;
            return false;
        }

        public void SetActiveBrand(string brand)
        {
            if (brand == _activeBrand) return;
            if (!HasBrand(brand)) return;
            _activeBrand = brand;
            HistoryStore.SetActiveBrand(brand);
            _sched.ResetForBrand(brand);
            RefreshView();
        }

        public void RunCheckinAsync(bool queryOnly, string brand)
        {
            if (_busy) return;
            if (string.IsNullOrEmpty(brand)) brand = _activeBrand;
            _busy = true;
            string reqBrand = brand;
            if (queryOnly) SetTray(TrayState.Unknown, "TraeSign - 查询中...");
            else SetTray(TrayState.Unknown, "TraeSign - 正在签到...");

            Task.Run(delegate { return CheckinRunner.Run(queryOnly, reqBrand); })
                .ContinueWith(t =>
                {
                    CheckinResult r;
                    try { r = t.Result; }
                    catch (Exception ex) { r = CheckinResult.Fail(-6, "调用签到程序异常: " + ex.Message, reqBrand); }
                    OnCheckinDone(r, queryOnly, reqBrand);
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void OnCheckinDone(CheckinResult r, bool queryOnly, string requestedBrand)
        {
            _busy = false;
            string brand = r.Brand ?? requestedBrand;
            bool ok = r.Success || r.CheckedIn;

            string userName = r.Username;
            if (string.IsNullOrEmpty(userName))
            {
                var acc = ActiveAccount();
                if (acc != null) userName = acc.Username;
            }
            string who = string.IsNullOrEmpty(userName) ? brand : userName + "（" + brand + "）";

            string statusText;
            if (ok)
            {
                statusText = who + " 今日已签到";
                if (r.Base.HasValue || r.Extra.HasValue)
                    statusText = who + " 今日已签到 +" + (r.Base.GetValueOrDefault(0) + r.Extra.GetValueOrDefault(0)) + "（基础" + r.Base + " 额外" + r.Extra + "）";
                SetTray(TrayState.CheckedIn, "TraeSign - " + statusText);
            }
            else if (r.Code == 9095)
            {
                statusText = who + " 本机今日已被另一账号签到";
                SetTray(TrayState.Failed, "TraeSign - " + statusText);
            }
            else if (r.Code == -6)
            {
                statusText = who + " 账号状态异常（token 可能已过期），请重新登录该软件或刷新账号状态";
                SetTray(TrayState.Failed, "TraeSign - " + statusText);
            }
            else
            {
                statusText = who + " 签到失败：" + (string.IsNullOrEmpty(r.Message) ? "未知原因" : r.Message);
                SetTray(TrayState.Failed, "TraeSign - " + statusText);
            }

            if (!string.IsNullOrEmpty(r.Username)) HistoryStore.SetUser(brand, r.Username);

            // 查询模式只在"已签到"时落库；签到模式无论成败都落库
            if (!queryOnly || ok)
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

            // 调度状态：只有真实签到尝试才回填（查询不改调度）
            if (!queryOnly) _sched.NotifyResult(r);

            if (!queryOnly)
            {
                string tip;
                ToolTipIcon ticon;
                if (r.Already)
                {
                    tip = "今天已签到，无需重复签到";
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

            // 仅当结果账号仍是当前选中账号时才刷新窗体（避免旧结果污染切换后的视图）
            if (brand == _activeBrand) RefreshView();
        }

        public void ExitApp()
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
            ExitThread();
        }
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

    // HTTP POST JSON（直连，证书失败降级重试一次）
    internal static class HttpApi
    {
        public static string PostJson(string url, Dictionary<string, string> headers, string bodyJson)
        {
            return PostCore(url, headers, bodyJson, false);
        }

        private static string PostCore(string url, Dictionary<string, string> headers, string bodyJson, bool insecure)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 60000;
            req.ReadWriteTimeout = 60000;
            req.Proxy = null;   // 直连，与 Node https 行为一致
            if (insecure) req.ServerCertificateValidationCallback = delegate { return true; };
            if (headers != null)
                foreach (var kv in headers) req.Headers[kv.Key] = kv.Value;
            byte[] payload = Encoding.UTF8.GetBytes(bodyJson ?? "{}");
            req.ContentLength = payload.Length;
            using (var s = req.GetRequestStream()) s.Write(payload, 0, payload.Length);
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rs = resp.GetResponseStream())
                using (var sr = new StreamReader(rs, Encoding.UTF8)) return sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                if (ex.Response != null)
                {
                    try
                    {
                        using (var rs = ex.Response.GetResponseStream())
                        using (var sr = new StreamReader(rs, Encoding.UTF8)) return sr.ReadToEnd();
                    }
                    catch { }
                }
                if (!insecure && ex.Status == WebExceptionStatus.SecureChannelFailure)
                    return PostCore(url, headers, bodyJson, true);
                throw;
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

    // ============ 每日调度 ============
    internal class DailyScheduler
    {
        private static readonly TimeSpan Target = new TimeSpan(0, 5, 0);
        private static readonly int[] BackoffMinutes = { 5, 15, 30, 60, 120 };

        private System.Windows.Forms.Timer _timer;
        private TrayApp _app;
        private DateTime? _lastAttemptDate;
        private DateTime? _nextRetryAt;
        private int _retryCount;

        public DailyScheduler(TrayApp app)
        {
            _app = app;
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
            _app.RunCheckinAsync(false, _app.ActiveBrand);
        }
    }

    // ============ 主窗口 ============
    internal class MainForm : Form
    {
        private TrayApp _app;
        private Label _titleStatus;
        private ComboBox _accountCombo;
        private Button _refreshBtn;
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

        public MainForm(TrayApp app)
        {
            _app = app;
            _changingCombo = false;
            Text = "TraeSign - 每日签到";
            Width = 460;
            Height = 680;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            Font = new Font("Microsoft YaHei UI", 9f);

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
            _titleStatus.Location = new Point(330, 16);
            _titleStatus.ForeColor = Color.Gray;

            titlePanel.Controls.Add(titleLabel);
            titlePanel.Controls.Add(_titleStatus);

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
            _refreshBtn.Text = "刷新账号状态";
            _refreshBtn.Location = new Point(330, 21);
            _refreshBtn.Size = new Size(84, 25);
            _refreshBtn.Click += delegate { _app.RefreshAccountsAndStatus(); };

            _accountDetailLabel = new Label();
            _accountDetailLabel.Location = new Point(14, 52);
            _accountDetailLabel.AutoSize = false;
            _accountDetailLabel.Width = 400;
            _accountDetailLabel.Text = "未找到登录态，请先运行 Trae 桌面端登录";

            accGroup.Controls.Add(_accountCombo);
            accGroup.Controls.Add(_refreshBtn);
            accGroup.Controls.Add(_accountDetailLabel);

            // ---- 今日签到区 ----
            var todayGroup = new GroupBox();
            todayGroup.Dock = DockStyle.Top;
            todayGroup.Height = 88;
            todayGroup.Text = "今日签到";
            todayGroup.Padding = new Padding(10, 6, 10, 4);

            _todayStatusLabel = new Label();
            _todayStatusLabel.Location = new Point(14, 24);
            _todayStatusLabel.AutoSize = false;
            _todayStatusLabel.Width = 400;
            _todayStatusLabel.Font = new Font(this.Font.FontFamily, 10f, FontStyle.Bold);
            _todayStatusLabel.Text = "今日：--";

            _creditsLabel = new Label();
            _creditsLabel.Location = new Point(14, 50);
            _creditsLabel.AutoSize = false;
            _creditsLabel.Width = 400;
            _creditsLabel.ForeColor = Color.FromArgb(76, 175, 80);
            _creditsLabel.Text = "积分：--";

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
            autoGroup.Text = "自动签到";
            autoGroup.Padding = new Padding(10, 6, 10, 6);

            _autoNoteLabel = new Label();
            _autoNoteLabel.Location = new Point(14, 24);
            _autoNoteLabel.AutoSize = false;
            _autoNoteLabel.Height = 20;
            _autoNoteLabel.Width = 400;
            _autoNoteLabel.Text = "每日 00:05 自动签到（仅当前选中账号）";

            _retryLabel = new Label();
            _retryLabel.Location = new Point(14, 52);
            _retryLabel.AutoSize = false;
            _retryLabel.Height = 20;
            _retryLabel.Width = 260;
            _retryLabel.ForeColor = Color.FromArgb(140, 140, 140);
            _retryLabel.Text = "";

            _checkinBtn = new Button();
            _checkinBtn.Text = "立即签到（手动）";
            _checkinBtn.Location = new Point(300, 48);
            _checkinBtn.Size = new Size(122, 32);
            _checkinBtn.Click += delegate { _app.RunCheckinAsync(false, _app.ActiveBrand); };

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
            Controls.Add(titlePanel);

            RefreshAccounts(_app.Accounts);
        }

        public void RefreshAccounts(List<AccountInfo> accounts)
        {
            _changingCombo = true;
            try
            {
                _accountCombo.Items.Clear();
                string active = _app.ActiveBrand;
                int idx = -1;
                foreach (var a in accounts)
                {
                    _accountCombo.Items.Add(a);
                    if (a.Brand == active) idx = _accountCombo.Items.Count - 1;
                }
                if (idx >= 0) _accountCombo.SelectedIndex = idx;
                else if (_accountCombo.Items.Count > 0) _accountCombo.SelectedIndex = 0;
                _accountCombo.Enabled = _accountCombo.Items.Count > 1;
            }
            finally { _changingCombo = false; }
        }

        private void OnAccountChanged()
        {
            if (_changingCombo) return;
            var sel = _accountCombo.SelectedItem as AccountInfo;
            if (sel != null && _app.ActiveBrand != sel.Brand)
                _app.SetActiveBrand(sel.Brand);
            RefreshView();
        }

        public void RefreshView()
        {
            string active = _app.ActiveBrand;
            AccountInfo acc = null;
            foreach (var a in _app.Accounts)
                if (a.Brand == active) { acc = a; break; }

            if (acc == null)
            {
                _titleStatus.Text = "状态：未登录";
                _accountDetailLabel.Text = "未找到登录态，请先运行 Trae 桌面端登录";
                _todayStatusLabel.Text = "今日：--";
                _creditsLabel.Text = "积分：--";
                _occupiedLabel.Visible = false;
                _calendarGroup.Text = "签到日历";
                return;
            }

            _accountDetailLabel.Text = "软件：" + acc.Brand + "   登录名：" + (string.IsNullOrEmpty(acc.Username) ? "--" : acc.Username);
            if (!string.IsNullOrEmpty(acc.ExpiredAt))
                _accountDetailLabel.Text += "   Token 过期：" + FormatExpiry(acc.ExpiredAt);

            var entry = HistoryStore.TodayEntry(active);
            bool ok = entry != null && entry.success;
            if (ok)
            {
                _titleStatus.Text = "状态：已签到";
                _titleStatus.ForeColor = Color.FromArgb(76, 175, 80);
                _todayStatusLabel.Text = "今日：已签到";
                if (entry.baseCredits.HasValue || entry.extra.HasValue)
                    _creditsLabel.Text = "积分：+" + (entry.baseCredits.GetValueOrDefault(0) + entry.extra.GetValueOrDefault(0)) + "（基础" + entry.baseCredits + " 额外" + entry.extra + "）";
                else
                    _creditsLabel.Text = "积分：已领取";
            }
            else if (entry != null && entry.occupied)
            {
                _titleStatus.Text = "状态：已占用";
                _titleStatus.ForeColor = Color.FromArgb(244, 67, 54);
                _todayStatusLabel.Text = "今日：未签到（本机已被另一账号签到）";
                _creditsLabel.Text = "积分：--";
                _occupiedLabel.Visible = true;
            }
            else if (entry != null && entry.code == -6)
            {
                _titleStatus.Text = "状态：账号异常";
                _titleStatus.ForeColor = Color.FromArgb(244, 67, 54);
                _todayStatusLabel.Text = "今日：账号状态异常（token 可能已过期）";
                _creditsLabel.Text = "积分：--";
                _occupiedLabel.Visible = false;
            }
            else
            {
                _titleStatus.Text = "状态：未签到";
                _titleStatus.ForeColor = Color.Gray;
                _todayStatusLabel.Text = "今日：未签到";
                _creditsLabel.Text = "积分：--";
                _occupiedLabel.Visible = false;
            }

            _autoNoteLabel.Text = "每日 00:05 自动签到（当前选中账号：" + (string.IsNullOrEmpty(acc.Username) ? acc.Brand : acc.Username) + "）";
            var retry = _app.Scheduler.NextRetryAt;
            _retryLabel.Text = retry.HasValue
                ? "上次失败，下次重试：" + retry.Value.ToString("HH:mm")
                : "自动签到已开启，失败后自动重试";

            _calendarGroup.Text = "签到日历（账号：" + (string.IsNullOrEmpty(acc.Username) ? acc.Brand : acc.Username) + "）";
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

        private void RefreshCalendar()
        {
            _calendar.SetData(HistoryStore.SuccessMap(_calendar.CurrentMonth.Year, _calendar.CurrentMonth.Month, _app.ActiveBrand));
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
    }
}
