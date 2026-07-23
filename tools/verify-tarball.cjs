#!/usr/bin/env node
// ============================================================================
// AgentCore Unity — Tarball Structural Integrity Verifier (Node.js port)
// ============================================================================
// Cross-platform replacement for tools/verify-tarball.ps1.
// Runtime: Node.js >= 14; no external deps. Requires 'tar' available on PATH
// (macOS/Linux ship it; Windows 10+ ships it as bsdtar).
//
// Purpose:
//   Verify that a produced .tgz contains all critical code directories AND
//   does NOT leak dev-only paths. Complements verify-meta.cjs (pre-pack source
//   check) with a POST-pack tarball check.
//
// Historical incident (v1.4.6):
//   '.npmignore' had 'tools/' (no leading '/'), which minimatch expanded to
//   match ANY 'tools/' anywhere in the tree, including 'Editor/Tools/'
//   (case-insensitive on Windows). The resulting tarball was missing ~150 .cs
//   files (Native/Cloud/FileSystem/Infrastructure/Safety tools) and failed to
//   compile in the target project. verify-meta.ps1 passed because it scans
//   the SOURCE tree, not the tarball.
//
// Usage:
//   node tools/verify-tarball.cjs                                # auto-pick newest
//   node tools/verify-tarball.cjs --tarball com.agentcore.unity-1.8.0.tgz
//   node tools/verify-tarball.cjs --quiet
//
// Exit codes (identical to verify-tarball.ps1):
//   0  Tarball contains all required paths and no forbidden paths
//   1  One or more critical paths missing or forbidden paths leaked
//   2  Script argument / environment error
// ============================================================================

'use strict';

const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

// ---------- ANSI colors -----------------------------------------------------
const useColor = process.stdout.isTTY && !process.env.NO_COLOR;
const c = {
    red:        (s) => useColor ? `\x1b[31m${s}\x1b[0m` : s,
    green:      (s) => useColor ? `\x1b[32m${s}\x1b[0m` : s,
    yellow:     (s) => useColor ? `\x1b[33m${s}\x1b[0m` : s,
    darkYellow: (s) => useColor ? `\x1b[33;2m${s}\x1b[0m` : s,
    cyan:       (s) => useColor ? `\x1b[36m${s}\x1b[0m` : s,
};

// ---------- Argument parsing ------------------------------------------------
function parseArgs(argv) {
    const args = { tarball: '', quiet: false };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--tarball' || a === '-Tarball') {
            args.tarball = argv[++i];
        } else if (a === '--quiet' || a === '-Quiet') {
            args.quiet = true;
        } else if (a === '--help' || a === '-h') {
            printHelp();
            process.exit(0);
        } else {
            process.stderr.write(`Unknown argument: ${a}\n`);
            printHelp();
            process.exit(2);
        }
    }
    return args;
}

function printHelp() {
    process.stderr.write(
        'Usage: node tools/verify-tarball.cjs [--tarball <path>] [--quiet]\n' +
        '  --tarball <path>  Explicit tarball path (default: newest com.agentcore.unity-*.tgz)\n' +
        '  --quiet           Suppress OK banner on success\n' +
        'Exit codes: 0=OK, 1=structural issue, 2=argument/env error\n'
    );
}

// ---------- Required / forbidden path table ---------------------------------
// KEEP IN SYNC with verify-tarball.ps1 — when moving/adding load-bearing code
// trees, update BOTH scripts in the same commit.
const REQUIRED_PATHS = [
    { path: 'package/Editor/AgentCore.Editor.asmdef',                                minCount: 1,  desc: 'Main asmdef' },
    { path: 'package/Editor/Bootstrap/',                                             minCount: 3,  desc: 'Bootstrap loader + resources' },
    { path: 'package/Editor/Bootstrap/Resources/SOUL.md',                            minCount: 1,  desc: 'SOUL.md (embedded)' },
    { path: 'package/Editor/Config/AgentCoreSettings.cs',                            minCount: 1,  desc: 'Settings core' },
    { path: 'package/Editor/Config/Settings/Pages/',                                 minCount: 5,  desc: 'Settings pages' },
    { path: 'package/Editor/Core/AgentLoop',                                         minCount: 5,  desc: 'AgentLoop partials' },
    { path: 'package/Editor/Core/SelfChallenge/',                                    minCount: 3,  desc: 'Self-Challenge scaffolding (Phase 9)' },
    { path: 'package/Editor/Extensions/',                                            minCount: 5,  desc: 'Extension host' },
    { path: 'package/Editor/Extensions/OptionalComponentDefaultsBootstrap.cs.meta',  minCount: 1,  desc: 'Bootstrap meta (v1.4.6 regression guard)' },
    { path: 'package/Editor/LLM/',                                                   minCount: 3,  desc: 'LLM clients' },
    { path: 'package/Editor/Session/',                                               minCount: 3,  desc: 'Session subsystem' },
    { path: 'package/Editor/Tools/IAgentTool.cs',                                    minCount: 1,  desc: 'Tool interface' },
    { path: 'package/Editor/Tools/Infrastructure/',                                  minCount: 3,  desc: 'Tool infrastructure' },
    { path: 'package/Editor/Tools/Native/',                                          minCount: 20, desc: 'Native tools (Unity API)' },
    { path: 'package/Editor/Tools/Cloud/',                                           minCount: 2,  desc: 'Cloud tools' },
    { path: 'package/Editor/Tools/FileSystem/',                                      minCount: 1,  desc: 'FileSystem tools' },
    { path: 'package/Editor/Tools/Safety/',                                          minCount: 3,  desc: 'Tool risk / policy layer' },
    { path: 'package/Editor/UI/ChatWindow',                                          minCount: 3,  desc: 'ChatWindow partials' },
    { path: 'package/Editor/UI/Components/',                                         minCount: 3,  desc: 'UI components' },
    { path: 'package/Editor/VCS/',                                                   minCount: 5,  desc: 'VCS optional component' },
    { path: 'package/Editor/Indexing/',                                              minCount: 5,  desc: 'Indexing optional component' },
    { path: 'package/Editor/Workspace/',                                             minCount: 5,  desc: 'Workspace infrastructure' },
    { path: 'package/Editor/Utils/',                                                 minCount: 3,  desc: 'Utilities' },
    { path: 'package/package.json',                                                  minCount: 1,  desc: 'Package manifest' },
    { path: 'package/README.md',                                                     minCount: 1,  desc: 'README' },
    { path: 'package/CHANGELOG.md',                                                  minCount: 1,  desc: 'CHANGELOG' },
    { path: 'package/LICENSE.md',                                                    minCount: 1,  desc: 'LICENSE' },
];

const FORBIDDEN_PATHS = [
    { path: 'package/tools/',              desc: 'Repo tooling scripts (should never ship)' },
    { path: 'package/plans/',              desc: 'Design docs (dev-only)' },
    { path: 'package/AGENTS.md',           desc: 'LLM dev rules (dev-only)' },
    { path: 'package/.agents/',            desc: 'AI tooling config (dev-only)' },
    { path: 'package/.roo/',               desc: 'AI tooling config (dev-only)' },
    { path: 'package/_archive/',           desc: 'Legacy archive (dev-only)' },
    { path: 'package/PROJECT-ANALYSIS.md', desc: 'Dev-only analysis doc' },
];

// ---------- Tarball auto-detection ------------------------------------------
function autoDetectTarball() {
    let entries;
    try {
        entries = fs.readdirSync('.', { withFileTypes: true });
    } catch (err) {
        process.stderr.write(`Failed to list current directory: ${err.message}\n`);
        process.exit(2);
    }
    const candidates = entries
        .filter((e) => e.isFile() && /^com\.agentcore\.unity-.*\.tgz$/.test(e.name))
        .map((e) => ({ name: e.name, mtime: fs.statSync(e.name).mtimeMs }))
        .sort((a, b) => b.mtime - a.mtime);
    if (candidates.length === 0) {
        process.stderr.write("No com.agentcore.unity-*.tgz found in current directory. Run 'npm pack' first or pass --tarball explicitly.\n");
        process.exit(2);
    }
    return candidates[0].name;
}

// ---------- Tarball listing (shell out to tar) ------------------------------
function listTarballEntries(tarball) {
    const res = spawnSync('tar', ['-tzf', tarball], { encoding: 'utf8' });
    if (res.error) {
        process.stderr.write(`Failed to invoke 'tar': ${res.error.message}\n`);
        process.stderr.write("Ensure 'tar' is on PATH (macOS/Linux ship it; Windows 10+ ships bsdtar).\n");
        process.exit(2);
    }
    if (res.status !== 0) {
        process.stderr.write(`tar exited with code ${res.status}: ${res.stderr}\n`);
        process.exit(2);
    }
    return res.stdout.split(/\r?\n/).filter((l) => l.length > 0);
}

// ---------- Main -------------------------------------------------------------
function main() {
    const args = parseArgs(process.argv.slice(2));

    if (!args.tarball) {
        args.tarball = autoDetectTarball();
    }

    if (!fs.existsSync(args.tarball)) {
        process.stderr.write(`Tarball not found: ${args.tarball}\n`);
        process.exit(2);
    }

    const entries = listTarballEntries(args.tarball);
    if (entries.length === 0) {
        process.stderr.write(`Tarball is empty or unreadable: ${args.tarball}\n`);
        process.exit(2);
    }

    // Substring check (PS uses regex-escaped substring; we do plain substring
    // to match the same semantics — no glob).
    const missing = [];
    for (const req of REQUIRED_PATHS) {
        const count = entries.filter((e) => e.includes(req.path)).length;
        if (count < req.minCount) {
            missing.push({ path: req.path, expected: `>= ${req.minCount}`, actual: count, desc: req.desc });
        }
    }

    const leaked = [];
    for (const fb of FORBIDDEN_PATHS) {
        const count = entries.filter((e) => e.includes(fb.path)).length;
        if (count > 0) {
            leaked.push({ path: fb.path, count, desc: fb.desc });
        }
    }

    if (missing.length === 0 && leaked.length === 0) {
        if (!args.quiet) {
            process.stdout.write(
                c.green(`[verify-tarball] OK — '${args.tarball}' passes all structural checks (${entries.length} entries total)`) + '\n'
            );
            process.stdout.write(
                c.green(`  ${REQUIRED_PATHS.length} required paths present, ${FORBIDDEN_PATHS.length} forbidden paths absent`) + '\n'
            );
        }
        process.exit(0);
    }

    process.stderr.write('\n');
    process.stderr.write(c.red(`[verify-tarball] FAIL — '${args.tarball}' has structural issues`) + '\n\n');

    if (missing.length > 0) {
        process.stderr.write(c.yellow(`  MISSING required paths (${missing.length}):`) + '\n');
        for (const m of missing) {
            process.stderr.write(c.red(`    - ${m.path}`) + '\n');
            process.stderr.write(c.darkYellow(`        Expected: ${m.expected}, Actual: ${m.actual}  [${m.desc}]`) + '\n');
        }
        process.stderr.write('\n');
        process.stderr.write(c.cyan("  Likely cause: '.npmignore' pattern accidentally excludes these paths.") + '\n');
        process.stderr.write(c.cyan("  Common trap: 'foo/' (no leading '/') matches 'foo/' at ANY depth in the tree.") + '\n');
        process.stderr.write(c.cyan("  Fix: use '/foo/' to anchor to repo root only.") + '\n\n');
    }

    if (leaked.length > 0) {
        process.stderr.write(c.yellow(`  LEAKED forbidden paths (${leaked.length}):`) + '\n');
        for (const l of leaked) {
            process.stderr.write(c.red(`    - ${l.path}  (${l.count} entries)`) + '\n');
            process.stderr.write(c.darkYellow(`        [${l.desc}]`) + '\n');
        }
        process.stderr.write('\n');
        process.stderr.write(c.cyan("  Fix: add these paths to '.npmignore' with proper anchoring.") + '\n\n');
    }

    process.exit(1);
}

main();
