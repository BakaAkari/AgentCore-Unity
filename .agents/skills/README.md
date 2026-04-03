# LLM AI Toolkit `.agents/skills` 技能索引

> AI Agent 根据任务类型加载对应的 Skill 文件。
> 技能路由表见 `AGENTS.md` §7.2。

---

## 技能清单

### 基础设施

| 技能 | 路径 | 说明 |
|------|------|------|
| Docker 镜像构建 | `docker-image-build/SKILL.md` | prepare-images.sh 补丁系统 |
| 部署流程 | `deployment/SKILL.md` | deploy.bat、WSL2、Docker Compose |
| 环境清理 | `env-cleanup/SKILL.md` | clean-ragmem.bat 清理逻辑 |

### 开发

| 技能 | 路径 | 说明 |
|------|------|------|
| MCP Server 开发 | `mcp-server-dev/SKILL.md` | ragmem-mcp Python 包开发 |

### 自动化与分发

| 技能 | 路径 | 说明 |
|------|------|------|
| 分发打包 | `dist-packaging/SKILL.md` | build-dist.bat 文件清单管理 |
| **脚本同步** | `script-sync/SKILL.md` | **核心技能 — 自动化脚本与项目结构同步** |

---

## 按任务选择技能

| 我要做什么？ | 加载哪个技能？ |
|-------------|---------------|
| 给 mem0 镜像加补丁 | `docker-image-build` |
| 修改部署流程 | `deployment` |
| 清理测试环境 | `env-cleanup` |
| 给 MCP Server 加工具 | `mcp-server-dev` |
| 打包分发 | `dist-packaging` |
| 增删改了文件，需要更新脚本 | `script-sync` |

---

## 技能间关系

```
script-sync（核心）
    ├── 被 docker-image-build 触发（新增补丁 → 更新 prepare-images.sh 编号）
    ├── 被 dist-packaging 触发（文件变更 → 更新 build-dist.bat）
    ├── 被 env-cleanup 触发（服务变更 → 更新 clean-ragmem.bat）
    ├── 被 deployment 触发（部署变更 → 更新 deploy.bat）
    └── 被 mcp-server-dev 触发（包结构变更 → 更新 build-dist.bat + deploy.bat）
```

> ⚠️ `script-sync` 几乎在所有变更后都需要检查。它是本项目的"免疫系统"。

---

## 版本历史

| 版本 | 日期 | 变更 |
|------|------|------|
| 1.0.0 | 2026-04-02 | 初始版本，6 个技能 |
