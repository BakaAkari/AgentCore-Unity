# v1.10.0 开发工作目录

> **创建日期**: 2026-07-24
> **父文档**: [`../v1.10.0-handoff.md`](../v1.10.0-handoff.md)
> **状态**: 步骤 0 (前置准备) 进行中

本目录归档 v1.10.0 开发阶段所有 verification note / 反射探测 dump / gap 清单等中间产物，避免 Chat 记录丢失。

## 文件清单

| 文件 | 步骤 | 状态 |
|---|---|---|
| [`g04-reflection-probe-script.md`](g04-reflection-probe-script.md) | 步骤 0 前置 (G04 决策依据) | ✅ 已完成 |
| [`g04-reflection-probe-result.md`](g04-reflection-probe-result.md) | 步骤 0 输出 (含决策矩阵) | ✅ 已完成，待用户选路径 A/B/C |
| [`g05-physics-shallow-audit.md`](g05-physics-shallow-audit.md) | 步骤 0 前置 (G05 精确 gap) | ✅ 已完成 |
| `g10-urp-verification.md` | 步骤 1 输出 | ⏳ 待跑 |
| `g03-framedebugger-verification.md` | 步骤 1 输出 | ⏳ 待跑 |

## 步骤 0 目的

在编码前完成三件事，避免走到 v1.10.0 第 7 步才发现 G04 无法反射（历史事故 v1.7.26/27/G02 双开关翻车的教训）：

1. **G04 反射探测**：确认 `Unity.MemoryProfiler.Editor.CachedSnapshot` 类型可解析、方法签名可用；否则 G04 挪到 v1.11.0
2. **G05 shallow 精确清单**：读 [`ManagePhysicsTool.cs`](../../Editor/Tools/Native/Specialized/ManagePhysicsTool.cs) 现有 raycast/overlap_test 全文，输出必须新增/扩展的字段清单，避免实施时 "发明轮子"
3. **`plans/v1.10.0/` 目录建立**：文档多处引用但父目录不存在
