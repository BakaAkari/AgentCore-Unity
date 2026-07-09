"""HTTP client for mem0 API.

mem0 API reference (self-hosted mem0 server — NO /v1 prefix):
  POST /memories          — Add memory
  POST /search            — Search memories
  GET  /memories          — List memories (query param: user_id)
  DELETE /memories/{id}   — Delete a memory
  GET  /docs              — Swagger UI (used as health check)

Note: The self-hosted mem0 server (main_override.py) does NOT use /v1 prefix.
      Only the mem0 cloud API uses /v1. Since we deploy self-hosted,
      all paths here are prefix-free.
      Search endpoint is POST /search (not /memories/search).
"""

from __future__ import annotations

import httpx
from typing import Any


class Mem0Client:
    """Async HTTP client for mem0 server API."""

    def __init__(self, base_url: str = "http://localhost:18910", timeout: float = 30.0):
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout

    def _client(self) -> httpx.AsyncClient:
        return httpx.AsyncClient(
            base_url=self.base_url,
            timeout=self.timeout,
            headers={"Content-Type": "application/json"},
        )

    async def health(self) -> dict[str, Any]:
        """Check mem0 health by hitting /docs endpoint."""
        async with self._client() as client:
            resp = await client.get("/docs")
            if resp.status_code == 200:
                return {"status": "ok", "endpoint": f"{self.base_url}/docs"}
            return {"status": "error", "code": resp.status_code, "detail": resp.text[:200]}

    async def add(
        self,
        content: str,
        user_id: str,
        metadata: dict[str, Any] | None = None,
        agent_id: str | None = None,
    ) -> dict[str, Any]:
        """Add a memory.

        Args:
            content: The memory content text.
            user_id: User identifier for memory scoping.
            metadata: Optional metadata dict.
            agent_id: Optional agent identifier.
        """
        payload: dict[str, Any] = {
            "messages": [{"role": "user", "content": content}],
            "user_id": user_id,
        }
        if metadata:
            payload["metadata"] = metadata
        if agent_id:
            payload["agent_id"] = agent_id

        async with self._client() as client:
            resp = await client.post("/memories", json=payload)
            resp.raise_for_status()
            return resp.json()

    async def search(
        self,
        query: str,
        user_id: str,
        limit: int = 10,
        agent_id: str | None = None,
    ) -> dict[str, Any]:
        """Search memories by semantic similarity.

        Args:
            query: Search query text.
            user_id: User identifier.
            limit: Maximum number of results.
            agent_id: Optional agent identifier.
        """
        payload: dict[str, Any] = {
            "query": query,
            "user_id": user_id,
            "limit": limit,
        }
        if agent_id:
            payload["agent_id"] = agent_id

        async with self._client() as client:
            resp = await client.post("/search", json=payload)
            resp.raise_for_status()
            return resp.json()

    async def list(self, user_id: str, agent_id: str | None = None) -> dict[str, Any]:
        """List all memories for a user.

        Args:
            user_id: User identifier.
            agent_id: Optional agent identifier.
        """
        params: dict[str, str] = {"user_id": user_id}
        if agent_id:
            params["agent_id"] = agent_id

        async with self._client() as client:
            resp = await client.get("/memories", params=params)
            resp.raise_for_status()
            return resp.json()

    async def delete(self, memory_id: str) -> dict[str, Any]:
        """Delete a specific memory.

        Args:
            memory_id: The memory ID to delete.
        """
        async with self._client() as client:
            resp = await client.delete(f"/memories/{memory_id}")
            resp.raise_for_status()
            return {"status": "deleted", "memory_id": memory_id}
