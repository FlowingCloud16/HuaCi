# 划词翻译 HuaCi

一个 Windows 平台的划词翻译小工具，使用 **DeepSeek** 作为翻译引擎。
在任意软件里用鼠标选中文字，旁边会出现一个「翻译」按钮，点一下就能在鼠标旁看到译文，结果流式逐字显示。
图片、视频、游戏等无法选中的文字，可以按 `Ctrl+Alt+Q` **截图翻译**。

> 技术栈：C# / .NET 8 / WPF，常驻系统托盘。系统要求：**Windows 10 1903 或更高版本**（含 Windows 11），x64。

## 功能

- **划词按钮（默认开启）**：拖动选择或双击选词后，鼠标旁出现一个小巧的「翻译」按钮，点击即翻译。
  - 点击别处、按任意键、滚动滚轮、切换窗口或 5 秒后自动消失（鼠标悬停在按钮上时不会消失）；
  - 尽量减少误触发：拖动距离过短、拖动标题栏 / 边框 / 滚动条、移动或缩放窗口、在桌面和任务栏上框选、在普通箭头光标下拖动（非文本区域）都不会出现按钮；
  - 点击后如果没取到文字（比如选中的是图片），按钮直接消失，不弹任何提示。
- **截图翻译（默认开启，`Ctrl+Alt+Q`）**：按快捷键（或托盘菜单"截图翻译"）后屏幕变暗，拖动框选一块区域，程序用 **Windows 自带的 OCR 在本机识别文字**，再交给 DeepSeek 翻译，结果显示在选区旁边。
  - 支持多显示器、不同缩放比例，可跨屏框选；`Esc` 或右键取消；
  - 识别结果在弹窗中默认展开且**可以直接修改**，改完点"重新翻译"或按 `Ctrl+Enter`；
  - OCR 语言自动选择（优先系统语言，必要时尝试多种已安装语言并挑最好的结果），也可在设置中指定；
  - DeepSeek 接口不支持图片输入，所以图片只在本机识别，不会上传，只发送识别出的文字。
  - 默认用 `Ctrl+Alt+Q` 而不是 `Ctrl+Alt+S`：后者是 Android Studio / IntelliJ 的"设置"快捷键；同时也避开了 QQ 截图 `Ctrl+Alt+A`、微信截图 `Alt+A`、系统截图 `Win+Shift+S`。（Visual Studio 调试时的"快速监视"也用 `Ctrl+Alt+Q`，冲突时可在设置中更换。）
- **划词快捷键（可选，默认关闭）**：在设置中开启后，选中文字按快捷键（默认 `Ctrl+Alt+D`，可修改）也能翻译。
- **自动识别语言，默认一律译成简体中文**（目标语言可改）。
  - 选中**中文单词**：给出拼音和汉语释义；
  - 选中**纯中文句子**：只提示"原文已是中文"，不发起请求、不产生费用；
  - **中外混排**：外文部分译成中文，中文保持不变；
  - 可选开启"中文原文改译为 English（或其他语言）"，恢复"中文 → 英文"的双向模式。
- **单词词典模式**：只选中一个外文单词时，给出音标和简明的词性释义。
- **流式输出**：一边生成一边显示。如果模型返回思考过程（`reasoning_content`），弹窗显示"思考中…"，思考内容不会混入译文。
- **翻译弹窗**：
  - 显示在鼠标附近，自动避开屏幕边缘，支持多显示器和不同缩放比例；
  - 可折叠显示原文；
  - 一键复制译文（弹窗内未选中文字时 `Ctrl+C` 也会复制全部译文）；
  - 📌 固定按钮：固定后点击外部不会关闭，下一次翻译直接在该窗口中显示；
  - 按 `Esc` 或点击弹窗外部关闭，标题栏可拖动。
- **原文可编辑**：弹窗里展开原文即可修改，点"重新翻译"（`Ctrl+Enter`）用修改后的文字再翻译一次。
- **托盘菜单**：设置 / 暂停 / 截图翻译 / 翻译剪贴板文字 / 退出；双击托盘图标打开设置。
- **模型选择**：设置里的模型下拉框会从接口（`GET {Base URL}/models`）获取可用模型，可点"刷新"重新获取，也可直接输入模型名称。填写 API Key 后会自动获取；没有选择模型时自动使用列表中的第一个。
- **思考模式开关**：DeepSeek 默认开启思考模式，本工具为了翻译速度默认关闭（发送 `thinking: {"type": "disabled"}`），需要时可在设置中开启。
- **安全**：API Key 使用 Windows DPAPI（当前用户）加密后保存，其他用户或其他电脑无法解密。
- **友好的错误提示**：没有 Key、Key 无效（401）、余额不足（402）、限流（429）、网络错误、超时等都会在弹窗中给出中文提示和"重试 / 打开设置"按钮。

## 获取 DeepSeek API Key

1. 打开 [DeepSeek 开放平台](https://platform.deepseek.com/) 并注册 / 登录。
2. 在 [API Keys](https://platform.deepseek.com/api_keys) 页面点击"创建 API Key"，复制生成的 `sk-...` 字符串（只显示一次）。
3. DeepSeek API 按用量计费，需要先在平台上充值少量余额，否则会提示"余额不足（402）"。
4. 运行本程序，在设置窗口粘贴 Key，模型列表会自动加载，点击"测试连接"确认正常后保存。

当前（2026 年 10 月）DeepSeek 接口返回的模型是 `deepseek-flash` 和 `deepseek-v4-pro`，翻译用 `deepseek-flash` 就够了，又快又便宜。模型名称以设置里获取到的列表为准。

## 使用

1. 下载并解压 `HuaCi-win-x64.zip`，双击 `HuaCi.exe`。
   - 这个版本（exe 约 25 MB）需要电脑装有 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)（选 "Desktop Runtime x64"）。缺少时系统会提示并引导下载。
   - 不想装运行时，可以用自带运行时的版本（分卷压缩为 `HuaCi-win-x64-selfcontained.7z.001`、`.002`……，解压后 exe 约 165 MB）：把所有分卷放在同一个文件夹里，用 [7-Zip](https://www.7-zip.org/) 打开 `.001` 解压即可。
2. 第一次运行会自动打开设置窗口，填入 API Key，确认模型后保存。
3. 程序常驻在系统托盘（任务栏右下角，图标是蓝底白色"译"字）。
4. 在任意程序里用鼠标拖动选中文字（或双击选词），点击旁边出现的「翻译」按钮。
5. 选不中的文字：按 `Ctrl+Alt+Q`，框选屏幕区域即可截图翻译。
6. 再次运行 `HuaCi.exe` 不会启动第二个实例，而是打开已运行实例的设置窗口。

配置文件与日志位置：`%AppData%\HuaCi\`（`settings.json`、`log.txt`）。

### 默认设置

| 项目 | 默认值 |
| --- | --- |
| Base URL | `https://api.deepseek.com` |
| 模型 | 不预设，从 `/models` 获取；未选择时自动用第一个 |
| 思考模式 | 关 |
| 译为 | 简体中文（所有语言） |
| 中文原文改译为 | 关（开启后默认 English） |
| 划词按钮 | 开 |
| 划词快捷键 | 关（开启后默认 `Ctrl+Alt+D`） |
| 截图翻译快捷键 | 开，`Ctrl+Alt+Q` |
| OCR 语言 | 自动 |
| 开机自启 | 关 |

Base URL 也可以填写其他 OpenAI 兼容服务的地址（程序会自动补上 `/chat/completions` 和 `/models`）。请求里带有 DeepSeek 专用的 `thinking` 参数，大多数兼容服务会忽略它；如果某个服务因此报 400 错误，欢迎反馈。

## 已知限制

- **取词原理**：点击按钮（或按快捷键）后，程序会先备份剪贴板，模拟按下 `Ctrl+C`，读取选中的文字后再还原剪贴板。因此：
  - 不支持 `Ctrl+C` 复制的程序（部分游戏、远程桌面、某些自绘界面、终端里 `Ctrl+C` 是中断）无法取词。复制失败时会尝试用 UI Automation 读取选中内容作为后备。
  - 剪贴板管理器可能会记录到这次临时复制。
  - 剪贴板中的特殊格式（如部分私有格式）可能无法完全还原，普通文字 / 图片 / 文件一般没有问题。
- **管理员权限窗口**：Windows 不允许普通权限程序监听或向管理员权限窗口发送按键，划词按钮在这类窗口里不会出现或取不到词。请同样以管理员身份运行本工具；或者先手动复制，再用托盘菜单的"翻译剪贴板文字"。
- **划词按钮的判断是启发式的**：
  - 少数程序在文字上显示的是普通箭头光标（不是 I 形光标），拖动选择时按钮不会出现，可以改用双击选词或开启快捷键；
  - 在某些非文字区域（如画布、表格拖拽）仍可能出现按钮，忽略即可，几秒后自动消失；
  - 命令行窗口（cmd / PowerShell / Windows Terminal）里不显示按钮，因为 `Ctrl+C` 在终端里可能是中断命令。
- **快捷键冲突**：如果快捷键已被其他程序占用，会弹出提示，请在设置中换一个组合。全局快捷键会"抢走"其他程序里的同一组合。
- **截图翻译 / OCR**：
  - 需要系统装有至少一种 OCR 语言。中文版 / 英文版 Windows 一般自带对应语言；如果提示"没有安装任何 OCR 语言"，请在"设置 → 时间和语言 → 语言和区域"中添加语言（勾选"光学字符识别"），或用管理员 PowerShell 运行 `Add-WindowsCapability -Online -Name "Language.OCR~~~zh-CN~0.0.1.0"`；
  - 识别准确度取决于 Windows OCR：小字号、艺术字、竖排文字、复杂背景可能识别不准，可在弹窗里手动修改后重新翻译；
  - 受 DRM 保护的视频、部分硬件加速画面截图可能是黑的；HDR 显示器上截图颜色可能偏淡，一般不影响识别；
  - 截图时会冻结当前画面，框选期间屏幕内容不会变化。

## 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。Windows、Linux、macOS 都可以编译（项目开启了 `EnableWindowsTargeting`），但只能在 Windows 上运行。
目标框架是 `net8.0-windows10.0.19041.0`（用于调用 Windows Runtime 的 OCR 接口），最低支持系统版本 `10.0.18362.0`（Windows 10 1903）。

```bash
# 调试构建
dotnet build

# 发布：依赖框架的单文件 exe（体积小，需要 .NET 8 桌面运行时）
dotnet publish src/HuaCi/HuaCi.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/win-x64

# 发布：自带运行时的单文件 exe（无需安装运行时，体积较大）
dotnet publish src/HuaCi/HuaCi.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/win-x64-sc

# 打包成每卷小于 25 MB 的 7z 分卷
7z a -t7z -mx=9 -md=256m -mf=BCJ2 -v23m HuaCi-win-x64-selfcontained.7z ./publish/win-x64-sc/HuaCi.exe README.md
```

## 项目结构

```
HuaCi.sln
src/HuaCi/
├── App.xaml(.cs)              # 程序入口：单实例、托盘、热键、整体装配
├── app.manifest               # Per-Monitor V2 DPI 感知
├── Assets/                    # 图标
├── Native/                    # Win32 互操作
│   ├── NativeMethods.cs       # P/Invoke 声明（SendInput、RegisterHotKey、钩子、显示器等）
│   ├── HotkeyManager.cs       # 全局热键
│   ├── HotkeyGesture.cs       # 快捷键解析 / 显示
│   ├── MouseHook.cs           # 低级鼠标 / 键盘钩子
│   └── ScreenHelper.cs        # 多显示器 / DPI 定位
├── Services/
│   ├── ScreenshotController.cs # 截图翻译流程
│   ├── ScreenshotSelector.cs  # 多显示器框选
│   ├── ScreenCapture.cs       # 截屏与裁剪
│   ├── OcrService.cs          # Windows.Media.Ocr 本地文字识别
│   ├── AppSettings.cs         # 设置模型
│   ├── SettingsService.cs     # 读写 %AppData%\HuaCi\settings.json
│   ├── SecretProtector.cs     # DPAPI 加密 API Key
│   ├── AutoStartService.cs    # 开机自启（HKCU\...\Run）
│   ├── SelectionCapture.cs    # 取词：剪贴板 + Ctrl+C，UIA 后备
│   ├── LanguageDetector.cs    # 中文 / 单词判断
│   ├── PromptBuilder.cs       # 翻译方向与系统提示词
│   ├── DeepSeekClient.cs      # DeepSeek 流式（SSE）、模型列表与错误处理
│   ├── TranslationController.cs # 取词 → 弹窗 → 翻译流程
│   ├── SelectIconWatcher.cs   # 划词检测与误判过滤
│   ├── TrayIconService.cs     # 托盘图标与菜单
│   └── Log.cs                 # 简单文件日志
└── Views/
    ├── TranslationPopup.xaml  # 翻译弹窗
    ├── SettingsWindow.xaml    # 设置窗口
    ├── SelectIconWindow.xaml  # 划词「翻译」按钮
    └── ScreenshotOverlay.xaml # 截图遮罩（每个显示器一个）
```
