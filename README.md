# WoWSBattleAssistant · 战舰世界实时战斗分析助手

一个为《战舰世界》（World of Warships）做的实时战术分析悬浮窗工具。开局读秒阶段截一张阵容图、对局中截一张小地图，AI 结合双方舰船参数与小地图态势，给出本局的打法建议、威胁评估与优先目标。

## 功能特性

- **悬浮窗设计**：半透明置顶，不遮挡游戏；截图瞬间自动隐藏避免入镜，截完恢复。
- **三步式流程**：截阵容 → AI 识别舰船名 → 截小地图 → AI 综合分析，操作直观。
- **AI 视觉识别**：自动识别阵容面板中的舰船名，并用知识库过滤掉 AI 把"玩家名"误判成"舰船名"的情况。
- **舰船参数知识库**：加载约 945 艘船的官方数据（JSON），按本局出现的舰船按需提取主炮/炮弹/鱼雷/副炮/存活/机动/隐蔽/防空等关键参数，构建精简知识库供 AI 参考，避免全量数据塞爆 Token。
- **双 AI 引擎可切换**：智谱 GLM-4V / GLM-4V-Plus、阿里通义千问 VL，均走 OpenAI 兼容协议。
- **战术输出四部分**：①怎么玩这艘船 ②敌方威胁评估（区分人机/真人）③优先攻击目标 ④整局局势与策略。
- **容错设计**：小地图无敌方时基于阵容推断；双方同型舰靠阵容图区分敌我；玩家名带冒号判定为人机。
- **键鼠友好**：屏蔽 TAB/空格/回车等按键避免焦点乱跳（玩家常按住游戏内 TAB 看阵容同时操作本程序）。

## 技术栈

- **框架**：WPF (.NET 10, `net10.0-windows`)
- **语言**：C#（启用 Nullable、ImplicitUsings）
- **依赖**：仅 .NET BCL（`System.Text.Json`、`System.Net.Http`、`System.Windows`），无第三方 NuGet 包
- **AI 协议**：OpenAI 兼容 `/chat/completions`（多图 + 文本）

## 项目结构

```
WoWSBattleAssistant/
├── Models/
│   ├── AppSettings.cs            # 应用配置（AI/数据路径/窗口位置等）
│   ├── BattleAnalysisRequest.cs  # 分析请求（含两张图与舰船列表）
│   └── ShipRecognitionResult.cs  # 阵容识别结果
├── Services/
│   ├── AI/
│   │   ├── IAIBattleAnalyzer.cs  # AI 接口 + OpenAI 兼容基类 + GLM/通义实现
│   │   └── AIAnalyzerFactory.cs  # 按 AiProvider 创建分析器
│   ├── ScreenCaptureService.cs   # 屏幕截图 + DPI 处理 + Base64 编码
│   ├── SettingsStore.cs          # 配置持久化（%AppData%）
│   └── ShipDatabase.cs           # 战舰数据知识库（索引 + 参数提取）
├── Views/
│   ├── RegionSelectorWindow.*    # 屏幕区域框选器
│   └── SettingsWindow.*          # 设置面板
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / .xaml.cs     # 悬浮窗主界面（三步流程）
├── WoWSBattleAssistant.csproj
└── app.manifest
```

## 环境要求

- Windows 10/11
- .NET 10 SDK（编译运行）
- 《战舰世界》国服或国际服客户端
- 智谱 或 通义千问 的多模态视觉模型 API Key

## 首次配置

1. 用 .NET 10 SDK 编译：`dotnet build -c Release`，或用 Visual Studio 2022 打开 `.csproj` 编译运行。
2. 启动后点击主界面右上角 **⚙** 打开设置。
3. 选择 AI 提供方并填写 API Key：
   - **智谱 GLM-4V**：到 https://open.bigmodel.cn 注册，在「API Keys」页面创建 Key；模型可选 `glm-4v` 或 `glm-4v-plus`。
   - **通义千问 VL**：到阿里云 DashScope https://dashscope.aliyuncs.com 开通，创建 API Key；模型可选 `qwen-vl-plus` 或 `qwen-vl-max`。
4. **战舰数据文件**：选择一份战舰数据 JSON（数组格式，每艘船需含 `name`、`tier`、`nation`、`vtype`、`ship_info_list` 等字段，约 33MB / 945 艘船）。可用配套爬虫生成 `wows_ships_data_*.json`。点「重新加载知识库」确认加载数量。
5. （可选）预设小地图区域：点「框选小地图区域」，在屏幕上框选游戏小地图位置。
6. 保存设置。

> 配置文件位于 `%AppData%\WoWSBattleAssistant\settings.json`，含 API Key，请勿随意分享。

## 使用说明

主界面三个步骤，按顺序操作：

**① 截阵容**（开局读秒阶段）
- 点「截阵容」按钮 → 主窗口自动隐藏 → 在屏幕上拖框选中双方阵容面板 → 截图自动回填。
- AI 识别图中舰船名（约 10-30 秒），并用知识库过滤掉非舰船名。
- 从下方下拉框中**选择你自己的战舰**（用于让 AI 在阵容图中定位我方阵营）。

**② 截小地图**（对局进行中）
- 点「截小地图」按钮 → 拖框选中游戏小地图区域 → 截图回填。

**③ 分析**
- 「我的战舰」与「小地图」都就绪后，「分析」按钮变亮。
- 点「分析」→ 程序自动构建本局舰船参数知识库，连同阵容图、小地图一并发给 AI。
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

- **「未配置 API Key」**：到设置里填写对应提供方的 Key 并保存。
- **「识别到 X 项但无一命中知识库」**：AI 把所有内容都识别成玩家名了，可手动在「所有舰船」框里输入舰船名（顿号/逗号/空格分隔）。
- **分析结果编造参数**：程序已强制要求 AI 只能用知识库数据，若仍出现，检查数据文件是否包含该舰。
- **截图区域不对**：确保游戏以窗口/全屏窗口模式运行；框选时主窗口会自动隐藏。
- **多显示器/高 DPI**：截图服务已处理 DPI 缩放，按设备像素精确截取。

## 构建

```bash
dotnet build -c Release
# 产物：bin/Release/net10.0-windows/WoWSBattleAssistant.exe
```

## 许可

本仓库为个人学习用途。战舰数据来源于游戏官方公开接口，AI 调用需自行承担对应平台的 API 费用。
