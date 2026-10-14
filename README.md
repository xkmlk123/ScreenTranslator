# ScreenTranslator · 屏幕实时翻译器

一个 Windows 桌面实时翻译工具：把指定窗口的画面实时 OCR 识别出来，经大模型翻译后，**以半透明覆盖层把译文直接盖回原文位置**，让原本看不懂的外语界面 / 文档 / 游戏瞬间变成中文。

适用于阅读外语文档、看外语视频 / 直播、玩外语游戏等场景。

> 本文档是**项目唯一主文档**，同时承担「项目介绍」与「交接说明」两个职责：接手本项目的 AI / 开发者读完本文即可开工，无需重新调查。原 `WGC报错排查交接文档.md` 已并入本文第 11 章。

---

## 目录

1. [项目定位](#1-项目定位)
2. [功能特性](#2-功能特性)
3. [运行环境](#3-运行环境)
4. [编译与运行](#4-编译与运行)
5. [使用步骤](#5-使用步骤)
6. [配置说明](#6-配置说明)
7. [项目结构与文件职责](#7-项目结构与文件职责)
8. [技术栈与依赖](#8-技术栈与依赖)
9. [主链路与线程模型](#9-主链路与线程模型)
10. [关键机制详解](#10-关键机制详解)
11. [屏幕捕获：WGC 与 GDI 降级（含完整故障排查史）](#11-屏幕捕获wgc-与-gdi-降级含完整故障排查史)
12. [已知问题与坑](#12-已知问题与坑)
13. [关键常量速查（IID / 标志位）](#13-关键常量速查iid--标志位)
14. [可复用规律（CsWinRT / COM interop 踩坑总结）](#14-可复用规律cswinrt--com-interop-踩坑总结)
15. [回归禁区（改动时不要破坏的既有行为）](#15-回归禁区改动时不要破坏的既有行为)
16. [待优化项](#16-待优化项)
17. [代码位置速查表](#17-代码位置速查表)
18. [编译常见报错](#18-编译常见报错)
19. [许可证](#19-许可证)

---

## 1. 项目定位

| 项 | 值 |
|---|---|
| 项目名 | ScreenTranslator |
| 类型 | Windows 桌面应用（.NET 8 + WPF） |
| 根目录 | 仓库根目录（即 `ScreenTranslator.csproj` 所在目录） |
| 目标框架 | `net8.0-windows10.0.26100.0`（远超 WGC 所需的 19041） |
| 一句话 | 截屏 → OCR → 大模型翻译 → 译文原地盖回 |
| 核心难点 | 屏幕捕获（WGC / GDI）、译文覆盖层的「看得见但抓不到」、变化检测降负载 |

---

## 2. 功能特性

- **实时覆盖式翻译**：译文原地覆盖在原文上，而不是另开一个副窗口
- **两种翻译模式**
  - 云端大模型 API（任何 OpenAI 兼容接口，如 DeepSeek）
  - 本地大模型 Ollama（`qwen2.5:3b` 等），完全离线、无需 API Key
- **两种屏幕捕获方式**：Windows Graphics Capture（WGC）优先，失败时自动降级 GDI
- **中文 OCR 开箱可用**：内置 PP-OCRv5 中文识别模型（中英混排一个模型搞定），首次启动自动下载 + SHA256 校验
- **识别区域（ROI）**：可手动框选只识别屏幕某一块区域，减少无效 OCR
- **变化检测**：画面没变时用灰度指纹直接跳过 OCR，静止画面（看文档 / 看代码）几乎零开销
- **译文缓存**：相同原文不重复调用 API，省 token
- **覆盖层防截屏**：覆盖层不会出现在截图 / 录屏 / 远程画面中，避免「译文被自己抓回去再翻译」的死循环
- **译文样式可调**：字号、背景大小、内边距、圆角、不透明度，设置界面带**实时预览**
- **位置微调**：译文 X / Y 方向像素级偏移
- **全局热键**：默认 `Alt+T` 一键开关翻译
- **调试工具**：仅显示识别框 / 只显示原文 / 显示 OCR 检测框 / 强制 GDI 捕获 / **WGC 仅计数不取像素（二分诊断）** / 一键导出诊断串
- **全局异常兜底**：崩溃信息写入同目录 `error.log`，不再「窗口一闪就没了」

---

## 3. 运行环境

| 项 | 要求 |
|---|---|
| 操作系统 | Windows 10 1803+ / Windows 11（WGC 需 DWM 正常合成） |
| 运行时 | .NET 8（SDK 用于编译；仅运行可装 Desktop Runtime） |
| 权限 | **需管理员权限运行**（见 `app.manifest`）——以便捕获以管理员身份运行的窗口 |
| 网络 | 使用云端翻译或首次下载 OCR 模型时需要（本地模式 + 模型已就绪时可完全离线） |
| 硬件 | 有 GPU 时优先走 DirectML，失败回退 CPU |

---

## 4. 编译与运行

```bash
git clone https://github.com/xkmlk123/ScreenTranslator.git
cd ScreenTranslator

dotnet restore
dotnet build -c Release
dotnet run   -c Release
```

产物：`bin\Release\net8.0-windows10.0.26100.0\ScreenTranslator.exe`

> 也可以直接用 Visual Studio 2022 打开 `ScreenTranslator.csproj`（选择 Release 配置）运行。
> 编译前**必须先关闭正在运行的程序**，详见第 18 章。

首次启动若检测到中文识别模型缺失，程序会在设置界面提示并引导下载（约 16MB），下载带 SHA256 校验与备用镜像。

---

## 5. 使用步骤

1. 以管理员身份启动程序（每次启动弹一次 UAC 属预期行为）
2. 在「翻译模式」里选择 **云端大模型 API**（填 API 地址 / Key / 模型名）或 **本地大模型（Ollama）**
   - 本地模式需先安装并启动 [Ollama](https://ollama.com)，执行 `ollama pull qwen2.5:3b`
3. 点「刷新」→ 在「目标窗口」下拉里选中你要翻译的窗口
4. （可选）点「⬚ 选择识别区域」，在屏幕上框选只识别的那一块；不框则识别整个目标窗口
5. 点「▶ 开启翻译」（或按全局热键 `Alt+T`）开始，再按一次停止
6. 译文出现位置 / 大小不满意 → 点「⚙ 设置」调整样式与偏移（带实时预览）
7. 排查捕获问题时 → 看状态栏的「帧：到达 N / 转图 N」两个数字，或点「导出诊断」拿到完整诊断串

---

## 6. 配置说明

配置保存在**程序同目录**的 `config.json`（首次运行自动生成），字段全表：

| 字段 | 说明 |
|---|---|
| `TranslationMode` | `Cloud`（云端 API）/ `Local`（Ollama） |
| `ApiBaseUrl` / `ApiKey` / `Model` | 云端 OpenAI 兼容接口的地址、密钥、模型名 |
| `LocalApiUrl` / `LocalModel` / `LocalConcurrency` | 本地 Ollama 地址、模型名、并发数（1~8） |
| `CaptureIntervalMs` | 捕获间隔（毫秒），建议 800~3000 |
| `OcrScale` | OCR 识别前放大倍数（约 1.5~2 倍），小字调大 |
| `OcrThreads` | OCR 推理线程数，`0` = 自动（推荐，实测并非越多越快） |
| `MinConfidence` | OCR 最低置信度（0~1） |
| `UseChangeDetection` / `ChangeMinCells` | 变化检测开关与灵敏度（判据是**绝对变化格数**，不是比例） |
| `UseRoi` / `RoiX/Y/W/H` | 识别区域（归一化坐标，随窗口缩放自动跟随） |
| `FontScale` / `BgScale` / `BgPadding` / `BgCornerRadius` / `BgOpacity` | 译文样式 |
| `OffsetX` / `OffsetY` / `OverlayOpacity` | 译文位置与不透明度 |
| `ToggleHotkey` | 全局热键，如 `Alt+T`、`Ctrl+Shift+S` |
| `OverlayExcludeFromCapture` | 覆盖层排除出屏幕捕获（推荐保持 `true`） |

> 注意：`config.json` 可能包含你的 API Key，**请勿提交到 Git 仓库**（已列入 `.gitignore`）。

---

## 7. 项目结构与文件职责

```
ScreenTranslator/
├── App.xaml(.cs)             程序入口 + 全局异常兜底（崩溃写 error.log）
├── MainWindow.xaml(.cs)      主窗口：模式选择、窗口选择、开关、状态栏、单帧调度
├── app.manifest              清单：requireAdministrator、Per-Monitor V2 DPI
├── ScreenTranslator.csproj   项目与 NuGet 依赖声明
├── Models/
│   ├── AppConfig.cs          应用配置（字段见第 6 章）
│   ├── OcrBlock.cs           单个 OCR 结果块（文本 + 框 + 置信度）
│   ├── TranslationSegment.cs 待翻译段落（合并后的文本 + 各行的框）
│   ├── WindowInfo.cs         可捕获窗口信息（句柄 + 标题）
│   └── v5/                   OCR 模型目录（首次启动自动下载，不入库）
├── Services/
│   ├── WgcCaptureService.cs  WGC 捕获（SoftwareBitmap 路径 + 自建 D3D 路径 + 诊断计数）
│   ├── GdiCaptureService.cs  GDI 降级捕获（PrintWindow，失败回退 BitBlt + 临时隐藏覆盖层）
│   ├── OcrService.cs         OCR 推理封装（中文模型、上采样、置信度过滤、结果合并）
│   ├── OcrRuntime.cs         ONNX Runtime 环境与线程数推荐（DirectML 优先，CPU 兜底）
│   ├── OcrModelManager.cs    中文模型下载 + SHA256 校验 + 镜像回退
│   ├── TextSegmenter.cs      按版面把 OCR 碎片合并成完整句子 / 段落
│   ├── TextNormalizer.cs     文本归一化（去重与缓存键的基础）
│   ├── TranslationService.cs 大模型翻译（云端批量 / 本地单条并发）
│   ├── TranslationCache.cs   译文缓存
│   ├── FrameFingerprint.cs   灰度指纹（变化检测）
│   ├── RoiGeometry.cs        识别区域几何换算（归一化 ↔ 像素）
│   ├── OcrBoxFitter.cs       识别框大小自适应（字号 clamp、避免切字）
│   ├── WindowEnumerator.cs   枚举可捕获窗口
│   ├── HotkeyManager.cs      全局热键注册
│   └── ConfigService.cs      配置读写（config.json）
└── Windows/
    ├── OverlayWindow.xaml(.cs)      译文覆盖层（透明穿透 + 防截屏 + 30Hz 跟随）
    ├── SettingsWindow.xaml(.cs)     设置界面（含实时预览）
    └── RegionSelectWindow.xaml(.cs) 识别区域框选（Esc 取消）
```

---

## 8. 技术栈与依赖

- **UI**：.NET 8 + WPF（`net8.0-windows10.0.26100.0`）
- **捕获**：Windows.Graphics.Capture（CsWinRT 投影，`Microsoft.Windows.SDK.NET.dll` + `WinRT.Runtime.dll`）+ SharpDX.Direct3D11 / DXGI + GDI 回退
- **OCR**：RapidOcrNet（ONNX Runtime，PP-OCRv5 中文模型）
- **图像**：SkiaSharp
- **翻译**：任何 OpenAI 兼容 Chat Completions 接口（云端）或 Ollama（本地）
- **配置**：Newtonsoft.Json

第三方组件的许可证（均为宽松型，与本项目的 MIT 许可兼容）：

| 组件 | 版本 | 许可证 |
|---|---|---|
| RapidOcrNet | 4.2.0 | Apache-2.0 |
| SharpDX.Direct3D11 | 4.2.0 | MIT |
| SkiaSharp / SkiaSharp.NativeAssets.Win32 | 3.119.1 | MIT |
| Newtonsoft.Json | 13.0.3 | MIT |
| PP-OCRv5 中文识别模型 | — | Apache-2.0（来自 PaddleOCR） |

> 二次分发时请一并保留上述组件的版权与许可声明。

---

## 9. 主链路与线程模型

### 9.1 调用顺序

```
用户点「开启翻译」
  → MainWindow.StartTranslation()
      校验配置 → 准备 OCR 模型 → 选择捕获后端（WGC 优先，或强制 GDI）
      → 创建并显示 OverlayWindow
      → 启动定时器，周期调用 ProcessFrame()
  → ProcessFrame()（捕获帧 → 变化检测 → OCR → 分段 → 翻译 → 绘制）
      1. 捕获：WgcCaptureService.TryGetFrame() / GdiCaptureService.Capture()
      2. 变化检测：FrameFingerprint.Compute() 与上一帧指纹比对
         └─ 未变化 → 直接跳过本帧剩余流程（零 OCR 开销）
      3. ROI 裁剪：RoiGeometry.ToSkRect()
      4. OCR：OcrService.Recognize() → 返回 List<OcrBlock>
      5. 分行分段：TextSegmenter.BuildSegments() → List<TranslationSegment>
      6. 翻译：TranslationService.TranslateAsync()
         ├─ 先查 TranslationCache，命中直接复用
         └─ Cloud → TranslateBatchAsync()（批量协议，严格回填）
            Local → TranslateIndividuallyAsync()（有限并发 + 指数退避重试）
      7. 绘制：OverlayWindow 渲染译文与识别框
```

### 9.2 线程模型

| 环节 | 所在线程 |
|---|---|
| 定时器触发、UI 状态更新、覆盖层绘制 | WPF UI 线程 |
| WGC 回调 `OnFrameArrived` | MTA 线程池线程（`CreateFreeThreaded` 保证回调串行） |
| `CopyResource` + `Map` 像素回读 | 同上（`ImmediateContext` **非线程安全**，因此严禁改成 `async void` 并发调用） |
| SoftwareBitmap 异步转换 | 线程池（`await` 续体，用 `Interlocked` 重入守卫 + 5 秒卡死自愈） |
| OCR 推理 | 后台线程（`OcrService` 内部） |
| 翻译请求 | 异步（云端批量 / 本地并发受限） |
| 覆盖层渲染循环 | 30Hz 定时器，快照未变化时只做平移，不重建视觉树 |

---

## 10. 关键机制详解

### 10.1 屏幕捕获

两条路：

- **WGC（首选）**：`Direct3D11CaptureFramePool.CreateFreeThreaded(...)` + `GraphicsCaptureSession`。可捕获被遮挡窗口，且与防截屏配合良好。内部又有两条取像素路径：
  - `UseSoftwareBitmapPath = true`（**当前默认**）：`SoftwareBitmap.CreateCopyFromSurfaceAsync(surface)` → `CopyToBuffer` → `DataReader.ReadBytes` → `SKBitmap`。全程只用 CsWinRT 投影 API，刻意不做原生指针 interop。
  - `UseSoftwareBitmapPath = false`（保留回退）：自建 D3D11 设备 → `CopyResource` 到 staging 纹理（**缓存复用，不每帧新建**）→ `MapSubresource` 回读像素。
- **GDI（降级）**：`PrintWindow(PW_CLIENTONLY)` 优先；若全黑 / 全白则判定无效，回退 `BitBlt` 从**屏幕 DC** 抓图（此时临时隐藏覆盖层，避免把译文抓回去）。

### 10.2 OCR

- 模型：PP-OCRv5，含**检测模型**、**识别模型（中文）**、**方向分类模型**、**词典**；路径在 `Models/v5/`，缺失时由 `OcrModelManager` 下载（SHA256 校验 + 备用镜像）。
- 关键选项（`RapidOcrOptions`）：`LimitSideLen`、`DoAngle = false`、`UnClipRatio = 1.1`。
- 流程：空图检查 → 按 `OcrScale` 上采样（约 1.5~2 倍，小字识别率的关键）→ `Detect()` → 置信度过滤（`MinConfidence`）→ 坐标还原回原图尺度 → `MergeFragments()` 合并碎片。
- 线程数：`OcrThreads = 0` 为自动推荐（实测 ORT 线程数是 U 型曲线，**不是越多越快**，建议 clamp 4~8）。

### 10.3 变化检测

`FrameFingerprint` 计算 ROI 范围内的**灰度分块指纹**（约 256 字节），与上一帧比对，变化格数低于 `ChangeMinCells` 即判定「画面没变」，整个 OCR + 翻译链路直接跳过。静止画面（看文档）几乎零开销。

### 10.4 覆盖层

- **透明穿透**：铺满虚拟桌面、`Topmost`、`WS_EX_LAYERED`、不抢焦点、不进任务栏。
- **防截屏**：调用 `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`，失败则降级 `WDA_MONITOR`，并读回校验。校验时 `WDA_MONITOR(0x01)` 与 `0x11` 都算成功。
  - 注意：`WS_EX_TRANSPARENT` **不防截屏**，它只影响鼠标命中测试。
- **坐标换算**：OCR 框坐标（目标窗口客户区）→ 屏幕坐标。偏移基准用 `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` 而非 `GetWindowRect`，以排除 Win10+ 不可见边框的差异。
- **渲染循环**：30Hz 跟随目标窗口移动；快照未变时只做整体平移，不重建视觉树。

---

## 11. 屏幕捕获：WGC 与 GDI 降级（含完整故障排查史）

> 本章由原 `WGC报错排查交接文档.md` 合并而来，**并已修正部分已被实测推翻的旧结论**。带「已修正」标记的段落是纠正后的版本。

### 11.1 状态栏怎么读（这是整个排查的仪表盘）

`MainWindow.xaml.cs` 的 `SetStatus()` 常驻输出：

```
状态：正在翻译（WGC模式）            ← 或「GDI（WGC 失败）模式」
窗口：xxx
<本帧详情>
帧：到达 N / 转图 N｜【错误】<LastFrameError>
```

若降级 GDI，则改为：

```
抓帧：PrintWindow N / BitBlt N（防截屏已生效 | 防截屏未生效）
WGC 失败原因：[WGC 步骤N] xxx
```

**三个数的含义（诊断体系的核心）**：

| 组合 | 含义 |
|---|---|
| 到达 0 | 会话压根没投递帧（窗口最小化 / 不可见、受保护内容） |
| 到达 N / 转图 0 | 帧到了，但 Surface → Bitmap 转换没成功 |
| 到达 N / 转图 N | 正常 |

对应代码：`Services/WgcCaptureService.cs`（`FrameArrivedCount` / `FrameTakenCount` / `FrameConvertedCount` / `LastFrameError`），消费端 `MainWindow.xaml.cs` 的 `ProcessFrame()` 与 `SetStatus()`。

> 注意：勾选「WGC 仅计数不取像素（诊断）」时，每帧取到后**立刻 return、不做任何转换**，转图**必然恒为 0**。看到「转图 0」先确认这个复选框是不是被勾上了。

### 11.2 故障链条总览（按时间顺序，每步都有报错原文）

| # | 症状 | 根因 | 状态 |
|---|---|---|---|
| 1 | 译文越用越大（阶梯式） | GDI 降级 → BitBlt 从**屏幕 DC** 抓图 → 把自己刚画的译文抓回去再翻译 → OCR 框变大 → 字号 ∝ 框高 | 已修复（防截屏 + 变化检测 + 去重） |
| 2 | WGC 启动即失败 | **UIPI 权限不对等**：程序 `asInvoker` vs 目标窗口管理员 | 已修复（manifest 改 `requireAdministrator`） |
| 3 | `[WGC 步骤6] Specified cast is not valid` | `GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>()` 在新版 CsWinRT 做的是**普通 cast 而非 QI**；且 `typeof(GraphicsCaptureItem).GUID` 是 **CLSID 不是接口 IID** | 已修复（`RoGetActivationFactory` + `AsInterface`） |
| 4 | `[WGC 步骤7] Failed to create a CCW ... System.__ComObject` | 步骤 5 用 `Marshal.GetObjectForIUnknown` 造的是**经典 COM RCW**，CsWinRT 的 `Create()` 要的是投影对象 | 已修复（`MarshalInterface<T>.FromAbi`） |
| 5 | `QueryInterface(IDXGISurface) 失败，HRESULT=0x80004002` | WinRT `IDirect3DSurface` 是**包装器**，本身不实现 `IDXGISurface` | 已修复（改走 `IDirect3DDxgiInterfaceAccess`） |
| 6 | `QueryInterface(IDirect3DDxgiInterfaceAccess) 失败，HRESULT=0x80004002`（7000+ 帧全灭） | **已修正**：旧文档归因于「手工指针身份问题」，实为**三处缺陷叠加**——① `IDirect3DDxgiInterfaceAccess` 的 IID 被误写成 `A37624AB-...`（那是 `IDirect3DDevice` 的 IID），错值必然 `E_NOINTERFACE`，是 5 条路径全灭的直接原因；② `IInspectable` IID 写成伪造值 `ECD4B446-...`，使诊断代码自身出错、把排查方向误导向「指针身份不可靠」；③ 帧池用 `Create` 而非 `CreateFreeThreaded`，跨 apartment 拿到代理对象，本地 interop 接口无 proxy/stub，QI 同样失败 | 已修复（IID 改 `A9B3D012-...`；IInspectable 改 `AF86E2E0-...`；帧池改 `CreateFreeThreaded`） |
| 7 | 帧到达但取像素时 `0x887A0005` `DXGI_ERROR_DEVICE_REMOVED` / `0x887A0006` `DXGI_ERROR_DEVICE_HUNG`（TDR），第 1~2 帧必崩 | **已修正**：旧文档归因于「跨适配器拷贝」，**已被二分实验证伪**。真正触发点是**自建 D3D context 路径的像素回读动作**（`CopyResource` + `MapSubresource`） | 已规避（改走 SoftwareBitmap 路径后 `0x887A0006` 消失），见 11.3 / 11.4 |

### 11.3 二分实验（决定性结论，请勿重复走弯路）

「磨损式排查」全部无效（详见 11.5），最终用**二分法**定位：

| 实验 | 操作 | 结果 | 结论 |
|---|---|---|---|
| A | 在 `OnFrameArrived` 加**时间戳丢帧节流**（每秒最多 5 帧） | 现象**完全不变**（到达=2 取帧=1 转图=1 即崩） | 「抓帧循环没节流导致 GPU 打满」**被证伪** |
| B | staging 纹理**缓存复用**（不再每帧 `new Texture2D`） | 现象无改善 | 「帧池 / 纹理每帧重建」**被证伪** |
| C | 勾选「**WGC 仅计数不取像素（诊断）**」= 每帧只计数、完全不做 `CopyResource+Map` | **到达 154 帧、转图 0、无任何帧错误** | **触发点就是像素回读动作本身**，与帧率、资源重建、COM 接口都无关 |

**由此得出的关键判断**：`0x887A0006`（DEVICE_HUNG，TDR）是**自建 D3D11 设备的回读操作**触发的，而不是帧率或资源创建频率导致的。

### 11.4 两种修复方案及取舍

| 方案 | 内容 | 结果 |
|---|---|---|
| **方案 A（已落地，构成稳定基线）** | 删除 `texture.Device` 诊断块（它引入了 `0xc0000005` 原生访问违规闪退）+ Map 回退 `MapFlags.None` | 不再闪退；WGC 报错时自动降级 GDI，**基线稳定可用** |
| **方案 C（已落地，当前默认）** | 彻底改走 WinRT 官方 `SoftwareBitmap` 路径，绕开自建 D3D context | **`0x887A0006` 消失**，帧错误历史为空，WGC 可稳定存活 150~216 帧 |

方案 C 的附加防护（已实现）：

- `Interlocked` 重入守卫：同一时刻只允许一个 SoftwareBitmap 转换在跑；
- 5 秒卡死自愈：转换超时未完成则复位守卫并记录错误（避免守卫永久卡死）；
- SB 诊断日志：只记前 3 次 `SB 已发起转换` / `SB 转换完成` / `SB 已转为 SKBitmap`。

> 关于 `0xc0000005` 的教训：**托管 `try/catch` 抓不到原生访问违规**（事件日志显示 `coreclr.dll` + `0xc0000005` + `.NET Runtime id=1023 exit code 0x80131506`）。在 CsWinRT interop 里「手工挖指针」属于高危操作。

### 11.5 已排除项（**别再走这些弯路**）

| 已排除 | 依据 |
|---|---|
| 权限不足 | 权限不足在**创建捕获项（步骤 6）**就抛，走不到收数千帧 |
| 目标窗口最小化 / 不可见 | 帧一直在到达（数千帧） |
| 硬件加速 / 双 GPU / HAGS | 只影响「帧内容」，影响不了 COM 接口查询与回读崩溃 |
| 注册表 DCI 超时 | 产生黑帧，不会产生 `E_NOINTERFACE` / `DEVICE_HUNG` 异常 |
| 抓帧循环没节流 | 见 11.3 实验 A，加了节流现象不变 |
| 帧池 / 纹理每帧重建 | 见 11.3 实验 B，复用后现象不变 |
| 字号 / 渲染层累积放大 | 已逐行确认 `Draw` / `DrawOcrBoxes` 对 blocks **零写回**，`OcrBoxFitter.ApplyTo` 幂等 |
| 翻译去重缺失 | 已有 `FrameFingerprint` 变化检测 + 文本标准化去重 + 翻译缓存 |
| 跨适配器拷贝 | 见 11.2 第 7 行「已修正」：**旧结论，已被二分实验证伪** |

> 注：网络上流传的「通用 WGC 排查清单」（改注册表、关硬件加速、重启等）针对的是「无报错 + 纯黑屏」场景，**对本问题无效**——本问题有明确报错与证据链。

### 11.6 当前状态：已解决（2026-10-14）

**WGC 路径已完整跑通**：状态栏显示 `WGC模式`，「帧：到达 N / 转图 N」两个数字**同步上涨**，不再触发降级；GDI 退居备用模式。

拿下这一串问题靠的是下列改动的**叠加生效**，缺任何一条都走不通：

| # | 改动 | 不做的后果 |
|---|---|---|
| 1 | `IDirect3DDxgiInterfaceAccess` IID 改为 `A9B3D012-...` | 5 条取指针路径全部 `E_NOINTERFACE` |
| 2 | `IInspectable` IID 改为 `AF86E2E0-...`（原为伪造值 `ECD4B446-...`） | 诊断自身的身份体检永远失败，把排查方向误导到「指针身份不可靠」 |
| 3 | 帧池改用 `CreateFreeThreaded` | 回调落到别的 apartment，拿到的是代理对象；本地 interop 接口没有 proxy/stub，QI 同样失败 |
| 4 | 改走 `SoftwareBitmap` 路径（`UseSoftwareBitmapPath = true`） | 自建 D3D context 的像素回读动作会触发 `0x887A0006`（DEVICE_HUNG） |
| 5 | 取消「WGC 仅计数不取像素（诊断）」的勾选 | 该开关让 `OnFrameArrived` 每帧立即 `return`，**转图恒为 0** |

> **第 5 条是最容易把自己坑进去的一条**：该复选框默认**不勾选**（`MainWindow.xaml` 的 `SkipReadbackCheck` 没有设 `IsChecked`），但做 11.3 的二分实验时一定会勾上它。实验做完忘了取消，就会看到「WGC 不报错、但转图恒为 0」这种假故障——这恰好是当时最后一份日志的形态。**排查 WGC 前，先确认这个框没被勾上。**

**历史记录**（问题未解决时的推断链，保留备查）：当时最后的日志特征是「转图 = 0」+「帧错误历史为空」+「无任何报错」。逐一排除所有会写 `LastFrameError` 的代码路径（转换异常、返回 null、守卫 5 秒自愈、设备移除）后，指向了「诊断复选框被勾着」这一解释，与最终现象吻合。若将来再遇到「转图 0」，11.8 的决策树仍然适用；另有一种独立成因：能看到 `SB 已发起转换` 但始终没有 `SB 转换完成`，说明 `SoftwareBitmap.CreateCopyFromSurfaceAsync(surface)` 永久挂起，根因在环境层（见 11.7）。

### 11.7 环境层嫌疑（重点怀疑对象）

本机环境的异常证据：

- `GameViewer Virtual Display Adapter`（虚拟显示适配器）+ 系统事件 4107 `SetDisplayConfig + SDC_FORCE_MODE_ENUMERATION`；
- `nvlddmkm` 事件 153 与 `Display` 事件 4101（GPU 侧的 TDR 痕迹）；
- `BasicRender`（WARP 软件设备）反复 4101；
- 本机存在**多块 DXGI 适配器**。

**远程控制 / 远程桌面会显著加剧（甚至直接造成）这类问题**——它可能引入虚拟显示适配器、改变 DWM 合成路径与 GPU 调度。若问题持续，建议：

1. 在**物理机前本地操作**（不通过远程控制）复现验证；
2. 临时**禁用 `GameViewer Virtual Display Adapter`** 后重测；
3. 用「仅计数不取像素」开关做二分，确认崩溃是发生在捕获侧还是回读侧。

### 11.8 决策树：拿到诊断串怎么走

```
运行后看状态栏
├─ 转图 N 开始上涨 ─────────────► WGC 通了。做 11.9 的收尾验证
│
└─ 转图 0
   ├─ 先确认「WGC 仅计数不取像素」复选框是否勾着 ──► 勾着就先取消重测
   ├─ 帧错误历史非空，且含 0x887A0005 / 0x887A0006
   │     ──► 回读侧触发 TDR，走 SoftwareBitmap 路径（UseSoftwareBitmapPath=true）
   ├─ 帧错误历史为空，且无「SB 已发起转换」日志
   │     ──► 回调根本没进来或提前 return，检查 SkipPixelReadback / 节流逻辑
   ├─ 有「SB 已发起转换」但无「SB 转换完成」
   │     ──► CreateCopyFromSurfaceAsync 永久挂起 ⇒ 环境层问题（见 11.7）
   └─ 全部失败 ──► 点「导出诊断」把完整诊断串交给下一个环节
```

### 11.9 修好后的收尾验证清单

1. 状态栏显示 WGC 模式，「帧：到达 N / 转图 N」两个数字**同步上涨**
2. 译文正常显示，**字号不再阶梯式变大**（原 bug 症状消失）
3. 按 `PrtScn` 截图 → 粘贴到画图：**看不到**译文与白底块（防截屏生效）
4. 换管理员权限的目标窗口、多显示器不同 DPI 各验证一次
5. `dotnet build -c Release` 保证 **0 warning 0 error**

---

## 12. 已知问题与坑

- **GDI 降级是安全网，不是主路径**：WGC 已跑通（见 11.6），但在**虚拟显示适配器、远程桌面 / 远程控制**等环境下仍可能触发 `DXGI_ERROR_DEVICE_HUNG`，此时程序自动降级 GDI，功能不受影响。GDI 要求目标窗口可见、未被完全遮挡，且**防截屏可能失效**（会启用临时隐藏覆盖层兜底，代价是抓帧瞬间闪一下）。详见第 11 章。
- **目标窗口以管理员身份运行**时，本程序也必须以管理员身份运行，否则系统会拒绝捕获。
- **导出诊断会抹掉实时日志**：`CopyDiag_Click` 每次覆盖写 `诊断日志.txt`，与 `AppendDiagLog` 写同一文件冲突，导出瞬间会把实时日志清空，导致看不到关键证据。详见第 16 章。
- **原生访问违规闪退无法被托管捕获**：在 CsWinRT interop 中手工取指针可能导致 `0xc0000005`，`try/catch` 抓不到，表现为「点开启翻译直接闪退」。
- **`ImmediateContext` 非线程安全**：不要为了「不阻塞回调」而把帧处理改成并发执行。

---

## 13. 关键常量速查（IID / 标志位）

| 名称 | 值 | 备注 |
|---|---|---|
| `IDXGISurface` IID | `CAFCB56C-6AC3-4889-BF47-9E23BBD260EC` | 反射比对 `typeof(SharpDX.DXGI.Surface).GUID` 一致 |
| `IDirect3DDxgiInterfaceAccess` IID | `A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1` | **正确值**（与代码一致）。四源交叉验证：FFmpeg `vsrc_gfxcapture_winrt.hpp`、ScreenRecorderLib、robmikh 官方 C# sample `Direct3D11Helpers.cs`、kennykerr gist |
| `IDirect3DDevice` IID | `A37624AB-8D5F-4650-9D3E-9EAE3D9BC670` | **极易混淆，勿用错**：这是 `IDirect3DDevice` 的 IID，**不是** `IDirect3DDxgiInterfaceAccess`。它曾因误读报错信息（那次报错的对象是 device）被错当成后者写进代码，导致 5 条取指针路径全部 `E_NOINTERFACE`（见 11.2 第 6 行） |
| `IInspectable` IID | `AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90` | **已修正**：旧文档写的 `ECD4B446-17B9-4E77-B0D9-B9BD4B1C2D90` 是错的，直接导致旧诊断逻辑失效、误导排查方向 |
| `IGraphicsCaptureItemInterop` IID | `3628E81B-3CAC-4C60-B7F4-23CE0E0C3356` | 官方头文件 |
| `IGraphicsCaptureItem` 接口 IID | `79C3F95B-31F7-4EC2-A464-632EF5D30760` | **不是** CLSID（`e79f1dd1-...`） |
| `IActivationFactory` IID | `00000035-0000-0000-C000-000000000046` | 标准 |
| `WDA_EXCLUDEFROMCAPTURE` | `0x00000011` | 读回校验时 `WDA_MONITOR(0x01)` 也算成功 |
| `DXGI_ERROR_DEVICE_REMOVED` | `0x887A0005` | 设备被移除 |
| `DXGI_ERROR_DEVICE_HUNG` | `0x887A0006` | TDR 超时 |
| `DXGI_ERROR_WAS_STILL_DRAWING` | `0x887A000A` | 非阻塞 Map 可能返回 |
| `E_NOINTERFACE` | `0x80004002` | QI 失败 |

---

## 14. 可复用规律（CsWinRT / COM interop 踩坑总结）

> 改代码前务必读本章。本文件的 interop 部分先后踩坑 5 次，全属下列类型。

1. **CsWinRT 投影对象不要用经典 COM interop 取指针。**
   `Marshal.GetIUnknownForObject(投影对象)` 返回的是 **CCW 身份指针**，不是底层 WinRT 对象的 IUnknown。
   官方 ABI 出口是 `MarshalInspectable<T>` / `MarshalInterface<T>.FromManaged`。
2. **`typeof(运行时类).GUID` 是 CLSID，不是接口 IID。** 传 `ref Guid iid` 必须传接口 IID。
3. **SharpDX 等 wrapper 的 `(IntPtr)` 构造函数不做 QI**，传错接口指针会静默调错方法（比崩更难查）。
4. **运行时类的静态 `As<T>()` 对 `[ComImport]` 接口会抛 `InvalidCastException`**（做的是托管 cast，不是 QI）。
5. **`catch { }` 是 bug 放大器**：异步回调里空 catch + `return null`，会把丰富异常压缩成无法诊断的上层症状。
   凡吞异常处必须留 `LastError` 属性给上层读。
6. **异步管道必须有分段进度计数**（到达 / 取帧 / 转图），否则断在哪一环无法二分定位。
7. **用「QI IInspectable」给任何 WinRT 指针做身份体检**：真对象必过，假货立现。（前提是 IID 正确，见第 13 章）
8. **`SetWindowDisplayAffinity` 才防截屏**，`WS_EX_TRANSPARENT` 只影响鼠标命中测试。
9. **托管 `try/catch` 抓不到原生 AV（`0xc0000005`）**，闪退类问题要去事件日志找 `coreclr.dll` + `.NET Runtime id=1023`。
10. **节流要用「时间戳丢帧」而非 `await Task.Delay`**；回调里不要引入新的并发（`ImmediateContext` 非线程安全）。

---

## 15. 回归禁区（改动时不要破坏的既有行为）

- 译文偏移（`DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` vs `GetWindowRect` 的边框差异）
- 盖不住原文（背景块尺寸与 `BgOpacity`）
- 图标被切字（字号 clamp 与 `availWidth` 收缩）
- ROI 归一化比例存法（窗口缩放自动跟随）
- 渲染 30Hz 解耦（快照未变只做平移，不重建视觉树）
- 变化检测（`FrameFingerprint`，判据是**绝对变化格数**而非比例）
- OCR 线程数 clamp（4~8，ORT 线程数 U 型曲线）
- 帧池必须用 `CreateFreeThreaded`（改回 `Create` 会让 `IDirect3DDxgiInterfaceAccess` 的 QI 全灭）

---

## 16. 待优化项

| 项 | 说明 |
|---|---|
| 导出诊断机制 | `CopyDiag_Click` 覆盖写 `诊断日志.txt`，会抹掉 `AppendDiagLog` 的实时日志。建议：导出时把实时日志内容拼进导出文本，并改写新文件（如 `导出诊断.txt`），不再覆盖实时日志 |
| 旧 D3D 回读路径的去留 | `UseSoftwareBitmapPath = false` 的自建 D3D 路径已证明会触发 `0x887A0006`，目前仅作回退保留；确认 SoftwareBitmap 路径稳定后可考虑删除，减少维护面 |
| 30 帧兜底阈值 | 「到达 ≥ 30 且 转图 == 0 → 降级 GDI」在 WGC 真能用时也会误伤（诊断开关被勾着就是典型）。建议改为「连续 N 秒转图无增长」 |
| 环境层健壮性 | WGC 已通（见 11.6）。若在别的机器上再出现 `DEVICE_HUNG`，先在物理机前本地操作、并尝试禁用虚拟显示适配器后复现（见 11.7） |

---

## 17. 代码位置速查表

| 想改什么 | 看哪里 |
|---|---|
| 开启 / 停止翻译、配置校验、捕获后端选择 | `MainWindow.xaml.cs` `StartTranslation()`（约 L209-369） |
| 单帧处理链路（捕获 → OCR → 翻译 → 绘制） | `MainWindow.xaml.cs` `ProcessFrame()`（L417） |
| 状态栏输出（诊断仪表盘） | `MainWindow.xaml.cs` `SetStatus()`（L1109） |
| 主界面 XAML（含各种诊断复选框、按钮） | `MainWindow.xaml` |
| 「导出诊断」按钮 | `MainWindow.xaml.cs` `CopyDiag_Click` |
| WGC 回调（计数 / 节流 / 重入守卫 / 触发转换） | [Services/WgcCaptureService.cs](Services/WgcCaptureService.cs) `OnFrameArrived`（L541） |
| WGC 帧池创建（必须 `CreateFreeThreaded`） | [Services/WgcCaptureService.cs](Services/WgcCaptureService.cs) L215 |
| SoftwareBitmap → SKBitmap 转换 | [Services/WgcCaptureService.cs](Services/WgcCaptureService.cs) `ConvertViaSoftwareBitmapAsync` / `SoftwareBitmapToSkBitmap` |
| 自建 D3D 回读路径（回退用） | [Services/WgcCaptureService.cs](Services/WgcCaptureService.cs) `ConvertSurfaceToSkBitmap`（L1038） |
| WGC 资源释放 | [Services/WgcCaptureService.cs](Services/WgcCaptureService.cs) `Dispose`（L1197） |
| 诊断日志写入 | `WgcCaptureService.cs` `AppendDiagLog` |
| GDI 抓帧（PrintWindow / BitBlt 回退） | `Services/GdiCaptureService.cs` `Capture()` |
| OCR 服务与选项 | `Services/OcrService.cs`（约 L23-82 选项、L84-154 初始化、L156-213 `Recognize()`） |
| OCR 线程数与 provider | `Services/OcrRuntime.cs`（约 L69-108） |
| 中文模型下载与校验 | `Services/OcrModelManager.cs`（约 L76-169） |
| 分行分段 | `Services/TextSegmenter.cs` `BuildSegments()`（约 L21-116） |
| 翻译入口与云端 / 本地区分 | `Services/TranslationService.cs` `TranslateAsync()`（约 L80-108）；本地实现约 L114-200 |
| 译文缓存 | `Services/TranslationCache.cs` |
| 变化检测（指纹） | `Services/FrameFingerprint.cs` |
| ROI 换算 | `Services/RoiGeometry.cs` |
| 识别框自适应 | `Services/OcrBoxFitter.cs` |
| 覆盖层防截屏 | `Windows/OverlayWindow.xaml.cs`（约 L49-70 设计说明、L218-287 实现） |
| 覆盖层渲染循环（30Hz 跟随） | `Windows/OverlayWindow.xaml.cs`（约 L319-426） |
| 设置界面与实时预览 | `Windows/SettingsWindow.xaml(.cs)` |
| 识别区域框选 | `Windows/RegionSelectWindow.xaml(.cs)` |
| 全局热键 | `Services/HotkeyManager.cs` |
| 配置读写 | `Services/ConfigService.cs` + `Models/AppConfig.cs` |

---

## 18. 编译常见报错

### 编译前必须关闭程序（高频卡点）

程序是**管理员权限**（manifest 提权），普通终端**杀不掉**；且运行中的实例会**锁住 exe**，导致 `dotnet build` 报 `MSB3027 / MSB3021`（文件被占用）。

**操作顺序**：关闭翻译器窗口 → 用任务管理器确认无 `ScreenTranslator` 进程 → 再 `dotnet build -c Release`。

若必须在程序运行时验证代码能否编译（不覆盖 exe）：

```bash
dotnet build -c Release -o <临时目录>
```

### 验证 dll 是否为最新版本（避免「改了源码但跑的是旧程序」）

代码全在 dll 里（exe 只是 apphost 启动器，通常不会被重写）。可用字节检索确认（在仓库根目录下执行）：

```python
d = open(r'bin\Release\net8.0-windows10.0.26100.0\ScreenTranslator.dll','rb').read()
print(b'abiIIns=0x'.decode().encode('utf-16-le') in d or b'abiIIns=0x' in d)
```

---

## 19. 许可证

本项目采用 **MIT License**，版权归 **xkmlk123** 所有。完整条款见仓库根目录的 [LICENSE](LICENSE)。

一句话概括：你可以自由使用、修改、分发本项目（包括商用与闭源再发布），**唯一条件是保留版权声明与许可声明**。

本项目依赖的第三方组件均为宽松型许可（Apache-2.0 / MIT），与本项目的 MIT 许可兼容，清单见第 8 章。
