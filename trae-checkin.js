// TraeWork / Trae solo 每日自动签到 —— 跨平台版（Windows/macOS/Linux）
// 原理: 读取本地 Trae 登录态(storage.json 的 iCubeAuthInfo://icube.cloudide)，AES-128-CBC 解密拿 token；
//       x-device-id 必须用 icube-dc 设备ID(iCubeAuthInfo://icube-dc:<id> 里的数值)，
//       否则服务端把请求当陌生设备 => 返回 9074"参与用户太多"；
//       同机多账号共享该 dcId，因此同一设备每天只允许一个账号签到(第二账号返回 9095"本设备已签到")。
// 安全: 只读登录态、不打印 token、不修改登录态文件。
const fs = require('fs');
const path = require('path');
const https = require('https');

// ---------- 解密常量（源自官方客户端派生逻辑，无需修改） ----------
const HP = 16, q8_AES128 = 16, WP = HP, rh = 64, Rv = 32, VP = 64, Em = 6;
const ure = Uint8Array.from([82,9,106,213,48,54,165,56,191,64,163,158,129,243,215,251,124,227,57,130,155,47,255,135,52,142,67,68,196,222,233,203,84,123,148,50,166,194,35,61,238,76,149,11,66,250,195,78,8,46,161,102,40,217,36,178,118,91,162,73,109,139,209,37]);
const dre = Uint8Array.from([31,221,168,51,136,7,199,49,177,18,16,89,39,128,236,95,96,81,127,169,25,181,74,13,45,229,122,159,147,201,156,239,160,224,59,77,174,42,245,176,200,235,187,60,131,83,153,97,23,43,4,126,186,119,214,38,225,105,20,99,85,33,12,125]);
async function sha512(data) { const h = await crypto.subtle.digest('SHA-512', data); return new Uint8Array(h); }
function xorArrays(a, b, n) { const r = new Uint8Array(n); for (let i = 0; i < n; i++) r[i] = a[i] ^ b[i]; return r; }
async function decrypt(b64) {
  const t = new Uint8Array(Buffer.from(b64, 'base64'));
  const key = t.slice(Em, Em + Rv);
  const sha = await sha512(key);
  const xor = xorArrays(ure, dre, VP);
  const comb = new Uint8Array(rh + VP); comb.set(sha, 0); comb.set(xor, rh);
  const hash = await sha512(comb);
  const aesKey = hash.slice(0, q8_AES128);
  const iv = hash.slice(q8_AES128, q8_AES128 + WP);
  const ct = t.slice(Rv + Em);
  const ck = await crypto.subtle.importKey('raw', aesKey, { name: 'AES-CBC' }, false, ['decrypt']);
  const dec = new Uint8Array(await crypto.subtle.decrypt({ name: 'AES-CBC', iv }, ck, ct));
  return new TextDecoder().decode(dec.slice(rh));
}

// ---------- 登录态与设备标识路径自适应(win: %APPDATA% / 其它: ~/Library/Application Support) ----------
function candidateDirs() {
  const list = [];
  const win = process.platform === 'win32';
  if (process.env.APPDATA) list.push(process.env.APPDATA);
  const home = process.env.HOME || process.env.USERPROFILE;
  if (!win && home) list.push(path.join(home, 'Library', 'Application Support'));
  const roots = list.filter(Boolean);
  const brands = ['TRAE SOLO CN', 'Trae CN', 'TraeWork'];
  const out = [];
  for (const d of roots) for (const b of brands) out.push({ brand: b, file: path.join(d, b, 'User', 'globalStorage', 'storage.json') });
  return out;
}
function readAuth(file) {
  try {
    const s = JSON.parse(fs.readFileSync(file, 'utf8'));
    let dcId = '';
    for (const k of Object.keys(s)) { const m = k.match(/^iCubeAuthInfo:\/\/icube-dc:(\d+)/); if (m) { dcId = m[1]; break; } }
    for (const k of Object.keys(s)) {
      if (k.startsWith('iCubeAuthInfo://icube.cloudide')) {
        return { enc: s[k], file, devId: dcId || s['telemetry.devDeviceId'] || s['telemetry.machineId'] || '' };
      }
    }
  } catch (e) { /* 不可读则跳过 */ }
  return null;
}
// brandFilter: 指定软件(brand)时只查该登录态；缺省取第一个可用
function findAuth(brandFilter) {
  for (const { brand, file } of candidateDirs()) {
    if (brandFilter && brand !== brandFilter) continue;
    const a = readAuth(file);
    if (a) { a.brand = brand; return a; }
  }
  return null;
}

// ---------- HTTP POST(JSON) ----------
function postJSON(url, { headers = {}, body = {} }, insecure = false) {
  return new Promise((resolve, reject) => {
    const u = new URL(url);
    const req = https.request({
      hostname: u.hostname, port: u.port || 443, path: u.pathname + (u.search || ''),
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...headers },
      rejectUnauthorized: !insecure,
      family: 4,
    }, (res) => {
      let data = '';
      res.on('data', c => (data += c));
      res.on('end', () => { try { resolve({ status: res.statusCode, body: JSON.parse(data || '{}') }); } catch (e) { resolve({ status: res.statusCode, body: null, raw: data }); } });
    });
    req.on('error', reject);
    req.write(JSON.stringify(body));
    req.end();
  });
}
// 本机若有自签证书拦截(代理)，自动降级为不校验证书重试一次
async function post(url, headers, body) {
  try { return await postJSON(url, { headers, body }); }
  catch (e) {
    const certErr = /SELF_SIGNED|UNABLE_TO_VERIFY|CERT|CERTIFICATE|DEPTH_ZERO/i.test(String(e.message || e.code));
    if (!certErr) throw e;
    return await postJSON(url, { headers, body }, true);
  }
}
const sleep = ms => new Promise(r => setTimeout(r, ms));

// ---------- 主流程 ----------
const STATUS_URL = 'https://api.trae.cn/trae/api/v2/ug/checkin_credits/status';
const CLAIM_URL  = 'https://api.trae.cn/trae/api/v2/ug/checkin_credits/claim';
const STATUS_ONLY = process.argv.includes('--status') || process.argv.includes('-s');
const JSON_MODE = process.argv.includes('--json');
const LIST_MODE = process.argv.includes('--list');

function argValue(name) {
  const i = process.argv.indexOf(name);
  return (i >= 0 && i + 1 < process.argv.length) ? process.argv[i + 1] : null;
}
const APP_BRAND = argValue('--app');   // 指定软件(brand)，缺省取第一个可用登录态

// --json 模式: stdout 只输出一行 JSON 后退出，诊断走 stderr
function emitJson(obj) {
  if (process.env.TRAE_CHECKIN_DEBUG) process.stderr.write('debug: ' + JSON.stringify(obj) + '\n');
  console.log(JSON.stringify(obj));
  process.exit(0);
}

// --list 模式: 枚举所有软件的登录态账号（不打印 token）
async function listAccounts() {
  const accounts = [];
  for (const { brand, file } of candidateDirs()) {
    const a = readAuth(file);
    if (!a) continue;
    try {
      const auth = JSON.parse(await decrypt(a.enc));
      accounts.push({ brand, username: (auth.account && auth.account.username) || null, expiredAt: auth.expiredAt || null, hasToken: !!auth.token });
    } catch (e) {
      accounts.push({ brand, username: null, expiredAt: null, hasToken: false });
    }
  }
  emitJson({ accounts });
}

async function main() {
  if (LIST_MODE) return listAccounts();

  const found = findAuth(APP_BRAND);
  if (!found) {
    if (JSON_MODE) return emitJson({ success: false, code: -1, checked_in: false, brand: APP_BRAND || null, username: null, base: null, extra: null, message: '未找到登录态，请先运行 Trae 桌面端登录' });
    console.log('ERROR: No auth data found. 请先用 Trae 桌面端登录(TRAE SOLO CN / Trae CN)。'); return;
  }
  const brand = found.brand;
  const auth = JSON.parse(await decrypt(found.enc));
  const username = (auth.account && auth.account.username) || null;
  if (!auth.token) {
    if (JSON_MODE) return emitJson({ success: false, code: -2, checked_in: false, brand, username, base: null, extra: null, message: '登录态无 token' });
    console.log('ERROR: No token in auth data'); return;
  }

  const headers = {
    'Authorization': `Cloud-IDE-JWT ${auth.token}`,
    'Content-Type': 'application/json',
    'x-device-id': found.devId,   // 关键: icube-dc 设备ID
  };
  if (auth.userRegion?.region) headers['X-User-Region'] = auth.userRegion.region;

  let base = null, extra = null;
  try {
    const status = (await post(STATUS_URL, headers, {})).body;
    base = status.credits != null ? Number(status.credits) : null;
    extra = status.extra_credits != null ? Number(status.extra_credits) : null;

    // 前置校验：credits/extra 全缺失说明 token 已失效或服务端拒绝（有效账号必返回积分）
    if (base === null && extra === null) {
      const msg = '账号状态异常（token 可能已过期），请重新登录该软件';
      if (JSON_MODE) return emitJson({ success: false, code: -6, checked_in: false, brand, username, base, extra, message: msg });
      console.log('ERROR: ' + msg); return;
    }

    if (STATUS_ONLY) {
      if (JSON_MODE) return emitJson({ success: status.checked_in === true, code: 0, checked_in: status.checked_in === true, already: status.checked_in === true, brand, username, base, extra, message: status.checked_in ? '今日已签到' : '今日未签到' });
      console.log(`Check-in status: checked_in=${status.checked_in} enable=${status.enable} credits=${status.credits}`); return;
    }
    if (status.checked_in) {
      // already=true: 今日已签到，未执行 claim（无需重复签到）
      if (JSON_MODE) return emitJson({ success: true, code: 0, checked_in: true, already: true, brand, username, base, extra, message: '今日已签到' });
      console.log(`Already checked in today. Credits: ${status.credits}`); return;
    }
    if (!status.enable) {
      if (JSON_MODE) return emitJson({ success: false, code: status.code || -4, checked_in: false, brand, username, base, extra, message: '签到未开启' });
      console.log('Check-in is not enabled'); return;
    }

    // --json 模式: 单次 claim + 二次确认（防 token 过期导致 claim 假成功），不做脚本内重试
    if (JSON_MODE) {
      const claim = (await post(CLAIM_URL, headers, { req_source: 1 })).body;
      if (claim.code === 0 || claim.checked_in) {
        // 二次确认：复查 status.checked_in，确认签到真实生效
        const confirm = (await post(STATUS_URL, headers, {})).body;
        if (confirm && confirm.checked_in === true) {
          const cBase = confirm.credits != null ? Number(confirm.credits) : base;
          const cExtra = confirm.extra_credits != null ? Number(confirm.extra_credits) : extra;
          return emitJson({ success: true, code: 0, checked_in: true, brand, username, base: cBase, extra: cExtra, message: '签到成功' });
        }
        return emitJson({ success: false, code: -6, checked_in: false, brand, username, base, extra, message: '签到结果异常（token 可能已过期），请重新登录该软件' });
      }
      if (claim.code === 9095) return emitJson({ success: false, code: 9095, checked_in: false, brand, username, base, extra, message: '本设备今日已有账号签到' });
      return emitJson({ success: false, code: claim.code != null ? claim.code : -5, checked_in: false, brand, username, base, extra, message: claim.message || '签到失败' });
    }

    const maxTries = 8, delays = [0, 5, 15, 30, 60, 120, 240, 480];
    for (let i = 0; i < maxTries; i++) {
      if (i > 0) { console.log(`retry #${i + 1} in ${delays[Math.min(i, delays.length - 1)]}s ...`); await sleep(delays[Math.min(i, delays.length - 1)] * 1000); }
      const claim = (await post(CLAIM_URL, headers, { req_source: 1 })).body;
      if (claim.code === 0 || claim.checked_in) {
        const total = Number(status.credits || 0) + Number(status.extra_credits || 0);
        console.log(`Check-in successful! Credits: ${total || '已领取'} (base=${status.credits ?? '-'}, extra=${status.extra_credits ?? '-'})`);
        return;
      }
      if (claim.code === 9074) { console.log(`[9074] 参与用户太多，稍后重试 (try ${i + 1}/${maxTries})`); continue; }
      if (claim.code === 9004) { console.log(`[9004] order parameters incorrect (try ${i + 1}/${maxTries})`); continue; }
      if (claim.code === 9095) { console.log('[9095] 本设备今日已有账号签到，明日再来。'); return; }
      console.log(`Check-in failed: code=${claim.code} ${claim.message || JSON.stringify(claim)}`); return;
    }
    console.log('Check-in failed: 多次重试后仍未成功，今日稍后再试。');
  } catch (e) {
    if (JSON_MODE) return emitJson({ success: false, code: -3, checked_in: false, brand, username, base, extra, message: String(e.message || e) });
    console.log('Error:', e.message);
  }
}
main().catch(e => console.log('Fatal:', e.message));
