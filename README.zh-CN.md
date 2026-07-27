# Vouch

[English](README.md) · **中文** · [Русский](README.ru.md) · [日本語](README.ja.md)

一款现代化的跨平台桌面 **Steam 身份验证器** —— 对已停止维护的
[Steam Desktop Authenticator](https://github.com/Jessecar96/SteamDesktopAuthenticator)
的完全重写，基于 Avalonia 构建，拥有简洁的多账号界面。

在一个应用中即可管理多个 Steam 账号、生成登录验证码、批准交易/市场确认、
查看并回应交易报价、浏览并转移库存物品、批准登录、管理账号已登录的设备，
以及绑定或解绑身份验证器。

- **技术栈：** Avalonia 12 (Fluent) · .NET 10 · CommunityToolkit.Mvvm · 登录用 SteamKit2 + 其余纯 HTTP 直连 Steam
- **平台：** Windows（主要）、Linux、macOS
- **语言：** English · 简体中文 · Русский · 日本語（可实时切换）

---

## ⚠️ 安全须知 —— 请务必先读

**你的 `.maFile` 就是你的账号。** 它保存着 2FA 种子（`shared_secret`）、
移动确认密钥（`identity_secret`）、撤销码、有效的会话令牌，并且在本应用中还包含你的
Steam **密码**。任何人只要拿到解密后的 maFile，就能彻底接管该账号。

**只从官方仓库下载 Vouch：**

> ### https://github.com/Monodesu/Vouch

任何其他构建 —— 无论是"镜像"、二次上传，还是某个分叉的发布版 —— **都可能被植入后门以窃取你的
maFile**。如果不是从上面的链接获取的，请一律视为恶意软件。Vouch 绝不会把你的密钥或密码发送到
除 Steam 官方服务器以外的任何地方。

**为静态文件加密：** 打开 **设置 → 加密磁盘上的 maFile** 并设置一个通行密钥。此后每个 maFile
（包括你的密码）都会在磁盘上用 **Argon2id** 派生密钥 + **AES-256-GCM** 加密 —— 密码错误或文件被篡改都会被检测出来，而不会被错误解密；每次启动只需输入一次通行密钥。加密可以保护静态存储的
文件，但它 **无法** 保护你免受运行被篡改的构建版本的风险 —— 这正是为什么"只从官方仓库下载"同样重要。

---

## 功能特性

### 账号
- 多账号侧边栏，支持 **搜索**、**多选** 和 **拖拽排序**（也可跨分组拖拽）
- **分组**：将账号组织进可折叠的分组（右键 → 移动到 / 新建分组），其余账号归入默认分组 ——
  排序和折叠状态都会持久保存
- 实时 **TOTP 登录验证码**，带倒计时环；一键复制
- 复制用户名 / 密码；就地编辑已保存的密码；可为每个账号添加自由文本 **备注**（保存在 maFile 中）
- 头像、个性昵称，以及 Steam **等级 / 封禁状态**（VAC · 游戏 · 交易）
- 一目了然的 **会话状态** —— 已登录 / 已过期 / 从未登录 —— 以侧边栏色条、头像圆环和标签的形式显示

### 登录
- 使用已保存的密码登录（仅在缺失/错误时才提示）
- 当仍有有效会话时显示 **重新登录**
- 在专用对话框中处理邮箱 Steam Guard 验证码
- 对选中项进行 **批量登录 / 更新信息**
- **在 Vouch 中批准登录** —— 在别处发起（未输入验证码）的登录，可在此处 **批准或拒绝**，就像手机 App 一样
- **二维码登录** —— 批准从剪贴板或全屏截图中读取的 Steam 登录二维码

### 设备
- **已登录设备**：列出某账号的活跃登录会话，并可 **登出** 其中任意一个
- 为单个账号或整批选中项 **登出全部设备**，之后可选择重新登录 Vouch

### 确认与报价
- 交易/市场 **确认**：批准 / 拒绝，可单个或批量操作
- 后台轮询每个已登录账号，在出现新确认或收到交易报价时发出 **系统通知**
  （已去重，可在设置中开关）
- **交易报价**：查看有效报价，打开详情对话框，其中包含物品 **图片**、
  双方的头像/昵称/SteamID，以及对方的等级、加入日期和好友关系 —— 然后 **接受 / 拒绝 / 取消**
- **通知** 列表，支持标为已读 / 全部标为已读

### 库存
- **库存查看器**：在应用内浏览某账号的物品 —— 只列出实际持有物品的游戏（就像 Steam 自己的下拉列表），带图标和数量
- **转移**：将某账号的可交易物品发送到已配置的交易链接 —— **全部物品** 或 **手动挑选** 的一部分（带图片），每款游戏一份报价；预设游戏（CS2、TF2、Dota 2、Rust、Steam）以及 **自定义 appid**；自动在移动端确认报价

### 身份验证器管理
- **添加身份验证器** 向导 —— **无需手机号** 也能使用（Steam 会通过邮件发送最终确认码）；
  撤销码会显示出来，并在单独的对话框中再次确认。添加完成后会自动登录该新账号
- 对选中项在 Steam 上 **停用身份验证器**，使用每个账号已保存的撤销码（批量），
  随后将它们从应用中移除

### CS2 配置同步
- 将某个账号的 **CS2 设置 / 按键绑定 / 视频配置** 复制到其他账号
  （全部、某个分组或手动挑选），并带有自动 **备份** 和一键 **还原**

### 应用
- 静态加密的 maFile（一个通行密钥，每次启动输入一次）
- 浅色 / 深色主题、托盘图标 + 最小化到托盘、启动即最小化
- 可配置的检查频率、剪贴板自动清除、可选的 Web API key
- 应用内针对 GitHub Releases 的 **更新检查**

---

## 下载

从 [Releases](https://github.com/Monodesu/Vouch/releases) 获取最新的
**`Vouch-vX.Y.Z-win-x64.exe`**。它是单个文件（依赖框架运行），需要安装
**[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)**。
直接运行即可 —— 无需安装程序。

## 从源码构建

需要 **.NET 10 SDK**。

```bash
git clone https://github.com/Monodesu/Vouch.git
cd Vouch

dotnet run --project Vouch.App        # run
dotnet test Vouch.Core.Tests          # tests
```

自行生成单文件 exe：

```bash
dotnet publish Vouch.App/Vouch.App.csproj -c Release -r win-x64 \
  --self-contained false -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish
# (delete publish/*.pdb; use --self-contained true for a no-runtime-needed build)
```

## 项目结构

| 路径 | 说明 |
|------|-----------|
| `Vouch.App/` | Avalonia 桌面应用 —— 视图、视图模型、对话框、资源 |
| `Vouch.Core/` | Steam 逻辑：TOTP、确认、报价、库存、绑定、存储 —— 不含 UI |
| `Vouch.Core.Tests/` | 针对纯逻辑/解析逻辑的 xUnit 测试 |

## 数据存放位置

全部数据都是**便携式**的，都放在 exe 旁边：

- `maFiles/` —— 你的账号（磁盘布局与原版 SDA 相同；启用加密后就地加密）
- `settings.json` —— 应用设置
- `cache/` —— 头像缓存

可用 `VOUCH_DATA_DIR` 环境变量覆盖存放位置。

## 更新

Vouch 会按需检查 GitHub Releases 是否有更新的标签（设置 → 检查更新），并链接到发布页面。
当推送 `v*` 标签时，CI 会自动生成发布版本。

## 许可证

采用 [GNU Affero General Public License v3.0](LICENSE) 授权。衍生作品 ——
**包括通过网络提供服务的作品** —— 也必须以 AGPL 开源发布。

Vouch 是 Jesse Cardone 所著、基于 MIT 许可的原版 **Steam Desktop Authenticator**
的衍生作品；该版权声明保留在 [NOTICE](NOTICE) 中。

## 致谢

- [Steam Desktop Authenticator](https://github.com/Jessecar96/SteamDesktopAuthenticator) —— 原版项目，提供了 maFile 格式以及它所开创的绑定/确认流程
- [Avalonia](https://avaloniaui.net/) —— 跨平台 UI 框架

## 依赖库

- [Avalonia](https://avaloniaui.net/) — 跨平台 UI 框架（Fluent 主题、Inter 字体）（MIT）
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MVVM 源生成器（MIT）
- [SteamKit2](https://github.com/SteamRE/SteamKit) — Steam 登录/认证握手；社区库（SteamRE），**非** Valve 官方（LGPL-2.1）
- [Konscious.Security.Cryptography](https://github.com/kmaragon/Konscious.Security.Cryptography) — Argon2id 密钥派生（maFile 加密）（MIT）
- [xUnit](https://xunit.net/) — 单元测试（Apache-2.0）

认证器核心（TOTP、maFile 格式、绑定、移动确认）是从 [SteamAuth](https://github.com/geel9/SteamAuth)（作者 Joshua Coffey / geel9，MIT）原生移植的 C# 实现。完整的第三方许可文本见 [NOTICE](NOTICE)。

---

> **免责声明：** Vouch 与 Valve 无任何关联，也未获得 Valve 的认可。Steam 是 Valve Corporation
> 的商标。使用风险自负。
