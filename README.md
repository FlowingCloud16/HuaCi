# 划词翻译 HuaCi

一个 Windows 平台的划词翻译小工具，使用 **DeepSeek** 作为翻译引擎。
在任意软件里选中文字，按下快捷键（默认 `Ctrl+Alt+D`），鼠标旁就会弹出翻译结果，译文流式逐字显示。

> 技术栈：C# / .NET 8 / WPF，常驻系统托盘。

## 功能

- **全局快捷键翻译**：在任何程序里选中文字后按 `Ctrl+Alt+D`（可在设置中修改）。
- **划词图标（可选，默认关闭）**：开启后，鼠标拖动选择或双击选词后，旁边会出现一个小图标，点击即可翻译。
- **自动判断翻译方向**：原文主要是中文 → 译成英文；其他语言 → 译成简体中文。两个目标语言都可以在设置中修改（可直接输入任意语言名称）。
- **单词词典模式**：只选中一个单词时，给出音标和简明的词性释义。
- **流式输出**：DeepSeek 一边生成一边显示，不用等全部翻译完。
- **翻译弹窗**：
  - 显示在鼠标附近，自动避开屏幕边缘，支持多显示器和不同缩放比例；
  - 可折叠显示原文；
  - 一键复制译文（弹窗内未选中文字时 `Ctrl+C` 也会复制全部译文）；
  - 📌 固定按钮：固定后点击外部不会关闭，下一次翻译直接在该窗口中显示；
  - 按 `Esc` 或点击弹窗外部关闭，标题栏可拖动。
- **托盘菜单**：设置 / 暂停 / 翻译剪贴板文字 / 退出；双击托盘图标打开设置。
- **设置**：API Key、Base URL、模型、快捷键、目标语言、开机自启、划词图标开关，以及"测试"连接按钮。
- **安全**：API Key 使用 Windows DPAPI（当前用户）加密后保存，其他用户或其他电脑无法解密。
- **友好的错误提示**：没有 Key、Key 无效（401）、余额不足（402）、限流（429）、网络错误、超时等都会在弹窗中给出中文提示和"重试 / 打开设置"按钮。

## 获取 DeepSeek API Key

1. 打开 [DeepSeek 开放平台](https://platform.deepseek.com/) 并注册 / 登录。
2. 在 [API Keys](https://platform.deepseek.com/api_keys) 页面点击"创建 API Key"，复制生成的 `sk-...` 字符串（只显示一次）。
3. DeepSeek API 按用量计费，需要先在平台上充值少量余额，否则会提示"余额不足（402）"。
4. 运行本程序，在设置窗口粘贴 Key，点击"测试"确认连接正常后保存。

## 使用

1. 下载并解压 `HuaCi-win-x64.zip`，双击 `HuaCi.exe`。
   - 这个版本体积小（约 0.3 MB），但需要电脑装有 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)（选 "Desktop Runtime x64"）。缺少时系统会提示并引导下载。
   - 不想装运行时，可以用 `HuaCi-win-x64-selfcontained.zip`（自带运行时，约 66 MB）。
2. 第一次运行会自动打开设置窗口，填入 API Key 并保存。
3. 程序常驻在系统托盘（任务栏右下角，图标是蓝底白色"译"字）。
4. 在任意程序里选中文字，按 `Ctrl+Alt+D`。
5. 再次运行 `HuaCi.exe` 不会启动第二个实例，而是打开已运行实例的设置窗口。

配置文件与日志位置：`%AppData%\HuaCi\`（`settings.json`、`log.txt`）。

### 默认设置

| 项目 | 默认值 |
| --- | --- |
| Base URL | `https://api.deepseek.com` |
| 模型 | `deepseek-chat`（可改为 `deepseek-reasoner`，但速度较慢） |
| 快捷键 | `Ctrl+Alt+D` |
| 中文译为 | English |
| 其他语言译为 | 简体中文 |
| 开机自启 | 关 |
| 划词图标 | 关 |

Base URL 也可以填写其他 OpenAI 兼容服务的地址（程序会自动补上 `/chat/completions`）。

## 已知限制

- **取词原理**：程序会先备份剪贴板，模拟按下 `Ctrl+C`，读取选中的文字后再还原剪贴板。因此：
  - 不支持 `Ctrl+C` 复制的程序（部分游戏、远程桌面、某些自绘界面、终端里 `Ctrl+C` 是中断）无法取词。复制失败时会尝试用 UI Automation 读取选中内容作为后备。
  - 剪贴板管理器可能会记录到这次临时复制。
  - 剪贴板中的特殊格式（如部分私有格式）可能无法完全还原，普通文字 / 图片 / 文件一般没有问题。
- **管理员权限窗口**：Windows 不允许普通权限程序向管理员权限窗口发送按键。如需在以管理员身份运行的程序里取词，请同样以管理员身份运行本工具；或者先手动复制，再用托盘菜单的"翻译剪贴板文字"。
- **快捷键冲突**：如果快捷键已被其他程序占用，会弹出提示，请在设置中换一个组合。
- **划词图标**：基于鼠标拖动 / 双击判断，在非文本区域拖动（如拖动窗口、框选桌面图标）也可能出现图标，忽略即可，几秒后自动消失。

## 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。Windows、Linux、macOS 都可以编译（项目开启了 `EnableWindowsTargeting`），但只能在 Windows 上运行。

```bash
# 调试构建
dotnet build

# 发布：依赖框架的单文件 exe（体积小，需要 .NET 8 桌面运行时）
dotnet publish src/HuaCi/HuaCi.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/win-x64

# 发布：自带运行时的单文件 exe（无需安装运行时，体积较大）
dotnet publish src/HuaCi/HuaCi.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/win-x64-sc
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
│   ├── MouseHook.cs           # 低级鼠标钩子
│   └── ScreenHelper.cs        # 多显示器 / DPI 定位
├── Services/
│   ├── AppSettings.cs         # 设置模型
│   ├── SettingsService.cs     # 读写 %AppData%\HuaCi\settings.json
│   ├── SecretProtector.cs     # DPAPI 加密 API Key
│   ├── AutoStartService.cs    # 开机自启（HKCU\...\Run）
│   ├── SelectionCapture.cs    # 取词：剪贴板 + Ctrl+C，UIA 后备
│   ├── LanguageDetector.cs    # 中文 / 单词判断
│   ├── PromptBuilder.cs       # 系统提示词
│   ├── DeepSeekClient.cs      # DeepSeek 流式（SSE）客户端与错误处理
│   ├── TranslationController.cs # 取词 → 弹窗 → 翻译流程
│   ├── SelectIconWatcher.cs   # 划词图标（可选）
│   ├── TrayIconService.cs     # 托盘图标与菜单
│   └── Log.cs                 # 简单文件日志
└── Views/
    ├── TranslationPopup.xaml  # 翻译弹窗
    ├── SettingsWindow.xaml    # 设置窗口
    └── SelectIconWindow.xaml  # 划词小图标
```
