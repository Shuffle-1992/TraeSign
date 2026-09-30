# TraeSign

**三合一自动签到托盘助手**：Trae / WorkBuddy / ZCode 三个 AI 编程工具的免费积分与 token，一个常驻托盘程序全自动帮你领。

## 支持的三种签到

| 平台 | 做什么 | 自动频率 | 凭证方式（无需输入密码） |
|---|---|---|---|
| **Trae**（字节的 AI IDE） | 每日签到领积分 | 每天 00:05 | 自动读取本机 Trae 客户端登录态，多账号可切换 |
| **WorkBuddy**（腾讯的 AI 工作台） | 每日签到领积分 | 每天 00:05 | 首次点"登录授权"过一次浏览器，之后 token 自动续期 |
| **ZCode**（智谱 Z.ai 编程客户端） | **免费活动套餐自动领取**（如 1 亿 GLM token） | 每 10 分钟检查 | 自动读取本机 ZCode 登录态，完全免授权 |

三个平台独立调度互不影响；托盘图标一眼看清整体状态（绿=全部完成 / 红=有异常 / 灰=进行中）；每个平台一张独立签到日历。

程序本体仅 `TraeSign.exe` 一个文件（约 100KB），全部功能内置，**无需安装任何运行环境**。

## 功能特性

- 三页签主窗口：Trae / WorkBuddy / ZCode 各自独立管理
- Trae / WorkBuddy 每日签到：失败按 5/15/30/60/120 分钟递增重试，签到成功二次确认防"假成功"
- WorkBuddy OAuth 授权：一次浏览器确认，refreshToken 滚动续期长期有效
- ZCode 活动套餐：发现即自动过阿里云无痕验证码领取（真实 Edge 环境自动通过，弹窗几秒自关），名额领完按服务端补货时间自动重试
- 签到日历：自绘月历，绿色标记成功日，跨天自动翻到当月，按平台独立记录
- 托盘聚合状态 + 各平台独立气泡通知

## 快速开始

1. 双击运行 `TraeSign.exe`（首次运行被 Defender 拦截属正常，程序无数字签名，点"仍要运行"）
2. **Trae**：已登录 Trae 客户端即自动签到
3. **WorkBuddy**：主窗口 WorkBuddy 页签点 **"登录授权"**，浏览器完成一次腾讯 SSO 确认
4. **ZCode**：本机已登录 ZCode 客户端/CLI（`~/.zcode/v2/credentials.json` 存在）即自动工作，**无需任何授权**；发现新活动套餐自动领取

详细说明见 [使用说明.md](使用说明.md)。

## 从源码编译

```
compile.bat
```

依赖 Windows 自带的 .NET Framework 4.8（`csc.exe`），零 NuGet 包，输出单文件 `TraeSign.exe`。

| 源文件 | 说明 |
|---|---|
| `TraeCheckinApp.cs` | 主程序（托盘、三平台调度、签到/领取引擎、凭证解密、Edge 验证码） |
| `CalendarControl.cs` | 自绘签到日历控件（GDI+） |

## 数据说明

- 签到历史：`%APPDATA%\TraeCheckin\history.json`（按品牌键控，删除即清空）
- WorkBuddy token：`%APPDATA%\TraeCheckin\workbuddy_token.json`（**DPAPI 加密**存储，绑定当前 Windows 用户）
- ZCode 状态：`%APPDATA%\TraeCheckin\zcode_state.json`（设备 ID 与已领套餐记录，不含凭证）
- 程序**只读** Trae / WorkBuddy / ZCode 的本地登录态文件，不修改、不上传登录凭证

## 限制

- 同机多个 Trae 软件共享设备 ID，服务端限制**每天仅一个账号可签到成功**，属服务端规则，非程序问题
- WorkBuddy 客户端 5.6+ 的本机登录态为加密格式（`$wbEncrypted`），程序无法直接解密，故采用官方插件 OAuth 授权流获取签到凭证
- ZCode 套餐领取需过阿里云无痕验证码：程序通过系统 Edge 自动完成（真实浏览器环境自动通过）；若风控升级为人工滑动会提示到客户端手动领取

## License

[MIT](LICENSE)
