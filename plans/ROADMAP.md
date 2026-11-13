# AgentCore Unity 路线图

> **版本**：以 `package.json` 为准（当前 1.18.0） | **更新**：2026-11-13
> 本文只写三件事：现状、主线、搁置项。完成的工作记在 `CHANGELOG.md`；1.18.0 之前的完整路线图（Phase 1~10 历程、ADR-1~19、风险评估）已归档到 [`_archive/roadmap/ROADMAP-v1.18.0-full.md`](_archive/roadmap/ROADMAP-v1.18.0-full.md)。

## 现状

- **定位**：Unity 编辑器内嵌的 agent。目标工程是企业级 Unity 2022.3 项目，版本控制以 SVN 为主（Git / Perforce 同样支持）；SVN 工作副本根是 WorkspaceRoot，Unity 工程是其下的子根。
- **已有能力**：约 65 个原生工具；多供应商 LLM（OpenAI 兼容 / 原生 Anthropic、prompt 缓存、备用 profile）；风险策略 + allow / ask / deny 权限规则 + 按消息检查点回退；SVN 全流程（`.meta` 跟随、锁、移动、Project 窗口角标）；代码符号索引 + ripgrep 文本搜索；会话、记忆、Skills。
- **质量**：Unity EditMode 测试 813 个，2026-11-13 结果为 812 通过 / 0 失败 / 1 个仅非 Windows 运行而跳过。
- **仍生效的原则**：ADR-17 极简（默认最优、少暴露选项）；审查级别完全由用户选择，YOLO 下不做任何阻断；有版本控制兜底，覆盖类操作执行而不报错；不打包第三方二进制。ADR 全文见归档文件 §4。
- **未发布**：main 领先 origin 约 140 个提交，团队尚未拿到 1.18.0。

## 主线

按顺序推进，前一项不完成不开始后一项的主体工作。

| 顺序 | 任务 | 完成标准 |
|------|------|----------|
| 1 | **发布 1.18.0 并让团队试用** | 推送；重新打 tgz 并通过 `tools/verify-tarball`、`tools/verify-meta`；在真实 SVN 工作副本手测：他人持锁的红色角标、新建文件夹提交带 `.meta`、更新冲突报告为 partial、externals 内 needs-lock 角标、`move_files` 后引用不断 |
| 2 | **按试用反馈修复** | 顺带完成：SVN 分支名格式统一；SVN 自由文本消息的路径改以 Unity 工程根为基准 |
| 3 | **MCP Server**（AgentCore 对外提供完整 MCP 能力） | 设计已定稿：[`mcp-server-design.md`](mcp-server-design.md)。relay 用 Unity 自带 Mono，默认跟随 AgentCore 审查级别，M1 验证 Claude Code 与 Codex。M1 拆为 5 张票：共享信任状态 → 编辑器桥接 → relay 与协议 → Domain Reload 恢复 → 接入配置与验收；M2 无状态协议 / Resources / Prompts / Elicitation，M3 Tasks 与本机 HTTP |

## 搁置项

| 项 | 原因 | 何时重新考虑 |
|----|------|--------------|
| AgentCore 作为 MCP 客户端（接入外部 MCP 服务） | 先把服务端做稳 | MCP Server M2 完成后 |
| `move_files` 扩展到 Git / Perforce、批量移动、移动后自动刷新 AssetDatabase | SVN 是主线，单路径已够用 | 试用中出现需求时 |
| SQLite 索引存储 | 需要下载原生库并在 Unity 内验证 | JSONL 索引出现性能瓶颈时 |
| `manage_file` 访问工程根以外的路径 | 是否放开未定 | 试用中出现需求时 |
| LightRAG 上传是否继续排除机密文件 | 当前行为保留，未决定是否改为确认 | 云端服务重新启用时 |
| Goal 模式（跨轮持久目标） | 没有实战反馈支撑 | 试用中出现“agent 忘了大任务”时 |
| 文档站、示例项目、Asset Store 分发 | 当前面向内部团队，只需 UPM 包 | 决定对外开源推广时 |
| 新增工具 | 65 个工具维护成本已高 | 有明确缺口且无法用现有工具组合时 |

## 维护规则

- 发版时同步 `package.json`、`CHANGELOG.md` 和本文的“现状”。
- 主线项完成后从表中删除（细节进 CHANGELOG），从搁置项提上来的要写明原因。
- 新的架构决策写成 `plans/adr-*.md` 独立文件，本文只链接。
