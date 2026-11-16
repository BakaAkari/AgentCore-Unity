# AgentCore Unity 路线图

> **版本**：以 `package.json` 为准（当前 1.20.2） | **更新**：2026-11-16
> 本文只写三件事：现状、主线、搁置项。完成的工作记在 `CHANGELOG.md`；1.18.0 之前的完整路线图（Phase 1~10 历程、ADR-1~19、风险评估）已归档到 [`_archive/roadmap/ROADMAP-v1.18.0-full.md`](_archive/roadmap/ROADMAP-v1.18.0-full.md)。

## 现状

- **定位**：Unity 编辑器内嵌的 agent。目标工程是企业级 Unity 2022.3 项目，版本控制以 SVN 为主（Git / Perforce 同样支持）；SVN 工作副本根是 WorkspaceRoot，Unity 工程是其下的子根。
- **已有能力**：60 个内置工具声明（其中 Native 目录 52 个；运行时数量随组件与配置变化）；多供应商 LLM（OpenAI 兼容 / 原生 Anthropic、prompt 缓存、备用 profile）；风险策略 + allow / ask / deny 权限规则 + 按消息检查点回退；SVN 全流程（`.meta` 跟随、锁、移动、Project 窗口角标）；代码符号索引 + ripgrep 文本搜索；会话、记忆、Skills；MCP Server（Claude Code / Codex 经 MCP 使用已启用工具（排除 Chat 专用辅助工具与外部 MCP 代理）、资源与 Skills prompt；2026-07-28 与 2025 系列协议、客户端内确认、Tasks、本机 HTTP 端点；跨 Domain Reload 不丢调用）；MCP 客户端（Chat 使用外部 MCP 服务器的工具）。
- **兼容修复**：1.20.2 将工具参数包装检测改用 `JObject.Property`，避免依赖宿主 Newtonsoft.Json 的 `ContainsKey` API；用户报错工程仍需安装后重新编译确认。
- **只读并发**：1.20.1 为同轮相邻的 `manage_file` 读取、目录枚举、文件信息与内容搜索启用最多 4 个并发；写入和其他工具为顺序屏障。索引 / VCS / 外部 MCP 查询尚未声明后台并发安全，保持串行。
- **质量**：Unity EditMode 测试 916 个，1.20.1 于 2026-11-14 的结果为 915 通过 / 0 失败 / 1 个仅非 Windows 运行而跳过。MCP M1~M3 与 MCP 客户端已在真实编辑器上用 Claude Code 2.1.305、Codex CLI 0.167 与验收脚本跑通（含改脚本触发编译与重载）。
- **仍生效的原则**：ADR-17 极简（默认最优、少暴露选项）；审查级别由用户选择；YOLO 绕过确认与 ask/deny 权限规则，当前代码仍执行 Play Mode 预检、参数校验和工具自身约束；有版本控制兜底，覆盖类操作执行而不报错；不打包第三方二进制。ADR 全文见归档文件 §4。
- **发布记录**：1.20.1 于 2026-11-15 推送至 origin/main（实现提交 `4d0ec4d`）；`com.agentcore.unity-1.20.1.tgz` 已生成，通过 verify-meta 与 verify-tarball（971 个条目）。此前 1.18.0 ~ 1.20.0 亦已推送。团队实际安装、大型工程试用与 SVN 工作副本验收仍待反馈，不能由“已推送”推断为已完成试用。

## 主线

按顺序推进，前一项不完成不开始后一项的主体工作。

| 顺序 | 任务 | 完成标准 |
|------|------|----------|
| 1 | **1.20.0 团队试用与 SVN 验收** | 在真实 SVN 工作副本手测：他人持锁的红色角标、新建文件夹提交带 `.meta`、更新冲突报告为 partial、externals 内 needs-lock 角标、`move_files` 后引用不断 |
| 2 | **按试用反馈修复** | 顺带完成：SVN 分支名格式统一；SVN 自由文本消息的路径改以 Unity 工程根为基准 |

## 搁置项

| 项 | 原因 | 何时重新考虑 |
|----|------|--------------|
| MCP 客户端的 OAuth 授权、外部服务器的 resources / prompts | 当前用 headers 填令牌即可，Chat 只用工具 | 接入需要 OAuth 的服务时 |
| `move_files` 扩展到 Git / Perforce、批量移动、移动后自动刷新 AssetDatabase | SVN 是主线，单路径已够用 | 试用中出现需求时 |
| SQLite 索引存储 | 需要下载原生库并在 Unity 内验证 | JSONL 索引出现性能瓶颈时 |
| `manage_file` 访问工程根以外的路径 | 是否放开未定 | 试用中出现需求时 |
| LightRAG 上传是否继续排除机密文件 | 当前行为保留，未决定是否改为确认 | 云端服务重新启用时 |
| Goal 模式（跨轮持久目标） | 没有实战反馈支撑 | 试用中出现“agent 忘了大任务”时 |
| 文档站、示例项目、Asset Store 分发 | 当前面向内部团队，只需 UPM 包 | 决定对外开源推广时 |
| 新增工具 | 现有内置工具维护成本已高 | 有明确缺口且无法用现有工具组合时 |

## 维护规则

- 发版时同步 `package.json`、`CHANGELOG.md` 和本文的“现状”。
- 主线项完成后从表中删除（细节进 CHANGELOG），从搁置项提上来的要写明原因。
- 新的架构决策写成 `plans/adr-*.md` 独立文件，本文只链接。
