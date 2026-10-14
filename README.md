# ScreenTranslator · 屏幕实时翻译器

一个 Windows 桌面实时翻译工具：把指定窗口的画面实时 OCR 识别出来，经大模型翻译后，**以半透明覆盖层把译文直接盖回原文位置**，让原本看不懂的外语界面/文档/游戏瞬间变成中文。

适用于阅读外语文档、看外语视频/直播、玩外语游戏等场景。

## 功能特性

- **实时覆盖式翻译**：译文原地覆盖在原文上，而不是另开一个副窗口
- **两种翻译模式**
  - 云端大模型 API（任何 OpenAI 兼容接口，如 DeepSeek）
  - 本地大模型 Ollama（`qwen2.5:3b` 等），完全离线、无需 API Key
- **两种屏幕捕获方式**：Windows Graphics Capture（WGC）优先，失败时自动降级 GDI
- **中文 OCR 开箱可用**：内置 PP-OCRv5 中文识别模型（中英混排一个模型搞定），首次启动自动下载 + SHA256 校验
- **识别区域（ROI）**：可手动框选只识别屏幕某一块区域，减少无效 OCR
- **变化检测**：画面没变时用灰度指纹直接跳过 OCR，静止画面（看文档/看代码）几乎零开销
- **译文缓存**：相同原文不重复调用 API，省 token
- **覆盖层防截屏**：覆盖层不会出现在截图/录屏/远程画面中，避免"译文被自己抓回去再翻译"的死循环
- **译文样式可调**：字号、背景大小、内边距、圆角、不透明度，设置界面带**实时预览**
- **位置微调**：译文 X/Y 方向像素级偏移
- **全局热键**：默认 `Alt+T` 一键开关翻译
- **调试工具**：仅显示识别框 / 只显示原文 / 显示 OCR 检测框，便于单独校验识别效果
- **全局异常兜底**：崩溃信息写入同目录 `error.log`，不再"窗口一闪就没了"

## 运行环境

| 项 | 要求 |
|---|---|
| 操作系统 | Windows 10 1803+ / Windows 11（WGC 需 DWM 正常合成） |
| 运行时 | .NET 8（SDK 用于编译；仅运行可装 Desktop Runtime） |
| 权限 | **需管理员权限运行**（见 `app.manifest`）——以便捕获以管理员身份运行的窗口 |
| 网络 | 使用云端翻译或首次下载 OCR 模型时需要（本地模式 + 模型已就绪时可完全离线） |

## 编译与运行

```bash
git clone https://github.com/<your-name>/ScreenTranslator.git
cd ScreenTranslator

dotnet restore
dotnet build -c Release
dotnet run   -c Release
```

> 也可以直接用 Visual Studio 2022 打开 `ScreenTranslator.csproj`（选择 Release 配置）运行。

首次启动若检测到中文识别模型缺失，程序会在设置界面提示并引导下载（约 16MB），下载带 SHA256 校验与备用镜像。

## 使用步骤

1. 以管理员身份启动程序
2. 在「翻译模式」里选择 **云端大模型 API**（填 API 地址 / Key / 模型名）或 **本地大模型（Ollama）**
   - 本地模式需先安装并启动 [Ollama](https://ollama.com)，执行 `ollama pull qwen2.5:3b`
3. 点「刷新」→ 在「目标窗口」下拉里选中你要翻译的窗口
4. （可选）点「⬚ 选择识别区域」，在屏幕上框选只识别的那一块；不框则识别整个目标窗口
5. 点「▶ 开启翻译」（或按全局热键 `Alt+T`）开始，再按一次停止
6. 译文出现位置/大小不满意 → 点「⚙ 设置」调整样式与偏移（带实时预览）

## 配置说明

配置保存在**程序同目录**的 `config.json`（首次运行自动生成），主要字段：

| 字段 | 说明 |
|---|---|
| `TranslationMode` | `Cloud`（云端 API）/ `Local`（Ollama） |
| `ApiBaseUrl` / `ApiKey` / `Model` | 云端 OpenAI 兼容接口的地址、密钥、模型名 |
| `LocalApiUrl` / `LocalModel` / `LocalConcurrency` | 本地 Ollama 地址、模型名、并发数（1~8） |
| `CaptureIntervalMs` | 捕获间隔（毫秒），建议 800~3000 |
| `OcrScale` | OCR 识别前放大倍数，小字调大 |
| `OcrThreads` | OCR 推理线程数，`0` = 自动（推荐，实测并非越多越快） |
| `MinConfidence` | OCR 最低置信度（0~1） |
| `UseChangeDetection` / `ChangeMinCells` | 变化检测开关与灵敏度 |
| `UseRoi` / `RoiX/Y/W/H` | 识别区域（归一化坐标，随窗口缩放自动跟随） |
| `FontScale` / `BgScale` / `BgPadding` / `BgCornerRadius` / `BgOpacity` | 译文样式 |
| `OffsetX` / `OffsetY` / `OverlayOpacity` | 译文位置与不透明度 |
| `ToggleHotkey` | 全局热键，如 `Alt+T`、`Ctrl+Shift+S` |
| `OverlayExcludeFromCapture` | 覆盖层排除出屏幕捕获（推荐保持 `true`） |

> ⚠️ `config.json` 可能包含你的 API Key，**请勿提交到 Git 仓库**（已列入 `.gitignore`）。

## 项目结构

```
ScreenTranslator/
├── App.xaml(.cs)            程序入口 + 全局异常兜底
├── MainWindow.xaml(.cs)     主窗口：模式选择、窗口选择、开关、状态栏
├── app.manifest             清单：要求管理员权限、Per-Monitor V2 DPI
├── Models/                  数据模型
│   ├── AppConfig.cs         应用配置
│   ├── OcrBlock.cs          单个 OCR 结果块
│   ├── TranslationSegment.cs
│   ├── WindowInfo.cs
│   └── v5/                  OCR 模型目录（首次启动自动下载，不入库）
├── Services/
│   ├── WgcCaptureService.cs      WGC 捕获（含 SoftwareBitmap / 自建 D3D 两条路径）
│   ├── GdiCaptureService.cs      GDI 降级捕获（PrintWindow + BitBlt）
│   ├── OcrService.cs             OCR 推理
│   ├── OcrRuntime.cs             ONNX Runtime 环境与线程推荐
│   ├── OcrModelManager.cs        中文模型下载 + SHA256 校验
│   ├── TextSegmenter.cs          识别结果分行分段
│   ├── TextNormalizer.cs         文本归一化
│   ├── TranslationService.cs     大模型翻译（云端 / 本地）
│   ├── TranslationCache.cs       译文缓存
│   ├── FrameFingerprint.cs       画面指纹（变化检测）
│   ├── RoiGeometry.cs            识别区域几何换算
│   ├── OcrBoxFitter.cs           识别框大小自适应
│   ├── WindowEnumerator.cs       枚举可捕获窗口
│   ├── HotkeyManager.cs          全局热键
│   └── ConfigService.cs          配置读写
└── Windows/
    ├── OverlayWindow.xaml(.cs)      译文覆盖层（透明穿透）
    ├── SettingsWindow.xaml(.cs)     设置界面（含实时预览）
    └── RegionSelectWindow.xaml(.cs) 识别区域框选
```

## 技术栈

- **UI**：.NET 8 + WPF（`net8.0-windows10.0.26100.0`）
- **捕获**：Windows.Graphics.Capture（CsWinRT）+ SharpDX / GDI 回退
- **OCR**：RapidOcrNet（ONNX Runtime，PP-OCRv5 中文模型）
- **图像**：SkiaSharp
- **翻译**：任何 OpenAI 兼容 Chat Completions 接口（云端）或 Ollama（本地）
- **配置**：Newtonsoft.Json

## 已知问题

- WGC 在部分环境（虚拟显示适配器、远程桌面/远程控制）下可能触发 `DXGI_ERROR_DEVICE_HUNG`，此时程序会自动降级为 GDI 捕获，功能仍可用（GDI 要求目标窗口可见、未被完全遮挡，且防截屏可能失效）。
- 若目标窗口以管理员身份运行，本程序也必须以管理员身份运行，否则系统会拒绝捕获。
- 详见仓库内 `WGC报错排查交接文档.md`（WGC 故障排查过程记录）。

## 许可证

本项目暂未指定开源许可证。如需他人使用/贡献，请补充 LICENSE 文件。
