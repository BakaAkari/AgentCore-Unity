#!/bin/bash
# ============================================
# Prepare Docker images for offline deployment
# Run this OUTSIDE the sandbox (with internet)
# via: wsl -d Ubuntu-24.04 -- bash /mnt/d/NextCloud/Code/Unity/ragmem/prepare-images.sh
# ============================================
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
IMAGES_DIR="$SCRIPT_DIR/images"

echo "============================================"
echo "  Prepare Docker Images for Sandbox"
echo "============================================"
echo ""

mkdir -p "$IMAGES_DIR"

# ------------------------------------------
# Phase 1: Build mem0-server from source
# ------------------------------------------
echo "[Phase 1] Building mem0-server from source..."

MEM0_BUILD_DIR="/tmp/mem0-build"
if [ ! -d "$MEM0_BUILD_DIR" ]; then
    echo "  Cloning mem0 repository..."
    git clone --depth 1 https://github.com/mem0ai/mem0.git "$MEM0_BUILD_DIR"
else
    echo "  Using existing clone at $MEM0_BUILD_DIR"
fi

# --- Apply patches to mem0 source ---
echo "  Applying patches to mem0 source..."
cd "$MEM0_BUILD_DIR/server"

# Patch 1: Fix psycopg dependency
# python:3.12-slim lacks libpq, so we need psycopg[binary] for pre-compiled bindings
# Also add [pool] for connection pooling support
echo "  [Patch 1/7] Fixing psycopg dependency in requirements.txt..."
sed -i 's/^psycopg>=.*/psycopg[binary,pool]>=3.2.8/' requirements.txt
echo "    → psycopg[binary,pool]>=3.2.8"

# Patch 2: Remove graph_store from DEFAULT_CONFIG in main.py
# The default config hardcodes neo4j as graph store, which pulls in langchain-neo4j,
# rank-bm25, and other heavy dependencies. Since we don't deploy Neo4j by default,
# we remove the graph_store block entirely. This makes mem0 work with just
# vector store (pgvector) + history DB (SQLite), which is the core functionality.
# Users who want graph memory can uncomment Neo4j in docker-compose.yml and
# rebuild the image with graph_store support.
echo "  [Patch 2/7] Removing graph_store (neo4j) from DEFAULT_CONFIG in main.py..."
python3 -c "
import re
with open('main.py', 'r') as f:
    content = f.read()

# Remove the graph_store block from DEFAULT_CONFIG
# Pattern: '\"graph_store\": {' ... '},' (the entire nested dict entry)
content = re.sub(
    r'\s*\"graph_store\":\s*\{[^}]*\{[^}]*\}[^}]*\},?\n?',
    '\n',
    content
)

# Also remove unused NEO4J/MEMGRAPH env var lines
content = re.sub(r'^NEO4J_.*\n', '', content, flags=re.MULTILINE)
content = re.sub(r'^MEMGRAPH_.*\n', '', content, flags=re.MULTILINE)

with open('main.py', 'w') as f:
    f.write(content)
"
echo "    → Removed graph_store block and neo4j/memgraph env vars"

# Patch 3: Ensure data directory exists in Dockerfile
# The HISTORY_DB_PATH defaults to /app/history/history.db but the directory
# doesn't exist. We create /app/data as the canonical data directory.
echo "  [Patch 3/7] Patching Dockerfile to create data directory..."
if ! grep -q "mkdir -p /app/data" Dockerfile; then
    sed -i '/^COPY \. \./i RUN mkdir -p /app/data' Dockerfile
    echo "    → Added 'RUN mkdir -p /app/data' to Dockerfile"
else
    echo "    → Already patched"
fi

# Patch 4: Parameterize main.py DEFAULT_CONFIG via environment variables
# Instead of hardcoding model names and providers, read from env vars at runtime.
# Default values preserve original behavior (openai for both LLM and embedder).
# Deployers switch providers by setting env vars in .env / docker-compose.yml.
#
# NOTE: Even if this patch fails on a new upstream format, the runtime mount of
# main_override.py (via docker-compose.yml) will override main.py entirely.
# This patch is a best-effort optimization to keep the image self-contained.
echo "  [Patch 4/7] Parameterizing DEFAULT_CONFIG in main.py..."
python3 -c "
import re

with open('main.py', 'r') as f:
    content = f.read()

# --- Step 1: Add env var imports near the top (after existing os.environ lines) ---
env_block = '''
# --- RagMem: read config from environment variables ---
LLM_MODEL = os.environ.get('LLM_MODEL', 'gpt-4o-mini')
EMBEDDER_PROVIDER = os.environ.get('EMBEDDER_PROVIDER', 'openai')
EMBEDDER_MODEL = os.environ.get('EMBEDDER_MODEL', 'text-embedding-3-small')
OLLAMA_BASE_URL = os.environ.get('OLLAMA_BASE_URL', 'http://host.docker.internal:11434')
EMBEDDING_DIM = int(os.environ.get('EMBEDDING_DIM', '1536'))
'''

# Insert after HISTORY_DB_PATH line (which is the last env var in upstream main.py)
if 'EMBEDDER_PROVIDER' not in content:
    content = re.sub(
        r'(HISTORY_DB_PATH\s*=\s*os\.environ\.get\([^)]+\))',
        r'\1\n' + env_block,
        content
    )

# --- Step 2: Replace hardcoded model in llm config ---
# Use line-by-line approach to handle any model name format (including dates, versions)
# This is more robust than cross-line regex which can fail on nested braces
lines = content.split('\n')
in_llm_block = False
llm_brace_depth = 0
patched_model = False
for i, line in enumerate(lines):
    stripped = line.strip()
    # Detect entry into 'llm' block
    if '\"llm\"' in stripped and '{' in stripped:
        in_llm_block = True
        llm_brace_depth = stripped.count('{') - stripped.count('}')
        continue
    if in_llm_block:
        llm_brace_depth += stripped.count('{') - stripped.count('}')
        # Replace 'model': 'anything' with 'model': LLM_MODEL
        if '\"model\"' in stripped and not patched_model:
            lines[i] = re.sub(r'(\"model\":\s*)\"[^\"]+\"', r'\1LLM_MODEL', line)
            patched_model = True
        if llm_brace_depth <= 0:
            in_llm_block = False
content = '\n'.join(lines)

if not patched_model:
    print('WARNING: Could not find model field in llm block. Runtime override will handle this.')

# --- Step 3: Replace embedder section to support multiple providers ---
new_embedder = '''\"embedder\": {
        \"provider\": EMBEDDER_PROVIDER,
        \"config\": {
            \"model\": EMBEDDER_MODEL,
            **(
                {\"ollama_base_url\": OLLAMA_BASE_URL}
                if EMBEDDER_PROVIDER == \"ollama\"
                else {}
            ),
            \"embedding_dims\": EMBEDDING_DIM,
        },
    }'''

# Replace the entire embedder block (handle nested braces properly)
content = re.sub(
    r'\"embedder\":\s*\{[^}]*\"config\":\s*\{[^}]*\}[^}]*\}',
    new_embedder,
    content
)

# --- Step 4: Parameterize vector_store embedding_model_dims and hnsw ---
content = re.sub(
    r'\"embedding_model_dims\":\s*\d+',
    '\"embedding_model_dims\": EMBEDDING_DIM',
    content
)
if '\"hnsw\"' not in content:
    content = re.sub(
        r'(\"embedding_model_dims\":\s*EMBEDDING_DIM)',
        r'\1,\n                \"hnsw\": EMBEDDING_DIM <= 2000',
        content
    )

# --- Step 5: Add startup logging ---
log_line = '''
import logging as _log
_log.basicConfig(level=_log.INFO, format='%(asctime)s - %(levelname)s - %(message)s')
_log.info('mem0 config: LLM=openai/%s, Embedder=%s/%s (dim=%d, hnsw=%s)',
          LLM_MODEL, EMBEDDER_PROVIDER, EMBEDDER_MODEL, EMBEDDING_DIM, EMBEDDING_DIM <= 2000)
'''
if 'mem0 config: LLM=' not in content:
    content = re.sub(
        r'((?:MEMORY_INSTANCE|memory)\s*=\s*Memory\.from_config)',
        log_line + r'\n\1',
        content,
        count=1
    )

with open('main.py', 'w') as f:
    f.write(content)
"
echo "    → DEFAULT_CONFIG now reads LLM_MODEL, EMBEDDER_PROVIDER, EMBEDDER_MODEL,"
echo "      OLLAMA_BASE_URL, EMBEDDING_DIM from environment variables"
echo "    → Default: openai provider for both LLM and embedder (backward compatible)"
echo "    → Set EMBEDDER_PROVIDER=ollama in .env to use Ollama for embedding"
echo "    → Runtime fallback: main_override.py mount ensures correct behavior"

# Patch 5: Remove 'store' parameter from mem0 openai.py
# The 'store' param is OpenAI-specific (stored completions). When routed through
# LiteLLM to non-OpenAI backends (Anthropic, Qwen, etc.), it causes errors.
# Removing it has no effect on OpenAI users (default is false).
echo "  [Patch 5/7] Removing 'store' parameter from openai.py..."
# We need to patch the installed package, so we do it in the Dockerfile
# by adding a RUN step after pip install
STORE_PATCH='RUN python3 -c "\
f = next((p / \"mem0/llms/openai.py\" for p in __import__(\"site\").getsitepackages() \
          if (p_path := __import__(\"pathlib\").Path(p) / \"mem0/llms/openai.py\").exists()), None); \
assert f, \"mem0 openai.py not found\"; \
t = open(f).read(); \
t = t.replace(\"openai_specific_generation_params = [\\\"store\\\"]\", \
              \"openai_specific_generation_params = []\"); \
open(f, \"w\").write(t); \
print(\"Patched: removed store param from\", f)"'

# Patch 6: Remove 'top_p' from mem0 base.py
# Anthropic rejects requests with both temperature and top_p.
# top_p default is 1.0 (no effect), so removing it is safe for all backends.
echo "  [Patch 6/7] Removing 'top_p' parameter from base.py..."
TOP_P_PATCH='RUN python3 -c "\
f = next((p / \"mem0/llms/base.py\" for p in __import__(\"site\").getsitepackages() \
          if (p_path := __import__(\"pathlib\").Path(p) / \"mem0/llms/base.py\").exists()), None); \
assert f, \"mem0 base.py not found\"; \
t = open(f).read(); \
t = t.replace(\"\\\"top_p\\\": self.config.top_p,\", \
              \"# \\\"top_p\\\": removed for non-OpenAI backend compatibility\"); \
open(f, \"w\").write(t); \
print(\"Patched: removed top_p from\", f)"'

# Patch 7: Add ollama Python package to requirements.txt
# Required when EMBEDDER_PROVIDER=ollama. Pre-installing avoids:
# 1. 5-10s delay on every container start (pip install at runtime)
# 2. Failure in offline/sandbox environments
echo "  [Patch 7/7] Adding ollama package to requirements.txt..."
if ! grep -q "^ollama" requirements.txt; then
    echo "ollama>=0.4.0" >> requirements.txt
    echo "    → Added ollama>=0.4.0 to requirements.txt"
else
    echo "    → Already present"
fi

# --- Inject Patch 5 & 6 into Dockerfile (after pip install) ---
echo "  Injecting runtime patches into Dockerfile..."
if ! grep -q "store param" Dockerfile; then
    # Find the line "RUN pip install" and append our patches after it
    # We use a Python script for reliable multi-line insertion
    python3 -c "
with open('Dockerfile', 'r') as f:
    lines = f.readlines()

new_lines = []
for line in lines:
    new_lines.append(line)
    # Insert patches after the pip install line
    if line.strip().startswith('RUN pip install') or line.strip().startswith('RUN pip3 install'):
        new_lines.append('\n')
        new_lines.append('# Patch 5: Remove OpenAI-specific store parameter (breaks non-OpenAI backends)\n')
        new_lines.append('RUN python3 -c \"\\\n')
        new_lines.append('    import site, pathlib; \\\n')
        new_lines.append('    f = next((pathlib.Path(p) / \\\"mem0/llms/openai.py\\\" for p in site.getsitepackages() \\\n')
        new_lines.append('              if (pathlib.Path(p) / \\\"mem0/llms/openai.py\\\").exists()), None); \\\n')
        new_lines.append('    assert f, \\\"mem0 openai.py not found\\\"; \\\n')
        new_lines.append('    t = open(f).read().replace(\\\"openai_specific_generation_params = [\\\\\\\"store\\\\\\\"]\\\", \\\n')
        new_lines.append('                               \\\"openai_specific_generation_params = []\\\"); \\\n')
        new_lines.append('    open(f, \\\"w\\\").write(t); \\\n')
        new_lines.append('    print(\\\"Patched: removed store param from\\\", f)\"\n')
        new_lines.append('\n')
        new_lines.append('# Patch 6: Remove top_p parameter (Anthropic rejects temperature + top_p)\n')
        new_lines.append('RUN python3 -c \"\\\n')
        new_lines.append('    import site, pathlib; \\\n')
        new_lines.append('    f = next((pathlib.Path(p) / \\\"mem0/llms/base.py\\\" for p in site.getsitepackages() \\\n')
        new_lines.append('              if (pathlib.Path(p) / \\\"mem0/llms/base.py\\\").exists()), None); \\\n')
        new_lines.append('    assert f, \\\"mem0 base.py not found\\\"; \\\n')
        new_lines.append('    t = open(f).read().replace(\\\"\\\\\\\"top_p\\\\\\\": self.config.top_p,\\\", \\\n')
        new_lines.append('                               \\\"# \\\\\\\"top_p\\\\\\\": removed for non-OpenAI compatibility\\\"); \\\n')
        new_lines.append('    open(f, \\\"w\\\").write(t); \\\n')
        new_lines.append('    print(\\\"Patched: removed top_p from\\\", f)\"\n')

with open('Dockerfile', 'w') as f:
    f.writelines(new_lines)
print('Dockerfile patched with store + top_p removal')
"
    echo "    → Injected Patch 5 & 6 into Dockerfile"
else
    echo "    → Already injected"
fi

echo "  All patches applied."
echo ""

# Verify patches
echo "  Verifying patches..."
echo "    requirements.txt:"
grep -E "psycopg|ollama" requirements.txt | sed 's/^/      /'
echo "    main.py graph_store references:"
GRAPH_COUNT=$(grep -c "graph_store" main.py 2>/dev/null || echo "0")
echo "      graph_store occurrences: $GRAPH_COUNT (should be 0)"
echo "    main.py EMBEDDER_PROVIDER:"
grep "EMBEDDER_PROVIDER" main.py | head -3 | sed 's/^/      /'
echo "    Dockerfile store patch:"
grep -c "store param" Dockerfile | xargs -I{} echo "      occurrences: {} (should be 1)"
echo ""

# --- Build the image ---
echo "  Building mem0-server:latest..."
docker build --no-cache -t mem0-server:latest -f Dockerfile .
cd "$SCRIPT_DIR"
echo "  mem0-server:latest built successfully"
echo ""

# ------------------------------------------
# Phase 2: Pull pre-built images
# ------------------------------------------
echo "[Phase 2] Pulling pre-built images..."

declare -A PULL_IMAGES
PULL_IMAGES["pgvector"]="ankane/pgvector:v0.5.1"
PULL_IMAGES["lightrag"]="ghcr.io/hkuds/lightrag:latest"

for name in "${!PULL_IMAGES[@]}"; do
    image="${PULL_IMAGES[$name]}"
    echo "  [$name] Pulling $image..."
    docker pull "$image"
    echo "  [$name] Done"
done
echo ""

# ------------------------------------------
# Phase 3: Export all images as tar files
# ------------------------------------------
echo "[Phase 3] Exporting images to tar files..."

declare -A ALL_IMAGES
ALL_IMAGES["mem0-server"]="mem0-server:latest"
ALL_IMAGES["pgvector"]="ankane/pgvector:v0.5.1"
ALL_IMAGES["lightrag"]="ghcr.io/hkuds/lightrag:latest"

for name in "${!ALL_IMAGES[@]}"; do
    image="${ALL_IMAGES[$name]}"
    tar_file="$IMAGES_DIR/${name}.tar"

    echo "  [$name] Saving $image → $tar_file..."
    docker save "$image" -o "$tar_file"

    size=$(du -h "$tar_file" | cut -f1)
    echo "  [$name] Done: $size"
done
echo ""

# ------------------------------------------
# Summary
# ------------------------------------------
echo "============================================"
echo "  Image Export Summary"
echo "============================================"
echo ""
ls -lh "$IMAGES_DIR"/*.tar
echo ""

TOTAL=$(du -sh "$IMAGES_DIR" | cut -f1)
echo "Total size: $TOTAL"
echo ""
echo "Images ready for sandbox deployment:"
echo "  - mem0-server:latest      (built from source, patched)"
echo "  - ankane/pgvector:v0.5.1  (PostgreSQL + vector extension)"
echo "  - ghcr.io/hkuds/lightrag  (LightRAG server)"
echo ""
echo "Applied patches to mem0-server:"
echo "  1. psycopg[binary,pool]  - pre-compiled PostgreSQL bindings for slim image"
echo "  2. Remove graph_store    - disable neo4j dependency (not deployed by default)"
echo "  3. /app/data directory   - for SQLite history database persistence"
echo "  4. Config parameterized  - LLM_MODEL, EMBEDDER_PROVIDER, EMBEDDING_DIM via env vars"
echo "  5. Remove 'store' param  - OpenAI-specific, breaks non-OpenAI backends"
echo "  6. Remove 'top_p' param  - Anthropic rejects temperature + top_p together"
echo "  7. ollama pre-installed  - no pip install at runtime, works offline"
echo ""
echo "Configuration (via environment variables in .env):"
echo "  EMBEDDER_PROVIDER=openai  → use LiteLLM/OpenAI for embedding (default)"
echo "  EMBEDDER_PROVIDER=ollama  → use local Ollama for embedding"
echo ""
echo "Next steps:"
echo "  1. Copy entire ragmem/ directory into sandbox"
echo "  2. In sandbox, run: ragmem\stack\deploy.bat"
echo ""
