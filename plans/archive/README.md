# Archived Plans

**保留原因**：这些文档记录了 v1.8.x - v1.11.x 时代对 "Editor 卡顿 / SSE stream 阻塞" 问题的调查、修复尝试和过渡方案。它们的结论**已被 v1.12.0-alpha.4 的实测数据推翻**：

- 真正的元凶是 `Editor/LLM/StreamingResponseParser.cs` 里 `while (!reader.EndOfStream)` 的同步阻塞（`EndOfStream` 属性 getter 在 `NetworkStream` 上会同步 peek 一字符，SSE 慢吐字下每次 ~7ms，一帧 28 次累计 199ms）。
- Silent 模式（S 按钮 / SessionMode）当初基于 "Chat UI 更新干扰 profiler 观测者效应" 的错误认知构建，Profiler 实测证明 UI 更新链在卡顿帧里全为 0。
- Silent 模式已在 v1.12.0-alpha.4 彻底删除，替代方案是修复 SSE loop 本身。

**这些文档不再作为架构参考**，仅作为历史决策路径存档。任何新的性能调查请参照 `CHANGELOG.md` v1.12.0-alpha.4 段落 + skill `agentcore-development/references/unity-editor-performance-diagnosis.md`（如已同步）。

## 归档文档

| 文件 | 时代 | 结论状态 |
|---|---|---|
| `perf-issue-agent-streaming-blocks-editor.md` | 早期 | **推翻** — 修复方向指向 UI 侧，实际元凶在 SSE 循环 |
| `perf-issue-editor-hang-during-agent-run-summary.md` | 中期 | **推翻** — 同上 |
| `v1.8.8-session-mode-handoff.md` | v1.8.8 | **推翻** — Silent 模式已删除 |
