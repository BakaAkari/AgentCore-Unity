"""HTTP client for LightRAG API.

LightRAG API reference (based on official LightRAG server):
  GET  /health                — Health check
  POST /documents/text        — Index text content
  POST /documents/file        — Index a file (multipart upload)
  POST /query                 — Query the knowledge base
  GET  /documents              — List indexed documents
  DELETE /documents/{id}       — Delete a document
"""

from __future__ import annotations

import httpx
from typing import Any


class LightRAGClient:
    """Async HTTP client for LightRAG server API."""

    def __init__(self, base_url: str = "http://localhost:18920", timeout: float = 60.0):
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout

    def _client(self) -> httpx.AsyncClient:
        return httpx.AsyncClient(
            base_url=self.base_url,
            timeout=self.timeout,
            headers={"Content-Type": "application/json"},
        )

    async def health(self) -> dict[str, Any]:
        """Check LightRAG health."""
        async with self._client() as client:
            resp = await client.get("/health")
            if resp.status_code == 200:
                return {"status": "ok", "endpoint": f"{self.base_url}/health"}
            return {"status": "error", "code": resp.status_code, "detail": resp.text[:200]}

    async def index_text(
        self,
        text: str,
        description: str | None = None,
    ) -> dict[str, Any]:
        """Index text content into the knowledge base.

        Args:
            text: The text content to index.
            description: Optional description of the content.
        """
        payload: dict[str, Any] = {"text": text}
        if description:
            payload["description"] = description

        async with self._client() as client:
            resp = await client.post("/documents/text", json=payload)
            resp.raise_for_status()
            return resp.json()

    async def index_file(self, file_path: str) -> dict[str, Any]:
        """Index a file into the knowledge base.

        Args:
            file_path: Path to the file to index (must be accessible from the LightRAG container).
                       For local files, place them in the lightrag/documents/ bind mount directory.
        """
        async with httpx.AsyncClient(base_url=self.base_url, timeout=self.timeout) as client:
            with open(file_path, "rb") as f:
                resp = await client.post(
                    "/documents/file",
                    files={"file": (file_path.split("/")[-1].split("\\")[-1], f)},
                )
            resp.raise_for_status()
            return resp.json()

    async def query(
        self,
        query: str,
        mode: str = "hybrid",
    ) -> dict[str, Any]:
        """Query the knowledge base.

        Args:
            query: The query text.
            mode: Search mode — one of 'naive', 'local', 'global', 'hybrid' (default: 'hybrid').
                  - naive: Simple vector similarity search
                  - local: Entity-centric search using knowledge graph neighborhoods
                  - global: High-level theme search using community summaries
                  - hybrid: Combines local + global for best results
        """
        payload: dict[str, Any] = {
            "query": query,
            "mode": mode,
        }

        async with self._client() as client:
            resp = await client.post("/query", json=payload)
            resp.raise_for_status()
            return resp.json()

    async def list_documents(self) -> dict[str, Any]:
        """List all indexed documents."""
        async with self._client() as client:
            resp = await client.get("/documents")
            resp.raise_for_status()
            return resp.json()

    async def delete_document(self, document_id: str) -> dict[str, Any]:
        """Delete a document from the knowledge base.

        Args:
            document_id: The document ID to delete.
        """
        async with self._client() as client:
            resp = await client.delete(f"/documents/{document_id}")
            resp.raise_for_status()
            return {"status": "deleted", "document_id": document_id}
