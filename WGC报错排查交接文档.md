# WGC 捕获报错排查交接文档

> **用途**：本文档交给后续接手的 AI / 开发者，使其无需重新调查即可继续解决 WGC 捕获问题。
> **项目**：ScreenTranslator（.NET 8 WPF 屏幕实时翻译器）
> **根目录**：`C:\Users\xkmlk\ScreenTranslator\`
> **配套文档**：`项目交接文档.md` 第 30~34 节（本问题的完整历史与可复用规律）

---

## 0. 一句话现状（读完这句就能开工）

WGC **已启动成功、帧正常到达（数千帧）**，但在「把 WinRT `IDirect3DSurface` 转成 `SKBitmap`」这步
**全帧失败**，错误是 COM 接口查询被拒（各种 HRESULT `0x80004002` = E_NOINTERFACE）。
当前源码已实现 **5 条并存的取指针路径 + 全程诊断串**，编译已完成，**只差运行一次读取新的诊断串**。

**下一步唯一动作：运行程序 → 开启翻译 → 读状态栏的「帧：到达 N / 转图 N」与 ⚠ 诊断串 → 按第 5 节决策树处理。**

---

## 1. 环境与基础操作

| 项 | 值 |
|---|---|
| 目标框架 | `net8.0-windows10.0.26100.0`（远超 WGC 所需的 19041） |
| 编译命令 | `cd /c/Users/xkmlk/ScreenTranslator && dotnet build -c Release` |
| 产物 | `bin\Release\net8.0-windows10.0.26100.0\ScreenTranslator.exe` |
| 配置 | `config.json`（运行目录下） |
| 权限 | `app.manifest` = **`requireAdministrator`**，每次启动弹一次 UAC（预期行为） |
| 关键依赖 | `Microsoft.Windows.SDK.NET.dll`（CsWinRT 投影）、`WinRT.Runtime.dll`、SharpDX |

### ⚠️ 编译前必须关闭程序（高频卡点）

程序是**管理员权限**（manifest 提权），普通终端 **杀不掉**；且运行中的实例会**锁住 exe**，
导致 `dotnet build` 报 `MSB3027 / MSB3021`（文件被占用）。

**操作顺序**：
1. 关闭翻译器窗口
2. `tasklist | grep -i ScreenTranslator` 确认无进程
3. 再 `dotnet build -c Release`

若必须在程序运行时验证代码能否编译（不覆盖 exe）：
```
dotnet build -c Release -o <临时目录>
```

### 验证 dll 是否为最新版本（避免"改了源码但跑的是旧程序"）

代码全在 dll 里（exe 只是 apphost 启动器）。可用字节检索确认：
```python
d = open(r'C:\Users\xkmlk\ScreenTranslator\bin\Release\net8.0-windows10.0.26100.0\ScreenTranslator.dll','rb').read()
print(b'abiIIns=0x'.decode().encode('utf-16-le') in d or b'abiIIns=0x' in d)
```

---

## 2. 状态栏怎么读（这是整个排查的仪表盘）

`MainWindow.xaml.cs` 的 `SetStatus()`（约 L1074-1105）常驻输出：

```
状态：正在翻译（WGC模式）           ← 或「GDI（WGC 失败）模式」
窗口：xxx
<本帧详情>
帧：到达 N / 转图 N｜⚠ <LastFrameError>
```

若降级 GDI，则改为：
```
抓帧：PrintWindow N / BitBlt N（防截屏已生效｜⚠ 防截屏未生效）
WGC 失败原因：[WGC 步骤N] xxx
```

**三个数的含义（诊断体系的核心）**：

| 组合 | 含义 |
|---|---|
| 到达 0 | 会话压根没投递帧（窗口最小化/不可见、受保护内容） |
| 到达 N / 转图 0 | **当前卡点**：帧到了但 Surface→Bitmap 转换全线失败 |
| 到达 N / 转图 N | 正常 |

对应代码：`Services/WgcCaptureService.cs` L287-296（`FrameArrivedCount` / `FrameTakenCount` /
`FrameConvertedCount` / `LastFrameError`），消费端 `MainWindow.xaml.cs` L415-441 与 L1093-1094。

---

## 3. 故障链条总览（按时间顺序，每步都有报错原文）

| # | 症状 | 根因 | 状态 |
|---|---|---|---|
| 1 | 译文越用越大（阶梯式） | GDI 降级 → BitBlt 从**屏幕 DC** 抓图 → 把自己刚画的译文抓回去再翻译 → OCR 框变大 → 字号 ∝ 框高 | ✅ 已修（防截屏 + 变化检测 + 去重） |
| 2 | WGC 启动即失败 | **UIPI 权限不对等**：程序 `asInvoker` vs 目标窗口管理员 | ✅ 已修（manifest 改 `requireAdministrator`） |
| 3 | `[WGC 步骤6] Specified cast is not valid` | `GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>()` 在新版 CsWinRT 做的是**普通 cast 而非 QI**；且 `typeof(GraphicsCaptureItem).GUID` 是 **CLSID 不是接口 IID** | ✅ 已修（`RoGetActivationFactory` + `AsInterface`） |
| 4 | `[WGC 步骤7] Failed to create a CCW ... System.__ComObject` | 步骤 5 用 `Marshal.GetObjectForIUnknown` 造的是**经典 COM RCW**，CsWinRT 的 `Create()` 要的是投影对象 | ✅ 已修（`MarshalInterface<T>.FromAbi`） |
| 5 | 「未取到画面」（WGC 模式，帧到达但转图 0） | 同第 6 步 | ⬇️ 见第 6 步 |
| 6 | `QueryInterface(IDXGISurface) 失败，HRESULT=0x80004002` | WinRT `IDirect3DSurface` 是**包装器**，本身不实现 IDXGISurface | ✅ 已改走 `IDirect3DDxgiInterfaceAccess` |
| 7 | `QueryInterface(IDirect3DDxgiInterfaceAccess) 失败，HRESULT=0x80004002`（7113 帧全灭） | （见第 4 节分析）指针身份问题 | 🔄 **当前卡点** |

---

## 4. 当前卡点详解：Surface → SKBitmap

### 4.1 最新诊断串（原文）

```
获取 DXGI Surface 失败（IInspectable, IWinRT=True, IIns=0x80004002,
                       TryAs=0x80004002, QI=0x80004002, Cast=InvalidCastException）
```

### 4.2 逐字段解读

| 字段 | 值 | 含义 |
|---|---|---|
| 类型名 | `IInspectable` | surface 的运行时类型是 CsWinRT **通用包装类** `WinRT.IInspectable`，不是投影类 |
| `IWinRT` | True | 实现了 `WinRT.IWinRTObject` |
| **`IIns`** | **0x80004002** | **决定性线索**：连 `IInspectable` 都 QI 失败。所有真 WinRT 对象**强制实现** IInspectable，连它都拒绝 ⇒ **手工取到的指针身份有问题**，不是"对象不支持某接口" |
| `TryAs` / `QI` | 0x80004002 | 三条路径全灭且 HRESULT 一致 ⇒ 不是路径问题，是指针身份问题 |
| `Cast` | InvalidCastException | C# 直接 cast 到 ComImport 接口不通 |

**结论**：`NativeObject.ThisPtr` 这种"手工从投影对象挖指针"的做法不可靠（包装层深度是投影实现的内部细节）。

### 4.3 当前源码：5 条路径并存

`Services/WgcCaptureService.cs`

| 位置 | 方法 / 路径 | 说明 |
|---|---|---|
| L402 | `AcquireDxgiSurfacePtr(surface, out diag)` | 五路径调度 + 诊断串生成 |
| L519 | `CallGetInterface(pAccess, sb)` | 手动调 `GetInterface`（vtable 第 4 槽），不走 RCW |
| L344 | `GetNativeUnknown(obj)` | 手工取指针（`IWinRTObject.NativeObject.ThisPtr`）——**已被证明不可靠，仅作备选** |
| L532 | `ConvertSurfaceToSkBitmap(surface)` | 主转换方法，调用上面三条 |

路径执行顺序（**先成功先返回**）：

| 路径 | 手段 | 备注 |
|---|---|---|
| **0** | `WinRT.MarshalInspectable<IDirect3DSurface>.FromManaged(surface, true)` | **新增·重点**：CsWinRT 官方 ABI 出口，负责剥掉所有包装层；并做 `IInspectable` 身份验证 |
| 1 | `IObjectReference.TryAs(Guid, out IntPtr)` | CsWinRT 原生 QI（参数是 **`out`** 不是 `ref`） |
| 2 | `Marshal.QueryInterface` | 经典 QI |
| 3 | `(IDirect3DDxgiInterfaceAccess)(object)surface` | 老 Sample 投影 cast |
| **4** | `WinRT.CastExtensions.As<IDirect3DDxgiInterfaceAccess>(surface)` | **新增**：官方显式 QI + RCW 构造 |

### 4.4 诊断字段字典（新版）

`abi=` 官方 ABI 指针值 ｜ `abiIIns=` 该指针的 IInspectable 体检结果 ｜ `abiQI=` QI access 结果 ｜
`ptr=` 手工指针值 ｜ `IIns=` 手工指针体检 ｜ `TryAs=` 路径1 ｜ `QI=` 路径2 ｜ `Cast=` 路径3 ｜ `As=` 路径4 ｜
`GetIface=` `GetInterface` 调用结果

---

## 5. 决策树：看到新诊断串怎么走

```
运行后状态栏
├─ 转图 N 开始上涨 ────────────────► ✅ WGC 通了。做收尾验证（见第 8 节）
│
└─ 转图 0，出现新诊断串
   ├─ abiIIns=0x0 且 abiQI=0x0 ─────► 路径0 拿到真指针但 QI access 仍失败
   │                                  ⇒ 该 surface 对象确实不暴露 IDirect3DDxgiInterfaceAccess
   │                                  ⇒ 换思路：读第 7 节的备选方案
   ├─ abiIIns≠0x0 ──────────────────► 官方 ABI 指针也不可靠 ⇒ 极可能是 apartment/线程上下文问题
   │                                  ⇒ 试 framePool.CreateFreeThreaded / 指定 STA 回调
   ├─ As=0x0 ───────────────────────► 路径4 成功（记录并保留）
   └─ 全部失败 ─────────────────────► 把完整诊断串贴给下一个环节；
                                      串中的「类型名」+「各 HRESULT」足以判定对象真身
```

---

## 6. 已排除项（**别再走这些弯路**）

| 已排除 | 依据 |
|---|---|
| 权限不足 | 权限不足在**创建捕获项（步骤 6）**就抛，走不到收 7000+ 帧 |
| 目标窗口最小化/不可见 | 帧一直在到达（数千帧） |
| 硬件加速 / 双 GPU / HAGS | 只影响"帧内容"，影响不了 COM 接口查询；且帧已到达 |
| 注册表 DCI 超时 | 产生黑帧，不会产生 `E_NOINTERFACE` 异常 |
| 字号/渲染层累积放大 | 已逐行确认 `Draw`/`DrawOcrBoxes` 对 blocks **零写回**，`OcrBoxFitter.ApplyTo` 幂等 |
| 翻译去重缺失 | 已有 `FrameFingerprint` 变化检测 + 文本标准化去重 + 翻译缓存 |

> 注：网络/其他 AI 给出的"通用 WGC 排查清单"（改注册表、关硬件加速、重启等）
> 针对的是"无报错 + 纯黑屏"场景，**对本问题无效**——本问题有明确报错与证据链。

---

## 7. 备选方案（若五条路径全失败）

按推荐顺序：

1. **换帧池类型**：`Direct3D11CaptureFramePool.CreateFreeThreaded(...)`，
   或在 `FrameArrived` 回调里显式确认 apartment（`IsFreeThreaded` / `IsInCurrentContext`）。
   跨 context 使用 ObjectReference 指针可能导致 QI 异常。
2. **绕开 surface 的 COM 取指针**：用 `Windows.Graphics.Capture` 的
   `Direct3D11CaptureFrame.ContentSize` 校验 + 直接把 frame.Surface 交给
   **CsWinRT 投影的 `IDirect3DDxgiInterfaceAccess`**（若 SDK 投影里有 public 版本，
   需反射确认 `Microsoft.Windows.SDK.NET.dll` 中是否存在该类型的公开投影）。
3. **换捕获后端**：放弃 WGC，正式接受 GDI 路径，但必须保证
   `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` 生效（防回灌），
   并接受"抓帧时覆盖层短暂隐藏"的闪烁代价（已有兜底实现）。
4. **换库**：用 `SharpDX` 之外的封装（如 Vortice.Windows / Silk.NET）做 WGC，
   它们对 CsWinRT 互操作的处理可能更成熟。

---

## 8. 修好后的收尾验证清单

1. 状态栏显示 **WGC 模式**，「帧：到达 N / 转图 N」两个数字**同步上涨**
2. 译文正常显示，**字号不再阶梯式变大**（原 bug 症状消失）
3. 按 `PrtScn` 截图 → 粘贴到画图：**看不到**译文与白底块（防截屏生效）
4. 换管理员权限的目标窗口、多显示器不同 DPI 各验证一次
5. `dotnet build -c Release` 保证 **0 warning 0 error**

---

## 9. 关键常量速查（已验证，勿改）

| 名称 | 值 | 验证方式 |
|---|---|---|
| `IDXGISurface` IID | `CAFCB56C-6AC3-4889-BF47-9E23BBD260EC` | 反射比对 `typeof(SharpDX.DXGI.Surface).GUID` 一致 |
| `IDirect3DDxgiInterfaceAccess` IID | `A37624AB-8D5F-4650-9D3E-9EAE3D9BC670` | 步骤 7 原始报错中出现 |
| `IInspectable` IID | `ECD4B446-17B9-4E77-B0D9-B9BD4B1C2D90` | WinRT 强制接口 |
| `IGraphicsCaptureItemInterop` IID | `3628E81B-3CAC-4C60-B7F4-23CE0E0C3356` | 官方头文件 |
| `IGraphicsCaptureItem` 接口 IID | `79C3F95B-31F7-4EC2-A464-632EF5D30760` | **不是** CLSID（`e79f1dd1-…`） |
| `IActivationFactory` IID | `00000035-0000-0000-C000-000000000046` | 标准 |

---

## 10. 可复用规律（本问题踩坑总结，改代码前务必读）

1. **CsWinRT 投影对象不要用经典 COM interop 取指针。**
   `Marshal.GetIUnknownForObject(投影对象)` 返回的是 **CCW 身份指针**，不是底层 WinRT 对象 IUnknown。
   官方 ABI 出口是 `MarshalInspectable<T>/MarshalInterface<T>.FromManaged`。
   （本文件 interop 已先后踩坑 5 次，全属此类。）
2. **`typeof(运行时类).GUID` 是 CLSID，不是接口 IID。** 传 `ref Guid iid` 必须传接口 IID。
3. **SharpDX 等 wrapper 的 `(IntPtr)` 构造函数不做 QI**，传错接口指针会静默调错方法（比崩更难查）。
4. **运行时类的静态 `As<T>()` 对 `[ComImport]` 接口会抛 InvalidCastException**（做的是托管 cast 非 QI）。
5. **`catch { }` 是 bug 放大器**：异步回调里空 catch + `return null`，会把丰富异常压缩成不可诊断的上层症状。
   凡吞异常处必须留 `LastError` 属性给上层读。
6. **异步管道必须有分段进度计数**（到达/转图），否则断在哪一环无法二分定位。
7. **用「QI IInspectable」给任何 WinRT 指针做身份体检**：真对象必过，假货立现。
8. **`WS_EX_TRANSPARENT` 不防截屏**（只影响鼠标命中测试）。要"看得见但抓不到"必须用
   `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`。
   注意其读回值可能是 `WDA_MONITOR(0x01)` 而非 `0x11`，校验时两者都算成功。

---

## 11. 回归禁区（改动时不要破坏的既有行为）

- 译文偏移（`DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` vs `GetWindowRect` 的边框差异）
- 盖不住原文（背景块尺寸与 `BgOpacity`）
- 图标被切字（字号 clamp 与 `availWidth` 收缩）
- ROI 归一化比例存法（窗口缩放自动跟随）
- 渲染 30Hz 解耦（快照未变只做平移，不重建视觉树）
- 变化检测（`FrameFingerprint`，判据是**绝对变化格数**而非比例）
- OCR 线程数 clamp（4~8，ORT 线程数 U 型曲线）
