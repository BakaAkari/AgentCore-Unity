# RagMem 部署实录与问题报告

> **读者**：负责维护本分发包的 LLM / 系统设计者
> **目的**：记录一次从零到端到端验证通过的完整部署过程，列出所有踩坑点和必须在分发包中预先修复的问题，使后续用户能通过 LLM 代理一次性快速部署。
> **部署环境**：Windows 10，WSL2 Ubuntu-24.04，Docker Engine 28.2.2（非 Docker Desktop），Ollama 本地运行，LiteLLM 远程代理
> **日期**：2026-04-02

---

## 1. 部署过程概述

按照 DEPLOY.md 的 LLM 自动部署流程执行。由于当前机器可联网且已有 WSL2/Docker 环境，阶段 A（联网准备）和阶段 B（部署）在同一台机器上完成。

最终服务状态：

| 容器 | 状态 | 端口 |
|------|------|------|
| ragmem-pgvector | healthy | 127.0.0.1:18930 |
| ragmem-mem0 | healthy | 0.0.0.0:18910 |
| ragmem-lightrag | healthy | 0.0.0.0:18920 |

端到端验证通过：`memory_add` 成功提取 4 条结构化记忆，`memory_search` 向量相似度检索正常返回，`memory_get_all` 列表查询正常。

---

## 2. 遇到的问题及解决方式（按时间顺序）

### 问题 1：`.env.example` 中 `LITELLM_BASE_URL` 末尾多了 `/v1`

**现象**：mem0 容器内 `OPENAI_BASE_URL` 变成 `http://172.16.249.43:8000/v1/v1`（双重 `/v1`），所有 LLM/Embedding 请求返回 404。

**根因**：`docker-compose.yml` 第 62 行写的是：
```yaml
OPENAI_BASE_URL: ${LITELLM_BASE_URL}/v1
```
而 `.env.example` 中 `LITELLM_BASE_URL` 已经包含了 `/v1`：
```
LITELLM_BASE_URL=http://172.16.249.43:8000/v1
```
两者拼接导致 `/v1/v1`。

**修复**：`.env` 中去掉末尾 `/v1`，改为 `LITELLM_BASE_URL=http://172.16.249.43:8000`。

**建议修复方式**：
- 方案 A（推荐）：修改 `.env.example`，去掉 `LITELLM_BASE_URL` 末尾的 `/v1`，并加注释说明 docker-compose.yml 会自动追加 `/v1`。
- 方案 B：修改 `docker-compose.yml`，去掉 `${LITELLM_BASE_URL}/v1` 中的 `/v1`，让用户在 `.env` 中填写完整 URL。
- 无论选哪种，两边必须对齐，且加醒目注释。

---

### 问题 2：mem0 镜像中 `main.py` 硬编码了 `gpt-4.1-nano-2025-04-14` 模型名

**现象**：mem0 调用 LLM 时报 `Invalid model name passed in model=gpt-4.1-nano-2025-04-14`。

**根因**：`prepare-images.sh` 构建 mem0-server 镜像时 `git clone --depth 1` 拉取的是 mem0 仓库最新代码。上游 mem0 在 `server/main.py` 的 `DEFAULT_CONFIG` 中把默认模型从 `gpt-4o` 改成了 `gpt-4.1-nano-2025-04-14`。`DEFAULT_CONFIG` 中 llm model 和 embedder model 都是字面量硬编码，完全忽略了 `LLM_MODEL` / `EMBEDDER_MODEL` 环境变量。

**修复**：创建 `mem0/main_override.py`，通过 volume 挂载覆盖容器内 `/app/main.py`，将所有配置项改为从环境变量读取。

**建议修复方式**：
- 在 `prepare-images.sh` 的 patch 阶段（已有 3 个 patch）新增一个 **Patch 4**，用 sed/python 将 `main.py` 中的硬编码模型名替换为 `os.environ.get("LLM_MODEL", "gpt-4o")` 和 `os.environ.get("EMBEDDER_MODEL", "text-embedding-3-small")`。这样 patch 会被 bake 进镜像，不再需要运行时 volume 覆盖。
- 或者直接在 `prepare-images.sh` 中维护一份 `main.py` 的完整替换版本。

---

### 问题 3：mem0 发送 `store` 参数，Anthropic 不支持

**现象**：`litellm.UnsupportedParamsError: anthropic does not support parameters: ['store']`。

**根因**：mem0 源码 `mem0/llms/openai.py` 第 129 行：
```python
openai_specific_generation_params = ["store"]
```
这个参数是 OpenAI 特有的（用于 OpenAI 的 stored completions 功能），但 mem0 对所有使用 openai provider 的请求都会附加此参数。当后端实际是 Anthropic（通过 LiteLLM 代理路由）时，此参数被拒绝。

**修复**：在 `entrypoint.sh` 中启动时 patch 该文件，将 `["store"]` 替换为 `[]`。

**建议修复方式**：
- 在 `prepare-images.sh` 新增 **Patch 5**，构建镜像时直接 patch 掉 `store` 参数。

---

### 问题 4：mem0 同时发送 `temperature` 和 `top_p`，Anthropic 拒绝

**现象**：`temperature and top_p cannot both be specified for this model`。

**根因**：mem0 源码 `mem0/llms/base.py` 第 127-132 行的 `_get_common_params` 方法：
```python
params = {
    "temperature": self.config.temperature,
    "max_tokens": self.config.max_tokens,
    "top_p": self.config.top_p,
}
```
总是同时发送 `temperature` 和 `top_p`。Anthropic API 要求两者只能取其一。

**修复**：在 `entrypoint.sh` 中启动时 patch，删除 `"top_p": self.config.top_p,` 这一行。

**建议修复方式**：
- 在 `prepare-images.sh` 新增 **Patch 6**，构建镜像时 patch 掉 `top_p`。

---

### 问题 5：分发包假设 Embedding 走 LiteLLM，但多数 LiteLLM 部署不含 Embedding 模型

**现象**：`Invalid model name passed in model=text-embedding-3-small`。LiteLLM 代理上只注册了 chat 模型，没有 embedding 模型。

**根因**：`docker-compose.yml` 和 `.env.example` 假设 embedding 和 LLM 使用同一个 LiteLLM 端点。但实际上很多 LiteLLM 部署（特别是只转发 Anthropic 的场景）不会注册 embedding 模型。

**本次解决方案**：使用用户本地 Windows 上已安装的 **Ollama + qwen3-embedding:4b** 作为 embedding 提供者。为此做了以下改动：
1. `.env` 新增 `OLLAMA_BASE_URL` 变量
2. `docker-compose.yml` 中 mem0 的 embedding 改用 ollama provider
3. `docker-compose.yml` 中 LightRAG 的 `EMBEDDING_BINDING` 改为 `ollama`，`EMBEDDING_BINDING_HOST` 指向 Ollama
4. 创建 `mem0/main_override.py` 将 embedder provider 从 `openai` 改为 `ollama`

**建议修复方式**：
- `.env.example` 应该明确区分 LLM 和 Embedding 的端点配置，增加 `OLLAMA_BASE_URL` 字段
- 在 DEPLOY.md 中增加 Embedding 来源的决策流程：先检查 LiteLLM 是否有 embedding 模型 → 否则检查本地 Ollama → 否则提示用户安装
- `docker-compose.yml` 应预置 ollama 作为 embedding 的默认 binding（而非假设一切走 LiteLLM）

---

### 问题 6：mem0 Docker 镜像中未预装 `ollama` Python 包

**现象**：`EOFError: EOF when reading a line`。mem0 的 `embeddings/ollama.py` 在导入时尝试 `input()` 交互式询问是否安装 ollama 包，容器中无 stdin 导致崩溃。

**根因**：mem0 镜像构建时只安装了 `requirements.txt` 中的依赖，ollama 不在其中（它是可选依赖）。

**修复**：在 `entrypoint.sh` 中 `pip install --quiet ollama`。

**建议修复方式**：
- 如果确定 ollama 是默认 embedding 方案，在 `prepare-images.sh` 构建 mem0-server 镜像时，往 `requirements.txt` 追加 `ollama>=0.4.0`，bake 进镜像。
- 这样可以省掉每次容器启动时的 pip install 延迟（约 5-10 秒）。

---

### 问题 7：pgvector HNSW 索引不支持超过 2000 维的向量

**现象**：`column cannot have more than 2000 dimensions for hnsw index`。

**根因**：`qwen3-embedding:4b` 输出 2560 维。pgvector 的 HNSW 索引最大支持 2000 维。mem0 的 pgvector 配置默认 `hnsw=True`。

**修复**：在 `main_override.py` 中根据 `EMBEDDING_DIM` 动态决定是否启用 HNSW：
```python
"hnsw": EMBEDDING_DIM <= 2000,
```

**建议修复方式**：
- 将此逻辑固化到分发包中。如果默认 embedding 模型维度超过 2000，HNSW 必须关闭。
- 在 `.env.example` 中注明维度与索引的关系。
- 如果性能是关注点，可考虑选用维度 <= 2000 的 embedding 模型（如 `nomic-embed-text` 768 维、`bge-m3` 1024 维等），或使用 pgvector 的 IVFFlat 索引。

---

### 问题 8：pgvector 表初始化维度不从环境变量读取

**现象**：即使 `.env` 中 `EMBEDDING_DIM=2560`，pgvector 仍按 1536 维建表，导致 `expected 1536 dimensions, not 2560`。

**根因**：mem0 原版 `main.py` 的 `DEFAULT_CONFIG` 中 `vector_store` 配置不包含 `embedding_model_dims` 字段，pgvector 默认使用 `PGVectorConfig` 中的 `embedding_model_dims=1536`。

**修复**：在 `main_override.py` 中显式传入 `"embedding_model_dims": EMBEDDING_DIM`。

**建议修复方式**：
- 与问题 2 统一处理。在镜像构建时 patch `main.py`，让 `embedding_model_dims` 从环境变量 `EMBEDDING_DIM` 读取。

---

### 问题 9：Ollama 默认只监听 127.0.0.1，WSL2/Docker 容器无法访问

**现象**：从 WSL2 内部 `curl http://172.26.96.1:11434` 超时或连接被拒绝。

**根因**：Windows 上 Ollama 默认只绑定 `127.0.0.1:11434`。WSL2 Docker 容器不在 Windows 的 localhost 网络中，需要通过宿主机 IP 访问，但 Ollama 不监听外部接口。

**修复**：需要用户手动操作：
1. 退出 Ollama
2. 设置环境变量 `OLLAMA_HOST=0.0.0.0`
3. 重启 Ollama

**建议修复方式**：
- 在 DEPLOY.md 的阶段 B 中（使用 Ollama embedding 之前），增加明确的检查步骤和用户提示：
  ```
  检查 Ollama 监听地址:
    netstat -an | findstr 11434
  如果显示 127.0.0.1:11434 而非 0.0.0.0:11434，提示用户执行以下操作...
  ```
- 在 `deploy.bat` 中加入自动检测逻辑。

---

### 问题 10：WSL2 网络拓扑 -- 确定正确的宿主机 IP

**现象**：WSL2 网关 IP `172.26.96.1`（来自 `/etc/resolv.conf`）无法连通 Ollama，但另一个网卡 IP `172.31.90.73` 可以。

**根因**：WSL2 有多种网络模式。在非镜像模式下，`resolv.conf` 中的 nameserver IP 不一定是所有服务的可达地址。实际可用 IP 取决于 Ollama 绑定的网卡和 Windows 防火墙规则。

**修复**：逐一尝试 Windows 的各网卡 IP，最终 `172.31.90.73` 可达。

**建议修复方式**：
- `deploy.bat` 或 LLM 部署流程中应包含自动探测逻辑：
  1. 获取所有 Windows IPv4 地址
  2. 从 WSL2 内部依次 `curl --connect-timeout 3 http://<IP>:11434/api/tags`
  3. 找到第一个可达的 IP 写入 `.env` 的 `OLLAMA_BASE_URL`
- 这个探测应在 `deploy.bat` 的 Phase 1 环境检查阶段自动完成。

---

## 3. 当前分发包中已修改/新增的文件清单

| 文件 | 状态 | 改动说明 |
|------|------|---------|
| `stack/.env` | 已修改 | `LITELLM_BASE_URL` 去掉 `/v1`；embedding 配置改为 Ollama |
| `stack/.env.example` | **未修改，但应更新** | 仍包含错误的 `/v1` 后缀和仅 LiteLLM 的 embedding 假设 |
| `stack/docker-compose.yml` | 已修改 | mem0 增加 entrypoint/volume 挂载；LightRAG embedding 改为 ollama binding |
| `stack/mem0/main_override.py` | **新增** | 替换容器内 main.py，embedder 改为 ollama，向量维度/hnsw 动态配置 |
| `stack/mem0/entrypoint.sh` | **新增** | 安装 ollama pip 包 + 运行时 patch store/top_p 兼容性问题 |
| `stack/mem0/config.yaml` | 未修改 | 仅文档，不影响运行 |
| `.vscode/mcp.json` | **新增** | VS Code Copilot 的 MCP 配置 |

AI 客户端 MCP 配置改动（系统级）：
- `~/.cursor/mcp.json` -- 新增 ragmem 服务
- `~/.claude.json` -- 当前项目下新增 ragmem 服务
- Roo Code `mcp_settings.json` -- ragmem 增加 `disabled: false` + `alwaysAllow`

---

## 4. 建议在下一版分发包中预先解决的事项

按优先级排序：

### P0 -- 阻断性问题（不修复则部署必然失败）

1. **修复 `.env.example` 中 `LITELLM_BASE_URL` 的 `/v1` 问题**（问题 1）
   - 去掉默认值末尾的 `/v1`，或修改 `docker-compose.yml` 不再追加 `/v1`
   - 加注释说明两者的拼接关系

2. **将 mem0 `main.py` 的 patch 固化到镜像构建流程中**（问题 2、8）
   - 在 `prepare-images.sh` 中新增 Patch 4：让 `DEFAULT_CONFIG` 从环境变量读取 `LLM_MODEL`、`EMBEDDER_MODEL`、`EMBEDDING_DIM`、`OLLAMA_BASE_URL`
   - 同时将 `embedding_model_dims` 和 `hnsw` 配置参数化
   - 这样就不需要 `main_override.py` 这个运行时 volume 覆盖了

3. **将 store/top_p patch 固化到镜像构建流程中**（问题 3、4）
   - 在 `prepare-images.sh` 中新增 Patch 5 和 Patch 6
   - 删除 `openai_specific_generation_params = ["store"]` 中的 `"store"`
   - 删除 `_get_common_params` 中的 `"top_p": self.config.top_p,`
   - 这些是 mem0 上游代码对非 OpenAI 后端的兼容性问题，只要 LLM 不是直连 OpenAI 就必须 patch

4. **在镜像中预装 `ollama` Python 包**（问题 6）
   - 在 `prepare-images.sh` 的 Patch 阶段追加 `echo "ollama>=0.4.0" >> requirements.txt`
   - 消除每次容器启动时的 pip install 延迟和网络依赖

### P1 -- 部署流程改进（不修复则需人工介入）

5. **Embedding 来源决策流程**（问题 5）
   - `.env.example` 增加 `OLLAMA_BASE_URL` 字段，注释说明 embedding 默认走 Ollama
   - `docker-compose.yml` 的 LightRAG 部分 `EMBEDDING_BINDING` 默认改为 `ollama`
   - DEPLOY.md 中增加 embedding 来源检测流程：
     ```
     检查 LiteLLM 是否有 embedding 模型 → 有则用 LiteLLM
     检查本地 Ollama 是否有 embedding 模型 → 有则用 Ollama
     都没有 → 提示用户安装 Ollama 并拉取 embedding 模型
     ```

6. **Ollama 监听地址检测与提示**（问题 9）
   - `deploy.bat` Phase 1 中检测 Ollama 端口绑定
   - 如果是 `127.0.0.1`，停止部署并给出明确操作指令

7. **自动探测 Ollama 可达 IP**（问题 10）
   - 在 WSL2 中遍历 Windows 的各 IPv4 地址，找到第一个能连通 Ollama 11434 端口的
   - 自动写入 `.env` 的 `OLLAMA_BASE_URL`

### P2 -- 稳健性改进

8. **pgvector HNSW 维度限制的文档和自动处理**（问题 7）
   - `.env.example` 中注明：`EMBEDDING_DIM > 2000 时 HNSW 索引将自动禁用`
   - 推荐默认 embedding 模型选用 <= 2000 维的（如 `nomic-embed-text:v1.5` 768 维）

9. **`deploy.bat` 的 `type file | wsl ... docker load` 不可靠**
   - 部署过程中发现 Windows `type` 命令管道传输 tar 文件到 WSL 时出现 `archive/tar: invalid tar header`
   - 改用 `wsl -d Ubuntu-24.04 -- docker load -i "/mnt/d/.../images/xxx.tar"` 直接从 WSL 可见的 Windows 路径加载，更可靠
   - 但要注意沙盘环境中 Windows 驱动器可能未挂载到 WSL2

10. **`update-config.bat` 需要同步新增文件**
    - 当前 `update-config.bat` 只同步 `.env`，不会同步 `main_override.py`、`entrypoint.sh`、`docker-compose.yml`
    - 如果用户修改了这些文件后执行 `update-config.bat`，变更不会生效

---

## 5. 推荐的分发包改造路线

### 短期（最小改动，修复阻断性问题）

```
prepare-images.sh 中新增 Patch 4-6：
  Patch 4: main.py 配置参数化（LLM_MODEL / EMBEDDER_MODEL / EMBEDDING_DIM / OLLAMA_BASE_URL / hnsw）
  Patch 5: 删除 openai.py 中的 store 参数
  Patch 6: 删除 base.py 中的 top_p 参数
  追加: requirements.txt 增加 ollama>=0.4.0

.env.example 修正：
  LITELLM_BASE_URL 去掉 /v1
  新增 OLLAMA_BASE_URL 字段
  EMBEDDING_MODEL / EMBEDDING_DIM 默认值改为 Ollama 模型

docker-compose.yml 修正：
  LightRAG EMBEDDING_BINDING 默认改为 ollama
  LightRAG EMBEDDING_BINDING_HOST 指向 OLLAMA_BASE_URL
  （可删除 mem0 的 entrypoint/volume 覆盖，因为 patch 已 bake 进镜像）
```

完成上述改动后，`mem0/main_override.py` 和 `mem0/entrypoint.sh` 就不再需要了——所有修复都在镜像构建时完成，运行时零 patch。

### 中期（改善部署体验）

- `deploy.bat` 增加 Ollama 监听地址检测、可达 IP 自动探测
- `deploy.bat` 增加 embedding 模型可用性检测
- DEPLOY.md 增加 Ollama embedding 的部署前置条件说明

---

## 6. 最终稳定运行的架构

```
Windows Host
├── Ollama (0.0.0.0:11434)
│   └── qwen3-embedding:4b (2560 dim)
│
├── LiteLLM Proxy (172.16.249.43:8000) [远程]
│   └── claude-opus-4-6 / claude-sonnet-4-5 / ...
│
└── WSL2 Ubuntu-24.04
    └── Docker Engine
        ├── ragmem-pgvector  (127.0.0.1:18930)  ← PostgreSQL + vector
        ├── ragmem-mem0      (0.0.0.0:18910)    ← Memory API
        │   ├── LLM → LiteLLM (Anthropic Claude)
        │   └── Embedding → Ollama (qwen3-embedding)
        └── ragmem-lightrag  (0.0.0.0:18920)    ← RAG API
            ├── LLM → LiteLLM (Anthropic Claude)
            └── Embedding → Ollama (qwen3-embedding)
```

数据流：
- `memory_add` → mem0 调用 Claude 提取事实 → 调用 Ollama 生成 embedding → 存入 pgvector
- `memory_search` → mem0 调用 Ollama 生成 query embedding → pgvector 向量检索
- `rag_query` → LightRAG 调用 Ollama 生成 query embedding → 本地向量检索 → Claude 生成回答
