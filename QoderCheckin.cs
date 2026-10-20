using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace TraeSign
{
    // ============ Qoder 凭证 ============
    internal class QoderCreds
    {
        public string Token;
        public string RefreshToken;
        public string Uid;
        public string UserName;
        public string ExpiresAt;
        public string RefreshExpiresAt;
    }

    // ============ Qoder 凭证获取（客户端 safeStorage + 自有 DPAPI 库） ============
    internal static class QoderAuth
    {
        public const string Platform = "Qoder";
        public const string BaseUrl = "https://openapi.qoder.com.cn";
        public const string DefaultClientVersion = "0.4.8";

        private static readonly object MachineIdGate = new object();
        private static string _machineId;

        internal class QoderTokenStoreDto
        {
            public int v;
            public string token;
            public string refresh_token;
            public string uid;
            public string user_name;
            public string expires_at;
            public string refresh_expires_at;
            public long saved_at;
        }

        // 自动检索：优先已知目录名，再扫 APPDATA 下任意含 auth.v1.dat 的 qoder 产品目录
        // （覆盖 CN/国际版、stable/insider 通道、产品改名；只需登录过一次，无需客户端在运行）
        private static readonly string[] KnownDataDirs = new string[]
        {
            "com.qodercn.app.stable",   // CN stable（现行）
            "com.qodercn.app",          // CN 其它通道
            "com.qoder.app.stable",     // 国际版 stable
            "com.qoder.app",
            "Qoder CN",
            "Qoder"
        };

        public static string DataDir()
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            // ① 已知目录名（顺序稳定）
            foreach (string name in KnownDataDirs)
            {
                string dir = Path.Combine(roaming, name);
                if (File.Exists(Path.Combine(dir, "auth.v1.dat"))) return dir;
            }
            // ② 自动检索：APPDATA 下任意名字含 qoder 且带 auth.v1.dat 的目录
            try
            {
                foreach (string dir in Directory.GetDirectories(roaming))
                {
                    string n = Path.GetFileName(dir);
                    if (n.IndexOf("qoder", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (File.Exists(Path.Combine(dir, "auth.v1.dat"))) return dir;
                }
            }
            catch { }
            // 兜底返回标准路径（供 MachineId 等在未登录时也能定位/创建）
            return Path.Combine(roaming, KnownDataDirs[0]);
        }

        public static string TokenStorePath
        {
            get { return Path.Combine(HistoryStore.DataDir, "qoder_token.json"); }
        }

        public static string FindInstallDir()
        {
            // ① 注册表 App Paths（标准安装）
            string dir = FindFromRegistry();
            if (!string.IsNullOrEmpty(dir)) return dir;

            // ② 注册表卸载项 DisplayIcon/InstallLocation（App Paths 缺失时最可靠的来源，
            //    本机实测：Qoder CN 装在 G 盘，只有卸载项记录了该路径）
            dir = FindFromUninstall();
            if (!string.IsNullOrEmpty(dir)) return dir;

            // ③ 正在运行的客户端进程
            dir = FindFromProcess();
            if (!string.IsNullOrEmpty(dir)) return dir;

            // ④ 常见安装目录（含跨盘：用户目录可能不在系统盘）
            foreach (string p in CandidateInstallDirs())
            {
                try
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    if (File.Exists(Path.Combine(p, "Qoder CN.exe"))
                        || File.Exists(Path.Combine(p, "Qoder.exe"))
                        || File.Exists(Path.Combine(p, "qoder-cn.exe"))) return p;
                }
                catch { }
            }
            // ⑤ 目录存在即接受（新版本可能改了主 exe 名）
            foreach (string p in CandidateInstallDirs())
            {
                try { if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p; }
                catch { }
            }
            return null;
        }

        // 候选安装目录：覆盖「当前会话的用户目录 + 各盘符下的同名用户目录 + Program Files」
        private static List<string> CandidateInstallDirs()
        {
            var list = new List<string>();
            string[] names = { "Qoder CN", "Qoder" };

            // 当前会话的用户目录
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            AddDir(list, local, "Programs");
            AddDir(list, pf, null);
            AddDir(list, pfx86, null);

            // 跨盘兜底：本机用户目录可能位于非系统盘（实测 Qoder 在 G:\Users\...）
            // 用 USERPROFILE 的父级结构推导其它盘的同名路径
            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string leaf = Path.GetFileName(profile);                       // Administrator
                string usersParent = Path.GetDirectoryName(profile);           // C:\Users
                string usersLeaf = string.IsNullOrEmpty(usersParent) ? "Users" : Path.GetFileName(usersParent);
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != DriveType.Fixed) continue;
                        string alt = Path.Combine(d.RootDirectory.FullName, usersLeaf, leaf, "AppData", "Local", "Programs");
                        AddDir(list, alt, null);
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        private static void AddDir(List<string> list, string baseDir, string sub)
        {
            if (string.IsNullOrEmpty(baseDir)) return;
            string root = string.IsNullOrEmpty(sub) ? baseDir : Path.Combine(baseDir, sub);
            foreach (string n in new string[] { "Qoder CN", "Qoder" })
                list.Add(Path.Combine(root, n));
        }

        // 卸载项：DisplayIcon（含 exe 路径）优先，InstallLocation 兜底
        private static string FindFromUninstall()
        {
            string[] roots = new string[]
            {
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            RegistryKey[] hives = new RegistryKey[] { Registry.CurrentUser, Registry.LocalMachine };
            foreach (RegistryKey hive in hives)
            {
                foreach (string sub in roots)
                {
                    try
                    {
                        using (RegistryKey k = hive.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            foreach (string name in k.GetSubKeyNames())
                            {
                                try
                                {
                                    using (RegistryKey item = k.OpenSubKey(name))
                                    {
                                        if (item == null) continue;
                                        string display = item.GetValue("DisplayName") as string;
                                        if (string.IsNullOrEmpty(display)) continue;
                                        if (display.IndexOf("Qoder", StringComparison.OrdinalIgnoreCase) < 0) continue;

                                        string dir = DirOfExe(item.GetValue("DisplayIcon") as string);
                                        if (!string.IsNullOrEmpty(dir)) return dir;

                                        string loc = item.GetValue("InstallLocation") as string;
                                        if (!string.IsNullOrEmpty(loc))
                                        {
                                            try
                                            {
                                                string p = Environment.ExpandEnvironmentVariables(loc.Trim().Trim('"'));
                                                if (Directory.Exists(p)) return p;
                                            }
                                            catch { }
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        private static string FindFromRegistry()
        {
            string[] subKeys = new string[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Qoder CN.exe",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Qoder.exe",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\qoder-cn.exe",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\qodercn.exe"
            };
            RegistryKey[] roots = new RegistryKey[] { Registry.CurrentUser, Registry.LocalMachine };
            foreach (RegistryKey root in roots)
            {
                foreach (string sub in subKeys)
                {
                    try
                    {
                        using (RegistryKey k = root.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            object v = k.GetValue(null);
                            string dir = DirOfExe(v == null ? null : v.ToString());
                            if (!string.IsNullOrEmpty(dir)) return dir;
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        private static string DirOfExe(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            try
            {
                string p = Environment.ExpandEnvironmentVariables(exe.Trim().Trim('"'));
                if (File.Exists(p)) return Path.GetDirectoryName(p);
            }
            catch { }
            return null;
        }

        private static string FindFromProcess()
        {
            string[] names = new string[] { "Qoder CN", "Qoder", "qoder-cn" };
            foreach (string name in names)
            {
                Process[] ps = null;
                try { ps = Process.GetProcessesByName(name); }
                catch { }
                if (ps == null) continue;
                foreach (Process p in ps)
                {
                    try
                    {
                        string ep = p.MainModule.FileName;
                        string dir = string.IsNullOrEmpty(ep) ? null : Path.GetDirectoryName(ep);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            return null;
        }

        public static string ClientVersion()
        {
            try
            {
                string dir = FindInstallDir();
                if (!string.IsNullOrEmpty(dir))
                {
                    string p = Path.Combine(dir, "resources", "build-manifest.json");
                    if (File.Exists(p))
                    {
                        var m = JsonUtil.ParseJson(File.ReadAllText(p, Encoding.UTF8));
                        string v = JsonUtil.GetStringAny(m, "productVersion", "version");
                        if (!string.IsNullOrEmpty(v)) return v;
                    }
                }
            }
            catch { }
            return DefaultClientVersion;
        }

        public static string MachineId()
        {
            lock (MachineIdGate)
            {
                if (!string.IsNullOrEmpty(_machineId)) return _machineId;
                string id = null;
                try
                {
                    string dir = DataDir();
                    string p = Path.Combine(dir, "auth.machine-id");
                    if (File.Exists(p))
                    {
                        string s = File.ReadAllText(p, Encoding.UTF8).Trim();
                        if (!string.IsNullOrEmpty(s)) id = s;
                    }
                    if (string.IsNullOrEmpty(id))
                    {
                        id = Guid.NewGuid().ToString();
                        try
                        {
                            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                            File.WriteAllText(p, id, new UTF8Encoding(false));
                        }
                        catch { }
                    }
                }
                catch { }
                if (string.IsNullOrEmpty(id)) id = Guid.NewGuid().ToString();
                _machineId = id;
                return id;
            }
        }

        // 客户端 safeStorage：DPAPI 解 os_crypt 密钥 → AES-256-GCM 解 auth.v1.dat（v10 布局）
        private static QoderCreds LoadClient()
        {
            try
            {
                string dir = DataDir();
                string statePath = Path.Combine(dir, "Local State");
                string authPath = Path.Combine(dir, "auth.v1.dat");
                if (!File.Exists(statePath) || !File.Exists(authPath)) return null;

                var state = JsonUtil.ParseJson(File.ReadAllText(statePath, Encoding.UTF8));
                var osCrypt = JsonUtil.GetDict(state, "os_crypt");
                string encKey = JsonUtil.GetString(osCrypt, "encrypted_key");
                if (string.IsNullOrEmpty(encKey)) return null;

                byte[] wrapped = Convert.FromBase64String(encKey);
                if (wrapped.Length <= 5) return null;
                if (wrapped[0] != (byte)'D' || wrapped[1] != (byte)'P' || wrapped[2] != (byte)'A'
                    || wrapped[3] != (byte)'P' || wrapped[4] != (byte)'I') return null;
                byte[] keyBlob = new byte[wrapped.Length - 5];
                Buffer.BlockCopy(wrapped, 5, keyBlob, 0, keyBlob.Length);
                byte[] key = ProtectedData.Unprotect(keyBlob, null, DataProtectionScope.CurrentUser);
                if (key == null || key.Length != 32) return null;

                byte[] blob = File.ReadAllBytes(authPath);
                if (blob.Length < 3 + 12 + 16) return null;
                if (blob[0] != (byte)'v' || blob[1] != (byte)'1' || blob[2] != (byte)'0') return null;
                int ctLen = blob.Length - 15 - 16;
                byte[] nonce = new byte[12];
                Buffer.BlockCopy(blob, 3, nonce, 0, 12);
                byte[] cipher = new byte[ctLen];
                if (ctLen > 0) Buffer.BlockCopy(blob, 15, cipher, 0, ctLen);
                byte[] tag = new byte[16];
                Buffer.BlockCopy(blob, blob.Length - 16, tag, 0, 16);

                // .NET 4.8 无 AesGcm，且本机 CNG-GCM 不可用，复用 ZCode 的纯托管实现
                byte[] plain = ZcodeGcm.Decrypt(key, nonce, tag, cipher);
                if (plain == null) return null;
                var root = JsonUtil.ParseJson(Encoding.UTF8.GetString(plain));
                string token = JsonUtil.GetString(root, "token");
                if (string.IsNullOrEmpty(token)) return null;

                var c = new QoderCreds();
                c.Token = token;
                c.RefreshToken = JsonUtil.GetString(root, "refreshToken");
                c.ExpiresAt = JsonUtil.GetString(root, "expiresAt");
                c.RefreshExpiresAt = JsonUtil.GetString(root, "refreshTokenExpiresAt");
                var user = JsonUtil.GetDict(root, "user");
                if (user != null)
                {
                    c.Uid = JsonUtil.GetString(user, "id");
                    c.UserName = JsonUtil.GetString(user, "name");
                }
                return c;
            }
            catch { return null; }
        }

        public static QoderCreds LoadStore()
        {
            try
            {
                string p = TokenStorePath;
                if (!File.Exists(p)) return null;
                var dto = new JavaScriptSerializer().Deserialize<QoderTokenStoreDto>(File.ReadAllText(p, Encoding.UTF8));
                if (dto == null || string.IsNullOrEmpty(dto.token)) return null;
                string token = DpapiUnprotect(dto.token);
                if (string.IsNullOrEmpty(token)) return null;
                var c = new QoderCreds();
                c.Token = token;
                c.RefreshToken = string.IsNullOrEmpty(dto.refresh_token) ? null : DpapiUnprotect(dto.refresh_token);
                c.Uid = dto.uid;
                c.UserName = dto.user_name;
                c.ExpiresAt = dto.expires_at;
                c.RefreshExpiresAt = dto.refresh_expires_at;
                return c;
            }
            catch { return null; }
        }

        public static QoderCreds Load()
        {
            QoderCreds client = LoadClient();
            QoderCreds store = LoadStore();
            // 自有库未过期则优先（续期轮换后比客户端文件更新），否则用客户端登录态
            if (store != null && !string.IsNullOrEmpty(store.Token) && !IsExpired(store.ExpiresAt)) return store;
            if (client != null && !string.IsNullOrEmpty(client.Token)) return client;
            return store;
        }

        public static bool Save(QoderCreds c)
        {
            if (c == null || string.IsNullOrEmpty(c.Token)) return false;
            try
            {
                var dto = new QoderTokenStoreDto();
                dto.v = 1;
                dto.token = DpapiProtect(c.Token);
                dto.refresh_token = string.IsNullOrEmpty(c.RefreshToken) ? null : DpapiProtect(c.RefreshToken);
                dto.uid = c.Uid;
                dto.user_name = c.UserName;
                dto.expires_at = c.ExpiresAt;
                dto.refresh_expires_at = c.RefreshExpiresAt;
                dto.saved_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string json = new JavaScriptSerializer().Serialize(dto);
                string p = TokenStorePath;
                if (!Directory.Exists(HistoryStore.DataDir)) Directory.CreateDirectory(HistoryStore.DataDir);
                string tmp = p + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(p)) File.Replace(tmp, p, null);
                else File.Move(tmp, p);
                return true;
            }
            catch { return false; }
        }

        // 只写自有库：不做 safeStorage 加密回写，避免写坏客户端登录态（本机 CNG-GCM 不可用）
        public static QoderCreds Refresh(QoderCreds c)
        {
            if (c == null || string.IsNullOrEmpty(c.RefreshToken)) return null;
            try
            {
                bool jobToken = c.RefreshToken.StartsWith("jrt-", StringComparison.Ordinal);
                string url = BaseUrl + (jobToken ? "/api/v1/jobToken/refresh" : "/api/v1/deviceToken/refresh");

                var headers = new Dictionary<string, string>();
                headers["User-Agent"] = "qoder-local-proxy";
                headers["Accept"] = "application/json";
                var reqBody = new Dictionary<string, object>();
                reqBody["refresh_token"] = c.RefreshToken;
                string bodyJson = new JavaScriptSerializer().Serialize(reqBody);

                ApiResp r = HttpApi.PostWithStatus(url, headers, bodyJson);
                if (r == null || r.Status < 200 || r.Status >= 300 || string.IsNullOrEmpty(r.Body)) return null;

                var root = JsonUtil.ParseJson(r.Body);
                var data = JsonUtil.GetDict(root, "data");
                if (data == null) data = root;
                var user = JsonUtil.GetDict(data, "user");

                string token = FirstString(data, "token", "access_token", "accessToken", "device_token");
                if (string.IsNullOrEmpty(token) && data != root)
                    token = FirstString(root, "token", "access_token", "accessToken", "device_token");
                if (string.IsNullOrEmpty(token)) return null;

                var n = new QoderCreds();
                n.Token = token;
                n.RefreshToken = FirstString(data, "refresh_token", "refreshToken");
                if (string.IsNullOrEmpty(n.RefreshToken)) n.RefreshToken = c.RefreshToken;
                n.Uid = FirstString(data, "user_id", "userId", "uid");
                if (string.IsNullOrEmpty(n.Uid) && user != null) n.Uid = FirstString(user, "id", "uid");
                if (string.IsNullOrEmpty(n.Uid)) n.Uid = c.Uid;
                n.UserName = FirstString(data, "user_name", "username", "nickname");
                if (string.IsNullOrEmpty(n.UserName) && user != null) n.UserName = FirstString(user, "name", "username", "nickname");
                if (string.IsNullOrEmpty(n.UserName)) n.UserName = c.UserName;
                // 续期响应未给出新过期时间时置空（未知=不判过期），避免旧时间导致库被误弃用
                n.ExpiresAt = FirstString(data, "expiresAt", "expires_at");
                if (string.IsNullOrEmpty(n.ExpiresAt) && data != root)
                    n.ExpiresAt = FirstString(root, "expiresAt", "expires_at");
                n.RefreshExpiresAt = FirstString(data, "refreshTokenExpiresAt", "refresh_expires_at");
                if (string.IsNullOrEmpty(n.RefreshExpiresAt)) n.RefreshExpiresAt = c.RefreshExpiresAt;

                Save(n);
                return n;
            }
            catch { return null; }
        }

        public static List<AccountInfo> ListAccounts()
        {
            var list = new List<AccountInfo>();
            try
            {
                QoderCreds c = Load();
                if (c == null || string.IsNullOrEmpty(c.Token)) return list;
                var info = new AccountInfo();
                info.Brand = Platform;
                info.Username = string.IsNullOrEmpty(c.UserName) ? c.Uid : c.UserName;
                info.ExpiredAt = c.ExpiresAt;
                info.HasToken = true;
                list.Add(info);
            }
            catch { }
            return list;
        }

        internal static bool IsExpired(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return false;
            DateTime dt;
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt)) return false;
            return dt <= DateTime.UtcNow;
        }

        private static string FirstString(Dictionary<string, object> d, params string[] keys)
        {
            if (d == null) return null;
            foreach (string k in keys)
            {
                string v = JsonUtil.GetString(d, k);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }

        private static string DpapiProtect(string plain)
        {
            byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain ?? ""), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
        }

        private static string DpapiUnprotect(string b64)
        {
            try
            {
                byte[] dec = ProtectedData.Unprotect(Convert.FromBase64String(b64 ?? ""), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch { return null; }
        }
    }

    // ============ Qoder UMID 风控三件套（Cosy-Machine*） ============
    internal static class QoderUmid
    {
        private static readonly object Gate = new object();
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(50);
        private static Dictionary<string, string> _cached;
        private static string _cachedUid;
        private static DateTime _cachedAt = DateTime.MinValue;

        public static Dictionary<string, string> Get(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            lock (Gate)
            {
                if (_cached != null && _cachedUid == uid && DateTime.UtcNow - _cachedAt < Ttl) return _cached;
                Dictionary<string, string> r = Run(uid);
                if (r != null)
                {
                    _cached = r;
                    _cachedUid = uid;
                    _cachedAt = DateTime.UtcNow;
                }
                return r;
            }
        }

        private static string FindComponent()
        {
            try
            {
                string install = QoderAuth.FindInstallDir();
                if (!string.IsNullOrEmpty(install))
                {
                    string p = Path.Combine(install, "resources", "umid", "runtime-info.exe");
                    if (File.Exists(p)) return p;
                }
                string bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".qoder-cn", ".bin");
                if (Directory.Exists(bin))
                {
                    string[] dirs = Directory.GetDirectories(bin, "umid-*");
                    Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                    for (int i = dirs.Length - 1; i >= 0; i--)
                    {
                        string p = Path.Combine(dirs[i], "runtime-info.exe");
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch { }
            return null;
        }

        private static Dictionary<string, string> Run(string uid)
        {
            string exe = FindComponent();
            if (string.IsNullOrEmpty(exe)) return null;
            Process proc = null;
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = "0 --account-stdin";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                proc = Process.Start(psi);
                if (proc == null) return null;

                var output = new StringBuilder();
                proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        lock (output) output.AppendLine(e.Data);
                    }
                };
                proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                try
                {
                    var req = new Dictionary<string, object>();
                    req["account"] = uid;
                    proc.StandardInput.WriteLine(new JavaScriptSerializer().Serialize(req));
                    proc.StandardInput.Close();
                }
                catch { }

                if (!proc.WaitForExit(25000))
                {
                    try { proc.Kill(); } catch { }
                    return null;
                }
                try { proc.WaitForExit(); } catch { }
                if (!proc.HasExited || proc.ExitCode != 0) return null;

                string text;
                lock (output) text = output.ToString();
                if (string.IsNullOrEmpty(text)) return null;
                string line = null;
                foreach (string l in text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = l.Trim();
                    if (t.Length > 0) { line = t; break; }
                }
                if (string.IsNullOrEmpty(line)) return null;

                var root = JsonUtil.ParseJson(line);
                string mt = JsonUtil.GetString(root, "machineToken");
                string mc = JsonUtil.GetString(root, "machineCode");
                string mtype = JsonUtil.GetString(root, "machineType");
                if (string.IsNullOrEmpty(mt) && string.IsNullOrEmpty(mc) && string.IsNullOrEmpty(mtype)) return null;

                var d = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(mt)) d["machineToken"] = mt;
                if (!string.IsNullOrEmpty(mc)) d["machineCode"] = mc;
                if (!string.IsNullOrEmpty(mtype)) d["machineType"] = mtype;
                return d;
            }
            catch { return null; }
            finally
            {
                try { if (proc != null) proc.Dispose(); } catch { }
            }
        }
    }

    internal class QoderCampaign
    {
        public string CampaignId;
        public string CampaignKey;
        public string ActionType;
        public string ClaimStatus;
        public int Amount;
        public int Days;
    }

    // ============ Qoder 每日 100 Credits 领取引擎 ============
    internal static class QoderRunner
    {
        public static List<QoderCampaign> ParseCampaigns(string json)
        {
            var list = new List<QoderCampaign>();
            try
            {
                var root = JsonUtil.ParseJson(json);
                object campaignsObj = null;
                root.TryGetValue("campaigns", out campaignsObj);
                if (!(campaignsObj is ArrayList))
                {
                    var data = JsonUtil.GetDict(root, "data");
                    if (data != null) data.TryGetValue("campaigns", out campaignsObj);
                }
                var arr = campaignsObj as ArrayList;   // JavaScriptSerializer 数组即 ArrayList（踩坑 #13）
                if (arr == null) return list;

                foreach (object item in arr)
                {
                    var d = item as Dictionary<string, object>;
                    if (d == null) continue;
                    var c = new QoderCampaign();
                    c.CampaignId = JsonUtil.GetString(d, "campaignId");
                    c.CampaignKey = JsonUtil.GetString(d, "campaignKey");
                    c.ActionType = JsonUtil.GetString(d, "actionType");
                    c.ClaimStatus = JsonUtil.GetString(d, "claimStatus");
                    var benefit = JsonUtil.GetDict(d, "benefit");
                    int amount = JsonUtil.GetInt(benefit, "amount");
                    c.Amount = amount < 0 ? 0 : amount;
                    int days = JsonUtil.GetInt(JsonUtil.GetDict(benefit, "validity"), "days");
                    c.Days = days < 0 ? 0 : days;
                    if (!string.IsNullOrEmpty(c.CampaignId)) list.Add(c);
                }
            }
            catch { }
            return list;
        }

        public static CheckinResult Run(bool queryOnly)
        {
            QoderCreds creds = QoderAuth.Load();
            if (creds == null || string.IsNullOrEmpty(creds.Token))
                return CheckinResult.Fail(-1, "未找到 Qoder 登录态，请先登录 Qoder CN 客户端", QoderAuth.Platform);

            string username = UserNameOf(creds);
            try
            {
                ApiResp list = GetCampaigns(creds);
                if (list == null || list.Status == 0)
                    return FailWith(username, -3, "网络异常: " + NetworkError(list));

                if (list.Status == 401 || list.Status == 403)
                {
                    QoderCreds refreshed = QoderAuth.Refresh(creds);
                    if (refreshed != null)
                    {
                        creds = refreshed;
                        username = UserNameOf(creds);
                        list = GetCampaigns(creds);
                    }
                    if (list == null || list.Status == 0)
                        return FailWith(username, -3, "网络异常: " + NetworkError(list));
                    if (list.Status == 401 || list.Status == 403)
                        return FailWith(username, -6, "Qoder 登录已失效，请重新登录 Qoder CN 客户端");
                }

                if (list.Status == 429)
                    return FailWith(username, 429, "请求过于频繁，稍后重试");
                if (!list.Ok)
                    return FailWith(username, list.Status, "Qoder 服务返回错误(" + list.Status + ")" + Suffix(ServerMessage(list.Body)));

                List<QoderCampaign> campaigns = ParseCampaigns(list.Body);
                QoderCampaign target = null;
                QoderCampaign claimed = null;
                foreach (QoderCampaign c in campaigns)
                {
                    if (c.ActionType != "CLAIM_BENEFIT") continue;
                    if (c.ClaimStatus == "CLAIMABLE")
                    {
                        if (target == null) target = c;
                    }
                    else if (c.ClaimStatus == "CLAIMED")
                    {
                        if (claimed == null) claimed = c;
                    }
                }

                if (queryOnly)
                {
                    if (target != null)
                    {
                        return new CheckinResult
                        {
                            Success = false, Code = 0, CheckedIn = false, Already = false,
                            Username = username, Base = target.Amount > 0 ? (int?)target.Amount : null,
                            HasClaimable = true,
                            DisplayText = "发现可领取：+" + AmountOf(target) + " Credits",
                            Brand = QoderAuth.Platform
                        };
                    }
                    if (claimed != null) return AlreadyResult(username, claimed);
                    // 活动未下发（当日 10:00 前）：非失败态，Code=0 供状态灯显示"待领取"
                    return new CheckinResult
                    {
                        Success = false, Code = 0, CheckedIn = false, Already = false,
                        Username = username,
                        DisplayText = "今日暂无可领取活动（每日 10:00 刷新）",
                        Brand = QoderAuth.Platform
                    };
                }

                if (target == null)
                {
                    if (claimed != null) return AlreadyResult(username, claimed);
                    return new CheckinResult
                    {
                        Success = false, Code = 0, CheckedIn = false, Already = false,
                        Username = username,
                        DisplayText = "今日活动 10:00（UTC+8）开启，稍后自动重试",
                        Brand = QoderAuth.Platform
                    };
                }

                return Claim(creds, target, username);
            }
            catch (Exception ex)
            {
                return FailWith(username, -3, "网络异常: " + ex.Message);
            }
        }

        private static CheckinResult Claim(QoderCreds creds, QoderCampaign target, string username)
        {
            var headers = BuildHeaders(creds);
            headers["Origin"] = QoderAuth.BaseUrl;
            string url = QoderAuth.BaseUrl + "/sash/api/v1/me/campaigns/"
                + Uri.EscapeDataString(target.CampaignId) + "/claim";

            ApiResp r = HttpApi.PostWithStatus(url, headers, "{}");
            if (r == null || r.Status == 0)
                return FailWith(username, -3, "网络异常: " + NetworkError(r));
            if (r.Status == 401 || r.Status == 403)
                return FailWith(username, -6, "Qoder 登录已失效，请重新登录 Qoder CN 客户端");
            if (r.Status == 429)
                return FailWith(username, 429, "请求过于频繁，稍后重试");

            var root = JsonUtil.ParseJson(r.Body);
            var data = JsonUtil.GetDict(root, "data");
            string result = JsonUtil.GetStringAny(root, "result", "status");
            if (string.IsNullOrEmpty(result)) result = JsonUtil.GetStringAny(data, "result", "status");
            bool replayed = JsonUtil.GetBool(root, "replayed") || (data != null && JsonUtil.GetBool(data, "replayed"));

            if (r.Status == 409
                || string.Equals(result, "ALREADY_CLAIMED", StringComparison.OrdinalIgnoreCase)
                || replayed)
                return AlreadyResult(username, target);

            if (!r.Ok)
                return FailWith(username, r.Status, "Qoder 服务返回错误(" + r.Status + ")" + Suffix(ServerMessage(r.Body)));

            var benefit = JsonUtil.GetDict(root, "benefit");
            if (benefit == null && data != null) benefit = JsonUtil.GetDict(data, "benefit");
            int amount = target.Amount > 0 ? target.Amount : 100;
            int days = target.Days > 0 ? target.Days : 30;
            int a = JsonUtil.GetInt(benefit, "amount");
            if (a > 0) amount = a;
            int d = JsonUtil.GetInt(JsonUtil.GetDict(benefit, "validity"), "days");
            if (d > 0) days = d;

            return new CheckinResult
            {
                Success = true, Code = 0, CheckedIn = true, Already = false,
                Username = username, Base = amount,
                DisplayText = "领取成功：+" + amount + " Credits（" + days + " 天有效）",
                Brand = QoderAuth.Platform
            };
        }

        private static CheckinResult AlreadyResult(string username, QoderCampaign c)
        {
            int amount = c != null && c.Amount > 0 ? c.Amount : 100;
            return new CheckinResult
            {
                Success = true, Code = 0, CheckedIn = true, Already = true,
                Username = username, Base = amount,
                DisplayText = "今日已领取 " + amount + " Credits",
                Brand = QoderAuth.Platform
            };
        }

        private static ApiResp GetCampaigns(QoderCreds creds)
        {
            try
            {
                string url = QoderAuth.BaseUrl + "/sash/api/v1/me/campaigns?forceRefresh=true";
                return HttpApi.GetWithStatus(url, BuildHeaders(creds));
            }
            catch (Exception ex)
            {
                var r = new ApiResp();
                r.Status = 0;
                r.Error = ex.Message;
                return r;
            }
        }

        private static Dictionary<string, string> BuildHeaders(QoderCreds creds)
        {
            var h = new Dictionary<string, string>();
            h["authorization"] = "Bearer " + creds.Token;
            h["cosy-clienttype"] = "10";
            h["accept"] = "application/json";
            h["accept-language"] = "zh-CN";
            h["user-agent"] = "Qoder";
            h["Cosy-Version"] = QoderAuth.ClientVersion();
            h["Cosy-MachineOS"] = "x86_64_win32";
            h["Cosy-MachineId"] = QoderAuth.MachineId();
            h["Cosy-MachineHostname"] = PrintableHostname();

            Dictionary<string, string> umid = QoderUmid.Get(creds.Uid);
            if (umid != null)
            {
                string v;
                if (umid.TryGetValue("machineToken", out v) && !string.IsNullOrEmpty(v)) h["Cosy-MachineToken"] = v;
                if (umid.TryGetValue("machineCode", out v) && !string.IsNullOrEmpty(v)) h["Cosy-MachineCode"] = v;
                if (umid.TryGetValue("machineType", out v) && !string.IsNullOrEmpty(v)) h["Cosy-MachineType"] = v;
            }
            return h;
        }

        private static string PrintableHostname()
        {
            string s = Environment.MachineName;
            if (string.IsNullOrEmpty(s)) return "Windows";
            var sb = new StringBuilder();
            foreach (char ch in s)
                if (ch >= 32 && ch <= 126) sb.Append(ch);
            string r = sb.ToString().Trim();
            return r.Length == 0 ? "Windows" : r;
        }

        private static string UserNameOf(QoderCreds c)
        {
            if (c == null) return null;
            return string.IsNullOrEmpty(c.UserName) ? c.Uid : c.UserName;
        }

        private static int AmountOf(QoderCampaign c)
        {
            return c != null && c.Amount > 0 ? c.Amount : 100;
        }

        private static CheckinResult FailWith(string username, int code, string message)
        {
            CheckinResult r = CheckinResult.Fail(code, message, QoderAuth.Platform);
            r.Username = username;
            return r;
        }

        private static string NetworkError(ApiResp r)
        {
            if (r != null && !string.IsNullOrEmpty(r.Error)) return r.Error;
            return "Qoder 服务不可达";
        }

        private static string ServerMessage(string body)
        {
            try
            {
                var d = JsonUtil.ParseJson(body);
                string m = JsonUtil.GetStringAny(d, "message", "msg", "error");
                if (!string.IsNullOrEmpty(m)) return m;
            }
            catch { }
            return null;
        }

        private static string Suffix(string message)
        {
            return string.IsNullOrEmpty(message) ? "" : "：" + message;
        }
    }
}
