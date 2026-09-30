# TraeSign

Trae（TraeWork / Trae SOLO CN / Trae CN）与 WorkBuddy **每日自动签到托盘助手**：常驻系统托盘，每天自动完成两个平台的签到领取积分，并在日历中记录每一天的签到结果。

程序本体仅 `TraeSign.exe` 一个文件，全部功能内置，**无需安装任何运行环境**。

## 功能特性

- 双平台：主窗口两个页签（Trae / WorkBuddy），各自独立调度，互不影响
- 自动签到：每天 00:05 自动签到，失败按 5/15/30/60/120 分钟递增重试
- Trae 多账号：自动读取本机 TRAE SOLO CN / Trae CN / TraeWork 的登录态，可切换签到账号
- WorkBuddy 授权：首次点"登录授权"完成一次浏览器登录，之后 token 自动续期长期有效
- 托盘状态：图标颜色实时反映两平台聚合状态（灰=进行中 / 绿勾=全部已签 / 红=有异常）
- 签到日历：自绘月历，绿色标记签到成功日，可翻月查看，按平台独立记录
- 二次确认：签到成功后复查服务端状态，杜绝 token 过期导致的"假成功"

## 快速开始

1. 双击运行 `TraeSign.exe`（首次运行被 Defender 拦截属正常，程序无数字签名，点"仍要运行"）
2. 程序缩到系统托盘；Trae 已登录客户端则自动完成 Trae 侧签到
3. 双击托盘图标打开主窗口，切到 **WorkBuddy** 页签点 **"登录授权"**，在浏览器完成一次腾讯 SSO 确认即可

详细说明（自动签到、WorkBuddy 授权原理、多账号限制、开机自启、常见问题）见 [使用说明.md](使用说明.md)。

## 从源码编译

```
compile.bat
```

依赖 Windows 自带的 .NET Framework 4.8（`csc.exe`），零 NuGet 包，输出单文件 `TraeSign.exe`。

| 源文件 | 说明 |
|---|---|
| `TraeCheckinApp.cs` | 主程序（托盘、双平台调度、签到引擎、凭证授权/解密） |
| `CalendarControl.cs` | 自绘签到日历控件（GDI+） |

## 数据说明

- 签到历史：`%APPDATA%\TraeCheckin\history.json`（本地记录，按品牌键控，删除即清空）
- WorkBuddy token：`%APPDATA%\TraeCheckin\workbuddy_token.json`（**DPAPI 加密**存储，绑定当前 Windows 用户）
- 程序**只读** Trae / WorkBuddy 的登录态文件，不修改、不上传登录凭证

## 限制

- 同机多个 Trae 软件共享设备 ID，服务端限制**每天仅一个账号可签到成功**，属服务端规则，非程序问题
- WorkBuddy 客户端 5.6+ 的本机登录态为加密格式（`$wbEncrypted`），程序无法直接解密，故采用官方插件 OAuth 授权流获取签到凭证

## License

[MIT](LICENSE)
