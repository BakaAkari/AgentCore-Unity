# MCP Server 开发技能

> 管理 `ragmem-mcp` Python 包的开发，包括工具定义、客户端封装和包结构。

---

## 1. 目的

`ragmem-mcp` 是连接 AI 客户端与 RagMem 服务栈的桥梁。它通过 MCP（Model Context Protocol）的 stdio 模式，将 mem0 和 LightRAG 的功能暴露为 AI 可调用的工具。

本技能定义了如何安全地开发和扩展 MCP Server。

---

## 2. 适用范围

- 新增 MCP 工具（tool）
- 修改现有工具的参数或行为
- 新增或修改 HTTP 客户端
- 修改包结构（pyproject.toml）
- 新增 Python 依赖

---

## 3. 触发场景

| 场景 | 操作 |
|------|------|
| 需要新的 AI 工具 | 在 server.py 中添加 `@mcp.tool()` 函数 |
| 需要访问新的后端 API | 新增或修改客户端模块 |
| 需要新的 Python 依赖 | 更新 pyproject.toml |
| 修改包名或入口点 | 更新 pyproject.toml + deploy.bat |

---

## 4. 核心原则

### 4.1 包结构

```
local-ragmem/mcp-server/
├── pyproject.toml              ← 包定义
├── README.md                   ← 使用说明
└── src/ragmem_mcp/
    ├── __init__.py             ← 版本号
    ├── server.py               ← MCP 工具定义（入口）
    ├── mem0_client.py          ← mem0 HTTP 客户端
    └── lightrag_client.py      ← LightRAG HTTP 客户端
```

### 4.2 工具定义模式

```python
@mcp.tool()
async def tool_name(
    required_param: str,
    optional_param: str | None = None,
) -> dict:
    """工具描述（会显示给 AI 客户端）。

    Args:
        required_param: 参数说明。
        optional_param: 可选参数说明。

    Returns:
        结果字典。
    """
    try:
        result = await client.some_method(required_param, optional_param)
        return result
    except Exception as e:
        return {"error": str(e)}
```

**规则**：
- 使用 `async def`
- 参数使用 Python 3.10+ 类型注解（`X | Y` 而非 `Optional[X]`）
- docstring 必须清晰描述工具用途（AI 客户端依赖此描述选择工具）
- 返回 `dict` 类型
- 捕获异常并返回结构化错误

### 4.3 当前工具清单

| 工具 | 客户端 | 说明 |
|------|--------|------|
| `memory_add` | mem0 | 存储记忆 |
| `memory_search` | mem0 | 语义搜索记忆 |
| `memory_list` | mem0 | 列出用户记忆 |
| `memory_delete` | mem0 | 删除记忆 |
| `rag_index_text` | LightRAG | 索引文本到知识库 |
| `rag_index_file` | LightRAG | 索引文件到知识库 |
| `rag_query` | LightRAG | 查询知识库 |
| `rag_list_documents` | LightRAG | 列出已索引文档 |
| `ragmem_health` | 两者 | 健康检查 |

### 4.4 客户端封装模式

```python
class ServiceClient:
    """Async HTTP client for 服务名 API."""

    def __init__(self, base_url: str):
        self.base_url = base_url.rstrip("/")

    @property
    def _client(self) -> httpx.AsyncClient:
        return httpx.AsyncClient(
            base_url=self.base_url,
            timeout=30.0,
        )

    async def method_name(self, param: str) -> dict[str, Any]:
        """方法说明。"""
        async with self._client as client:
            resp = await client.post("/endpoint", json={"key": param})
            resp.raise_for_status()
            return resp.json()
```

**规则**：
- 使用 `httpx.AsyncClient` 作为 HTTP 客户端
- 使用 `async with` 上下文管理器
- `base_url` 从环境变量读取，不硬编码
- 超时设置合理（默认 30s）

### 4.5 环境变量

```python
# server.py 中的环境变量读取
MEM0_BASE_URL = os.environ.get("MEM0_BASE_URL", "http://localhost:8080")
LIGHTRAG_BASE_URL = os.environ.get("LIGHTRAG_BASE_URL", "http://localhost:9621")
MEM0_API_KEY = os.environ.get("MEM0_API_KEY", "")
RAGMEM_USER_ID = os.environ.get("RAGMEM_USER_ID", "default")
```

新增后端服务时，添加对应的 `*_BASE_URL` 环境变量。

### 4.6 pyproject.toml 维护

```toml
[project]
name = "ragmem-mcp"
version = "0.1.0"
requires-python = ">=3.10"
dependencies = [
    "mcp[cli]>=1.0.0",
    "httpx>=0.27",
]

[project.scripts]
ragmem-mcp = "ragmem_mcp.server:main"
```

**新增依赖时**：
1. 添加到 `dependencies` 列表
2. 使用最小版本约束（`>=X.Y`）
3. 如果是可选依赖，考虑使用 `[project.optional-dependencies]`

---

## 5. 安装与运行

### 5.1 开发模式

```bash
cd local-ragmem/mcp-server
pip install -e .
ragmem-mcp
```

### 5.2 生产模式（uvx）

```bash
# deploy.bat Phase 4.5 中的安装方式
uvx --from /path/to/mcp-server ragmem-mcp
```

### 5.3 MCP 客户端配置

```json
{
  "ragmem": {
    "type": "stdio",
    "command": "wsl",
    "args": [
      "-d", "Ubuntu-24.04", "--",
      "bash", "-lc",
      "source ~/.local/bin/env 2>/dev/null; exec uvx --from ~/ragmem/mcp-server ragmem-mcp"
    ]
  }
}
```

---

## 6. 新增工具的完整流程

```
1. 确定工具的用途和参数
2. 如果需要新的后端 API：
   a. 在对应的 *_client.py 中添加方法
   b. 或创建新的客户端模块
3. 在 server.py 中添加 @mcp.tool() 函数
4. 更新 README.md 中的工具列表
5. 触发 script-sync 技能：
   - 如果新增了 Python 模块 → 更新 build-dist.bat
   - 如果新增了依赖 → 可能需要更新 deploy.bat
6. 更新 .agents/context/project-overview.md 中的工具清单
```

---

## 7. 关联技能

- `script-sync` — 包结构变更时触发
- `dist-packaging` — 新增模块时需要更新 build-dist.bat
- `deployment` — 安装方式变更时需要更新 deploy.bat
