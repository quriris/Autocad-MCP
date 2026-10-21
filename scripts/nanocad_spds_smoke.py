# scripts/nanocad_spds_smoke.py
"""Try the СПДС plugin against the nanoCAD on this machine — one JSON report.

1. Loads nanocad_plugin/bin/McpSpds.dll into the running nanoCAD (NETLOAD)
   unless it already answers, and pings it.
2. READ-ONLY on the drawing you have open: lists its СПДС objects and reads
   every parameter of one object of each class. This is how the parameter
   names of the stamp fields and of the level mark are learned.
3. In a NEW drawing: creates an A3 format, a position leader, a multilayer
   leader and a break, copies one object, lists them — then closes that
   drawing without saving.

Open nanoCAD СПДС with one of your schemes (for step 2), then:

    .venv\\Scripts\\python scripts\\nanocad_spds_smoke.py

The report (nanocad_spds_smoke_report.json) contains parameter VALUES of the
open drawing's objects — the stamp's text, for instance. Look through it
before sharing if the drawing is confidential, or open a copy.
"""

from __future__ import annotations

import asyncio
import json
import os
import sys
import traceback
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
os.environ.setdefault("AUTOCAD_MCP_BACKEND", "nanocad")

REPORT = ROOT / "nanocad_spds_smoke_report.json"


async def run(report: dict) -> None:
    from backends.com_backend import ComBackend

    b = ComBackend()
    await b.connect()
    steps: list[dict] = []
    report["steps"] = steps

    async def step(name, coro_factory):
        try:
            result = await coro_factory()
            steps.append({"step": name, "ok": True, "result": result})
            print(f"  OK   {name}")
            return result
        except Exception as exc:
            steps.append(
                {
                    "step": name,
                    "ok": False,
                    "error": f"{type(exc).__name__}: {exc}"[:1500],
                    "trace": traceback.format_exc()[-1500:],
                }
            )
            print(f"  FAIL {name} -> {exc}"[:200])
            return None

    # 1. plugin
    try:
        ping = await b.spds_call("ping", timeout=5)
        steps.append({"step": "ping (was already loaded)", "ok": True, "result": ping})
        print("  OK   plugin already loaded")
    except Exception:
        ping = await step("spds_plugin_load", lambda: b.spds_plugin_load())
    if ping is None:
        from backends import spds_bridge

        marker = spds_bridge.loaded_marker()
        report["bridge_dir"] = str(spds_bridge.bridge_dir())
        report["loaded_marker"] = marker.read_text("utf-8") if marker.is_file() else None
        report["plugin_log"] = spds_bridge.log_tail(lines=40)
        return

    # 2. read-only on the open drawing
    listed = await step("list (open drawing)", lambda: b.spds_call("list", {"limit": 5000}))
    if listed:
        one_per_class: dict[str, str] = {}
        for obj in listed.get("objects", []):
            one_per_class.setdefault(obj.get("mc_class", "?"), obj["handle"])
        report["classes_in_open_drawing"] = {
            k: sum(1 for o in listed["objects"] if o.get("mc_class") == k) for k in one_per_class
        }
        if one_per_class:
            await step(
                "describe one of each class (open drawing)",
                lambda: b.spds_call("describe", {"handles": list(one_per_class.values())}),
            )
    await step("formats (open drawing)", lambda: b.spds_call("formats"))

    # 3. create in a new drawing
    await step("drawing_new", lambda: b.drawing_new())
    fmt = await step(
        "format_create A3",
        lambda: b.spds_call(
            "format_create",
            {"size": "A3", "origin": [0, 0, 0], "horizontal": True, "template": "SPDS"},
        ),
    )
    await step(
        "note_position_create",
        lambda: b.spds_call(
            "note_position_create",
            {
                "origin": [200, 150, 0],
                "first_line": "1",
                "second_line": "",
                "targets": [[150, 100, 0]],
            },
        ),
    )
    await step(
        "note_multilayer_create",
        lambda: b.spds_call(
            "note_multilayer_create",
            {
                "start": [100, 200, 0],
                "end": [160, 240, 0],
                "lines": ["Бетон В15 - 100 мм", "Песок - 200 мм"],
            },
        ),
    )
    brk = await step(
        "break_create",
        lambda: b.spds_call(
            "break_create", {"start": [50, 50, 0], "end": [50, 120, 0], "type": "Linear"}
        ),
    )
    source = (brk or fmt or {}).get("handle") if isinstance(brk or fmt, dict) else None
    if source:
        await step(
            "clone (+100, 0)",
            lambda: b.spds_call("clone", {"handle": source, "dx": 100, "dy": 0}),
        )
    await step("list (new drawing)", lambda: b.spds_call("list", {"limit": 100}))
    await step("screenshot", lambda: _shot(b))
    await step("close new drawing", lambda: b.drawing_close(save=False))


async def _shot(b) -> dict:
    data = await b.view_screenshot()
    path = Path(os.environ.get("TEMP", ".")) / "nanocad_spds_smoke.png"
    if data:
        path.write_bytes(data)
    return {"path": str(path), "bytes": len(data or b"")}


def main() -> int:
    if not sys.platform.startswith("win"):
        print("Run this on Windows with nanoCAD СПДС open.")
        return 2
    report: dict = {}
    try:
        asyncio.run(run(report))
    except Exception as exc:
        report["crash"] = f"{type(exc).__name__}: {exc}"
    REPORT.write_text(
        json.dumps(report, ensure_ascii=False, indent=2, default=str), encoding="utf-8"
    )
    failed = [s["step"] for s in report.get("steps", []) if not s["ok"]]
    print("Report:", REPORT)
    if "crash" in report:
        print("Crash:", report["crash"])
    return 0 if not failed and "crash" not in report else 1


if __name__ == "__main__":
    sys.exit(main())
