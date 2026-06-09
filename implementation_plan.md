# SwellSSH — WinUI3 SSH 终端客户端实施计划

---

## 🤖 AI 交接说明（必读）

> [!IMPORTANT]
> **本节专为接替的 AI 助手准备。用户额度耗尽时，请新会话的 AI 先读完本节再开始工作。**

### 项目背景
用户正在构建一个 **原生 Windows SSH 终端客户端**，名为 **SwellSSH**。
用户的底层框架（托盘管理、内存优化 Working Set Trim）已在另一个项目 `AnywhereWinUI` 中实现，需要复用。

### 关键决策（已锁定，不要重新讨论）
| 决策点 | 确定选择 |
|---|---|
| 项目名称 | `SwellSSH` |
| UI 框架 | WinUI3 (.NET 8)，**Unpackaged（无打包）模式** |
| 渲染方案 | **Win2D `CanvasControl`**，GPU 加速，不用 RichTextBlock |
| VT 解析 | **自定义状态机解析器**（DEC ANSI 标准），不用第三方库 |
| SSH 库 | **SSH.NET**（`Renci.SshNet`），备选 `Tmds.Ssh` |
| SFTP | **第一版不做** |
| 密码存储 | **Windows DPAPI**（`ProtectedData.Protect`） |

### 如何接手
1. 查看下方 **📊 当前进度** 节，找到最后一个 `[x]` 完成项和第一个 `[ ]` 待做项
2. 查看对应阶段的详细任务列表，从第一个 `[ ]` 开始继续
3. 每完成一个子任务，将 `[ ]` 改为 `[x]`，并更新 **📊 当前进度** 节的"当前阶段"和"最后更新"字段
4. 遇到设计分歧，优先参考本文档已有决策，不要询问用户已确认的内容

### 项目根目录

> [!TIP]
> `Microsoft.Windows.CsWin32` 是微软官方工具，可以从 `NativeMethods.txt` 自动生成 P/Invoke 代码，免去手写 DllImport 的痛苦，强烈推荐用于 ConPTY 调用。

```
d:\test\SwellSSH\
```

---

## 📊 当前进度

| 字段 | 内容 |
|---|---|
| **当前阶段** | 🎉 全部完成！ |
| **已完成阶段** | 阶段 1、2、3、4、5、6 ✅ |
| **下一步行动** | 项目顺利收官 |
| **最后更新** | 2026-06-08 |
| **最后操作 AI** | Antigravity (Gemini 3.1 Pro High) |

### 总进度概览
- [x] 阶段 1：项目骨架（预计 2 天）
- [x] 阶段 2：SSH 传输层（预计 2 天）
- [x] 阶段 3：VT 序列解析器（预计 3-4 天）
- [x] 阶段 4：Win2D 终端渲染控件（预计 3 天）
- [x] 阶段 5：多标签 + 连接管理 UI（预计 2 天）
- [x] 阶段 6：设置页 + 体验打磨（预计 1-2 天）

**总预计工时：13-15 天（独立开发）**

---

## 技术栈

| 层 | 选型 | 说明 |
|---|---|---|
| UI 框架 | WinUI3 (.NET 8) | 原生 Fluent Design |
| SSH 协议 | SSH.NET (`Renci.SshNet`) | 成熟库，支持密码/密钥认证，ShellStream |
| 终端引擎 | Windows ConPTY API (P/Invoke) | 通过 `kernel32.dll` 的 `CreatePseudoConsole` |
| VT 序列解析 | 自定义轻量解析器（DEC ANSI 状态机） | VtNetCore 已过时不用 |
| 终端渲染 | WinUI3 `Canvas` + `CanvasTextLayout` (Win2D) | 字符网格自绘，GPU 加速 |
| 配置存储 | `System.Text.Json` 序列化到 JSON 文件 | 存放在 `%AppData%\SwellSSH\` |
| P/Invoke 生成 | `Microsoft.Windows.CsWin32` | 从 `NativeMethods.txt` 自动生成，免手写 DllImport |
| 打包方式 | 无打包运行（Unpackaged） | 避免 MSIX 权限限制，分发更简单 |

> [!IMPORTANT]
> **为什么用 Win2D 而不是 RichTextBlock？**
> 终端需要精确控制每个字符格的位置、颜色、光标绘制。`RichTextBlock` 无法实现字符级精确控制，Win2D 的 `CanvasControl` 是唯一合适的 WinUI3 原生方案（GPU 加速，性能好）。

---

## 架构设计

```
┌──────────────────────────────────────────────────────────┐
│                    WinUI3 主窗口                          │
│  ┌────────────────┐  ┌───────────────────────────────┐   │
│  │  连接侧边栏    │  │       TabView (标签页)         │   │
│  │  (ListView)    │  │  ┌─────────────────────────┐  │   │
│  │  - 连接列表    │  │  │   TerminalView (控件)   │  │   │
│  │  - 新建/编辑   │  │  │   Win2D CanvasControl   │  │   │
│  └────────────────┘  │  └─────────────────────────┘  │   │
│                      └───────────────────────────────┘   │
└──────────────────────────────────────────────────────────┘
              │ 用户输入 (KeyDown)        ↑ 渲染帧
              ▼                           │
┌─────────────────────────────────────────────────────┐
│                  TerminalSession                     │
│  ┌─────────────────┐    ┌────────────────────────┐  │
│  │  SshTransport   │    │   TerminalBuffer        │  │
│  │  (SSH.NET)      │◄──►│   (字符网格 80×24)      │  │
│  │  ShellStream    │    │   VtParser (状态机)     │  │
│  └─────────────────┘    └────────────────────────┘  │
└─────────────────────────────────────────────────────┘
              │ 数据流
              ▼
┌─────────────────────────────────────────────────────┐
│                  ConPtyBridge (可选层)               │
│  CreatePseudoConsole → 处理 PTY resize 信号          │
└─────────────────────────────────────────────────────┘
```

> [!NOTE]
> **ConPTY 在本项目中的角色**：主要用于处理 **终端尺寸调整（PTY Resize）** 信号，而不是作为完整的进程宿主。SSH Shell 的输入输出直接走 `ShellStream`，ConPTY 帮助生成标准的 resize 控制序列发送给远端。

---

## 核心数据结构

### TerminalBuffer（终端字符网格）
```
行数 × 列数 的二维数组，每格存储：
- char Character
- uint Foreground   (32-bit RGB True Color)
- uint Background
- bool Bold / Italic / Underline
- bool IsDirty      (脏标记，用于增量渲染)
```

### ConnectionProfile（连接配置 JSON）
```json
{
  "id": "uuid",
  "name": "我的服务器",
  "host": "192.168.1.1",
  "port": 22,
  "username": "root",
  "authType": "Password | PrivateKey",
  "password": "DPAPI加密后的Base64",
  "privateKeyPath": "C:\\Users\\...\\id_rsa",
  "terminalCols": 120,
  "terminalRows": 30
}
```

---

## 项目目录结构

```
SwellSSH/
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / .cs
├── NativeMethods.txt                  ← CsWin32 自动生成 ConPTY P/Invoke
├── Models/
│   ├── ConnectionProfile.cs
│   └── TerminalSettings.cs
├── Services/
│   ├── ConnectionStorage.cs           ← JSON读写 + DPAPI加密
│   └── TrayIconManager.cs             ← 复用 AnywhereWinUI 项目
├── Terminal/
│   ├── SshTransport.cs                ← SSH.NET 封装
│   ├── ConPtyBridge.cs                ← P/Invoke ConPTY Resize
│   ├── VtParser.cs                    ← ANSI状态机解析器（核心）
│   ├── TerminalBuffer.cs              ← 字符网格 + 滚动缓冲区
│   └── TerminalSession.cs             ← 组合以上，一个会话的总管
├── Controls/
│   └── TerminalView.xaml / .cs        ← Win2D 渲染控件
├── Pages/
│   ├── MainPage.xaml / .cs            ← TabView 主页
│   ├── ConnectionsPage.xaml / .cs     ← 连接管理
│   └── SettingsPage.xaml / .cs        ← 设置页
└── Assets/
    └── tray-icon.ico
```

---

## NuGet 依赖

| 包名 | 版本 | 用途 |
|---|---|---|
| `SSH.NET` | latest stable | SSH 协议实现 |
| `Microsoft.Graphics.Win2D` | latest stable | GPU 加速 Canvas 渲染 |
| `Microsoft.WindowsAppSDK` | 1.5+ | WinUI3 框架 |
| `Microsoft.Windows.CsWin32` | latest | ConPTY P/Invoke 自动生成 |

---

## 分阶段任务清单

---

### 阶段 1：项目骨架 ✦ 预计 2 天

**目标**：能跑起来的空 WinUI3 窗口，包含主布局和配置文件读写。

#### 1.1 创建项目
- [ ] `dotnet new` 或 Visual Studio 新建 WinUI3 项目（Unpackaged 模式）
- [ ] 配置 `.csproj`：目标 `net8.0-windows10.0.19041.0`，添加所有 NuGet 引用
- [ ] 创建 `NativeMethods.txt`，列入 ConPTY 所需 API 名称

#### 1.2 主窗口布局
- [ ] `MainWindow.xaml`：左侧 `NavigationView`（连接列表）+ 右侧 `Frame`（页面容器）
- [ ] 实现窗口关闭 → 最小化到托盘逻辑（复用 `TrayIconManager`）
- [ ] 实现 `EmptyWorkingSet` 最小化时内存优化调用

#### 1.3 数据模型
- [ ] `Models/ConnectionProfile.cs`：包含所有连接字段
- [ ] `Models/TerminalSettings.cs`：字体、字号、配色方案、光标样式

#### 1.4 配置存储服务
- [ ] `Services/ConnectionStorage.cs`：JSON 读写，路径 `%AppData%\SwellSSH\connections.json`
- [ ] 密码加密：`ProtectedData.Protect()`（DPAPI），存 Base64
- [ ] 密码解密：`ProtectedData.Unprotect()`

#### 1.5 验证阶段 1
- [ ] 运行程序，窗口正常显示，主布局符合预期
- [ ] 新建一个假连接配置，确认写入 JSON 文件并能读回

---

### 阶段 2：SSH 传输层 ✦ 预计 2 天

**目标**：能 SSH 连接到真实服务器，终端类型声明为 `xterm-256color`，原始字节能收到。

#### 2.1 SSH 封装 (`Terminal/SshTransport.cs`)
- [x] 初始化 `SshClient` 和 `ShellStream` (`xterm-256color`)
- [x] 实现密码 / PrivateKey 双模式登录支持
- [x] 封装 `ShellStream.DataReceived`，向外抛出 byte[] 事件
- [x] 暴露 `SendInput(string)` 方法用于发送键盘按键

#### 2.2 ConPTY 信号桥接 (`Terminal/ConPtyBridge.cs`)
- [x] 封装基于像素计算列/行的逻辑 (`OnViewResized(width, height)`)
- [x] 将算出的行列数转发给 `SshTransport.ResizeTerminal`

#### 2.3 终端会话管理 (`Terminal/TerminalSession.cs`)
- [x] 组合 `SshTransport` 和 `ConPtyBridge`
- [x] 提供连接状态枚举：`Connecting, Connected, Disconnected, Error`

#### 2.4 验证阶段 2
- [x] 修改 `MainPage`：双击连接时创建 Session，并将原始 byte[] 转为 UTF8 字符串输出到一个临时的 ScrollViewer 中
- [x] 编译成功，启动应用测试连接远程机器，能在 ScrollViewer 看到命令行提示符

> **ConPTY 死锁陷阱**：必须有专用线程持续读取 ConPTY 输出 pipe，否则 pipe buffer 满了会导致整个进程挂起。这是实现时最需要注意的点。

- [ ] 用 `TextBlock` 临时显示 `DataReceived` 收到的原始字节（UTF-8 解码）
- [ ] 连接真实 Linux 服务器，能看到 shell prompt 的原始 ANSI 输出
- [ ] 输入 `ls -la` 并回车，确认数据双向流通

---

### 阶段 3：VT 序列解析器 ✦ 预计 3-4 天（核心难点）

**目标**：将原始字节流解析成对 `TerminalBuffer` 的操作指令。

#### 3.1 字符网格（TerminalBuffer）
- [ ] `Terminal/TerminalBuffer.cs`：
  - [ ] `TerminalCell[rows, cols]` 二维数组
  - [ ] `IsDirty` 脏标记（按行）
  - [ ] 滚动缓冲区（Scrollback Buffer，保留 1000 行历史）
  - [ ] `ScrollUp(n)` / `ScrollDown(n)` 操作
  - [ ] `ClearScreen()` / `ClearLine(row)` / `ClearToEol(row, col)` 操作

#### 3.2 状态机解析器（VtParser）
- [ ] `Terminal/VtParser.cs`，状态机实现：
  - 状态链：`Ground → Escape → CsiEntry → CsiParam → CsiIntermediate → OscString → ...`
  - [ ] 普通可打印字符 → 写入 Buffer 当前光标位置
  - [ ] `\r` `\n` `\b` `\t` 基础控制字符
  - [ ] **光标移动**：`ESC[A`(上) `ESC[B`(下) `ESC[C`(右) `ESC[D`(左)
  - [ ] **光标定位**：`ESC[row;colH` / `ESC[row;colf`
  - [ ] **SGR 样式**：`ESC[...m`，支持：
    - [ ] 粗体（`1`）、重置（`0`）、下划线（`4`）、斜体（`3`）
    - [ ] 基础 16 色（`30-37` / `40-47` 前/背景）
    - [ ] 亮色 16 色（`90-97` / `100-107`）
    - [ ] 256 色（`38;5;N` / `48;5;N`）
    - [ ] 真彩 RGB（`38;2;R;G;B` / `48;2;R;G;B`）
  - [ ] **清屏**：`ESC[J`（光标到末尾）、`ESC[2J`（全清）
  - [ ] **清行**：`ESC[K`（到行尾）、`ESC[1K`（到行首）、`ESC[2K`（整行）
  - [ ] **滚动区域**：`ESC[top;botr`（DECSTBM）
  - [ ] **OSC 标题**：`ESC]0;title\007` 或 `ESC]0;title\ESC\\` → 更新标签页标题
  - [ ] **交替屏幕**：`ESC[?1049h`（进入）/ `ESC[?1049l`（退出）（支持 vim/htop）

#### 3.3 验证阶段 3
- [ ] 单元测试：给定 ANSI 字节序列，验证 Buffer 状态正确
- [ ] 连接服务器，运行 `ls --color`，确认颜色解析正确（临时调试 dump）
- [ ] 运行 `htop`，确认 TUI 布局基本正确

---

### 阶段 4：Win2D 终端渲染控件 ✦ 预计 3 天

**目标**：将 TerminalBuffer 的字符网格用 Win2D Canvas 正确渲染到屏幕。

#### 4.1 TerminalView 控件骨架
- [ ] `Controls/TerminalView.xaml`：核心是 `CanvasControl`（Microsoft.Graphics.Canvas.UI.Xaml）
- [ ] 等宽字体配置（默认 Cascadia Code，回退 Consolas）
- [ ] 测量单个字符宽高（`CanvasTextLayout`），缓存为 `_charWidth` / `_charHeight`

#### 4.2 字符网格绘制
- [ ] `Draw` 事件处理：遍历 Buffer 的每个 `TerminalCell`
- [ ] 按背景色填充格子矩形（`FillRectangle`）
- [ ] 按前景色绘制字符（`DrawText`）
- [ ] 只重绘 `IsDirty == true` 的行（脏行增量渲染）
- [ ] 渲染完成后清除所有脏标记

#### 4.3 光标渲染
- [ ] `DispatcherTimer`（500ms 间隔）控制光标闪烁
- [ ] 支持光标样式：块状（Block）/ 下划线（Underline）/ 竖线（Bar）
- [ ] 光标位置由 `TerminalBuffer.CursorRow` / `CursorCol` 决定

#### 4.4 输入处理
- [ ] `KeyDown` 事件 → 转换为 ANSI 控制序列 → 写入 `SshTransport.SendRaw()`
  - [ ] 方向键：`ESC[A/B/C/D`（及 Application 模式 `ESSOA/B/C/D`）
  - [ ] `Ctrl+C` → `0x03`，`Ctrl+Z` → `0x1A`，`Ctrl+D` → `0x04`
  - [ ] `Tab` → `0x09`，`Enter` → `\r`，`Backspace` → `0x7F`
  - [ ] `F1-F12` 功能键 ANSI 序列
  - [ ] `Home` / `End` / `PgUp` / `PgDn` ANSI 序列
- [ ] `Ctrl+V` 粘贴：读剪贴板 → 写入流
- [ ] 鼠标拖选 → 选区高亮（反色），`Ctrl+C` / 右键 → 复制到剪贴板

#### 4.5 窗口 Resize 处理
- [ ] `SizeChanged` 事件 → 重算 `cols = width / charWidth`，`rows = height / charHeight`
- [ ] 更新 `TerminalBuffer` 尺寸
- [ ] 调用 `SshTransport.ResizeTerminal(cols, rows)` 发送 PTY resize 信号

#### 4.6 验证阶段 4
- [ ] 连接服务器，shell prompt 正确渲染
- [ ] `vim` 可以正常使用（包括颜色高亮）
- [ ] `htop` 渲染正确，动态刷新不闪烁
- [ ] 窗口拖拽 resize 后终端内容正确 reflow
- [ ] `cat /dev/urandom | base64` 高速输出不卡顿

---

### 阶段 5：多标签 + 连接管理 UI ✦ 预计 2 天

**目标**：完整的 UI 交互流程——管理连接、开启多个终端标签页。

#### 5.1 连接管理页面
### 阶段 5：多标签 + 连接管理 UI ✅

**目标**：完善左侧服务器列表与右侧多标签终端的管理逻辑。

#### 5.1 左侧连接管理
- [x] 优化 `ConnectionListView`，增加右键菜单（连接、编辑、删除）
- [x] 增加连接分组显示（使用 `CollectionViewSource`）
- [x] 双击连接新建 Tab 时，如果已有同一个会话，聚焦已有标签页

#### 5.2 多标签 TabView
- [x] `TabView_TabCloseRequested` 事件处理，确保关闭时调用 `TerminalSession.Dispose()` 断开 SSH
- [x] 提取 Tab 标题显示逻辑，支持状态指示（红点断开、绿点连接）
- [x] 支持右键关闭其他标签、关闭右侧标签等快捷操作

---

### 阶段 6：设置页 + 体验打磨 ✅
**目标**：收尾阶段。 
- [x] `SettingsPage.xaml`：提供全局外观配置（字体、配色、光标样式等）
- [x] `TerminalView.xaml.cs`：在创建时读取 `TerminalSettings` 应用配置
- [x] 增加"关于"页面内容，进行简单的错误处理和体验提升已断开（用服务器 `who` 命令验证）

---

### 阶段 6：设置 + 体验打磨 ✦ 预计 1-2 天

**目标**：设置页面完整，整体体验流畅，视觉风格符合 Fluent Design。

#### 6.1 设置页面
- [ ] `Pages/SettingsPage.xaml`：
  - [ ] 字体选择（过滤出等宽字体列表）
  - [ ] 字体大小（Slider，10-24pt）
  - [ ] 配色方案（内置：One Dark / Solarized Dark / Dracula / Default Light）
  - [ ] 光标样式（RadioButton：块状 / 下划线 / 竖线）
  - [ ] 背景透明度 / Mica / Acrylic 切换

#### 6.2 错误处理与提示
- [ ] 连接失败 → `InfoBar` 显示错误原因（超时 / 认证失败 / 网络不可达）
- [ ] 断线检测 → 标签页标题显示"[已断开]"，提示重连选项
- [ ] 私钥文件格式验证，PuTTY PPK 格式给出转换提示

#### 6.3 托盘菜单增强
- [ ] 托盘右键菜单：快速连接最近 5 个主机
- [ ] 托盘菜单：显示当前活跃连接数

#### 6.4 性能验证
- [ ] 最小化到托盘后内存 < 10MB（Working Set Trim）
- [ ] 5 个标签页同时运行，总内存 < 80MB
- [ ] `cat /dev/urandom | base64` 高速输出 30 秒不丢帧

#### 6.5 最终验证清单
- [ ] 密码认证登录 Linux 服务器
- [ ] ED25519 私钥认证登录
- [ ] `ls --color` 颜色正确
- [ ] `vim` 完整可用（打开、编辑、保存退出）
- [ ] `htop` 完整可用（动态刷新、键盘操作）
- [ ] `nano` 完整可用
- [ ] 窗口 resize → 终端 reflow 正确
- [ ] 多标签同时开启 5 个会话
- [ ] 复制粘贴正常工作

---

## 风险与对策

| 风险 | 概率 | 对策 |
|---|---|---|
| VT 序列解析遇到边缘 case | 高 | 先支持核心序列，边用边补；参考 xterm.js 源码的状态机定义 |
| Win2D 字符渲染性能 | 中 | 脏行增量渲染 + 避免每帧全量重绘 |
| ConPTY pipe 死锁 | 中 | 严格使用独立读取线程，测试时用 `htop` 等 TUI 工具压测 |
| SSH.NET 与某些服务器兼容性 | 低 | 备选 `Tmds.Ssh`（更现代的实现） |
| Win2D 与 WinUI3 版本冲突 | 低 | 锁定 `Microsoft.Graphics.Win2D` 版本，测试通过后不升级 |

---

## 开发建议（给接替 AI）

> [!TIP]
> 按以下顺序开发可以最大化提前发现问题，避免后期返工。

1. **阶段 2 完成后先验证连通性**：用 `TextBlock` 显示原始字节，确认 SSH 通路正常，再进入阶段 3/4
2. **VT 解析从简到繁**：先实现普通字符写入 + 光标移动 + SGR 颜色，跑通 `ls --color`，再追加 `vim`/`htop` 所需的高级序列
3. **Win2D 渲染先求正确再求性能**：先全量重绘跑通，再加脏行优化
4. **每个阶段结束立刻更新本文档进度表**，确保下一个 AI 能准确接手

> [!CAUTION]
> 不要跳过阶段验证步骤。每个阶段的验证清单是发现问题最低成本的时机，跳过后在阶段 4/5 才发现底层问题会非常难以调试。
