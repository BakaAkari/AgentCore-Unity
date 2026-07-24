#!/usr/bin/env node
// ============================================================================
// AgentCore Unity — Tool Inventory Extractor (audit helper, NOT for shipping)
// ============================================================================
// Purpose:
//   One-shot script to enumerate all 47 [AgentTool]-decorated tools and emit
//   a structured inventory: name, Category, Visibility, action enum, Handle*
//   method names, key Unity API touchpoints.
//
// Usage:
//   node tools/tool-inventory.cjs                          # print table to stdout
//   node tools/tool-inventory.cjs --json > inventory.json  # dump JSON for further processing
//
// Excluded from tgz via .npmignore /tools/ rule.
// ============================================================================

'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = 'Editor/Tools/Native';
const JSON_MODE = process.argv.includes('--json');

// ---------- Recursively find *.cs under ROOT ---------------------------------
function walk(dir, out) {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, e.name);
        if (e.isDirectory()) walk(full, out);
        else if (e.isFile() && e.name.endsWith('.cs')) out.push(full);
    }
    return out;
}

// ---------- Extractors -------------------------------------------------------
// AgentTool attribute — capture name (positional string) + all named args
// (Description, Category, Visibility, RequiresMainThread, etc.).
function extractAgentTool(src) {
    // Look for [AgentTool("<name>", ...)] — attribute can span many lines.
    const m = src.match(/\[AgentTool\(\s*"([^"]+)"([^]*?)\)\]/);
    if (!m) return null;
    const name = m[1];
    const argsBlob = m[2];
    // Extract named args like Category = "specialized", Visibility = ToolVisibility.OnDemand
    const category = (argsBlob.match(/Category\s*=\s*"([^"]+)"/) || [])[1] || '';
    const visibility = (argsBlob.match(/Visibility\s*=\s*ToolVisibility\.(\w+)/) || [])[1] || '';
    const requiresMainThread = /RequiresMainThread\s*=\s*true/.test(argsBlob);
    // Description is long, we truncate for display; keep full in JSON mode.
    const descRaw = (argsBlob.match(/Description\s*=\s*((?:"[^"]*"\s*\+?\s*)+)/) || [])[1] || '';
    const description = descRaw
        .replace(/"\s*\+\s*"/g, '')   // stitch adjacent literals
        .replace(/^"|"$/g, '')
        .trim();
    return { name, category, visibility, requiresMainThread, description };
}

// action enum lives inside the _parametersSchema JSON literal, which is a C#
// verbatim string @"..." using DOUBLED quotes as escape: ""action"" .
// Strip doubled quotes back to single quotes before matching JSON structure.
function extractActionEnum(src) {
    const normalized = src.replace(/""/g, '"');
    const m = normalized.match(/"action"\s*:\s*\{[^}]*?"enum"\s*:\s*\[([^\]]+)\]/);
    if (!m) return [];
    return [...m[1].matchAll(/"([^"]+)"/g)].map((x) => x[1]);
}

// Handle* method names — the standard dispatcher pattern for AgentCore tools.
// Signature can be public / private / async / static; we just want the method name.
function extractHandlers(src) {
    const found = new Set();
    const re = /\b(?:public|private|internal|protected|static|async|Task|IEnumerator|ExecutionResult|ToolResponse)[^\n{]*?\b(Handle\w+)\s*\(/g;
    let m;
    while ((m = re.exec(src)) !== null) {
        found.add(m[1]);
    }
    return [...found].sort();
}

// Key Unity API touchpoints — hunt for well-known namespaces / static classes.
// Purpose: quick visual sanity check for "what Unity module does this tool touch".
const API_PATTERNS = [
    { key: 'AssetDatabase',          re: /\bAssetDatabase\./ },
    { key: 'EditorApplication',      re: /\bEditorApplication\./ },
    { key: 'EditorUtility',          re: /\bEditorUtility\./ },
    { key: 'EditorPrefs',            re: /\bEditorPrefs\./ },
    { key: 'PlayerPrefs',            re: /\bPlayerPrefs\./ },
    { key: 'Selection',              re: /\bSelection\./ },
    { key: 'Undo',                   re: /\bUndo\./ },
    { key: 'PrefabUtility',          re: /\bPrefabUtility\./ },
    { key: 'SerializedObject',       re: /\bSerializedObject\b/ },
    { key: 'ScriptableObject',       re: /\bScriptableObject\b/ },
    { key: 'GameObject',             re: /\bnew GameObject\b|\bGameObject\.Find/ },
    { key: 'SceneManager',           re: /\bSceneManager\./ },
    { key: 'EditorSceneManager',     re: /\bEditorSceneManager\./ },
    { key: 'Lightmapping',           re: /\bLightmapping\./ },
    { key: 'GraphicsSettings',       re: /\bGraphicsSettings\./ },
    { key: 'QualitySettings',        re: /\bQualitySettings\./ },
    { key: 'RenderSettings',         re: /\bRenderSettings\./ },
    { key: 'Physics',                re: /\bPhysics\./ },
    { key: 'Physics2D',              re: /\bPhysics2D\./ },
    { key: 'NavMesh',                re: /\bNavMesh\.|\bNavMeshBuilder\./ },
    { key: 'Camera',                 re: /\bCamera\.main|\bnew Camera\b/ },
    { key: 'Volume/VolumeProfile',   re: /\bVolume(Profile|Component|Parameter)?\b/ },
    { key: 'ProfilerRecorder',       re: /\bProfilerRecorder\b/ },
    { key: 'ProfilerDriver',         re: /\bProfilerDriver\b/ },
    { key: 'FrameDebugger',          re: /\bFrameDebugger/ },
    { key: 'MemoryProfiler',         re: /\bMemoryProfiler|Unity\.MemoryProfiler/ },
    { key: 'CompilationPipeline',    re: /\bCompilationPipeline\b/ },
    { key: 'SceneView',              re: /\bSceneView\./ },
    { key: 'SearchService',          re: /\bSearchService\b/ },
    { key: 'Presets',                re: /\bUnityEditor\.Presets|\bPreset\.Apply/ },
    { key: 'InputSystem',            re: /\bUnityEngine\.InputSystem|\bInputSystem\./ },
    { key: 'AudioMixer',             re: /\bAudioMixer\b/ },
    { key: 'BuildPipeline',          re: /\bBuildPipeline\./ },
    { key: 'PackageManager',         re: /\bUnityEditor\.PackageManager|\bClient\.Add|\bClient\.List/ },
    { key: 'TextureImporter',        re: /\bTextureImporter\b/ },
    { key: 'ModelImporter',          re: /\bModelImporter\b/ },
    { key: 'AudioImporter',          re: /\bAudioImporter\b/ },
    { key: 'OcclusionCulling',       re: /\bStaticOcclusionCulling\b/ },
    { key: 'Terrain',                re: /\bTerrain\b/ },
    { key: 'ProBuilder',             re: /\bProBuilder|UnityEngine\.ProBuilder/ },
    { key: 'Cinemachine',            re: /\bCinemachine\b/ },
    { key: 'Timeline',               re: /\bTimeline\b/ },
    { key: 'UIElements',             re: /\bUnityEditor\.UIElements|\bUnityEngine\.UIElements/ },
    { key: 'Reflection',             re: /\bSystem\.Reflection\b|\btypeof\([^)]+\)\.Get(Method|Property|Field)/ },
];

function extractApiTouchpoints(src) {
    const hits = [];
    for (const p of API_PATTERNS) {
        if (p.re.test(src)) hits.push(p.key);
    }
    return hits;
}

// ---------- Main --------------------------------------------------------------
const files = walk(ROOT, []);
const inventory = [];
for (const f of files) {
    const src = fs.readFileSync(f, 'utf8');
    const meta = extractAgentTool(src);
    if (!meta) continue;
    inventory.push({
        file: f,
        ...meta,
        actions: extractActionEnum(src),
        handlers: extractHandlers(src),
        apis: extractApiTouchpoints(src),
        sizeBytes: src.length,
    });
}

// Deterministic sort: name asc.
inventory.sort((a, b) => a.name.localeCompare(b.name));

if (JSON_MODE) {
    process.stdout.write(JSON.stringify(inventory, null, 2) + '\n');
    process.exit(0);
}

// Human-readable summary.
process.stdout.write(`AgentCore Tool Inventory — ${inventory.length} tools\n`);
process.stdout.write('='.repeat(80) + '\n\n');

// Category summary
const byCat = {};
for (const t of inventory) {
    const c = t.category || '(none)';
    byCat[c] = (byCat[c] || 0) + 1;
}
process.stdout.write('By Category:\n');
for (const [c, n] of Object.entries(byCat).sort()) {
    process.stdout.write(`  ${c.padEnd(20)} ${n}\n`);
}
process.stdout.write('\n');

// Visibility summary
const byVis = {};
for (const t of inventory) {
    const v = t.visibility || '(none)';
    byVis[v] = (byVis[v] || 0) + 1;
}
process.stdout.write('By Visibility:\n');
for (const [v, n] of Object.entries(byVis).sort()) {
    process.stdout.write(`  ${v.padEnd(20)} ${n}\n`);
}
process.stdout.write('\n');

// Per-tool table
process.stdout.write('Per-tool details:\n');
process.stdout.write('-'.repeat(80) + '\n');
for (const t of inventory) {
    process.stdout.write(`\n[${t.name}]  cat=${t.category}  vis=${t.visibility}  mainThread=${t.requiresMainThread}\n`);
    process.stdout.write(`  file:    ${t.file}\n`);
    process.stdout.write(`  actions (${t.actions.length}): ${t.actions.join(', ') || '(none / non-action tool)'}\n`);
    process.stdout.write(`  handlers (${t.handlers.length}): ${t.handlers.slice(0, 8).join(', ')}${t.handlers.length > 8 ? ' ...' : ''}\n`);
    process.stdout.write(`  apis (${t.apis.length}): ${t.apis.join(', ')}\n`);
}

process.stdout.write('\n');
process.stdout.write('='.repeat(80) + '\n');
process.stdout.write(`Total: ${inventory.length} tools, ${inventory.reduce((s, t) => s + t.actions.length, 0)} action variants across all tools.\n`);
