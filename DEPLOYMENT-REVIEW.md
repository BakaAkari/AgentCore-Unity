# 部署报告审阅与修复计划

> **审阅者**：Roo（分发包原始设计者视角）
> **审阅对象**：`DEPLOYMENT-REPORT.md`（另一位 LLM 的部署实录）
> **日期**：2026-04-02

---

## 总体评价

这份部署报告质量很高，问题定位精准，根因分析到位，建议方案也合理。10 个问题中：

- **4 个是分发包自身的 bug**（问题 1、2、8、10 中的 `.env.example` 部分）
- **3 个是 mem0 上游代码对非 OpenAI 后端的兼容性缺陷**（问题 3、4、6）
- **2 个是部署环境假设不完整**（问题 5、9）
- **1 个是 WSL2 网络拓扑的固有复杂性**（问题 10）

核心结论我完全认同：**应将所有运行时 patch 固化到 `prepare-images.sh` 的镜像构建阶段**，让用户部署时零 patch。

---

## 逐项分析与我的判断

### 问题 1：`.env.example` 中 `LITELLM_BASE_URL` 末尾多了 `/v1`

**报告判断**：✅ 完全正确

**我的分析**：这是我的设计失误。[`docker-compose.yml`](local-ragmem/stack/docker-compose.yml:62) 第 62 行写了：
```yaml
OPENAI_BASE_URL: ${LITELLM_BASE_URL:-http://host.docker.internal:4000}/v1
```
而 [`.env.example`](local-ragmem/stack/.env.example:14) 第 14 行：
```
LITELLM_BASE_URL=http://172.16.249.43:8000/v1
```
两者拼接必然产生 `/v1/v1`。

**采纳方案**：方案 A（修改 `.env.example`，去掉 `/v1`，加注释说明 compose 会自动追加）。理由：
- `docker-compose.yml` 中追加 `/v1` 是有意为之——LiteLLM 的 base URL 本身不含 `/v1`，OpenAI SDK 需要 `/v1` 前缀
- LightRAG 的 `LLM_BINDING_HOST` 直接用 `${LITELLM_BASE_URL}`（不追加 `/v1`），说明 LightRAG 自己处理路径
- 所以 `.env` 中应该存储**不含 `/v1` 的裸地址**，由各服务按需追加

---

### 问题 2：mem0 `main.py` 硬编码模型名

**报告判断**：✅ 完全正确

**我的分析**：这是 mem0 上游的问题，但我在设计 `prepare-images.sh` 时应该预见到。当前 [`prepare-images.sh`](local-ragmem/prepare-images.sh:27) 第 27 行用 `git clone --depth 1` 拉最新代码，上游随时可能改默认模型名。

**采纳方案**：在 `prepare-images.sh` 新增 Patch 4，用 Python 脚本将 `DEFAULT_CONFIG` 中的硬编码值替换为 `os.environ.get()` 调用。这比维护完整的 `main.py` 替换版本更可维护——上游 API 路由变化时我们的 patch 仍然有效。

**额外考虑**：报告中部署者创建的 [`main_override.py`](local-ragmem/stack/mem0/main_override.py:1) 有 345 行，是完整的 FastAPI 应用。一旦 patch 固化到镜像，这个文件就不需要了，`docker-compose.yml` 中的 volume 挂载也可以删除。

---

### 问题 3 & 4：`store` 参数和 `top_p` 冲突

**报告判断**：✅ 完全正确

**我的分析**：这两个都是 mem0 上游代码假设后端是 OpenAI 的问题。只要 LLM 通过 LiteLLM 路由到非 OpenAI 模型（Anthropic、Qwen 等），就必然触发。

- `store` 参数：OpenAI 的 stored completions 功能，其他提供商不支持
- `top_p` + `temperature` 同时发送：Anthropic 明确拒绝

**采纳方案**：Patch 5 和 Patch 6 固化到 `prepare-images.sh`。这是最干净的方案——删除这两个参数对 OpenAI 用户也无实质影响（`store` 默认 false，`top_p` 默认 1.0 等于不生效）。

当前 [`entrypoint.sh`](local-ragmem/stack/mem0/entrypoint.sh:1) 中的运行时 patch 虽然能工作，但每次容器重启都要执行，且依赖 Python 包的安装路径（`/usr/local/lib/python3.12/site-packages/`），如果上游 Python 版本变化就会失败。

---

### 问题 5：Embedding 来源假设单一

**报告判断**：✅ 正确，但需要更深入讨论

**我的分析**：这是分发包设计中最大的架构假设问题。原始设计假设 LiteLLM 同时提供 LLM 和 Embedding，但实际上：

1. 很多 LiteLLM 部署只转发 chat 模型，不注册 embedding 模型
2. Anthropic 本身不提供 embedding API
3. 本地 Ollama 是最普遍可用的 embedding 来源

**我的决定**：

| 方面 | 原设计 | 改为 |
|------|--------|------|
| Embedding 默认来源 | LiteLLM（OpenAI 兼容） | **Ollama**（本地） |
| `.env.example` | 只有 `EMBEDDING_MODEL` | 新增 `OLLAMA_BASE_URL` |
| `docker-compose.yml` LightRAG | `EMBEDDING_BINDING: openai` | `EMBEDDING_BINDING: ollama` |
| `prepare-images.sh` mem0 | embedder provider = openai | embedder provider = **ollama** |

这意味着 Ollama 从"可选"变为"默认 embedding 提供者"。这是合理的——Ollama 在目标用户群体中几乎是标配。

**但有一个重要前提**：需要在 DEPLOY.md 中明确 Ollama 是前置依赖，并提供 embedding 模型推荐列表。

---

### 问题 6：mem0 镜像未预装 `ollama` Python 包

**报告判断**：✅ 完全正确

**我的分析**：如果 Ollama 成为默认 embedding 方案（问题 5 的结论），那么 `ollama` Python 包必须预装到镜像中。当前 [`entrypoint.sh`](local-ragmem/stack/mem0/entrypoint.sh:5) 第 5 行的 `pip install --quiet ollama` 有两个问题：

1. 每次容器启动延迟 5-10 秒
2. 离线环境（沙盘）中 pip install 会失败

**采纳方案**：在 `prepare-images.sh` 的 Patch 阶段追加 `echo "ollama>=0.4.0" >> requirements.txt`，bake 进镜像。

---

### 问题 7：pgvector HNSW 不支持 >2000 维

**报告判断**：✅ 正确

**我的分析**：这是一个需要权衡的问题：

| 方案 | 优点 | 缺点 |
|------|------|------|
| 动态禁用 HNSW（报告方案） | 兼容任意维度模型 | >2000 维时检索性能下降（退回顺序扫描） |
| 推荐 ≤2000 维模型 | 性能最优 | 限制了模型选择 |
| 使用 IVFFlat 索引 | 支持高维 + 有索引加速 | 需要定期 REINDEX |

**我的决定**：采用**双保险**策略：
1. `.env.example` 中默认推荐 `nomic-embed-text:v1.5`（768 维），注释中列出其他选项
2. `main.py` patch 中保留 `hnsw = EMBEDDING_DIM <= 2000` 的动态逻辑作为安全网
3. 文档中说明维度与索引的关系

---

### 问题 8：pgvector 表维度不从环境变量读取

**报告判断**：✅ 正确，与问题 2 统一处理

**我的分析**：这和问题 2 是同一个根因——`main.py` 的 `DEFAULT_CONFIG` 不读环境变量。Patch 4 统一解决。

---

### 问题 9：Ollama 默认只监听 127.0.0.1

**报告判断**：✅ 正确

**我的分析**：这是 Ollama 的默认行为，不是 bug。但对于 WSL2 Docker 容器访问 Windows 宿主机上的 Ollama，确实需要 Ollama 监听 `0.0.0.0`。

**采纳方案**：在 DEPLOY.md 和 `deploy.bat` 中增加检测和提示。但**不应自动修改用户的 Ollama 配置**——这涉及安全性（监听所有接口意味着局域网内其他机器也能访问）。

---

### 问题 10：WSL2 网络拓扑复杂性

**报告判断**：✅ 正确

**我的分析**：WSL2 的网络模式确实复杂。`/etc/resolv.conf` 中的 nameserver IP 不一定是服务可达地址。

**采纳方案**：在 `deploy.bat` 中增加自动探测逻辑。但需要注意：
- 探测应在 Ollama 确认监听 `0.0.0.0` 之后执行
- 应该用 `curl --connect-timeout 3` 而非 `ping`（ping 可能被防火墙拦截但 HTTP 端口开放）
- 找到可达 IP 后自动写入 `.env` 的 `OLLAMA_BASE_URL`

---

## 报告中未提及但我注意到的问题

### 问题 A：`deploy.bat` Phase 2 未同步 `main_override.py` 和 `entrypoint.sh`

当前 [`deploy.bat`](local-ragmem/stack/deploy.bat:230) Phase 2 只同步了 `docker-compose.yml`、`start.sh`、`mem0/config.yaml`、`.env`，但**没有同步** `mem0/main_override.py` 和 `mem0/entrypoint.sh`。

这意味着如果用户先运行 `deploy.bat`（此时 WSL2 中没有这两个文件），`docker-compose.yml` 中的 volume 挂载会失败（文件不存在）。

**如果 patch 固化到镜像**，这个问题自然消失——不再需要这两个文件。但在短期修复中，`deploy.bat` 必须同步这两个文件。

### 问题 B：`deploy.bat` Phase 3 的 `type ... | docker load` 不可靠

报告问题 9（P2）中提到了这个问题。[`deploy.bat`](local-ragmem/stack/deploy.bat:303) 第 303 行：
```bat
type "%%T" | wsl -d Ubuntu-24.04 --cd ~ -- bash -c "docker load"
```
Windows `type` 命令通过管道传输二进制 tar 文件到 WSL 时，可能因为 CR/LF 转换或缓冲区问题导致 tar 头损坏。应改为直接从 WSL 可见的 Windows 路径加载。

### 问题 C：`docker-compose.yml` 中 LightRAG 的 `LLM_BINDING_HOST` 缺少 `/v1`

[`docker-compose.yml`](local-ragmem/stack/docker-compose.yml:109) 第 109 行：
```yaml
LLM_BINDING_HOST: ${LITELLM_BASE_URL:-http://host.docker.internal:4000}
```
如果 `.env.example` 修复后 `LITELLM_BASE_URL` 不含 `/v1`，那 LightRAG 的 LLM binding host 也不含 `/v1`。需要确认 LightRAG 的 openai binding 是否自动追加 `/v1`。如果不是，这里也需要追加。

### 问题 D：`DEPLOY.md` 中 OpenCode MCP 配置模板格式错误 ✅ 已修复

[`DEPLOY.md`](DEPLOY.md) B5 节中给出的 OpenCode MCP 配置模板使用了与 Cursor/Claude Desktop 相同的 JSON 格式（`"mcp": { "name": { "command": ..., "args": [...] } }`），但 OpenCode 的 `opencode.json` 有自己独立的 schema：

- HTTP 类型：`type: "remote"` + `url`
- stdio 类型：`type: "local"` + `command`（数组格式，不是 `command` + `args` 分开）

直接使用 Cursor 格式会导致 `Invalid input` 错误，OpenCode 完全无法启动。

**修复**：
1. [`DEPLOY.md`](DEPLOY.md) 中 OpenCode 配置改为引用自动配置脚本
2. 新增 [`configure-opencode-mcp.ps1`](unity-mcp-setup/tools/configure-opencode-mcp.ps1) — 自动生成正确格式的 OpenCode MCP 配置
3. 修复了脚本的 PowerShell 5.1 兼容性问题（`-AsHashtable` 不可用）和 UTF-8 BOM / `\u003e` 转义问题

---

## 修复优先级与执行计划

### 第一批：P0 阻断性修复（固化到 `prepare-images.sh`） ✅ 已完成

| # | 修改文件 | 内容 | 状态 |
|---|---------|------|------|
| 1 | `prepare-images.sh` | 新增 Patch 4：`main.py` 配置参数化（`LLM_MODEL`、`EMBEDDER_PROVIDER`、`EMBEDDER_MODEL`、`EMBEDDING_DIM`、`OLLAMA_BASE_URL`、`hnsw` 动态）。默认值保持 openai，通过环境变量切换 provider，不影响灵活性 | ✅ |
| 2 | `prepare-images.sh` | 新增 Patch 5：删除 `openai.py` 中的 `store` 参数（注入 Dockerfile RUN 层） | ✅ |
| 3 | `prepare-images.sh` | 新增 Patch 6：删除 `base.py` 中的 `top_p` 参数（注入 Dockerfile RUN 层） | ✅ |
| 4 | `prepare-images.sh` | 追加 `ollama>=0.4.0` 到 `requirements.txt`（Patch 7） | ✅ |
| 5 | `.env.example` | `LITELLM_BASE_URL` 去掉 `/v1`；新增 `EMBEDDER_PROVIDER`、`EMBEDDING_HOST`、`OLLAMA_BASE_URL`；默认 embedding 改为 `ollama` + `nomic-embed-text:v1.5`（768 维） | ✅ |
| 6 | `docker-compose.yml` | 注释掉 `main_override.py`/`entrypoint.sh` 挂载和 entrypoint 覆盖（保留注释供深度定制）；新增 `EMBEDDER_PROVIDER`/`EMBEDDING_DIM`/`EMBEDDING_HOST` 环境变量传递；确认 LightRAG `LLM_BINDING_HOST` 不追加 `/v1`（LightRAG 内部处理） | ✅ |

### 第二批：P1 部署流程改进

| # | 修改文件 | 内容 |
|---|---------|------|
| 7 | `deploy.bat` | Phase 3 改用 WSL 路径直接 `docker load`，不走 `type` 管道 |
| 8 | `deploy.bat` | Phase 1 新增 Ollama 监听地址检测 + 可达 IP 自动探测 |
| 9 | `update-config.bat` | 同步所有配置文件（`docker-compose.yml`、`.env`），不只是 `.env` |
| 10 | `DEPLOY.md` | 新增 Ollama 前置条件说明、embedding 模型推荐、维度与索引关系 |

### 第三批：P2 清理

| # | 修改文件 | 内容 |
|---|---------|------|
| 11 | 删除 `mem0/main_override.py` | Patch 4 固化后不再需要 |
| 12 | 删除 `mem0/entrypoint.sh` | Patch 5/6 + ollama 预装后不再需要 |
| 13 | `deploy.bat` Phase 2 | 删除 `main_override.py` 和 `entrypoint.sh` 的同步逻辑 |

---

## 关于推荐的默认 Embedding 模型

报告中使用了 `qwen3-embedding:4b`（2560 维），触发了 HNSW 限制。我建议默认推荐：

| 模型 | 维度 | 大小 | HNSW 兼容 | 多语言 | 推荐度 |
|------|------|------|-----------|--------|--------|
| `nomic-embed-text:v1.5` | 768 | 274MB | ✅ | ✅ | ⭐⭐⭐ 首选 |
| `bge-m3` | 1024 | 1.2GB | ✅ | ✅ | ⭐⭐ |
| `mxbai-embed-large` | 1024 | 670MB | ✅ | 英文为主 | ⭐ |
| `qwen3-embedding:4b` | 2560 | 4.9GB | ❌ | ✅ | 不推荐作为默认 |

`.env.example` 中应默认设置 `EMBEDDING_MODEL=nomic-embed-text:v1.5` 和 `EMBEDDING_DIM=768`。

---

## 最终结论

报告中的 10 个问题全部确认有效，建议方案基本可以直接采纳。核心改造思路（将运行时 patch 固化到镜像构建阶段）是正确的方向。

**预计改动量**：
- `prepare-images.sh`：新增约 60 行（Patch 4-6 + ollama 依赖）
- `.env.example`：修改约 10 行
- `docker-compose.yml`：修改约 5 行（删除 entrypoint/volume 覆盖）
- `deploy.bat`：修改约 30 行（镜像加载方式 + Ollama 检测）
- `DEPLOY.md`：新增约 20 行（Ollama 前置条件）
- 删除 2 个文件（`main_override.py`、`entrypoint.sh`）

改造后的部署流程将变为：
1. 用户在联网机器运行 `prepare-images.sh`（所有 patch 在此阶段 bake 进镜像）
2. 拷贝到沙盘
3. 配置 `.env`（填 LiteLLM 地址 + Ollama 地址）
4. 运行 `deploy.bat`（零 patch，直接启动）
