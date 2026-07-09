"""RagMem MCP Server — mem0 (memory) + LightRAG (knowledge base) tools.

Environment variables:
    MEM0_URL        — mem0 server URL (default: http://localhost:18910)
    LIGHTRAG_URL    — LightRAG server URL (default: http://localhost:18920)
    RAGMEM_USER_ID  — Default user_id for mem0 operations (default: "default")
    RAGMEM_AGENT_ID — Optional agent_id for mem0 scoping

Usage:
    # stdio mode (recommended for MCP clients)
    ragmem-mcp-server

    # or via uvx
    uvx --from ragmem-mcp ragmem-mcp-server
"""

from __future__ import annotations

import os
import logging

from fastmcp import FastMCP

from ragmem_mcp.mem0_client import Mem0Client
from ragmem_mcp.lightrag_client import LightRAGClient

logger = logging.getLogger("ragmem-mcp")

# ---------------------------------------------------------------------------
# Configuration from environment
# ---------------------------------------------------------------------------
MEM0_URL = os.environ.get("MEM0_URL", "http://localhost:18910")
LIGHTRAG_URL = os.environ.get("LIGHTRAG_URL", "http://localhost:18920")
DEFAULT_USER_ID = os.environ.get("RAGMEM_USER_ID", "default")
DEFAULT_AGENT_ID = os.environ.get("RAGMEM_AGENT_ID", None)

# ---------------------------------------------------------------------------
# Clients
# ---------------------------------------------------------------------------
mem0 = Mem0Client(base_url=MEM0_URL)
lightrag = LightRAGClient(base_url=LIGHTRAG_URL)

# ---------------------------------------------------------------------------
# MCP Server
# ---------------------------------------------------------------------------
mcp = FastMCP(
    "ragmem",
    instructions=(
        "RagMem provides two capabilities:\n"
        "1. **Memory** (mem0): Store and retrieve cross-session memories per user. "
        "Use memory_add to save important decisions, preferences, or context. "
        "Use memory_search to find relevant past memories.\n"
        "2. **Knowledge Base** (LightRAG): Index documents and query them with RAG. "
        "Use rag_index_text to add knowledge, rag_query to search it.\n\n"
        "Default user_id is auto-configured. You don't need to specify it unless "
        "working with multiple users."
    ),
)


# ===========================================================================
# mem0 Tools
# ===========================================================================


@mcp.tool()
async def memory_add(
    content: str,
    user_id: str | None = None,
    metadata: dict | None = None,
) -> dict:
    """Store a memory for future retrieval.

    Use this to save important decisions, user preferences, project context,
    or any information that should persist across sessions.

    Args:
        content: The memory content to store (natural language text).
        user_id: User identifier (defaults to configured RAGMEM_USER_ID).
        metadata: Optional key-value metadata to attach to the memory.

    Returns:
        The created memory object with its ID.
    """
    uid = user_id or DEFAULT_USER_ID
    return await mem0.add(
        content=content,
        user_id=uid,
        metadata=metadata,
        agent_id=DEFAULT_AGENT_ID,
    )


@mcp.tool()
async def memory_search(
    query: str,
    user_id: str | None = None,
    limit: int = 10,
) -> dict:
    """Search memories by semantic similarity.

    Use this to find relevant past memories, decisions, or context
    that may help with the current task.

    Args:
        query: Natural language search query.
        user_id: User identifier (defaults to configured RAGMEM_USER_ID).
        limit: Maximum number of results to return (default: 10).

    Returns:
        List of matching memories ranked by relevance.
    """
    uid = user_id or DEFAULT_USER_ID
    return await mem0.search(
        query=query,
        user_id=uid,
        limit=limit,
        agent_id=DEFAULT_AGENT_ID,
    )


@mcp.tool()
async def memory_list(user_id: str | None = None) -> dict:
    """List all stored memories for a user.

    Args:
        user_id: User identifier (defaults to configured RAGMEM_USER_ID).

    Returns:
        All memories for the specified user.
    """
    uid = user_id or DEFAULT_USER_ID
    return await mem0.list(user_id=uid, agent_id=DEFAULT_AGENT_ID)


@mcp.tool()
async def memory_delete(memory_id: str) -> dict:
    """Delete a specific memory by its ID.

    Args:
        memory_id: The ID of the memory to delete.

    Returns:
        Confirmation of deletion.
    """
    return await mem0.delete(memory_id=memory_id)


# ===========================================================================
# LightRAG Tools
# ===========================================================================


@mcp.tool()
async def rag_index_text(
    text: str,
    description: str | None = None,
) -> dict:
    """Index text content into the knowledge base.

    Use this to add documentation, code explanations, architecture decisions,
    or any reference material to the searchable knowledge base.

    Args:
        text: The text content to index.
        description: Optional description of what this content is about.

    Returns:
        Indexing result with document ID.
    """
    return await lightrag.index_text(text=text, description=description)


@mcp.tool()
async def rag_index_file(file_path: str) -> dict:
    """Index a file into the knowledge base.

    The file must be accessible from the machine running this MCP server.
    For best results, place files in the LightRAG documents directory.

    Args:
        file_path: Path to the file to index.

    Returns:
        Indexing result with document ID.
    """
    return await lightrag.index_file(file_path=file_path)


@mcp.tool()
async def rag_query(
    query: str,
    mode: str = "hybrid",
) -> dict:
    """Query the knowledge base using RAG.

    Searches indexed documents and returns relevant information.

    Args:
        query: Natural language query.
        mode: Search mode (default: 'hybrid'). Options:
            - 'naive': Simple vector similarity search
            - 'local': Entity-centric search using knowledge graph
            - 'global': High-level theme search using community summaries
            - 'hybrid': Combines local + global (recommended)

    Returns:
        Query results with relevant content.
    """
    return await lightrag.query(query=query, mode=mode)


@mcp.tool()
async def rag_list_documents() -> dict:
    """List all documents indexed in the knowledge base.

    Returns:
        List of indexed documents with their metadata.
    """
    return await lightrag.list_documents()


# ===========================================================================
# Health / Utility Tools
# ===========================================================================


@mcp.tool()
async def ragmem_health() -> dict:
    """Check health status of all RagMem services (mem0 + LightRAG).

    Returns:
        Health status for each service.
    """
    mem0_health = await mem0.health()
    lightrag_health = await lightrag.health()

    all_ok = mem0_health.get("status") == "ok" and lightrag_health.get("status") == "ok"

    return {
        "overall": "ok" if all_ok else "degraded",
        "mem0": mem0_health,
        "lightrag": lightrag_health,
        "config": {
            "mem0_url": MEM0_URL,
            "lightrag_url": LIGHTRAG_URL,
            "default_user_id": DEFAULT_USER_ID,
        },
    }


# ===========================================================================
# Entry point
# ===========================================================================


def main():
    """Run the MCP server (stdio transport)."""
    mcp.run()


if __name__ == "__main__":
    main()
