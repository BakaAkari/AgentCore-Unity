# 版本控制系统集成设计方案

> **文档版本**: v1.1
> **创建日期**: 2026-05-18
> **更新日期**: 2026-05-18
> **状态**: 设计确认中
> **目标版本**: v0.5.4 (只读查询) + v0.6.0 (操作类 + UI)

---

## 用户需求确认

-  **VCS 优先级**: SVN > Perforce > Git
-  **Phase 1 范围**: 只读查询 actions + VersionControlPanel UI（状态显示、变更列表、提交历史）
-  **Phase 2 范围**: 操作类 actions（commit, branch, stash 等）+ 用户确认机制 + UI 操作按钮
-  **UI 需求**: 独立的 VersionControlPanel（类似 KnowledgeBasePanel 和 MemoryPanel）

**关键说明**：
- Phase 1 的只读 actions 既可以通过 Chat 对话调用（Agent 自动使用），也可以通过 VersionControlPanel UI 手动刷新查看
- VersionControlPanel 从 Phase 1 就开始实现，用于可视化显示 VCS 状态、变更列表、提交历史
- Phase 2 在 UI 中添加操作按钮（提交、切换分支等），这些按钮触发需要用户确认的操作类 actions

---

## 1. 需求概述

### 1.1 核心目标

让 AgentCore 能够获取版本控制系统（VCS）的几乎所有状态信息，使 Agent 能够：

1. **感知项目状态** — 当前分支、未提交修改、暂存区状态
2. **理解变更历史** — 提交记录、分支历史、标签
3. **辅助决策** — 基于 VCS 状态提供更智能的建议
4. **避免冲突** — 在修改文件前检查是否有未提交的更改
5. **跨 VCS 支持** — 统一接口支持 Git、Perforce、SVN

### 1.2 使用场景

| 场景 | Agent 行为 | VCS 能力需求 |
|------|-----------|-------------|
| **代码修改前检查** | "检测到 PlayerController.cs 有未提交的修改，是否需要先提交？" | `get_status` |
| **理解变更上下文** | "这个文件最近由 Alice 修改（3 天前），改动涉及输入系统重构" | `get_log`, `get_blame` |
| **分支管理建议** | "当前在 main 分支，建议创建 feature/new-ui 分支进行开发" | `get_branch`, `create_branch` |
| **冲突预警** | "检测到 5 个文件与远程分支有冲突，建议先 pull 再修改" | `get_status`, `get_remote` |
| **回滚建议** | "上次提交引入了编译错误，是否需要回滚到 commit abc123？" | `get_log`, `revert` |

---

## 2. 架构设计

### 2.1 工具命名与定位

**工具名称**: `manage_version_control`  
**分类**: `Native/Utility`  
**文件路径**: `Editor/Tools/Native/Utility/ManageVersionControlTool.cs`

### 2.2 多 VCS 支持策略

采用 **适配器模式** + **统一接口**：

```
ManageVersionControlTool (统一接口)
    ↓
VcsDetector (自动检测当前项目使用的 VCS)
    ↓
├─ SvnAdapter (svn CLI 调用) — 优先级 1
├─ PerforceAdapter (p4 CLI 调用) — 优先级 2
└─ GitAdapter (git CLI 调用) — 优先级 3
```

**检测逻辑**（按优先级顺序）:
1. 检查是否存在 `.svn/` → SVN（优先级最高）
2. 检查是否存在 `.p4config` 或 `P4CLIENT` 环境变量 → Perforce
3. 检查项目根目录是否存在 `.git/` → Git
4. 如果都不存在 → 返回 "No VCS detected"

**优先级说明**: 如果项目同时存在多个 VCS 标记（罕见情况），按 SVN > Perforce > Git 顺序选择。

### 2.3 实现方案对比

| 方案 | 优点 | 缺点 | 推荐度 |
|------|------|------|--------|
| **Unity VersionControl API** | 跨平台、统一接口 | 功能有限、依赖 Unity 版本 |  |
| **直接调用 CLI** | 功能完整、灵活 | 需要系统安装 VCS 工具 |  |
| **第三方库（LibGit2Sharp）** | 纯 C# 实现、无需 Git | 仅支持 Git、依赖管理复杂 |  |

**最终选择**: **直接调用 CLI**（方案 2）

**理由**:
- Git/Perforce/SVN 在开发环境中通常已安装
- CLI 提供最完整的功能
- 易于调试和维护
- 可通过 `Process.Start()` 统一调用

---

## 3. Actions 设计

### 3.1 Phase 1: 只读查询 Actions（v0.5.4）

> **范围**: 所有 VCS 支持的只读查询功能，不修改任何文件或状态。

| Action | 说明 | 参数 | 返回值 | Git 命令 | Perforce 命令 | SVN 命令 |
|--------|------|------|--------|---------|-----------|---------|
| **detect_vcs** | 检测当前项目使用的 VCS | 无 | `{ "vcs": "git/perforce/svn/none" }` | - | - | - |
| **get_status** | 获取工作区状态 | `verbose` (bool) | 未提交文件列表 | `git status --porcelain` | `p4 status` | `svn status` |
| **get_branch** | 获取当前分支和所有分支 | `include_remote` (bool) | 分支列表 | `git branch -a` | `p4 client -o` | `svn info` |
| **get_log** | 获取提交历史 | `max_count`, `since`, `author` | 提交记录列表 | `git log --oneline` | `p4 changes` | `svn log` |
| **get_diff** | 查看文件差异 | `file_path`, `target` | Diff 内容 | `git diff` | `p4 diff` | `svn diff` |

### 3.2 Git 只读查询 Actions（Phase 1）

| Action | 说明 | 参数 | 返回值 | Git 命令 |
|--------|------|------|--------|---------|
| **get_remote** | 获取远程仓库信息 | 无 | 远程仓库列表 | `git remote -v` |
| **get_tags** | 获取所有标签 | 无 | 标签列表 | `git tag -l` |
| **get_blame** | 查看文件逐行提交历史 | `file_path` | Blame 信息 | `git blame` |
| **get_commit_info** | 获取特定提交详情 | `commit_hash` | 提交详情 | `git show` |

### 3.3 Perforce 只读查询 Actions（Phase 1）

| Action | 说明 | 参数 | 返回值 | Perforce 命令 |
|--------|------|------|--------|--------------|
| **get_client_info** | 获取客户端工作区信息 | 无 | 客户端配置 | `p4 client -o` |
| **get_changelist** | 获取变更列表 | `changelist_id` | 变更详情 | `p4 describe` |

### 3.4 SVN 只读查询 Actions（Phase 1）

| Action | 说明 | 参数 | 返回值 | SVN 命令 |
|--------|------|------|--------|---------|
| **get_info** | 获取仓库信息 | 无 | 仓库 URL、版本号 | `svn info` |

### 3.5 Phase 2: 操作类 Actions（v0.6.0）

> **范围**: 修改文件、提交、分支管理等操作 — **所有操作需要用户确认**。

**Git 操作类**:
- `stage_files`, `unstage_files`, `commit`, `create_branch`, `switch_branch`, `stash`, `stash_pop`

**Perforce 操作类**:
- `checkout_files`, `revert_files`, `submit`, `sync`

**SVN 操作类**:
- `update`, `commit_svn`, `revert_svn`, `add_files`

**用户确认机制**:
```csharp
// 操作类 action 执行前，先返回确认请求
if (IsOperationAction(action))
{
    return ToolResponse.OkWithData(new {
        requires_confirmation = true,
        action = action,
        parameters = parameters,
        warning = "此操作将修改版本控制状态，是否继续？"
    }, "需要用户确认");
}
```

---

## 4. 参数 Schema 设计

### 4.1 Phase 1 Schema（只读查询）

```json
{
  "type": "object",
  "properties": {
    "action": {
      "type": "string",
      "enum": [
        "detect_vcs",
        "get_status",
        "get_branch",
        "get_log",
        "get_diff",
        "get_remote",
        "get_tags",
        "get_blame",
        "get_commit_info",
        "get_client_info",
        "get_changelist",
        "get_info"
      ],
      "description": "要执行的操作（Phase 1: 只读查询）"
    }
  },
  "required": ["action"]
}
```

### 4.2 Phase 2 Schema（操作类 — 需要用户确认）

```json
{
  "type": "object",
  "properties": {
    "action": {
      "type": "string",
      "enum": [
        "stage_files",
        "unstage_files",
        "commit",
        "create_branch",
        "switch_branch",
        "stash",
        "stash_pop",
        "checkout_files",
        "revert_files",
        "submit",
        "sync",
        "update",
        "commit_svn",
        "revert_svn",
        "add_files"
      ],
      "description": "要执行的操作（Phase 2: 操作类 — 需要用户确认）"
    },
    "confirmed": {
      "type": "boolean",
      "description": "用户是否已确认此操作"
    }
  },
  "required": ["action", "confirmed"]
}
```

### 4.3 各 Action 的参数示例

**get_status**:
```json
{
  "action": "get_status",
  "verbose": false
}
```

**get_log**:
```json
{
  "action": "get_log",
  "max_count": 10,
  "since": "2026-05-01",
  "author": "Alice"
}
```

**commit**:
```json
{
  "action": "commit",
  "message": "Fix: 修复 PlayerController 输入延迟问题",
  "author": "AgentCore <agent@unity.local>"
}
```

**get_diff**:
```json
{
  "action": "get_diff",
  "file_path": "Assets/Scripts/PlayerController.cs",
  "target": "HEAD"
}
```

---

## 5. 返回值设计

### 5.1 统一返回格式

所有 actions 返回 `ToolResponse`：

```csharp
// 成功
ToolResponse.OkWithData(new {
    vcs = "git",
    current_branch = "main",
    status = new {
        modified = new[] { "Assets/Scripts/PlayerController.cs" },
        staged = new[] { "Assets/Prefabs/Player.prefab" },
        untracked = new[] { "Assets/Temp.meta" }
    }
}, "获取状态成功");

// 失败
ToolResponse.Fail("Git 未安装或不在 PATH 中");
```

### 5.2 各 Action 返回值示例

**detect_vcs**:
```json
{
  "success": true,
  "data": {
    "vcs": "git",
    "version": "2.45.0",
    "root_path": "D:/Unity Project/unity-agent"
  }
}
```

**get_status**:
```json
{
  "success": true,
  "data": {
    "modified": [
      "Assets/Scripts/PlayerController.cs",
      "Assets/Scripts/GameManager.cs"
    ],
    "staged": [
      "Assets/Prefabs/Player.prefab"
    ],
    "untracked": [
      "Assets/Temp.meta"
    ],
    "deleted": [],
    "conflicted": []
  }
}
```

**get_log**:
```json
{
  "success": true,
  "data": {
    "commits": [
      {
        "hash": "abc123",
        "author": "Alice",
        "date": "2026-05-18T10:30:00Z",
        "message": "Fix: 修复输入延迟",
        "files_changed": 3
      },
      {
        "hash": "def456",
        "author": "Bob",
        "date": "2026-05-17T15:20:00Z",
        "message": "Feature: 添加新 UI 系统",
        "files_changed": 12
      }
    ],
    "total_count": 2
  }
}
```

**get_diff**:
```json
{
  "success": true,
  "data": {
    "file_path": "Assets/Scripts/PlayerController.cs",
    "diff": "@@ -45,7 +45,7 @@\n void Update()\n {\n-    float h = Input.GetAxis(\"Horizontal\");\n+    float h = Input.GetAxisRaw(\"Horizontal\");\n     transform.Translate(h * speed * Time.deltaTime, 0, 0);\n }",
    "additions": 1,
    "deletions": 1
  }
}
```

---

## 6. 实现细节

### 6.1 VCS 检测器

```csharp
public enum VcsType
{
    None,
    Git,
    Perforce,
    Svn
}

public static class VcsDetector
{
    public static VcsType DetectVcs(string projectPath)
    {
        if (Directory.Exists(Path.Combine(projectPath, ".git")))
            return VcsType.Git;
        
        if (File.Exists(Path.Combine(projectPath, ".p4config")) || 
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("P4CLIENT")))
            return VcsType.Perforce;
        
        if (Directory.Exists(Path.Combine(projectPath, ".svn")))
            return VcsType.Svn;
        
        return VcsType.None;
    }
}
```

### 6.2 CLI 调用封装

```csharp
public static class CliExecutor
{
    public static (bool success, string output, string error) Execute(
        string command, 
        string arguments, 
        string workingDirectory)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode == 0, output, error);
    }
}
```

### 6.3 适配器接口

```csharp
public interface IVcsAdapter
{
    VcsType Type { get; }
    bool IsAvailable();
    ToolResponse GetStatus(bool verbose);
    ToolResponse GetBranch(bool includeRemote);
    ToolResponse GetLog(int maxCount, string since, string author);
    ToolResponse GetDiff(string filePath, string target);
    // ... 其他方法
}
```

### 6.4 Git 适配器示例

```csharp
public class GitAdapter : IVcsAdapter
{
    private readonly string _projectPath;

    public VcsType Type => VcsType.Git;

    public bool IsAvailable()
    {
        var (success, _, _) = CliExecutor.Execute("git", "--version", _projectPath);
        return success;
    }

    public ToolResponse GetStatus(bool verbose)
    {
        var args = verbose ? "status" : "status --porcelain";
        var (success, output, error) = CliExecutor.Execute("git", args, _projectPath);

        if (!success)
            return ToolResponse.Fail($"Git 命令执行失败: {error}");

        var status = ParseGitStatus(output);
        return ToolResponse.OkWithData(status, "获取状态成功");
    }

    private object ParseGitStatus(string output)
    {
        // 解析 git status --porcelain 输出
        // 格式: XY filename
        // X = 暂存区状态, Y = 工作区状态
        // M = modified, A = added, D = deleted, ?? = untracked
        
        var modified = new List<string>();
        var staged = new List<string>();
        var untracked = new List<string>();
        var deleted = new List<string>();

        foreach (var line in output.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var status = line.Substring(0, 2);
            var file = line.Substring(3);

            if (status[0] != ' ') staged.Add(file);
            if (status[1] == 'M') modified.Add(file);
            if (status[1] == 'D') deleted.Add(file);
            if (status == "??") untracked.Add(file);
        }

        return new { modified, staged, untracked, deleted };
    }
}
```

---

## 7. 安全性与权限控制

### 7.1 危险操作保护

以下操作标记为 `MayModifyScripts = true`（触发编译监控）：

- `commit` / `commit_svn` / `submit`
- `switch_branch`
- `revert_files` / `revert_svn`
- `stash_pop`

### 7.2 操作确认机制

对于破坏性操作，建议在 SOUL.md 中添加规则：

```markdown
## VCS 操作规则

1. **提交前确认**: 执行 `commit` 前，先用 `get_status` 确认要提交的文件
2. **分支切换警告**: 切换分支前，检查是否有未提交的修改
3. **还原操作确认**: 执行 `revert` 前，询问用户确认
4. **禁止强制操作**: 不使用 `git reset --hard` 等强制命令
```

### 7.3 错误处理

```csharp
public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken ct)
{
    var sw = Stopwatch.StartNew();
    try
    {
        // 1. 检测 VCS
        var vcsType = VcsDetector.DetectVcs(Application.dataPath);
        if (vcsType == VcsType.None)
            return Task.FromResult(ToolResponse.Fail("未检测到版本控制系统").ToToolResult(0));

        // 2. 创建适配器
        IVcsAdapter adapter = vcsType switch
        {
            VcsType.Git => new GitAdapter(Application.dataPath),
            VcsType.Perforce => new PerforceAdapter(Application.dataPath),
            VcsType.Svn => new SvnAdapter(Application.dataPath),
            _ => null
        };

        // 3. 检查可用性
        if (!adapter.IsAvailable())
            return Task.FromResult(ToolResponse.Fail($"{vcsType} 未安装或不在 PATH 中").ToToolResult(0));

        // 4. 执行操作
        var action = ToolHelpers.GetRequiredString(parameters, "action");
        var response = action switch
        {
            "detect_vcs" => HandleDetectVcs(),
            "get_status" => adapter.GetStatus(ToolHelpers.GetOptionalBool(parameters, "verbose", false)),
            "get_branch" => adapter.GetBranch(ToolHelpers.GetOptionalBool(parameters, "include_remote", false)),
            // ... 其他 actions
            _ => ToolResponse.Fail($"未知操作: {action}")
        };

        sw.Stop();
        return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
    }
    catch (Exception ex)
    {
        sw.Stop();
        return Task.FromResult(ToolResponse.Fail($"执行失败: {ex.Message}").ToToolResult(sw.Elapsed.TotalMilliseconds));
    }
}
```

---

## 8. 测试计划

### 8.1 单元测试

创建 `Editor/Tests/Tools/ManageVersionControlToolTests.cs`：

```csharp
[Test]
public void TestVcsDetection_Git()
{
    var vcs = VcsDetector.DetectVcs(Application.dataPath);
    Assert.AreEqual(VcsType.Git, vcs);
}

[Test]
public void TestGitStatus_ParsesCorrectly()
{
    var adapter = new GitAdapter(Application.dataPath);
    var response = adapter.GetStatus(false);
    Assert.IsTrue(response.Success);
}
```

### 8.2 集成测试

| 测试场景 | 预期结果 |
|---------|---------|
| 在 Git 项目中调用 `detect_vcs` | 返回 `"git"` |
| 在无 VCS 项目中调用 `get_status` | 返回错误 "未检测到版本控制系统" |
| 调用 `get_log` 获取最近 5 条提交 | 返回最多 5 条提交记录 |
| 调用 `get_diff` 查看未提交文件差异 | 返回 diff 内容 |
| 在 Perforce 项目中调用 Git 专属 action | 返回错误 "当前 VCS 不支持此操作" |

### 8.3 手动测试清单

- [ ] Git 项目：`get_status`, `get_log`, `get_diff`, `get_branch`
- [ ] Perforce 项目：`get_status`, `get_changelist`, `checkout_files`
- [ ] SVN 项目：`get_status`, `get_log`, `get_info`
- [ ] 无 VCS 项目：所有操作返回友好错误
- [ ] Git 未安装：返回 "Git 未安装或不在 PATH 中"

---

## 9. 性能考虑

### 9.1 缓存策略

某些操作结果可以缓存（短时间内不会变化）：

- `detect_vcs` — 缓存 5 分钟
- `get_branch` — 缓存 1 分钟
- `get_remote` — 缓存 5 分钟

```csharp
private static readonly Dictionary<string, (DateTime expiry, object data)> _cache = new();

private static T GetCached<T>(string key, TimeSpan ttl, Func<T> factory)
{
    if (_cache.TryGetValue(key, out var cached) && cached.expiry > DateTime.Now)
        return (T)cached.data;

    var data = factory();
    _cache[key] = (DateTime.Now + ttl, data);
    return data;
}
```

### 9.2 异步执行

CLI 调用可能耗时较长（特别是 `get_log` 和 `get_diff`），建议：

- 设置超时时间（默认 30 秒）
- 大型操作支持分页（如 `get_log` 的 `max_count` 参数）
- 提供进度反馈（如果可能）

---

## 10. ROADMAP 集成

### 10.1 建议的 Phase 和版本

**Phase 6.4 — 版本控制集成** (v0.5.4 或 v0.6.0)

| # | 任务 | 说明 | 优先级 | 状态 |
|---|------|------|--------|------|
| 6.4.1 | **VCS 检测与适配器架构** | 实现 VcsDetector + IVcsAdapter 接口 + CliExecutor | P0 | [ ] |
| 6.4.2 | **Git 基础支持** | 实现 GitAdapter（get_status, get_branch, get_log, get_diff） | P0 | [ ] |
| 6.4.3 | **Git 高级支持** | 实现 Git 操作类 actions（commit, branch, stash） | P1 | [ ] |
| 6.4.4 | **Perforce 基础支持** | 实现 PerforceAdapter（get_status, get_changelist, checkout_files） | P1 | [ ] |
| 6.4.5 | **SVN 基础支持** | 实现 SvnAdapter（get_status, get_log, get_info） | P2 | [ ] |
| 6.4.6 | **SOUL.md VCS 规则** | 添加 VCS 操作规则和最佳实践 | P1 | [ ] |
| 6.4.7 | **测试覆盖** | 单元测试 + 集成测试 | P1 | [ ] |

### 10.2 里程碑规划

```
v0.5.4 — 版本控制集成 Phase 1（Git 基础支持）
  ├─ VCS 检测与适配器架构
  ├─ Git 基础查询（status, branch, log, diff）
  └─ SOUL.md VCS 规则

v0.5.5 — 版本控制集成 Phase 2（Git 高级 + Perforce）
  ├─ Git 操作类 actions（commit, branch, stash）
  ├─ Perforce 基础支持
  └─ 测试覆盖

v0.6.0 — 版本控制集成 Phase 3（SVN + 完善）
  ├─ SVN 基础支持
  ├─ 缓存优化
  └─ 文档完善
```

---

## 8. VersionControlPanel UI 设计

> 参考 `KnowledgeBasePanel` 和 `MemoryPanel` 的设计模式，提供独立的版本控制可视化界面。

### 8.1 UI 布局结构

```
┌─────────────────────────────────────────────┐
│ Version Control                    [标题]   │
├─────────────────────────────────────────────┤
│ ┌─ 状态 ─────────────────────────────────┐ │
│ │ VCS 类型: Git                          │ │
│ │ 当前分支: main                         │ │
│ │ 未提交变更: 3 个文件                   │ │
│ │ [刷新状态] [打开设置]                  │ │
│ └────────────────────────────────────────┘ │
├─────────────────────────────────────────────┤
│ ┌─ 未提交变更 ──────────────── [刷新] ─┐ │
│ │ ┌──────────────────────────────────┐ │ │
│ │ │ M  Assets/Scripts/Player.cs      │ │ │
│ │ │ A  Assets/Prefabs/Enemy.prefab   │ │ │
│ │ │ D  Assets/Textures/old.png       │ │ │
│ │ └──────────────────────────────────┘ │ │
│ │ 3 个文件待提交                        │ │
│ └────────────────────────────────────────┘ │
├─────────────────────────────────────────────┤
│ ┌─ 提交历史 ──────────────────── [刷新] ─┐ │
│ │ ┌──────────────────────────────────┐ │ │
│ │ │ abc1234 - Fix player movement    │ │ │
│ │ │   2 hours ago by John            │ │ │
│ │ │ ─────────────────────────────    │ │ │
│ │ │ def5678 - Add enemy AI           │ │ │
│ │ │   1 day ago by Jane              │ │ │
│ │ └──────────────────────────────────┘ │ │
│ │ 显示最近 10 条提交                    │ │
│ └────────────────────────────────────────┘ │
├─────────────────────────────────────────────┤
│ ┌─ 快速操作 ─────────────────────────────┐ │
│ │ [查看差异] [提交变更] [切换分支]      │ │
│ └────────────────────────────────────────┘ │
└─────────────────────────────────────────────┘
```

### 8.2 核心功能

| 功能区 | 功能描述 | 实现方式 |
|--------|---------|---------|
| **状态区** | 显示 VCS 类型、当前分支、未提交变更数量 | 调用 `detect_vcs`, `get_branch`, `get_status` |
| **未提交变更区** | 列表显示所有未提交的文件及其状态（M/A/D/R） | 调用 `get_status`，解析输出 |
| **提交历史区** | 显示最近 N 条提交记录（hash, message, author, time） | 调用 `get_log`，限制条数 |
| **快速操作区** | 提供常用操作按钮，点击后触发 Agent 执行 | 通过事件触发 `OnVcsActionRequested` |

### 8.3 UI 交互流程

```
用户打开 VersionControlPanel
  ↓
OnActivated() 自动刷新状态
  ↓
显示 VCS 类型、分支、变更数量
  ↓
用户点击「刷新」按钮
  ↓
调用 VersionControlTool 的只读 actions
  ↓
更新 UI 显示
  ↓
用户点击「提交变更」按钮
  ↓
触发 OnVcsActionRequested 事件
  ↓
ChatWindow 切换到 Chat 模块
  ↓
填充提示词："提交当前所有变更，请帮我生成合适的提交信息"
  ↓
Agent 执行 commit action（需用户确认）
```

### 8.4 用户确认对话框设计

Phase 2 中，所有操作类 action 需要用户确认。设计如下：

```csharp
// 在 VersionControlTool.cs 中

private ToolResponse ShowConfirmationDialog(string action, JObject parameters)
{
    string message = BuildConfirmationMessage(action, parameters);
    
    bool confirmed = EditorUtility.DisplayDialog(
        "确认 VCS 操作",
        message,
        "确认执行",
        "取消"
    );
    
    if (!confirmed)
    {
        return ToolResponse.Fail("用户取消了操作");
    }
    
    // 继续执行实际操作
    return ExecuteConfirmedAction(action, parameters);
}

private string BuildConfirmationMessage(string action, JObject parameters)
{
    switch (action)
    {
        case "commit":
            string message = ToolHelpers.GetRequiredString(parameters, "message");
            var files = parameters["files"]?.ToObject<List<string>>() ?? new List<string>();
            return $"即将提交以下变更：\n\n" +
                   $"提交信息：{message}\n" +
                   $"文件数量：{files.Count}\n\n" +
                   $"此操作将修改版本控制历史，是否继续？";
        
        case "create_branch":
            string branchName = ToolHelpers.GetRequiredString(parameters, "branch_name");
            return $"即将创建新分支：{branchName}\n\n是否继续？";
        
        case "switch_branch":
            string targetBranch = ToolHelpers.GetRequiredString(parameters, "branch_name");
            return $"即将切换到分支：{targetBranch}\n\n" +
                   $"请确保当前变更已提交或暂存，是否继续？";
        
        default:
            return $"即将执行 VCS 操作：{action}\n\n是否继续？";
    }
}
```

### 8.5 集成到 ChatWindow.Hub.cs

```csharp
// 在 ChatWindow.Hub.cs 中添加 VersionControl 模块

private VersionControlPanel _versionControlPanel;

private void BuildHubUI()
{
    // ... 现有代码 ...
    
    // 添加 VersionControl 按钮到 HubRail
    _hubRail.AddModule("versioncontrol", "Version Control", OnVersionControlModuleSelected);
    
    // 创建 VersionControlPanel
    _versionControlPanel = new VersionControlPanel();
    _versionControlPanel.style.display = DisplayStyle.None;
    _versionControlPanel.OnVcsActionRequested += OnVcsActionRequested;
    _hubContentArea.Add(_versionControlPanel);
}

private void OnVersionControlModuleSelected()
{
    // 隐藏其他面板
    _chatPanel.style.display = DisplayStyle.None;
    _knowledgeBasePanel.style.display = DisplayStyle.None;
    _memoryPanel.style.display = DisplayStyle.None;
    
    // 显示 VersionControlPanel
    _versionControlPanel.style.display = DisplayStyle.Flex;
    _versionControlPanel.OnActivated();
}

private void OnVcsActionRequested(string prompt)
{
    // 切换回 Chat 模块并填充提示词
    OnChatModuleSelected();
    _inputField.value = prompt;
    _inputField.Focus();
}
```

### 8.6 样式设计要点

参考 `KnowledgeBasePanel` 和 `MemoryPanel` 的样式模式：

- **统一的 section 样式** — 使用 `vcs-panel__section` 类
- **按钮样式分级** — primary（蓝色）、accent（橙色）、secondary（灰色）、small（小尺寸）
- **状态徽章** — 使用不同颜色表示文件状态（M=黄色, A=绿色, D=红色）
- **ScrollView 限高** — 变更列表和提交历史使用 `max-height: 200px`
- **响应式布局** — 使用 `flex-grow` 和 `flex-shrink` 适应窗口大小

---

## 9. 测试验证清单

### 9.1 Phase 1 测试（v0.5.4）

| 测试项 | 验证内容 |
|--------|---------|
| **VCS 检测** | 正确识别 SVN > Perforce > Git 优先级 |
| **Git 查询** | get_status, get_branch, get_log, get_diff, get_remote, get_tags 正常工作 |
| **Perforce 查询** | get_client_info, get_changelist, get_status 正常工作 |
| **SVN 查询** | get_info, get_status, get_log 正常工作 |
| **错误处理** | VCS 未安装、命令失败、权限不足时返回清晰错误 |
| **路径安全** | 只能操作项目根目录内的文件 |

### 9.2 Phase 2 测试（v0.6.0）

| 测试项 | 验证内容 |
|--------|---------|
| **用户确认** | 所有操作类 action 必须经过用户确认 |
| **Git 操作** | commit, create_branch, switch_branch, stash, checkout_files, revert_files 正常工作 |
| **Perforce 操作** | submit, sync, revert_files 正常工作 |
| **SVN 操作** | commit_svn, update, revert_svn, add_files 正常工作 |
| **Domain Reload** | 操作过程中触发编译，恢复后正确处理 |
| **并发安全** | 多个操作不会相互干扰 |
| **UI 功能** | VersionControlPanel 正确显示状态、变更、历史 |
| **UI 交互** | 快速操作按钮正确触发 Agent 执行 |
| **UI 性能** | 大量文件时列表渲染流畅（考虑虚拟化） |

---

## 10. 里程碑与交付物

### 10.1 Phase 1 交付物（v0.5.4）— 只读查询 + UI 可视化

**后端工具**：
- [ ] `Editor/Tools/Native/VersionControl/` 目录结构
- [ ] `VcsDetector.cs` — VCS 检测逻辑
- [ ] `IVcsAdapter.cs` + 三个 Adapter 实现（Git, Perforce, SVN）
- [ ] `VersionControlTool.cs` — 工具入口（只读 actions）
- [ ] 单元测试（VCS 检测、命令构建、输出解析）

**前端 UI**：
- [ ] `VersionControlPanel.cs` — UI 组件（状态区、变更列表区、提交历史区）
- [ ] `VersionControlPanel.uss` — 样式文件
- [ ] 集成到 `ChatWindow.Hub.cs`（添加 VersionControl 模块）
- [ ] UI 数据刷新逻辑（调用只读 actions 获取数据）

**文档**：
- [ ] 更新 `CHANGELOG.md` 和 `ROADMAP.md`
- [ ] 更新 `TOOLS.md.template`（添加工具使用说明）

### 10.2 Phase 2 交付物（v0.6.0）— 操作类 actions + 用户确认

**后端工具**：
- [ ] `VersionControlTool.cs` — 添加操作类 actions（commit, branch, stash 等）
- [ ] 用户确认机制（`EditorUtility.DisplayDialog`）
- [ ] `BuildConfirmationMessage` 方法（为不同操作生成确认信息）

**前端 UI**：
- [ ] 在 `VersionControlPanel` 中添加快速操作按钮（提交变更、切换分支等）
- [ ] 实现 `OnVcsActionRequested` 事件处理（切换到 Chat 并填充提示词）

**文档**：
- [ ] 更新 `CHANGELOG.md` 和 `ROADMAP.md`
- [ ] 更新 `SOUL.md`（添加 VCS 操作规则和确认策略）

---

## 11. 风险评估

| 风险 | 可能性 | 影响 | 缓解措施 |
|------|--------|------|---------|
| VCS 工具未安装 | 中 | 中 | 提供清晰的错误提示和安装指南 |
| CLI 输出格式变化 | 低 | 高 | 使用稳定的 `--porcelain` 格式，添加版本检测 |
| 跨平台路径问题 | 中 | 中 | 使用 `Path.Combine()` 和 `Path.GetFullPath()` |
| 大型仓库性能问题 | 中 | 中 | 实现分页、缓存、超时机制 |
| 破坏性操作误触发 | 低 | 高 | 强制用户确认对话框，在 SOUL.md 中添加确认规则 |
| Perforce/SVN 测试不足 | 高 | 中 | 优先实现 Git，Perforce/SVN 作为 P1/P2 |
| UI 性能问题（大量文件） | 中 | 中 | 使用虚拟化列表，限制显示数量（如最多 100 条） |
| Domain Reload 中断操作 | 中 | 中 | 保存操作上下文到 `DomainReloadState` |

---

## 12. 未来扩展

### 12.1 可能的增强功能

- **分支图可视化** — 在 VersionControlPanel 中显示分支拓扑图
- **冲突解决辅助** — 自动检测冲突并提供解决建议
- **代码审查集成** — 与 GitHub/GitLab/Bitbucket API 集成
- **自动提交建议** — 基于文件修改自动生成 commit message
- **分支策略推荐** — 根据项目规模推荐 Git Flow / GitHub Flow
- **差异可视化** — 在 UI 中直接显示文件差异（类似 Git GUI）
- **提交模板** — 支持自定义 commit message 模板

### 12.2 与其他功能的协同

- **与 `manage_script` 协同** — 修改脚本前自动检查 VCS 状态
- **与 `manage_knowledge` 协同** — 索引 commit message 和代码变更历史
- **与 `manage_memory` 协同** — 记住用户的 VCS 偏好和工作流
- **与 `scene_analysis` 协同** — 分析场景变更对 VCS 的影响

---

## 13. 开发检查清单

实现前必须确认：

- [ ] 用户确认需要支持的 VCS（Git / Perforce / SVN）
- [ ] 用户确认需要的 actions 优先级（只读 vs 操作类）
- [ ] 用户确认安全策略（哪些操作需要确认）
- [ ] 确定目标版本（v0.5.4 或 v0.6.0）
- [ ] 更新 ROADMAP.md 添加 Phase 6.4 任务
- [ ] 更新 AGENTS.md 添加 VCS 工具开发规范（如果需要）

实现后必须验证：

- [ ] 所有 actions 的参数 schema 正确
- [ ] 所有 actions 的返回值格式统一
- [ ] 错误处理完整（VCS 未安装、命令失败、权限不足）
- [ ] 跨平台测试（Windows / macOS / Linux）
- [ ] 性能测试（大型仓库、长历史）
- [ ] 文档更新（SOUL.md、TOOLS.md.template）

---

## 14. 参考资料

- [Git CLI 文档](https://git-scm.com/docs)
- [Perforce CLI 文档](https://www.perforce.com/manuals/cmdref/Content/CmdRef/Home-cmdref.html)
- [SVN CLI 文档](https://svnbook.red-bean.com/en/1.7/svn.ref.svn.html)
- [Unity VersionControl API](https://docs.unity3d.com/ScriptReference/VersionControl.html)
- [Process.Start() 文档](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.start)

---

> **下一步**: 等待用户确认设计方案，然后开始实现。
> **预估工作量**: 中等（3-5 天开发 + 2 天测试）
> **依赖**: 无（独立功能）
