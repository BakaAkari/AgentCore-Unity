# AgentCore Unity — 紧急离线卸载指南

> **适用场景**：安装了旧版 AgentCore（1.5.6 之前）后，Unity Editor 一启动就弹出
> "Moving F:/…/Temp/UnityTempFile-<hash> to …/Preferences/AgentCore/Settings.asset:
> 系统找不到指定的路径" 的 Force Quit 对话框，**根本进不了 Package Manager**，
> 无法通过正常途径卸载插件。
>
> 本指南提供两条路径：**方式 A（推荐，双击 .bat）** 与 **方式 B（手动步骤，最保守）**。
> 无需 PowerShell / Python / 任何第三方工具，纯 Windows 内置 `cmd.exe` 即可。

---

## 前置说明：为什么单独删除 `Packages/com.agentcore/` 不够？

AgentCore 在项目中可能有多种存在形态，卸载必须**同时**清理所有相关位置：

| 类别 | 路径 | 是否必须清理 |
|------|------|---------------|
| manifest 依赖声明 | `<项目>/Packages/manifest.json` 中含 `"com.agentcore.unity"` 的那一行 | **必须（手动）** |
| 依赖锁文件 | `<项目>/Packages/packages-lock.json` | 建议（Unity 会重建） |
| embedded 源码 | `<项目>/Packages/com.agentcore/` | 若使用 embedded 安装则必须 |
| Package 缓存 | `<项目>/Library/PackageCache/com.agentcore.unity@*/` | **必须** |
| 项目会话数据 | `<项目>/Library/AgentCore/` | 建议 |
| **全局偏好目录（跨项目共享）** | `%APPDATA%/Unity/Editor-*.x/Preferences/AgentCore/` | 若下次装新版仍卡则必须 |

只删 `Packages/com.agentcore/` 不够 —— Unity 会从 `Library/PackageCache/` 重新加载。
只删 Cache 不够 —— `manifest.json` 里仍写着依赖，下次启动会再拉一次。
只清项目局部不够 —— 全局 `Preferences/AgentCore/` 里损坏的 `Settings.asset` 会同样触发问题。
**六个位置必须一起清**。

---

## 方式 A：使用一键 BAT 脚本（推荐）

### A.1 获取脚本

- **从插件仓库源码**：位于 `Packages/com.agentcore/tools/emergency-uninstall.bat`
- **从 tarball**：因发布 tarball 会剥除 `tools/` 目录，无法获取；请从上述仓库路径拿。
- 无 PowerShell 依赖，纯 `cmd.exe` 内置命令。

### A.2 执行（双击 或 命令行）

**方式 A-1：双击**
- 若把 `.bat` 放在 `<项目>/Packages/com.agentcore/tools/` 下，双击即可自动识别项目路径。
- 否则双击后会提示你输入项目路径。

**方式 A-2：命令行**

```cmd
REM 关掉 Unity（脚本也会自动 kill 进程，但手动关一次更保险）

REM 显式指定项目路径
emergency-uninstall.bat "D:\Your\UnityProject"

REM 连全局偏好目录也一起清（下次装新版仍卡时使用）
emergency-uninstall.bat "D:\Your\UnityProject" /prefs

REM 跳过所有确认提示（危险，先在测试项目跑一遍）
emergency-uninstall.bat "D:\Your\UnityProject" /prefs /yes
```

### A.3 脚本会自动做什么

1. **Step 1** — Kill `Unity.exe` / `UnityShaderCompiler.exe` / 相关进程，释放文件锁
2. **Step 2** — 删除 `<项目>/Packages/com.agentcore/` 及 `.meta`
3. **Step 3** — 删除 `<项目>/Library/PackageCache/com.agentcore.unity*/` 全部匹配目录
4. **Step 4** — 删除 `<项目>/Library/AgentCore/`
5. **Step 5** — 删除 `<项目>/Packages/packages-lock.json`（Unity 下次启动会重建）
6. **Step 6（可选）** — 传入 `/prefs` 时删除 `%APPDATA%/Unity/Editor-*/Preferences/AgentCore/`

### A.4 脚本不会做什么（需要你手工完成）

**编辑 `<项目>/Packages/manifest.json`** —— 删除含 `"com.agentcore.unity"` 的那一行。
BAT 无法安全解析 JSON，所以这一步交给你。脚本最后会自动为你打开 Notepad。

**如何改**：找到类似下面的一行，整行删掉：

```json
{
  "dependencies": {
    "com.agentcore.unity": "file:../_local/com.agentcore.unity-1.5.6.tgz",   ← 删掉这一行
    "com.unity.render-pipelines.universal": "17.0.3",
    ...
  }
}
```

**JSON 语法要求**：如果被删除的是最后一个依赖项（下一行就是 `}`），需要把它上面一行末尾的 `,` 也一起删掉，否则 JSON 无效。中间项则不用管逗号。

---

## 方式 B：完全手动步骤（不使用任何脚本）

若不便运行 BAT，或想完全掌控每一步：

### B.1 关闭 Unity

任务管理器结束所有 `Unity.exe`、`UnityShaderCompiler.exe`、`UnityHelper.exe`、`UnityCrashHandler64.exe` 进程。

### B.2 编辑 `Packages/manifest.json`

同 §A.4，用 Notepad 打开 `<项目>/Packages/manifest.json`，删除含 `"com.agentcore.unity"` 的那一行，注意 JSON 语法。

### B.3 删除锁文件与缓存

用 Windows 资源管理器或 `cmd` 逐个删除（不存在则跳过）：

```
<项目>/Packages/packages-lock.json
<项目>/Packages/com.agentcore/
<项目>/Packages/com.agentcore.meta   （若存在）
<项目>/Library/PackageCache/com.agentcore.unity@*    （通配符，可能有多个版本）
<项目>/Library/AgentCore/
```

### B.4 （可选）清全局偏好

如果 §B.1–§B.3 处理完 Unity 还是弹同样错，说明全局偏好目录仍在触发。删除：

- **Windows**：`%APPDATA%/Unity/Editor-*.x/Preferences/AgentCore/`
  - 在文件资源管理器地址栏输入 `%APPDATA%\Unity` 可直接跳转
  - `Editor-*.x` 是 Unity 主版本号（例如 `Editor-2022.x`、`Editor-6000.x`）
- **macOS**：`~/Library/Preferences/Unity/Editor-*.x/Preferences/AgentCore/`
- **Linux**：`~/.config/unity3d/Preferences/AgentCore/`

### B.5 启动 Unity

打开 Unity Hub 加载该项目。Unity 会：
- 重新解析 `manifest.json`，跳过 AgentCore
- 重建 `packages-lock.json`
- 重建 `Library/`

正常情况下项目会恢复到"未安装 AgentCore"的状态，且不会再弹 Force Quit。

---

## 装回新版本 1.5.6+

已修复本 bug。卸载完成后：

1. 把 `com.agentcore.unity-1.5.6.tgz` 放到项目下一个稳定位置，例如 `<项目>/_local/`
2. 编辑 `<项目>/Packages/manifest.json`，在 `dependencies` 里加一行：

   ```json
   "com.agentcore.unity": "file:../_local/com.agentcore.unity-1.5.6.tgz"
   ```

   （注意末尾逗号：若下面还有其他依赖，则末尾要有 `,`；若是最后一项则无 `,`）

3. 打开 Unity，Package Manager 会自动解压并加载。1.5.6 起首次 Save 会自动创建 `Preferences/AgentCore/` 目录，不会再卡。

---

## 常见 FAQ

**Q1: 只删 `Packages/com.agentcore/` 就够了吗？**
不够。`Library/PackageCache/` 还留着编译产物，`manifest.json` 还写着依赖，Unity 下次启动会再从 tgz / registry 拉一次。

**Q2: 会不会误删项目其他内容？**
BAT 脚本只操作以下路径：`Packages/com.agentcore*`、`Packages/packages-lock.json`、`Library/PackageCache/com.agentcore.unity*`、`Library/AgentCore/`，以及 `/prefs` 时的 `%APPDATA%/Unity/Editor-*/Preferences/AgentCore/`。`Assets/`、`ProjectSettings/`、`UserSettings/`、其他 Package 均不动。**它不会修改 manifest.json**（这是刻意为之，避免脚本破坏 JSON 结构）。

**Q3: `manifest.json` 备份怎么做？**
BAT 脚本不改 `manifest.json`，所以无需备份。但改之前你可以自己复制一份 `manifest.json.bak` 以防万一。

**Q4: 为什么要清全局 `Preferences/AgentCore/`？**
1.5.6 之前的 bug 是：目录不存在时首次 Save 卡死。装 1.5.6 后新版本会**主动创建目录**，不会再有这个问题。但如果历史遗留的 `Settings.asset` 文件本身损坏，也会引发其他保存异常 —— 全清最保险，1.5.6 会按默认值重建。

**Q5: Unity Hub 里还显示这个项目"Broken"怎么办？**
执行完卸载后，在 Unity Hub 里 Add → 选择项目根目录，重新添加即可。

**Q6: 双击 BAT 提示"Windows 已保护你的电脑"？**
点击"更多信息" → "仍要运行"。或者从 `cmd` 命令行运行，不会弹这个提示。
