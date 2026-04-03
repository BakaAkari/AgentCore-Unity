# Docker 镜像构建技能

> 管理 `prepare-images.sh` 中的补丁系统和 Docker 镜像构建流程。

---

## 1. 目的

`prepare-images.sh` 是 mem0 自定义镜像的构建脚本。它从官方 `mem0ai/mem0:latest` 镜像出发，通过一系列编号补丁注入修复和配置参数化，生成 `mem0-server:latest` 镜像。

本技能定义了如何安全地新增、修改补丁。

---

## 2. 适用范围

- 需要修复 mem0 源码中的 bug
- 需要参数化 mem0 的硬编码配置
- 需要添加 Python 依赖到 mem0 镜像
- 需要修改 Dockerfile 构建流程

---

## 3. 触发场景

| 场景 | 操作 |
|------|------|
| 发现 mem0 新 bug | 新增补丁修复 |
| 需要新的环境变量控制 | 修改 Patch 4（main.py 参数化） |
| 需要新的 Python 包 | 修改 Patch 7 或新增补丁 |
| 上游 mem0 镜像更新 | 验证所有补丁兼容性 |

---

## 4. 核心原则

### 4.1 补丁编号系统

```
格式：Patch N/M
  N = 当前补丁序号（从 1 开始，永不改变）
  M = 补丁总数（每次新增补丁时更新所有补丁的 M）
```

**当前补丁清单**（截至 2026-04-02）：

| 补丁 | 目标文件 | 说明 | 类型 |
|------|---------|------|------|
| Patch 1/7 | Dockerfile | 添加 `psycopg[binary]` | 依赖 |
| Patch 2/7 | Dockerfile | 移除 `graph_store` 代码 | Bug 修复 |
| Patch 3/7 | Dockerfile | 创建 `/app/data` 目录 | 环境 |
| Patch 4/7 | main.py | DEFAULT_CONFIG 参数化 | 配置 |
| Patch 5/7 | openai.py | 移除 `store` 参数 | Bug 修复 |
| Patch 6/7 | base.py | 移除 `top_p` 参数 | Bug 修复 |
| Patch 7/7 | requirements.txt | 添加 `ollama>=0.4.0` | 依赖 |

### 4.2 新增补丁流程

```
1. 确定补丁目的和目标文件
2. 选择注入方式：
   - Dockerfile RUN 层（适合修改已安装的 Python 包）
   - sed/python3 -c（适合修改源码文件）
   - heredoc 追加（适合添加新文件/内容）
3. 在 prepare-images.sh 末尾（docker build 之前）追加补丁
4. 更新所有补丁的总数 M
5. 更新文件末尾的 Summary 注释
6. 触发 script-sync 技能检查
```

### 4.3 补丁注入方式参考

**方式 A：sed 替换（简单文本替换）**
```bash
echo "--- Patch N/M: 描述 ---"
sed -i 's/旧文本/新文本/g' "$BUILD_DIR/目标文件"
```

**方式 B：Python 脚本注入（复杂逻辑）**
```bash
echo "--- Patch N/M: 描述 ---"
python3 -c "
import re
path = '$BUILD_DIR/目标文件'
with open(path) as f:
    content = f.read()
content = content.replace('旧内容', '新内容')
with open(path, 'w') as f:
    f.write(content)
"
```

**方式 C：Dockerfile RUN 层（修改已安装的包）**
```bash
echo "--- Patch N/M: 描述 ---"
cat >> "$BUILD_DIR/Dockerfile" << 'PATCH_EOF'
RUN python3 -c "
import site, os
for sp in site.getsitepackages():
    target = os.path.join(sp, 'package/module.py')
    if os.path.exists(target):
        # 修改逻辑
        break
"
PATCH_EOF
```

### 4.4 不修改已有补丁

- 已有补丁的编号和内容**不得修改**
- 如果需要修正已有补丁的 bug，新增一个补丁来覆盖
- 例外：更新补丁总数 M 不算修改

---

## 5. 环境变量与 Patch 4 的关系

Patch 4 是最复杂的补丁，它将 mem0 的 `DEFAULT_CONFIG` 参数化：

```python
# Patch 4 注入的环境变量读取
LLM_MODEL        = os.environ.get("LLM_MODEL", "gpt-4o-mini")
EMBEDDER_PROVIDER = os.environ.get("EMBEDDER_PROVIDER", "openai")
EMBEDDER_MODEL    = os.environ.get("EMBEDDER_MODEL", "text-embedding-3-small")
OLLAMA_BASE_URL   = os.environ.get("OLLAMA_BASE_URL", "http://host.docker.internal:11434")
EMBEDDING_DIM     = int(os.environ.get("EMBEDDING_DIM", "1536"))
```

如果需要新增可配置项，应修改 Patch 4 的内容（这是唯一允许修改已有补丁的例外情况，因为 Patch 4 本质上是一个配置模板）。

---

## 6. 构建产物

- 输入：`mem0ai/mem0:latest`（官方镜像）
- 输出：`mem0-server:latest`（自定义镜像）
- 导出：`local-ragmem/images/mem0-server.tar`

其他镜像（LightRAG、pgvector）直接 pull 并导出，不做修改。

---

## 7. 关联技能

- `script-sync` — 新增补丁后必须更新补丁总数和 Summary
- `deployment` — 镜像名/标签变更影响 deploy.bat
- `env-cleanup` — 镜像名变更影响 clean-ragmem.bat
