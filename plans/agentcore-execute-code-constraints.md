# AgentCore `execute_code` Tool — Runtime Constraints

Empirical constraints discovered while probing Unity internal APIs via AgentCore's `execute_code` tool (Mono.CSharp.Evaluator).

## Hard Compile Rules

1. **NO `using X;` directives** — Pre-imported: `System`, `System.IO`, `System.Text`, `System.Text.RegularExpressions`, `System.Linq`, `System.Collections.Generic`, `UnityEngine`, `UnityEngine.SceneManagement`, `UnityEditor`, `UnityEditor.SceneManagement`. For anything else, use **fully qualified type names** (e.g. `System.Reflection.BindingFlags`, `UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility`).
2. **NO top-level `return`** — code is not inside a method body. To return a value, put an **expression without trailing `;`** as the last statement (e.g. `var x = 40; x + 2`).
3. **No `async/await`** — Mono.CSharp.Evaluator limitation.
4. **No C# 8+ syntax** — no records, switch expressions, `using` declarations, target-typed `new`, pattern matching syntax sugars. Use classic statements.
5. **`Object` is ambiguous** — always write `UnityEngine.Object` or `System.Object`, never bare `Object`.
6. **No `action` parameter** — `execute_code` takes only `code`. `execute_code(action=execute, code=…)` fails hard with an "action not supported" error.

## Available Assemblies (types + extension methods available without extra reference)

- `UnityEngine.CoreModule`
- `UnityEditor.CoreModule`
- `UnityEditor.SceneManagerModule`
- `System.Core` (LINQ)
- `UnityEngine.ImageConversionModule` (Texture2D.EncodeToPNG/JPG/LoadImage)
- `UnityEngine.JSONSerializeModule` (JsonUtility)
- `UnityEngine.AssetBundleModule`
- `UnityEngine.PhysicsModule`
- `UnityEngine.UI` (when installed)

## Common Pitfalls

- **Scoped loop variables** — Mono.CSharp is strict about variable scope. `f` inside a `foreach (var f in fields)` cannot be referenced from a later `foreach (var m in methods)` block. Renaming the outer variable or restructuring is required. This bit us when probing FrameDebugger API — a `f.IsPrivate` mistakenly appeared inside the method loop; the compiler correctly rejected it with `CS0103: The name 'f' does not exist in the current context`.
- **Category activation gate** — Some tool categories (like `Scripting` containing `execute_code`) are `Restricted` and must be `activate`d before use in a given session. If `execute_code` "does not exist", first call `manage_tool_categories action=activate categories=Scripting`.
- **Reflection dump — pick the right FullName** — When probing Unity internals, verify the namespace first via a **scan**: iterate `typeof(EditorWindow).Assembly.GetTypes()` filtered by name-contains, and print `t.FullName`. Otherwise you'll pass wrong-namespace strings to `Type.GetType(...)`, get `null`, and dump nothing. Example: `FrameDebuggerUtility` is under `UnityEditorInternal.FrameDebuggerInternal`, NOT `UnityEditorInternal`.

## Probe Pattern

For probing internal APIs, use this two-phase approach:

**Phase 1: Discovery — find real FullName**
```csharp
var asm = typeof(UnityEditor.EditorWindow).Assembly;
var sb = new System.Text.StringBuilder();
foreach (var t in asm.GetTypes())
{
    if (t.FullName != null && t.FullName.IndexOf("FrameDebugger", System.StringComparison.OrdinalIgnoreCase) >= 0)
        sb.AppendLine(t.FullName);
}
UnityEngine.Debug.Log(sb.ToString());
sb.ToString()
```

**Phase 2: Member dump — using resolved FullName**
```csharp
var t = System.Type.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility, UnityEditor.CoreModule");
// … dump t.GetFields/Properties/Methods with BindingFlags.Public|NonPublic|Static|Instance|DeclaredOnly
```

Two phases avoids the "wrong namespace → null → empty dump" trap that wasted a full round-trip on the FrameDebugger G03 spike.

## Verification

Test call sequence to warm this up:
```
manage_tool_categories action=list        # confirm Scripting is Restricted+inactive
manage_tool_categories action=activate categories=Scripting
execute_code code="1 + 1"                 # smoke test — must return 2
```
