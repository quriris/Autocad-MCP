# scripts/nanocad_spds_smoke.py
"""Try the СПДС plugin against the nanoCAD on this machine — one JSON report.

1. Loads nanocad_plugin/bin/McpSpds.dll into the running nanoCAD (NETLOAD)
   unless it already answers, and pings it.
2. READ-ONLY on the drawing you have open: counts its СПДС objects by class
   and takes the class of a level mark (nothing else of it goes in the report).
3. In a NEW drawing: an A3 format at scale 1 with test stamp fields, a
   position leader, a break and a copy of it, two level marks at y=0 and
   y=3000 (to see how the mark takes its value) and an attempt to set one by
   hand — then closes that drawing without saving.

Open nanoCAD СПДС with one of your schemes (for step 2), then:

    .venv\\Scripts\\python scripts\\nanocad_spds_smoke.py

The report (nanocad_spds_smoke_report.json) holds only class counts from the
open drawing; everything else in it comes from the new test drawing.
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

    # 2. read-only on the open drawing: what is there, and the class of a level mark
    listed = await step("list (open drawing)", lambda: b.spds_call("list", {"limit": 5000}))
    mark_class = None
    if listed:
        counts: dict[str, int] = {}
        for obj in listed.get("objects", []):
            key = f"{obj.get('mc_class')} / {obj.get('type')}"
            counts[key] = counts.get(key, 0) + 1
            if mark_class is None and "уровн" in (obj.get("mc_class") or "").lower():
                mark_class = obj.get("class_id")
        report["classes_in_open_drawing"] = counts
        report["level_mark_class_id"] = mark_class
        # The listing is reported as counts only: positions and handles of the
        # operator's own drawing are not needed in the report.
        steps[-1]["result"] = {"total": listed.get("total")}

    # 3. create in a new drawing
    await step("drawing_new", lambda: b.drawing_new())
    await step(
        "format_create A3 scale 1 + stamp fields",
        lambda: b.spds_call(
            "format_create",
            {
                "size": "A3",
                "origin": [0, 0, 0],
                "horizontal": True,
                "template": "SPDS",
                "scale": 1,
                "props": {
                    "Designation": "ТЕСТ-001",
                    "Drawing type": "Исполнительная схема (тест)",
                    "Stage": "ИД",
                    "Sheet": "1",
                    "SheetCount": "1",
                },
            },
        ),
    )
    await step(
        "note_position_create scale 1",
        lambda: b.spds_call(
            "note_position_create",
            {
                "origin": [200, 150, 0],
                "first_line": "Узел 1",
                "targets": [[150, 100, 0]],
                "scale": 1,
            },
        ),
    )
    brk = await step(
        "break_create scale 1",
        lambda: b.spds_call(
            "break_create",
            {"start": [50, 50, 0], "end": [50, 120, 0], "type": "Linear", "scale": 1},
        ),
    )
    if brk:
        await step(
            "clone break (+100, 0)",
            lambda: b.spds_call("clone", {"handle": brk["handle"], "dx": 100, "dy": 0}),
        )
    if mark_class:
        m0 = await step(
            "level mark create at y=0",
            lambda: b.spds_call("create", {"class_id": mark_class, "at": [300, 0, 0], "scale": 1}),
        )
        m1 = await step(
            "level mark create at y=3000",
            lambda: b.spds_call(
                "create", {"class_id": mark_class, "at": [300, 3000, 0], "scale": 1}
            ),
        )
        if m1:
            await step(
                "level mark: Associative=false",
                lambda: b.spds_call(
                    "set", {"handle": m1["handle"], "props": {"Associative": False}}
                ),
            )
            await step(
                "level mark: describe after Associative=false",
                lambda: b.spds_call("describe", {"handles": [m1["handle"]]}),
            )
            await step(
                "level mark: try Text=+9,999",
                lambda: b.spds_call("set", {"handle": m1["handle"], "props": {"Text": "+9,999"}}),
            )
        await step(
            "level mark create with Text=+5,500 and a comment",
            lambda: b.spds_call(
                "create",
                {
                    "class_id": mark_class,
                    "at": [330, 0, 0],
                    "scale": 1,
                    "props": {"Text": "+5,500", "Comment": "Пол 2 этажа"},
                },
            ),
        )
        report["level_mark_texts"] = {
            "y=0": ((m0 or {}).get("properties", {}).get("Text") or {}).get("value"),
            "y=3000": ((m1 or {}).get("properties", {}).get("Text") or {}).get("value"),
        }
    else:
        report["level_mark_class_id"] = "no level mark in the open drawing"
    await step("list (new drawing)", lambda: b.spds_call("list", {"limit": 100}))
    await step("zoom extents", lambda: b.view_zoom_extents())
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
