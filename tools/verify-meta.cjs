#!/usr/bin/env node
// ============================================================================
// AgentCore Unity — Meta File Integrity Verifier (Node.js port)
// ============================================================================
// Cross-platform replacement for tools/verify-meta.ps1.
// Runtime: Node.js >= 14 (fs.promises / recursive readdir); no external deps.
//
// Purpose:
//   Scan every .cs / .uxml / .uss / .asmdef / .md / .template / .json / .txt
//   file under Editor/ and confirm each has a sibling .meta file. Unity's
//   read-only UPM package model REQUIRES .meta files: assets without .meta
//   in a read-only package are silently NOT compiled into their target asmdef.
//
// Historical context:
//   v1.4.5 shipped OptionalComponentDefaultsBootstrap.cs without its .meta,
//   causing the [InitializeOnLoadMethod] to never run and VCS auto-enable to
//   fail on every fresh install. This script exists to make that class of bug
//   impossible to ship again.
//
// Usage:
//   node tools/verify-meta.cjs                 # default: Root=Editor
//   node tools/verify-meta.cjs --root Editor   # explicit root
//   node tools/verify-meta.cjs --quiet         # suppress OK banner
//
// Exit codes (identical to verify-meta.ps1):
//   0  All assets have corresponding .meta files
//   1  One or more assets are missing .meta (details printed to stderr)
//   2  Script argument / environment error
// ============================================================================

'use strict';

const fs = require('fs');
const path = require('path');

// ---------- ANSI colors (auto-disabled when NO_COLOR or non-TTY) -------------
const useColor = process.stdout.isTTY && !process.env.NO_COLOR;
const c = {
    red:    (s) => useColor ? `\x1b[31m${s}\x1b[0m`    : s,
    green:  (s) => useColor ? `\x1b[32m${s}\x1b[0m`    : s,
    yellow: (s) => useColor ? `\x1b[33m${s}\x1b[0m`    : s,
    cyan:   (s) => useColor ? `\x1b[36m${s}\x1b[0m`    : s,
    dim:    (s) => useColor ? `\x1b[2m${s}\x1b[0m`     : s,
};

// ---------- Argument parsing (minimal, no yargs dependency) -----------------
function parseArgs(argv) {
    const args = { root: 'Editor', quiet: false };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--root' || a === '-Root') {
            args.root = argv[++i];
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
        'Usage: node tools/verify-meta.cjs [--root <dir>] [--quiet]\n' +
        '  --root <dir>   Root directory to scan (default: Editor)\n' +
        '  --quiet        Suppress OK banner on success\n' +
        'Exit codes: 0=OK, 1=missing meta, 2=argument/env error\n'
    );
}

// ---------- Recursive collection --------------------------------------------
const CANDIDATE_EXTENSIONS = new Set(['.cs', '.uxml', '.uss', '.asmdef', '.md', '.template', '.json', '.txt']);

function walk(dir, files, dirs) {
    let entries;
    try {
        entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch (err) {
        process.stderr.write(`Failed to read directory '${dir}': ${err.message}\n`);
        process.exit(2);
    }
    for (const e of entries) {
        const full = path.join(dir, e.name);
        if (e.isDirectory()) {
            dirs.push(full);
            walk(full, files, dirs);
        } else if (e.isFile()) {
            files.push(full);
        }
        // symlinks / others skipped intentionally
    }
}

// ---------- Main -------------------------------------------------------------
function main() {
    const args = parseArgs(process.argv.slice(2));

    if (!fs.existsSync(args.root)) {
        process.stderr.write(`Root directory not found: ${args.root}\n`);
        process.exit(2);
    }
    const stat = fs.statSync(args.root);
    if (!stat.isDirectory()) {
        process.stderr.write(`Root is not a directory: ${args.root}\n`);
        process.exit(2);
    }

    const files = [];
    const dirs = [];
    walk(args.root, files, dirs);

    // Filter to Unity-visible candidates (excluding .meta itself and files with
    // unrecognized extensions). Special-case '*.md.template' to mirror PS behavior.
    const candidates = files.filter((f) => {
        const base = path.basename(f);
        if (base.endsWith('.meta')) return false;
        const ext = path.extname(f);
        if (CANDIDATE_EXTENSIONS.has(ext)) return true;
        if (base.endsWith('.md.template')) return true;
        return false;
    });

    // Every Unity-visible file MUST have a sibling .meta or Unity will silently
    // exclude it from compilation (read-only UPM package).
    const missing = candidates.filter((f) => !fs.existsSync(f + '.meta'));

    // Every directory under Root must also have a .meta (Unity requires
    // directory meta for asmdef nesting to work).
    const dirMissing = dirs.filter((d) => !fs.existsSync(d + '.meta'));

    const totalMissing = missing.length + dirMissing.length;

    if (totalMissing === 0) {
        if (!args.quiet) {
            process.stdout.write(
                c.green(`[verify-meta] OK — ${candidates.length} files + ${dirs.length} dirs under '${args.root}' all have .meta`) + '\n'
            );
        }
        process.exit(0);
    }

    process.stderr.write('\n');
    process.stderr.write(c.red(`[verify-meta] FAIL — ${totalMissing} asset(s) missing .meta file(s):`) + '\n\n');

    if (missing.length > 0) {
        process.stderr.write(c.yellow('  Files without .meta:') + '\n');
        for (const m of missing) {
            const rel = path.relative(process.cwd(), m);
            process.stderr.write(c.red(`    - ${rel}`) + '\n');
        }
        process.stderr.write('\n');
    }

    if (dirMissing.length > 0) {
        process.stderr.write(c.yellow('  Directories without .meta:') + '\n');
        for (const m of dirMissing) {
            const rel = path.relative(process.cwd(), m);
            process.stderr.write(c.red(`    - ${rel}`) + '\n');
        }
        process.stderr.write('\n');
    }

    process.stderr.write(c.cyan('How to fix:') + '\n');
    process.stderr.write(c.cyan('  1. Open the workspace in Unity (Editor auto-generates missing .meta on import)') + '\n');
    process.stderr.write(c.cyan('  2. Verify the generated .meta is committed to the repo') + '\n');
    process.stderr.write(c.cyan('  3. Re-run: node tools/verify-meta.cjs') + '\n');
    process.stderr.write('\n');
    process.stderr.write(c.yellow('DO NOT hand-write .meta files — Unity relies on stable GUIDs generated on import.') + '\n');
    process.stderr.write('\n');

    process.exit(1);
}

main();
