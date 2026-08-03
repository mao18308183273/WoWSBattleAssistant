# WoWSBattleAssistant · 战舰世界实时战斗分析助手

![version](https://img.shields.io/badge/version-2.0.0-16A085) ![license](https://img.shields.io/badge/license-personal-F39C12) ![dotnet](https://img.shields.io/badge/.NET-10-blue) ![platform](https://img.shields.io/badge/platform-Win10%2B11-lightgrey)

一个为《战舰世界》（World of Warships）做的实时战术分析悬浮窗工具。开局读秒阶段截一张阵容图、对局中截一张小地图，AI 结合双方舰船参数、**联网查询的玩家战绩**与小地图态势，给出本局的打法建议、威胁评估与优先目标。

## 功能特性

- **悬浮窗设计**：半透明置顶，不遮挡游戏；截图瞬间自动隐藏避免入镜，截完恢复。
- **三步式流程**：截阵容 → AI 识别舰船名 → 截小地图 → AI 综合分析，操作直观。
- **AI 视觉识别**（V2.0.0 重构）：自动识别阵容面板中的「玩家名 + 舰船名」配对，并用知识库过滤掉 AI 把玩家名误判成舰船名的情况。**分析阶段 AI 会自行从阵容图的「队友/敌方」标题判断敌我**，不再完全依赖前置识别结果，识别错误时 AI 能以阵容图为准自行纠正。
- **玩家战绩联网查询**：识别完成后自动调用 [shinoaki](https://wows.mgaia.top) 公开 API：
  - 按玩家名搜索判定**真人 / 人机**（搜到 = 真人；玩家名含冒号或搜不到 = 人机）。
  - 真人玩家拉取战绩：PR 值与评级、总场数、胜率、场均伤害、场均击杀、KD。
  - 战绩以紧凑文本注入 AI，让威胁评估基于真实数据而非「看名字风格」。
- **舰船参数知识库**：加载约 945 艘船的官方数据（JSON），按本局出现的舰船按需提取主炮/炮弹/鱼雷/副炮/存活/机动/隐蔽/防空等关键参数，构建精简知识库供 AI 参考，避免全量数据塞爆 Token。
- **三 AI 引擎可切换**：
  - **智谱 GLM-4V / GLM-4V-Plus**（OpenAI 兼容官方 API）
  - **阿里通义千问 VL**（qwen-vl-plus / qwen-vl-max，OpenAI 兼容）
  - **DeepSeek 视觉**（chat.deepseek.com 网页版协议逆向，支持思考链）
- **战术输出四部分**：①怎么玩这艘船 ②敌方威胁评估（区分人机/真人，引用战绩）③优先攻击目标 ④整局局势与策略。
- **容错设计**：小地图无敌方时基于阵容推断；双方同型舰靠阵容图区分敌我；战绩查询失败降级为「未知」不影响主流程。
- **键鼠友好**：屏蔽 TAB/空格/回车等按键避免焦点乱跳（玩家常按住游戏内 TAB 看阵容同时操作本程序）。

## 技术栈

- **框架**：WPF (.NET 10, `net10.0-windows`)
- **语言**：C#（启用 Nullable、ImplicitUsings）
- **依赖**：仅 .NET BCL（`System.Text.Json`、`System.Net.Http`、`System.Windows`），无第三方 NuGet 包
- **AI 协议**：
  - 智谱 / 通义：OpenAI 兼容 `/chat/completions`
  - DeepSeek：网页版私有协议（会话创建 + PoW 挑战 + 文件上传 + SSE 流式）
- **DeepSeek PoW 求解**：`pow_solver.js` + `sha3_wasm_bg.wasm`（作为嵌入资源，运行时释放到 `%LocalAppData%\WoWSBattleAssistant\pow\`），通过 **Node.js 子进程**执行

## 项目结构

```
WoWSBattleAssistant/
├── Models/
│   ├── AppSettings.cs            # 应用配置（AI/数据路径/服务器/窗口位置等）
│   ├── BattleAnalysisRequest.cs  # 分析请求（含两张图、舰船列表、玩家威胁文本）
│   ├── ShipRecognitionResult.cs   # 阵容识别结果（舰船名 + 玩家名配对）
│   └── PlayerThreatInfo.cs       # 玩家威胁信息（真/人机 + 战绩字段）
├── Services/
│   ├── AI/
│   │   ├── IAIBattleAnalyzer.cs   # AI 接口 + OpenAI 兼容基类 + GLM/通义实现
│   │   ├── AIAnalyzerFactory.cs   # 按 AiProvider 创建分析器
│   │   └── DeepSeek/
│   │       ├── DeepSeekVisionAnalyzer.cs  # DeepSeek 视觉分析器（私有协议）
│   │       ├── DeepSeekPowSolver.cs        # PoW 求解（启动 Node 子进程）
│   │       ├── pow_solver.js               # PoW 计算 JS
│   │       └── sha3_wasm_bg.wasm           # SHA3 WASM 模块
│   ├── Shinoaki/
│   │   └── ShinoakiApiClient.cs  # 玩家搜索 + 战绩查询（判真/人机）
│   ├── ScreenCaptureService.cs   # 屏幕截图 + DPI 处理 + Base64 编码
│   ├── SettingsStore.cs          # 配置持久化（%AppData%）
│   └── ShipDatabase.cs           # 战舰数据知识库（索引 + 参数提取）
├── Views/
│   ├── RegionSelectorWindow.*    # 屏幕区域框选器
│   └── SettingsWindow.*          # 设置面板
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / .xaml.cs     # 悬浮窗主界面（三步流程）
└── WoWSBattleAssistant.csproj
```

## 环境要求

- Windows 10/11（64 位）
- .NET 10 SDK（编译运行）
- 《战舰世界》客户端（国服/亚服/欧服/美服/俄服均可）
- **任选其一** AI 提供方的凭证：
  - 智谱 GLM-4V 的 API Key，或
  - 通义千问 VL 的 API Key，或
  - DeepSeek 网页版登录态（Token + Cookie）
- **仅当使用 DeepSeek 时**：还需安装 [Node.js](https://nodejs.org/) v18+，并确保 `node` 在 PATH 中（PoW 求解依赖它）

## 首次配置

1. 用 .NET 10 SDK 编译：`dotnet build -c Release`，或用 Visual Studio 2022 打开 `.csproj` 编译运行。
2. 启动后点击主界面右上角 **⚙** 打开设置。
3. 选择 AI 提供方并填写凭证：

   **智谱 GLM-4V**（推荐，最稳定）
   - 到 https://open.bigmodel.cn 注册，在「API Keys」页面创建 Key。
   - 模型可选 `glm-4v` 或 `glm-4v-plus`。

   **通义千问 VL**
   - 到阿里云 DashScope https://dashscope.aliyuncs.com 开通，创建 API Key。
   - 模型可选 `qwen-vl-plus` 或 `qwen-vl-max`。

   **DeepSeek 视觉**（网页版逆向，非官方 API）
   - 浏览器登录 https://chat.deepseek.com，按 F12 打开开发者工具。
   - **Token**：Network → 任意 `/api/v0/` 请求 → Headers → `authorization: Bearer xxx`，复制 `Bearer ` 后面的部分。
   - **Cookie**：Network → 任意请求 → Headers → `cookie:` 整行复制（含 `ds_session_id` 等）。
   - 需额外安装 Node.js v18+。
   - 注意：Token/Cookie 会过期，失效后需重新抓取；该方式有被风控的可能。

4. **战舰数据文件**：选择一份战舰数据 JSON（数组格式，每艘船需含 `name`、`tier`、`nation`、`vtype`、`ship_info_list` 等字段，约 33MB / 945 艘船）。可用配套爬虫生成 `wows_ships_data_*.json`。点「重新加载知识库」确认加载数量。
5. **游戏服务器**：选择你玩的服（`cn` 国服 / `asia` 亚服 / `eu` 欧服 / `na` 美服 / `ru` 俄服），用于 shinoaki 玩家战绩查询。
6. （可选）预设小地图区域：点「框选小地图区域」，在屏幕上框选游戏小地图位置。
7. 保存设置。

> 配置文件位于 `%AppData%\WoWSBattleAssistant\settings.json`，含 API Key / Token / Cookie，请勿随意分享。

## 使用说明

主界面三个步骤，按顺序操作：

**① 截阵容**（开局读秒阶段）
- 点「截阵容」按钮 → 主窗口自动隐藏 → 在屏幕上拖框选中双方阵容面板 → 截图自动回填。
- AI 识别图中「玩家名 + 舰船名」配对（约 10-30 秒），并用知识库过滤掉非舰船名。
- 从下方下拉框中**选择你自己的战舰**（用于让 AI 在阵容图中定位我方阵营）。

**② 截小地图**（对局进行中）
- 点「截小地图」按钮 → 拖框选中游戏小地图区域 → 截图回填。

**③ 分析**
- 「我的战舰」与「小地图」都就绪后，「分析」按钮变亮。
- 点「分析」，程序自动依次完成：
  1. 用本局舰船名构建舰船参数知识库。
  2. **调用 shinoaki 查询每个玩家的真人/人机身份与战绩**（并发 5，含进度提示）。
  3. 把阵容图、小地图、知识库、玩家威胁文本一并发给 AI。
- AI 返回四部分建议：怎么玩这艘船 / 敌方威胁评估 / 优先攻击目标 / 整局局势策略。
- 点「复制」可把结果复制到剪贴板。

### 辅助操作

- **清空**：重置所有步骤状态，开始下一局分析。
- **▲/▼ 折叠**：收起中间步骤区，只留结果，缩小悬浮窗。
- **拖动标题栏**移动窗口；位置/尺寸自动记忆。

## 数据文件格式

战舰数据 JSON 必须是数组，每个对象至少包含：

```json
[
  {
    "name": "蒙大拿",
    "tier": 10,
    "nation": "usa",
    "vtype": "战列舰",
    "is_premium": false,
    "is_special": false,
    "ship_info_list": [
      { "key": "artillery", "deploy_list": [ { "parameter_list": [ ... ] } ] }
    ],
    "ai_review": "..."
  }
]
```

- `name`：舰船中文名（知识库按它建索引）
- `tier` / `nation` / `vtype`：等级 / 系别 / 舰种
- `ship_info_list`：参数分项数组，程序会递归扁平化提取 `artillery_*`、`torpedoes_*`、`atbas_*`、`health_*`、`mobility_*`、`concealment_*` 等键
- `ai_review`：可选的第三方 AI 评价

匹配规则：精确 → 大小写不敏感 → 包含匹配（带长度校验，避免用户名夹带船名误命中）。

## 常见问题

- **「未配置 API Key / Token」**：到设置里填写对应提供方的凭证并保存。
- **DeepSeek 报「未找到 Node.js」**：安装 Node.js v18+ 并确保 `node` 在 PATH，重启程序。
- **DeepSeek 报「PoW 求解超时」**：Node 启动过慢或难度过高，重试；检查 Node 版本。
- **DeepSeek 报 401/业务失败**：Token / Cookie 过期，重新到浏览器抓取。
- **「识别到 X 项但无一命中知识库」**：AI 把所有内容都识别成玩家名了，可手动在「所有舰船」框里输入舰船名（顿号/逗号/空格分隔）。
- **玩家战绩全部「未知」**：检查「游戏服务器」设置是否正确；shinoaki 服务可能临时不可用，不影响主流程。
- **分析结果编造参数**：程序已强制要求 AI 只能用知识库数据，若仍出现，检查数据文件是否包含该舰。
- **截图区域不对**：确保游戏以窗口/全屏窗口模式运行；框选时主窗口会自动隐藏。
- **多显示器/高 DPI**：截图服务已处理 DPI 缩放，按设备像素精确截取。

## 构建

```bash
dotnet build -c Release
# 产物：bin/Release/net10.0-windows/WoWSBattleAssistant.exe
```

### 单文件 EXE 发布（可选）

打成自包含单文件，目标机无需装 .NET：

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
# 产物：bin/Release/net10.0-windows/win-x64/publish/WoWSBattleAssistant.exe（约 60MB）
```

> 使用 DeepSeek 引擎时，目标机仍需单独安装 Node.js（PoW 求解依赖）。

## 下载

直接到 [GitHub Releases](https://github.com/mao18308183273/WoWSBattleAssistant/releases) 下载最新版：

- **WoWSBattleAssistant.exe**：单文件主程序，约 60MB，自包含运行时，双击即用
- **wows_ships_data_*.json**：战舰数据文件，首次使用需在程序设置中加载

## 更新日志

### V2.0.0（2026-08-03）

**核心改进 · AI 自主识别敌我**
- 重构 AI 提示词：分析阶段 AI **自行从阵容图的「队友」（绿色标题）/「敌方」（红色标题）判断敌我**，不再依赖前置识别结果。
- 识别与解耦：前置识别仅作参考，AI 以阵容图实际画面为准自行验证，识别错误时 AI 能纠正。
- 明确玩家名/舰船名识别规则：等级前缀为罗马数字（I-XII），紧随空格+舰船名，避免拆分。

**其他改进**
- 完善 `PlayerThreatInfo` 模型，威胁清单字段更规范。
- 优化 `ShinoakiApiClient` 战绩查询逻辑。
- DeepSeek 引擎提示词同步重构，敌我判断逻辑统一。

### V1.0.0（2026-08-01）

- 首个版本：悬浮窗三步式分析流程。
- 三 AI 引擎：智谱 GLM-4V / 通义千问 VL / DeepSeek 视觉。
- 945 艘船参数知识库 + shinoaki 玩家战绩联网查询。
- 纯视觉方案，无安全风险，完全开源。

## 许可

本仓库为个人学习用途。战舰数据来源于游戏官方公开接口，shinoaki 为第三方公开 API，DeepSeek 走网页版逆向协议。AI 调用与战绩查询需自行承担对应平台的相关费用与合规风险。
