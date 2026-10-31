# WtClickerGUI — WuwaTappo 桌宠连点器

针对《鸣潮》周边桌宠应用 **WuwaTappo** 的智能连点器（C# WinForms，.NET 8，单文件 exe）。
后台注入点击，**不接管鼠标**——可以边用电脑边挂机。

## 核心特性

- **实时视觉调度**：PrintWindow 抓帧 + 模板匹配 OCR，实时读取桌宠计数、倒计时气泡、宝箱图标，所有调度决策基于「现在看到的」
- **智能宝箱策略**：
  - 宝箱图标弹出 → 独立线程 0.3s 巡检，立即点击
  - 倒计时气泡排队（= 已攒满待领取的宝箱）且实时计数 ≥ 气泡数 × 1000 → 暂停防溢出
  - 计数未攒满 → 全速补门槛；无气泡 → 全速攒下一个 1000
  - 气泡期可选「匀速模式」：把缺口按剩余冷却摊开成恒定低速点击，平滑不突发
- **联机模式兼容**：以「✦ 星形圆钮」结构签名定位本机计数胶囊，其他玩家名牌（含数字用户名）从结构上被排除
- **拟人化节奏**：抖动 + 微/长休息，防「点击数异常」检测；宝箱紧迫时自动跳过长休息
- **健壮性细节**：防系统休眠/熄屏、用户拖动桌宠时暂停注入（防鼠标漂移）、配置持久化、构建时间版本戳、日志落盘

## 构建单文件 exe

```bash
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true -p:EnableCompressionInSingleFile=true -o dist
```

产物：`dist/WtClickerGUI.exe`（约 65MB，自包含运行时，免安装）。

## 离线识别回归测试

`tools/ReaderTest` 是一个最小测试工程，用真实抓帧验证 CounterReader / ChestDetector：

```bash
cd tools/ReaderTest
dotnet run -c Release -- ../<帧文件>.bin
```

帧文件为 `.png` 转换的 BGRA 原始数据（前 8 字节为 int32 宽、高），可用 `tools/cap_frame.py` 抓取。

## 项目结构

| 文件 | 职责 |
|---|---|
| `Engine.cs` | 调度核心：点击线程 / 视觉线程 / 宝箱线程，策略与心跳 |
| `Scanner.cs` | PrintWindow 抓帧 + 亮块聚类 |
| `CounterReader.cs` | 本机计数胶囊 OCR（✦ 圆钮锚点 + 列光剖面） |
| `CountdownReader.cs` | 倒计时气泡 OCR（mm:ss 模板匹配） |
| `ChestDetector.cs` | 宝箱图标检测（掩码 MAD 模板匹配，背景无关） |
| `Native.cs` | Win32 API 封装（注入、DPI、电源状态） |
| `MainForm.cs` | UI（手绘 WinForms，卡片 100ms 实时刷新） |

## 免责声明

仅供学习交流，请遵守游戏相关用户协议，滥用产生的账号风险自行承担。
