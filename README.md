# CUnlock

基于蓝牙信号强度的 Windows 靠近/离开自动控制工具。

离开电脑一定距离时自动关屏或锁屏，回到附近时自动点亮屏幕。适合把手机或智能手表当作"随身钥匙"。

> 仓库名为 `CUnlock`，程序与工程内部命名仍为 `ProximityLock`（历史原因，功能一致）。

---

## 功能

| 功能 | 说明 |
|------|------|
| 蓝牙 BLE 扫描 | 主动扫描，实时读取 RSSI 信号强度 |
| 距离判定 | RSSI 换算为近似距离（米），带迟滞防抖 |
| 自动锁屏 | 两种模式：真正锁屏 / 仅关闭显示器 |
| 靠近唤醒 | 回到附近时点亮屏幕 |
| 已配对设备支持 | 枚举系统已配对设备，经典蓝牙设备按连接状态判定 |
| Apple 设备支持 | iPhone / Apple Watch 无广播名称，按厂商 ID 识别；随机地址轮换时自动跟随 |
| 托盘运行 | 关闭窗口最小化到托盘，右键可一键暂停 |
| 开机自启 | 可选，写入当前用户 `Run` 注册表项 |
| 配置持久化 | JSON 保存在 `%AppData%\ProximityLock\config.json` |

---

## 环境要求

- Windows 10 1809 及以上 / Windows 11
- 蓝牙 4.0（BLE）适配器
- 开发需要 .NET 7 SDK

---

## 构建

```powershell
# 普通构建
dotnet build ProximityLock.sln -c Release

# 单文件自包含发布（无需目标机安装 .NET 运行时）
dotnet publish ProximityLock/ProximityLock.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

产物在 `publish\ProximityLock.exe`，约 170 MB（内嵌运行时）。双击即可运行，无需安装。

---

## 使用

1. 启动程序，窗口打开后点「刷新扫描」
2. 拿着手机/手表站到电脑前，点「绑定最强者」自动绑定信号最强的设备
   - 也可以在列表里手动选择（已配对设备排在前面）
3. 按实际体验调整两个距离滑块
4. 需要时关闭「自动锁屏」或「自动唤醒」开关

### 关于距离

RSSI 无法精确换算距离，程序用对数距离模型估算：

```
RSSI = -59 - 20 × log10(距离)
```

误差较大（墙体、人体遮挡、握持方向都会影响 2.4GHz 信号）。**建议实测校准**：拿着设备走到你希望触发的位置，看界面显示的「约 X m」，再把滑块调到对应值。

### 两种锁屏模式

| 模式 | 行为 | 恢复时是否需要凭证 |
|------|------|------------------|
| 仅关闭显示器（默认） | 发送关屏指令，系统**不锁定** | 不需要（需配合下方 Windows 设置） |
| 真正锁屏 | 调用 `LockWorkStation()` | 需要 PIN / 密码 / 生物识别 |

选择「仅关闭显示器」时，必须手动设置：

> 设置 → 账户 → 登录选项 → 「如果你离开，Windows 何时要求你重新登录」→ 从不

否则从关屏恢复后仍会停留在登录界面。

---

## 已知限制

### 无法真正自动解锁

Windows 的锁屏界面运行在独立的 secure desktop 上，普通用户进程既不能向来宾桌面发送按键，也没有任何解除锁屏的 API。**只有系统签名的凭据提供程序（Credential Provider）才能参与解锁流程**，因此：

- 「真正锁屏」模式下，靠近只能点亮屏幕，仍须手动输入 PIN / 密码
- 「仅关闭显示器」模式下不锁定，所以恢复时可以直接回到桌面（代价是安全性）
- 若使用「仅关闭显示器」模式且电脑处于公共环境，请谨慎：任何人走近都可直接使用电脑

### Apple 设备识别不稳定

- iPhone / Apple Watch 不广播蓝牙名称，也不响应 GATT 名称读取
- iOS 设备使用随机蓝牙地址并定期轮换，Windows 无法解析真实身份
- 程序采用「同厂商 + 信号强度连续性」的近似方案跟随地址轮换，存在两类误差：
  - 附近有多台 Apple 设备且信号接近时可能绑定到错误设备
  - 地址轮换瞬间（约 15~20 分钟一次）存在数秒盲区

### 距离判定是估算值

RSSI 受环境影响很大，同一个人距离在不同姿态下可能相差 10 dBm 以上。滑块调节以实际使用体感为准，不要按字面米数理解。

### 经典蓝牙设备只有"连接 / 断开"两态

已配对的经典蓝牙设备（耳机、安卓手机等）不提供距离信息，程序只能通过连接状态判断，没有距离分级。从未连接过的低功耗配对设备不会被用于判定，避免误锁。

### 其他

- 电脑进入睡眠（S3）后，靠近无法唤醒，需要键盘或电源键
- 程序以当前用户权限运行，无需管理员
- 无代码签名，首次运行可能出现 SmartScreen 提示

---

## 目录结构

```
ProximityLock/
├── App.xaml / App.xaml.cs          程序入口、全局单例
├── AppConfig.cs                    配置模型与 JSON 持久化
├── BluetoothScanner.cs             BLE 扫描、已配对设备枚举
├── LockService.cs                  距离状态机、锁屏、点亮屏幕
├── MainWindow.xaml / .xaml.cs      界面与托盘
└── app.manifest                    DPI 与兼容性清单
```

---

## 配置说明

配置文件路径：`%AppData%\ProximityLock\config.json`

| 字段 | 说明 |
|------|------|
| `TargetDeviceAddress` / `TargetDeviceName` | 绑定的设备 |
| `LockThreshold` / `WakeThreshold` | 内部使用的 RSSI 阈值（界面上以米显示） |
| `LockTriggerCount` | 连续判定次数，用于防抖 |
| `ScanIntervalMs` | 扫描间隔（毫秒） |
| `LockCooldownSeconds` | 锁屏冷却时间 |
| `MonitorOffMode` | `true` 仅关屏，`false` 真正锁屏 |
| `AutoLockEnabled` / `AutoWakeEnabled` | 两个总开关 |
| `AutoStart` | 开机自启 |

---

## 许可证

本仓库暂未指定开源许可证。如需在其他项目中使用，请先联系仓库作者。
