## 🙏 致谢

- 特别感谢 [ForZTN](https://sponsorship.forztn.com/github/yaog6700-bit/Swell-SSHb) 为本项目提供服务器资源支持。
- 感谢 [@david1025](https://github.com/david1025) 贡献了 v2.0 的导航栏 UI 重构（PR #3）。


# SwellSSH

**SwellSSH** 是一款为 Windows 11 打造的现代 SSH 客户端，使用 WinUI 3 原生框架与 Win2D GPU 加速渲染构建，提供流畅、美观的终端体验。

![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078d4?logo=windows)
![framework](https://img.shields.io/badge/framework-.NET%2010%20%7C%20WinUI%203-512bd4?logo=dotnet)
![license](https://img.shields.io/github/license/yaog6700-bit/Swell-SSH)
![release](https://img.shields.io/github/v/release/yaog6700-bit/Swell-SSH)

---

## ✨ 功能特性

### 终端体验
- 🖥️ **Win2D GPU 加速渲染** — 基于 Direct2D 的硬件加速 2D 渲染，流畅无卡顿
- ⚡ **完整 VT/ANSI 解析器** — 支持标准 ANSI 转义序列、256 色、加粗、光标控制
- 📜 **可配置滚回缓冲区** — 默认保留 1000 行历史记录，支持鼠标滚轮浏览
- 📋 **鼠标选择与剪贴板** — 拖选复制，右键粘贴

### SSH 连接
- 🔐 **SSH.NET 协议栈** — 支持密码认证与公钥认证
- 🔑 **DPAPI 加密存储** — 密码使用 Windows 数据保护 API 加密，绑定当前用户
- 🔄 **多标签管理** — 同时打开多个 SSH 会话，独立标签页切换
- 📁 **连接分组** — 支持为连接添加分组标签，方便管理大量服务器
- 🗂️ **导航面板连接列表** — 连接列表集成于左侧导航面板，全宽终端区域更宽敞
- 🔍 **连接选择器** — 新建标签时弹出带搜索过滤的连接选择弹窗

### 外观定制
- 🎨 **8 种内置配色方案**
  - One Dark · Dracula · Solarized Dark
  - Catppuccin Mocha · Tokyo Night · Nord · Gruvbox Dark
  - Default Light（浅色主题）
- 🪟 **Mica / Acrylic 窗口背景** — 随系统主题动态变化，终端背景透明融合
- 🖱️ **3 种光标样式** — 块状 / 下划线 / 竖线，支持光标闪烁开关
- 🔠 **字体与字号可配置** — 支持系统全部等宽字体，默认 Consolas 16pt
- 🌙 **即时主题切换** — 标签栏内一键切换配色，无需重启

### 其他
- 🔔 **自动更新** — 内置更新检查，支持一键下载并静默替换更新
- 🔲 **系统托盘** — 关闭窗口可最小化到托盘，保持后台连接
- 📦 **免安装** — 无需 MSIX 打包，解压即用，单文件 EXE

---

## 📸 截图
![nSkNJC6ZkdcYCgzyQrnJe6gpaj3oTJui.webp](https://cdn.nodeimage.com/i/nSkNJC6ZkdcYCgzyQrnJe6gpaj3oTJui.webp)
![vivu45BojbsuqAnUpMqJUg5WOhliq9VY.webp](https://cdn.nodeimage.com/i/vivu45BojbsuqAnUpMqJUg5WOhliq9VY.webp)
![QJah705yO1eE5LX8m8s0tVMORJlC3LJt.webp](https://cdn.nodeimage.com/i/QJah705yO1eE5LX8m8s0tVMORJlC3LJt.webp)
![4m32VEC6WgiIhkqiFQZ0D4H71Ph435Po.webp](https://cdn.nodeimage.com/i/4m32VEC6WgiIhkqiFQZ0D4H71Ph435Po.webp)
---

## 🚀 快速开始

### 系统要求

| 项目 | 要求 |
|---|---|
| 操作系统 | Windows 10 (1809+) / Windows 11 |
| 架构 | x64 / ARM64 |
| 运行时 | 已内置，无需单独安装 |

### 下载安装

1. 前往 [Releases](https://github.com/yaog6700-bit/Swell-SSH/releases/latest) 页面
2. 根据你的 CPU 架构下载对应的 `SwellSSH-win-x64.zip` 或 `SwellSSH-win-arm64.zip`
3. 解压到任意目录，双击 `SwellSSH.exe` 即可运行

> **提示**：首次运行时 Windows 可能会弹出 SmartScreen 提示，点击「更多信息」→「仍要运行」即可。

## ⌨️ 快捷键

| 快捷键 | 功能 |
|---|---|
| `Ctrl+C` | 发送中断信号（INT） |
| `Ctrl+D` | 发送 EOF |
| `Ctrl+L` | 清屏 |
| `Ctrl+Z` | 发送挂起信号（SUSP） |
| 右键单击 | 粘贴剪贴板 |
| 左键拖选 | 选中并复制文本 |
| 鼠标滚轮 | 浏览历史滚回缓冲 |

---

## 📁 数据存储位置

| 文件 | 路径 |
|---|---|
| 连接配置 | `%AppData%\SwellSSH\connections.json` |
| 外观设置 | `%AppData%\SwellSSH\settings.json` |
| 更新日志 | `%LocalAppData%\SwellSSH\Updates\updater.log` |

> 密码字段使用 Windows DPAPI 加密，仅当前用户可解密，配置文件可安全备份。

---

## 🏗️ 技术栈

| 组件 | 技术 |
|---|---|
| UI 框架 | [WinUI 3](https://learn.microsoft.com/windows/apps/winui/winui3/) (Windows App SDK 2.0) |
| 终端渲染 | [Win2D](https://github.com/microsoft/Win2D) (Direct2D GPU 加速) |
| SSH 协议 | [SSH.NET](https://github.com/sshnet/SSH.NET) |
| 窗口扩展 | [WinUIEx](https://github.com/dotMorten/WinUIEx) |
| 布局组件 | [CommunityToolkit.WinUI](https://github.com/CommunityToolkit/Windows) |
| 加密存储 | Windows DPAPI (`System.Security.Cryptography.ProtectedData`) |
| 构建目标 | .NET 10, win-x64 / win-arm64 |

---

## 🤝 贡献

欢迎提交 Issue 和 Pull Request！

1. Fork 本仓库
2. 创建你的特性分支：`git checkout -b feature/amazing-feature`
3. 提交改动：`git commit -m 'Add some amazing feature'`
4. 推送分支：`git push origin feature/amazing-feature`
5. 发起 Pull Request

---

## 📄 许可证

本项目基于 [MIT License](LICENSE) 开源。
