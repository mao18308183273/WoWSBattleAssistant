# WoWSBattleAssistant 兼容性与对局数据渠道调研报告

> 调研日期：2026-09-18　|　对象：国服 360 版客户端（J:\Games\World_of_Warships_CN360）+ 新抓 chat.deepseek.com 前端
> 方法：4 路并行——DeepSeek 新 bundle 静态比对 / 本地文件系统勘察 / PE 静态逆向（IDA 桥未连上，降级 pefile）/ 联网生态调研。

---

## 第一部分　DeepSeek 网页版（4.1 flash）兼容性核查

**总体结论：部分兼容，需小改，不用重写。** 核心协议骨架（路径 / PoW / 风控头 / SSE patch / 版本号）几乎没动；主要变化是「识图模式」并入了通用模型。

### 1.1 基本没动、可放心保留的部分
| 元素 | 项目现状 | 新版 bundle 实测 | 结论 |
|---|---|---|---|
| 5 个核心 API 路径 | `/chat_session/create`、`/create_pow_challenge`、`/file/upload_file`、`/file/fetch_files`、`/chat/completion` | 全部仍在，未改名 | ✅ |
| PoW 算法 | `DeepSeekHashV1`，prefix `${salt}_${expireAt}_` | 同名同 prefix，`wasm_solve` ABI 一致 | ✅ |
| PoW wasm | 内嵌 `sha3_wasm_bg.wasm` | 新下载 `sha3_wasm_bg.7b9ca65ddd.wasm` 与项目内嵌版 **SHA-256 完全相同**（26612 字节一字节不差） | ✅ 无需替换 |
| 风控头 | `hif-dliq/hif-leim` 双域名 + `x-hif-*` / `x-hif-ttl` | 全部保留 | ✅ |
| 请求头 | `x-client-version: 2.5.0`、`x-client-bundle-id: com.deepseek.chat`、`x-client-platform: web` | 大陆用户（is_mainland）bundle-id 仍为 `com.deepseek.chat`，版本号仍 `2.5.0` | ✅ |
| SSE 增量 | `{p,o,v}` patch，操作集 SET/BATCH/APPEND，`event: close`、`FINISHED`、`quasi_status` | delta schema 仍是 `{p,o,v}`，操作集不变 | ✅ |
| Token 刷新 | `data.biz_data.token` | 位置未变 | ✅ |

### 1.2 真正要改的点（按优先级）
1. **【头号风险，需实测】`model_type: "vision"` 很可能要改成 `"default"`。**
   - 证据：新 settings 里 `model_configs` 变为 `default / expert / vision` 三项，其中 vision 标记 `enabled:false / switchable:false`；banner 明写「快速、专家、识图模式已合并升级」；新建会话默认 `modelType:"default"`，识图能力下沉进 default。
   - 改两处：completion body 的 `model_type`（`DeepSeekVisionAnalyzer.cs` 约 227 行）与上传头 `x-model-type`（约 306 行）。
   - 「4.1 flash」在 bundle 里没有客户端枚举，是服务端路由的模型名，客户端无需关心。
2. **【中】SSE 新增 `event: ready`** 直接带 `response_message_id`；项目现在从 `v.response.message_id` 取 id，多轮追问（复用会话）可能断上下文。建议兼容从 ready 事件取 message_id。
3. **【中】completion body 新增可选 `source` 字段**（不传大概率不影响，建议实测）。
4. **【低】新增 `chat_hcaptcha` / AWS WAF 风控**（405 + 验证码），项目无解，至少给个明确报错文案。

### 1.3 不确定项
本次抓取只有 GET 静态资源、没有 POST 抓包，POST 响应形状是按「路径未改则形状大概率未变」推断的；`model_type:"vision"` 是否仍被服务端接受，静态看不出来——**建议把 `model_type` 改成 `"default"` 后实测一次**。

---

## 第二部分　战舰世界国服客户端：对局内信息渠道

**总前提（决定性）：WoWS 是服务器权威模型**——客户端只收到服务器「认为你该看到」的事件，敌舰只在被点亮时才下发位置。任何外部工具拿到的「敌船位」天然等于小地图所见，做不出真透视。

### 2.1 已确认的事实
- **原生客户端不开放任何对局中实时的本地 HTTP/WebSocket/管道接口。** 全量扫描 stock `scripts.zip`（5704 个 .pyc）无 `127.0.0.1 / HTTPServer / websocket / Flask` 命中；`localhost:7777 + PyWebView + WebSocket++` 是内嵌 CEF 浏览器/聊天，不是对局 API。
- **本机当前没有任何活跃 mod**：两个版本目录的 `res_mods` 都是空的；`Aslain_Modpack` 是装在早已废弃的 G 盘旧版本（12668862）残留，未生效。
- **反作弊（仅风险评估，非封号结论）**：静态扫描 7 个二进制未见 EAC / BattlEye / TenProtect / nProtect 等第三方反作弊或内核驱动痕迹；唯一 `anticheat_enabled` 邻域全是 WG 崩溃遥测（cat.wargaming.net）配置。但静态查不到服务端行为检测 / FairFight，不能据此说「官方不封」。国服执行更严（2026-07 有 Aslain 用户被封 7 天的一方说法）。
- `wg360_api.dll/exe` 走命名管道 `fa2df20f-...-pipe`，命令全是 WGC 登录鉴权/商城/覆盖层/观战，无战斗数据。

### 2.2 渠道评估（按价值排序）

| 渠道 | 能拿到什么 | 实时性 | 难度 | 跨版本稳定 | 风险 | 证据 |
|---|---|---|---|---|---|---|
| **① 实时 tail `.wowsreplay` 主体（二进制事件流）** | 逐 tick：船位/血量/击毁/烟雾/飞机/占点/**小地图 is_visible** | 对局中持续追加 | 中高（需逆向 BigWorld 包协议；已有开源解析器） | 中（包协议随版本可能变） | **低**（只读文件） | 回放头 `12 32 34 11` magic + 长度前缀 + arenaInfo JSON；头部后 ~1.17MB 是事件流。stock `scripts/wows_replays/` 文件名即事件类型：ShotEvents/HealthEvents(ShipDamage/ShipRegen)/ShipKill/SmokeEvent/PlaneEvents/VehicleEvents/VehicleVisibility/ConsumableUsage |
| **② 自写 res_mods Python mod + 本地端口** | 进程内订阅战斗回调（onArenaStateReceived / receiveDamageStat / updateMinimapVisionInfo）→ 任意结构化实时数据，可主动开本地端口吐给助手 | 对局中实时 | 中（社区 overlay 标准做法） | 低（BigWorld Python API 稳定） | **中**（改游戏文件/加载 mod，国服执行严） | 二进制内字面量 `import BigWorld, GUI, Math, Keys`；`res_mods\readme.txt` 原文 "Use this directory for custom game mods."；有 `--safemode` 无 mod 启动开关 |
| **③ tempArenaInfo.json（现状保留）** | 开局地图 + 双方阵容 {shipId, relation 阵营, id, name} | 开局写一次，对局中不刷新 | 已实现 | 高 | 低 | 字节头已逐字节验证（纯 JSON 首字节 0x7B；二进制包装偏移 8 读长度），与 ApeRadar 一致 |
| ④ Wargaming Public API | 局后/聚合战绩，无任何 live battle 接口；realm 仅 RU/EU/NA/Asia，不含 360 国服 | 局后 | 已接入 | 高 | 低 | 官方 API 文档 |
| ⑤ 360 国服 API | 社区共识：**不开放公开查询 API**；只有官方「战舰助手」APP 后端（api.wows.360.cn，无文档）+ 逆向的 shinoaki 类接口；国服与外服不互通 | 局后 | 已接入 shinoaki | 低 | 低 | NGA 社区共识 |
| ⑥ Overwolf | ~~WoWS 事件~~：mapName/players/己方血量/damage/death，**2024-06-16 已被 Overwolf 移除**；且即便活着也只有己方血量+静态阵容，无逐 tick 敌船；国服不支持 | — | — | — | — | **已淘汰** |
| ⑦ res_packages 静态数据 | 舰船/地图静态参数（知识库用） | 非实时 | 需解包 | 高 | 低 | .pkg 包 |
| ⑧ 进程注入 / ReadProcessMemory / Hook | 理论上任意数据 | 实时 | 高 | 低 | **高**（封号风险） | 不建议 |

**开源参考**：回放解析 `Monstrofil/replays_unpack`、`toalba/wows-replay-parser`（2026 仍活跃，提供 `state_at(t)` 快照）；wows-stats.com/agent 类做法是「开局读阵容、局后读完整回放上传」，宣称不注入/不改文件/不进游戏 overlay。

### 2.3 明确淘汰
- Overwolf（事件已死）
- WG 实时对局 API / spectator 推送（不存在）
- 进程注入 / 内存读取（高封号风险）

---

## 第三部分　对 WoWSBattleAssistant 的优先级建议

### A. DeepSeek 引擎（马上能做，小改）
1. 把 `model_type` 从 `"vision"` 改为 `"default"`（completion body + 上传头 `x-model-type` 两处），**实测一次**能否正常出图分析。
2. SSE 增加 `event: ready` 取 `response_message_id` 的兼容，保住追问续接。
3. completion body 补可选 `source` 字段；对 hcaptcha/405 给友好报错。
4. PoW / wasm / hif / 版本号 / bundle-id 全部不动。

### B. 提升对局中实时性（中期，按顺序）
1. **首选：实时 tail `.wowsreplay` 二进制事件流**（渠道①）。这是唯一「对局中 + 结构化船位/血量/视野/击毁/占点 + 只读文件低风险」的外部通道，且数据天然等于小地图所见、不构成透视。建议用 C# 起 Python 子进程跑 `wows-replay-parser`，**先实测对局中文件是否持续可追加、是否被引擎独占锁**。这能把现在「截一张小地图 OCR」升级成「持续精确船位/血量」，是分析准确度最大的一次提升。
2. **次选（若①文件追加不可行）**：写一个 `res_mods` Python mod，hook 战斗回调后开本地端口把实时数据吐给悬浮窗——数据最全、最准，但要接受「加载 mod」在国服的合规/封号中风险，且国服执行严，建议先用小号/测试。
3. **维持现状**：tempArenaInfo.json 继续只做开局阵容；战绩查询（shinoaki + WG 外服）维持不变。

### 风险提示
- 读文件 / tail 回放 / 截图 OCR = 低风险；加载 mod = 中风险；注入内存 / 自动瞄准 / 显示本不可见信息 = 高风险（ReShade 已实锤封号）。
- 以上反作弊/封号判断均为风险评估，非官方结论。
