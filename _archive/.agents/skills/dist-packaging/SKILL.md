# 分发打包技能

> 管理 `build-dist.bat` 的文件清单，确保分发包包含所有必要文件。

---

## 1. 目的

`build-dist.bat` 将项目中的可分发文件复制到 `dist/llm-ai-toolkit-YYYYMMDD/` 目录，供团队成员使用。本技能定义了如何维护文件清单。

---

## 2. 适用范围

- 新增、删除或重命名任何可分发文件
- 修改分发包的目录结构
- 更新分发包的版本号或命名规则

---

## 3. 触发场景

| 场景 | 操作 |
|------|------|
| 新增源码文件 | 在对应 Phase 添加 copy 命令 |
| 删除源码文件 | 移除对应的 copy 命令 |
| 重命名文件 | 更新 copy 命令中的路径 |
| 新增子目录 | 先 mkdir 再 copy |
| 新增顶层分发文件 | 可能需要新增 Phase |

---

## 4. 核心原则

### 4.1 分发包包含规则

**包含在分发包中的文件**：
- `DEPLOY.md` — 用户部署指南
- `local-ragmem/` — 完整的 RagMem 核心（源码 + 配置 + 镜像）
- `unity-agent-rules/` — Unity Agent Rules（含 `.agents/`、`.vscode/`、deploy 脚本）
- `unity-mcp-setup/` — Unity MCP 安装工具（packages、tools、docs）

**不包含在分发包中的文件**：
- `AGENTS.md` — AI Agent 规则（开发工具）
- `.agents/` — AI 技能和上下文（开发工具）
- `DEPLOYMENT-REVIEW.md` — 部署审查（开发过程产物）
- `DEPLOYMENT-REPORT.md` — 部署实录（开发过程产物）
- `.vscode/` — IDE 配置
- `.ruff_cache/` — Linter 缓存
- `build-dist.bat` 自身 — 打包工具不需要分发
- `clean-ragmem.bat` — 清理工具不需要分发

### 4.2 build-dist.bat 结构

```batch
REM [1/6] DEPLOY.md（根文件）
REM [2/6] local-ragmem/（核心目录）
REM   ├── mcp-server/（逐文件复制）
REM   ├── stack/（逐文件复制）
REM   ├── prepare-images.sh/bat
REM   ├── images/*.tar（条件复制）
REM   └── .gitattributes, .gitignore
REM [3/6] unity-agent-rules/（Agent Rules）
REM   ├── 根文件（AGENTS.md, README.md, .gitignore）
REM   ├── .agents/（xcopy 递归）
REM   ├── .vscode/mcp.json
REM   └── tools/（deploy-agent-rules.ps1, generate-snapshot.ps1）
REM [4/6] unity-mcp-setup/（MCP 安装工具）
REM   ├── README.md
REM   ├── packages/*.tgz + pypi-cache/*.whl
REM   ├── tools/*.ps1 + .json
REM   └── docs/*.md
REM [5/6] 验证（dir /s /b + 文件计数）
REM [6/6] 汇总
```

### 4.3 新增文件的操作模板

```batch
REM 新增单个文件
copy /Y "%SRC%路径\文件名" "%DIST%\路径\" >nul

REM 新增需要创建目录的文件
mkdir "%DIST%\新目录" >nul 2>nul
copy /Y "%SRC%新目录\文件名" "%DIST%\新目录\" >nul

REM 新增条件复制（如 .tar 文件）
if exist "%SRC%路径\*.ext" (
    copy /Y "%SRC%路径\*.ext" "%DIST%\路径\" >nul
)
```

### 4.4 步骤计数

- 当前为 `[1/5]` 到 `[5/5]`
- 如果新增顶层分类（Phase），需要更新所有步骤编号
- 验证和汇总始终是最后两步

---

## 5. 验证清单

修改 `build-dist.bat` 后，应验证：

1. 所有 `copy` 命令的源路径是否存在
2. 所有 `mkdir` 命令是否在 `copy` 之前
3. 步骤编号是否连续
4. 新增文件是否在验证步骤的 `dir /s /b` 输出中可见
5. `.agents/` 是否仍然被排除

---

## 6. 关联技能

- `script-sync` — 本技能是 script-sync 的子集，专注于 build-dist.bat
- `mcp-server-dev` — MCP Server 新增模块时触发
- `deployment` — 部署配置文件变更时触发
