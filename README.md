# FileTransfer — 跳板机双跳 SFTP 文件中转工具

基于 **.NET 8 + Avalonia UI + SSH.NET** 构建的跨平台桌面工具（重点针对 **Windows 10 LTSC 1809 x64** 单文件免安装运行优化），用于在**跳板机禁止直接端口转发**、必须通过跳板机中转访问内网文件服务器的受限网络环境下，提供类似 **WinSCP / Explorer++** 的双栏可视化文件传输体验。

---

## 核心特性

### 1. 双跳中转与连接稳定性
- **两跳协同传输**：本地 ↔ 跳板机（SFTP） ↔ 内网文件服务器（基于跳板机交互式 `ShellStream` PTY 会话）。
- **多人共用安全隔离**：跳板机中转暂存文件采用 `ft_relay_<yyyyMMddHHmmss>_<taskId>_<fileName>` 时间戳命名，避免团队多人共用同一跳板机账号时相互覆盖；传输完成或取消自动清理中转文件。
- **保活与快速自动重连**：后台结合真实 SFTP `STAT` 与 PTY 心跳探测连接状态，断线后自动重连并重试当前操作。

### 2. WinSCP 风格双栏交互与效率体验
- **双栏联动浏览**：左侧为本地计算机，右侧为远端文件服务器；面板标题栏实时汇总**当前目录总项数 + 已选数量与大小**。
- **多种传输方式**：
  - 跨栏拖拽上传/下载（保持多选高亮状态）；
  - 从 Windows 资源管理器直接拖入右侧列表上传；
  - 快捷键 `Ctrl+U`（上传）、`Ctrl+D`（下载）及右键菜单操作。
- **拼音搜索与自然排序**：
  - 支持中文全拼、拼音首字母及英文实时过滤；
  - 支持中英文、数字及中文星期混排自然排序（如 `周一`、`周三`、`周五` 及数字序号正确递增）。
- **全套键盘与可选 Vim 模式**：
  - `Tab` / `Alt+1` / `Alt+2` 左右切栏；
  - 非 Vim 模式下支持 Explorer++ 风格 `A-Z / 0-9` 直接敲键按首字母/拼音循环定位；
  - 顶部「设置」可一键开启 **Vim 键盘模式**（`j/k` 上下、`h/l` 上级/进入、`g/G` 首尾行）并持久化保存。
- **细节体验**：
  - 统一 `16×16` SVG 矢量文件图标，高 DPI 下清晰锐利；
  - 支持 `System / Light / Dark` 三态主题切换；
  - 长文件名仅在出现 `...` 截断时弹出 `WrapWithOverflow` 整齐折行提示；
  - 传输成功/失败通知横幅支持定时自动消失（5s / 8s）。

### 3. 团队分发安全机制
- **覆盖前确认**：上传/下载目标目录存在同名文件时弹窗确认（支持「覆盖 / 跳过同名 / 取消」）。
- **远端高危目录硬拦截**：禁止删除或重命名 `/`, `/bin`, `/etc`, `/usr`, `/var`, `/data` 等系统/关键根目录。
- **凭据安全**：勾选「记住密码」时通过本机加密（DPAPI）保存；未勾选时断开连接或关闭窗口立即清空内存与旧凭据。

---

## 快捷键一览

| 分类 | 快捷键 | 说明 |
| :--- | :--- | :--- |
| **导航** | `Tab` / `Alt+1` / `Alt+2` | 切换左侧本地 / 右侧远端列表焦点 |
| | `Enter` | 进入目录；打开本地文件；单选或批量确认下载远端文件 |
| | `Backspace` / `Alt+↑` | 返回上级目录 |
| | `Ctrl + L` | 聚焦路径输入框（`Enter` 跳转，`Esc` 退回列表） |
| | `F5` | 刷新左右两侧文件列表 |
| **定位** | `A-Z` / `0-9` | 非 Vim 模式下直接敲键按首字母/拼音循环定位 |
| | `/` 或 `Ctrl + F` | 聚焦当前面板过滤框（`Enter` 进列表，`Esc` 清空） |
| | `j / k / h / l / g / G` | Vim 模式（设置中开启）：上下移动、上级/进入、首尾行 |
| **操作** | `Ctrl + U` / `Ctrl + D` | 上传左侧选中项 / 下载右侧选中项到左侧 |
| | `Ctrl + Shift + N` / `F7` | 新建文件夹/目录 |
| | `F2` | 重命名选中文件或目录 |
| | `Ctrl + Shift + C` | 复制选中文件或当前目录路径 |
| | `Delete` | 删除选中项（本地移至回收站；远端拦截高危目录） |
| **系统** | `Ctrl + ,` | 打开综合设置（主题切换、Vim 模式、记住密码） |
| | `F1` | 打开快捷键与操作说明 |

---

## 工程结构

```text
FileTransfer.sln
├── src/FileTransfer.App/
│   ├── Assets/
│   │   └── app.ico                              # 多分辨率透明背景应用图标
│   ├── Models/                                  # 配置、本地/远端文件条目、传输任务模型
│   ├── Security/
│   │   └── CredentialProtector.cs               # 本机 DPAPI 凭据加解密
│   ├── Services/
│   │   ├── ConfigService.cs                     # 配置持久化
│   │   ├── FileServerPtySession.cs              # 跳板机到文件服务器交互式 PTY SFTP 会话
│   │   ├── FileVectorIconService.cs             # 统一 16x16 SVG 矢量文件图标
│   │   ├── IRelayTransferService.cs             # 双跳传输服务接口
│   │   ├── PinyinSearchHelper.cs                # 中文拼音过滤与自然排序
│   │   ├── RelayTransferService.cs              # 双跳调度、保活心跳与断线重连
│   │   └── SftpListingParser.cs                 # ls -la 输出解析与参数安全转义
│   ├── ViewModels/
│   │   ├── MainWindowViewModel.cs               # 核心状态、配置、主题与日志
│   │   ├── MainWindowViewModel.Connection.cs    # 连接/断开与凭据清理
│   │   ├── MainWindowViewModel.LocalPane.cs     # 本地面板浏览、过滤与文件管理
│   │   ├── MainWindowViewModel.RemotePane.cs    # 远端面板浏览、过滤与文件管理
│   │   └── MainWindowViewModel.Transfer.cs      # 上传/下载编排、覆盖确认与取消
│   └── Views/
│       ├── MainWindow.axaml(.cs)                # 主窗口布局、快捷键路由与跨栏拖拽
│       ├── GridNavigationHelper.cs              # VisualTree 命中检测、Vim 与连续按键定位
│       └── WindowDialogHelper.cs                # 设置、帮助、重命名、覆盖/确认弹窗
└── tests/FileTransfer.Tests/
    └── CoreTests.cs                             # 核心解析、排序、安全与凭据单元测试
```

---

## 构建、测试与运行指南

### 一、基础环境准备（按操作系统）

> 统一要求：**.NET 8.0 SDK**（用于编译、测试与跨平台发布）。

#### 1. Windows（Windows 10 / 11）
- 安装 [.NET 8.0 SDK (x64)](https://dotnet.microsoft.com/download/dotnet/8.0)，或使用 `winget` 一键安装：
  ```powershell
  winget install Microsoft.DotNet.SDK.8
  ```
- 打开新的 PowerShell / 终端，验证版本：
  ```powershell
  dotnet --version
  ```

#### 2. macOS（Apple Silicon / Intel）
- 方式一（推荐 Homebrew）：
  ```bash
  brew install dotnet@8
  ```
  配置环境变量（写入 `~/.zshrc`）：
  ```bash
  export PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH"
  export DOTNET_ROOT="/opt/homebrew/opt/dotnet@8/libexec"
  ```
  *(Intel Mac 将 `/opt/homebrew` 替换为 `/usr/local`)*
- 方式二：从 [.NET 8.0 官方下载页](https://dotnet.microsoft.com/download/dotnet/8.0) 安装 `.pkg` 安装包。
- 验证版本：
  ```bash
  dotnet --version
  ```

#### 3. Linux（Ubuntu / Debian / RHEL 等桌面发行版）
- **Ubuntu / Debian** 安装 .NET 8 SDK：
  ```bash
  sudo apt-get update && sudo apt-get install -y dotnet-sdk-8.0
  ```
- 若在 Linux 上直接运行 GUI（Avalonia X11），建议确保桌面基础库已安装（一般桌面版已自带）：
  ```bash
  sudo apt-get install -y libx11-6 libice6 libsm6 libfontconfig1
  ```
- 验证版本：
  ```bash
  dotnet --version
  ```

---

### 二、还原依赖、运行测试与本地调试运行

以下命令在 **Windows / macOS / Linux** 仓库根目录下通用：

1. **还原 NuGet 依赖与编译**：
   ```bash
   dotnet restore FileTransfer.sln
   dotnet build FileTransfer.sln -c Release
   ```
2. **运行单元测试**：
   ```bash
   dotnet test tests/FileTransfer.Tests/FileTransfer.Tests.csproj --verbosity normal
   ```
3. **直接编译并启动桌面客户端**：
   ```bash
   dotnet run --project src/FileTransfer.App/FileTransfer.App.csproj
   ```

---

### 三、各平台打包发布

#### 1. 发布 Windows x64 独立单文件（Windows / macOS / Linux 均可交叉编译）
```bash
dotnet publish src/FileTransfer.App/FileTransfer.App.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=None \
  -p:DebugSymbols=false
```
- **产物路径**：`src/FileTransfer.App/bin/Release/net8.0/win-x64/publish/FileTransfer.exe`
- **说明**：已配置 `<SatelliteResourceLanguages>zh-Hans;en</SatelliteResourceLanguages>` 进行多语言包瘦身，体积约 **45 MB**，可在 **Windows 10 LTSC 1809 及以上**系统免安装 .NET 运行时直接运行。

#### 2. 发布 macOS / Linux 可执行程序
- **macOS (Apple Silicon)**：将 `-r win-x64` 替换为 `-r osx-arm64`（Intel Mac 使用 `-r osx-x64`），产物在 `src/FileTransfer.App/bin/Release/net8.0/osx-arm64/publish/FileTransfer`。
- **Linux (x64)**：将 `-r win-x64` 替换为 `-r linux-x64`，产物在 `src/FileTransfer.App/bin/Release/net8.0/linux-x64/publish/FileTransfer`（首次运行前执行 `chmod +x FileTransfer`）。
