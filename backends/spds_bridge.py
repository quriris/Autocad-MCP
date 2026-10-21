"""The COM side of the nanoCAD СПДС tools: talk to the McpSpds plugin.

СПДС objects are MultiCAD objects that plain ActiveX cannot create or read
(nanoCAD 24.1 shows them to COM as an anonymous ``IAcadEntity``). The
``nanocad_plugin/McpSpds`` plugin runs inside nanoCAD and does, through
MultiCAD.NET. This module is the server's half of the conversation:

1. write ``%TEMP%/acadmcp-spds/request.json`` (``{"id", "op", "args"}``),
   atomically, so the plugin never reads half a request;
2. send the ``MCP_SPDS`` command to the document over COM;
3. wait for ``response-<id>.json`` and return its ``result``, or raise its
   ``error``.

The functions take the COM document as an argument and do no COM work of their
own beyond ``GetVariable`` / ``SetVariable`` / ``SendCommand``, so they are
testable with a fake document on any OS.
"""

from __future__ import annotations

import json
import os
import tempfile
import time
import uuid
from pathlib import Path
from typing import Any

#: The command the plugin registers (``[CommandMethod("MCP_SPDS")]``).
COMMAND = "MCP_SPDS"

#: The plugin built into this repository.
DEFAULT_PLUGIN = Path(__file__).resolve().parents[1] / "nanocad_plugin" / "bin" / "McpSpds.dll"

#: What the server tells the operator when the plugin does not answer.
NOT_LOADED_HINT = (
    "The nanoCAD СПДС plugin (McpSpds.dll) did not answer. Load it once with the "
    "spds_plugin_load tool, or in nanoCAD run NETLOAD and pick "
    f"{DEFAULT_PLUGIN}; to load it on every start add it to the startup list "
    "(APPLOAD / «Загрузить приложения»)."
)


def bridge_dir() -> Path:
    """Where requests and responses are exchanged: the user's temp folder.

    Both processes resolve it the same way (``%TEMP%``), the server through
    ``tempfile`` and the plugin through ``Path.GetTempPath()``.
    """
    override = os.environ.get("SPDS_BRIDGE_DIR", "").strip()
    return Path(override) if override else Path(tempfile.gettempdir()) / "acadmcp-spds"


def plugin_path() -> Path:
    override = os.environ.get("SPDS_PLUGIN_PATH", "").strip()
    return Path(override) if override else DEFAULT_PLUGIN


def write_request(op: str, args: dict | None, folder: Path | None = None) -> str:
    """Write the request atomically; returns its id."""
    folder = folder or bridge_dir()
    folder.mkdir(parents=True, exist_ok=True)
    request_id = uuid.uuid4().hex
    payload = json.dumps({"id": request_id, "op": op, "args": args or {}}, ensure_ascii=False)
    tmp = folder / f"request-{request_id}.tmp"
    tmp.write_text(payload, encoding="utf-8")
    os.replace(tmp, folder / "request.json")
    return request_id


def wait_response(
    request_id: str,
    timeout: float,
    folder: Path | None = None,
    poll: float = 0.05,
    sleep=time.sleep,
    clock=time.monotonic,
) -> dict:
    """The plugin's reply to ``request_id``; ``TimeoutError`` if it never comes."""
    folder = folder or bridge_dir()
    target = folder / f"response-{request_id}.json"
    deadline = clock() + timeout
    while True:
        if target.is_file():
            try:
                data = json.loads(target.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                data = None  # caught mid-rename; the next poll reads it whole
            if data is not None:
                try:
                    target.unlink()
                except OSError:
                    pass
                return data
        if clock() >= deadline:
            raise TimeoutError(NOT_LOADED_HINT)
        sleep(poll)


def _require_idle(doc) -> None:
    try:
        busy = int(doc.GetVariable("CMDACTIVE"))
    except Exception:
        busy = 0
    if busy:
        raise RuntimeError(
            "nanoCAD has an active command or prompt (CMDACTIVE). Press ESC in nanoCAD and retry."
        )


def call(doc, op: str, args: dict | None = None, timeout: float = 30.0, **wait_kw) -> Any:
    """Run one plugin operation and return its ``result``."""
    _require_idle(doc)
    folder = wait_kw.pop("folder", None) or bridge_dir()
    request_id = write_request(op, args, folder)
    try:
        doc.SendCommand(f"{COMMAND}\n")
        reply = wait_response(request_id, timeout, folder, **wait_kw)
    finally:
        stale = folder / "request.json"
        # A request the plugin never consumed must not be answered later, by
        # a stray MCP_SPDS typed in nanoCAD, in place of the next one.
        try:
            if stale.is_file() and request_id in stale.read_text(encoding="utf-8"):
                stale.unlink()
        except OSError:
            pass
    if not reply.get("ok"):
        raise RuntimeError(f"СПДС plugin: {reply.get('error', 'unknown error')}")
    return reply.get("result")


def netload(doc, path: Path | None = None) -> str:
    """Ask nanoCAD to load the plugin; returns the path it was asked to load.

    ``NETLOAD`` opens a file dialog unless FILEDIA is 0, in which case it reads
    the path from the command line. FILEDIA is put back afterwards.
    """
    target = Path(path) if path else plugin_path()
    if not target.is_file():
        raise FileNotFoundError(f"plugin not found: {target}")
    _require_idle(doc)
    try:
        old = int(doc.GetVariable("FILEDIA"))
    except Exception:
        old = None
    try:
        if old not in (None, 0):
            doc.SetVariable("FILEDIA", 0)
        doc.SendCommand(f'_NETLOAD "{target}"\n')
        # SendCommand can return before the command has run; restoring FILEDIA
        # too early would turn the queued NETLOAD into a file dialog.
        deadline = time.monotonic() + 15.0
        while time.monotonic() < deadline:
            try:
                if not int(doc.GetVariable("CMDACTIVE")):
                    break
            except Exception:
                break
            time.sleep(0.1)
    finally:
        if old not in (None, 0):
            try:
                doc.SetVariable("FILEDIA", old)
            except Exception:
                pass
    return str(target)
