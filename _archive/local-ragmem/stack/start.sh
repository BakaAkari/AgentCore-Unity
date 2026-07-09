#!/bin/bash
# ============================================
# RagMem - Deployment Script
# Run inside WSL2 Ubuntu-24.04
# ============================================
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo "============================================"
echo "  RagMem - Deployment"
echo "============================================"
echo ""

# ------------------------------------------
# Pre-flight checks
# ------------------------------------------
echo -e "${YELLOW}[1/6] Pre-flight checks...${NC}"

# Check Docker
if ! command -v docker &> /dev/null; then
    echo -e "${RED}ERROR: Docker not found${NC}"
    exit 1
fi
echo "  Docker: $(docker --version)"

# Check Docker Compose
if ! docker compose version &> /dev/null; then
    echo -e "${RED}ERROR: Docker Compose not found${NC}"
    exit 1
fi
echo "  Compose: $(docker compose version --short)"

# Check Docker daemon
if ! docker info &> /dev/null 2>&1; then
    echo -e "${RED}ERROR: Docker daemon not running${NC}"
    echo "  Try: sudo systemctl start docker"
    exit 1
fi
echo "  Daemon: running"

# Check .env file
if [ ! -f .env ]; then
    echo -e "${RED}ERROR: .env file not found${NC}"
    echo "  Copy and edit the example:"
    echo "    cp .env.example .env"
    echo "    nano .env"
    exit 1
fi
echo "  Config: .env found"

# Strip Windows \r line endings (files piped from Windows via deploy.bat keep CRLF)
if grep -qP '\r' .env 2>/dev/null; then
    sed -i 's/\r$//' .env
    echo "  Config: stripped CRLF line endings"
fi

# Check required env vars
source .env
if [ -z "$LITELLM_BASE_URL" ] || [ "$LITELLM_BASE_URL" = "http://your-litellm-endpoint:port" ]; then
    echo -e "${RED}ERROR: LITELLM_BASE_URL not configured in .env${NC}"
    exit 1
fi
if [ -z "$LITELLM_API_KEY" ] || [ "$LITELLM_API_KEY" = "your-api-key-here" ]; then
    echo -e "${RED}ERROR: LITELLM_API_KEY not configured in .env${NC}"
    exit 1
fi
echo "  LiteLLM: $LITELLM_BASE_URL"
echo ""

# ------------------------------------------
# Check disk space
# ------------------------------------------
echo -e "${YELLOW}[2/6] Checking disk space...${NC}"
AVAIL_MB=$(df -m "$SCRIPT_DIR" | awk 'NR==2 {print $4}')
echo "  Available: ${AVAIL_MB}MB"
if [ "$AVAIL_MB" -lt 2048 ]; then
    echo -e "${RED}WARNING: Less than 2GB available. Deployment may fail.${NC}"
    read -p "  Continue anyway? (y/N) " -n 1 -r
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        exit 1
    fi
fi
echo ""

# ------------------------------------------
# Check if images are loaded
# ------------------------------------------
echo -e "${YELLOW}[3/6] Checking Docker images...${NC}"

# Image name → tar file name mapping
declare -A IMAGE_MAP
IMAGE_MAP["mem0-server:latest"]="mem0-server"
IMAGE_MAP["ankane/pgvector:v0.5.1"]="pgvector"
IMAGE_MAP["ghcr.io/hkuds/lightrag:latest"]="lightrag"

IMAGES_MISSING=()

for img in "${!IMAGE_MAP[@]}"; do
    if docker image inspect "$img" &> /dev/null; then
        echo -e "  ${GREEN}✓${NC} $img"
    else
        echo -e "  ${RED}✗${NC} $img (not found)"
        IMAGES_MISSING+=("$img")
    fi
done

if [ ${#IMAGES_MISSING[@]} -gt 0 ]; then
    echo ""
    echo -e "${YELLOW}Missing images. Looking for tar files...${NC}"

    # Try to load from tar files in parent images/ directory
    IMAGES_DIR="${SCRIPT_DIR}/../images"
    if [ -d "$IMAGES_DIR" ]; then
        for tar_file in "$IMAGES_DIR"/*.tar; do
            if [ -f "$tar_file" ]; then
                echo "  Loading: $(basename $tar_file)..."
                docker load -i "$tar_file"
            fi
        done
    else
        echo -e "${RED}ERROR: No images/ directory found at $IMAGES_DIR${NC}"
        echo "  Please load images manually:"
        for img in "${IMAGES_MISSING[@]}"; do
            tar_name="${IMAGE_MAP[$img]}"
            echo "    docker load -i <path-to-${tar_name}.tar>"
        done
        exit 1
    fi

    # Re-check
    for img in "${IMAGES_MISSING[@]}"; do
        if ! docker image inspect "$img" &> /dev/null; then
            echo -e "${RED}ERROR: Image $img still not available${NC}"
            exit 1
        fi
    done
fi
echo ""

# ------------------------------------------
# Create data directories
# ------------------------------------------
echo -e "${YELLOW}[4/6] Creating data directories...${NC}"
mkdir -p lightrag/data
mkdir -p lightrag/documents
echo "  Done (pgvector uses Docker named volume)"
echo ""

# ------------------------------------------
# Start services
# ------------------------------------------
echo -e "${YELLOW}[5/6] Starting services...${NC}"
docker compose up -d
echo ""

# ------------------------------------------
# Health checks
# ------------------------------------------
echo -e "${YELLOW}[6/6] Waiting for services to be healthy...${NC}"
echo "  (This may take 30-60 seconds)"
echo ""

MAX_WAIT=90
INTERVAL=5
ELAPSED=0

while [ $ELAPSED -lt $MAX_WAIT ]; do
    PGVECTOR_OK=false
    MEM0_OK=false
    LIGHTRAG_OK=false

    # Check pgvector
    if docker compose exec -T pgvector pg_isready -U "${POSTGRES_USER:-mem0}" &> /dev/null; then
        PGVECTOR_OK=true
    fi

    # Check mem0 (no /health endpoint; use /docs instead)
    if curl -sf http://localhost:${MEM0_PORT:-18910}/docs > /dev/null 2>&1; then
        MEM0_OK=true
    fi

    # Check LightRAG
    if curl -sf http://localhost:${LIGHTRAG_PORT:-18920}/health > /dev/null 2>&1; then
        LIGHTRAG_OK=true
    fi

    # Print status
    echo -ne "\r  pgvector: $([ "$PGVECTOR_OK" = true ] && echo -e "${GREEN}UP${NC}" || echo -e "${RED}...${NC}")  "
    echo -ne "mem0: $([ "$MEM0_OK" = true ] && echo -e "${GREEN}UP${NC}" || echo -e "${RED}...${NC}")  "
    echo -ne "LightRAG: $([ "$LIGHTRAG_OK" = true ] && echo -e "${GREEN}UP${NC}" || echo -e "${RED}...${NC}")  "
    echo -ne "[${ELAPSED}s/${MAX_WAIT}s]"

    if [ "$PGVECTOR_OK" = true ] && [ "$MEM0_OK" = true ] && [ "$LIGHTRAG_OK" = true ]; then
        echo ""
        break
    fi

    sleep $INTERVAL
    ELAPSED=$((ELAPSED + INTERVAL))
done

echo ""
echo "============================================"
echo "  Deployment Summary"
echo "============================================"
echo ""

# Final status
docker compose ps --format "table {{.Name}}\t{{.Status}}\t{{.Ports}}"

echo ""
echo "Service Endpoints:"
echo "  mem0 API:    http://localhost:${MEM0_PORT:-18910}  (also accessible from Windows)"
echo "  LightRAG:    http://localhost:${LIGHTRAG_PORT:-18920}  (also accessible from Windows)"
echo "  pgvector:    localhost:${POSTGRES_PORT:-18930} (internal only)"
echo ""

# Quick test
echo "Quick Test:"
echo "  curl http://localhost:${MEM0_PORT:-18910}/docs       # mem0 (Swagger UI)"
echo "  curl http://localhost:${LIGHTRAG_PORT:-18920}/health  # LightRAG"
echo ""

if [ "$PGVECTOR_OK" = true ] && [ "$MEM0_OK" = true ] && [ "$LIGHTRAG_OK" = true ]; then
    echo -e "${GREEN}All services are running!${NC}"
else
    echo -e "${YELLOW}Some services may still be starting. Check logs:${NC}"
    echo "  docker compose logs -f"
fi
echo ""
