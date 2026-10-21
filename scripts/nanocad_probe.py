# scripts/nanocad_probe.py
"""Check this fork against the nanoCAD running on THIS machine — one JSON report.

Run it once after installing; send ``nanocad_probe_report.json`` back so the
remaining differences can be fixed. Two parts:

1. **ActiveX surface** — attaches to nanoCAD and reads, from its own type
   library, which members exist on the objects the COM backend uses. Every
   member the backend calls that nanoCAD lacks is listed under ``missing``.
2. **Backend smoke** — the same backend methods the MCP tools call, in a NEW
   drawing: lines, circle, polyline, text, mtext, hatch, layer with a
   linetype, a linear dimension, a table, save as DXF, export PDF, screenshot.
   Each step is recorded as ok or with its error; one failure never stops the
   rest. The new drawing is closed without saving at the end.

Your open drawings are never touched. Start nanoCAD first (an empty drawing is
fine), then:

    python scripts\\nanocad_probe.py

Exit code 0: everything passed; 1: something failed (see the report);
2: nanoCAD not reachable.
"""

from __future__ import annotations

import asyncio
import json
import os
import platform
import sys
import tempfile
import time
import traceback
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
os.environ.setdefault("AUTOCAD_MCP_BACKEND", "nanocad")

REPORT = ROOT / "nanocad_probe_report.json"

#: Members the COM backend calls, per object. Gathered from com_backend.py.
REQUIRED = {
    "Application": [
        "ActiveDocument",
        "Documents",
        "Preferences",
        "Version",
        "Name",
        "FullName",
        "HWND",
        "Visible",
        "ZoomExtents",
        "ZoomWindow",
        "ZoomCenter",
        "GetInterfaceObject",
    ],
    "Document": [
        "ModelSpace",
        "PaperSpace",
        "Layers",
        "Linetypes",
        "Blocks",
        "Layouts",
        "TextStyles",
        "DimStyles",
        "Dictionaries",
        "Views",
        "UserCoordinateSystems",
        "SelectionSets",
        "RegisteredApplications",
        "Utility",
        "Plot",
        "Database",
        "ActiveLayer",
        "ActiveLayout",
        "ActiveTextStyle",
        "ActiveDimStyle",
        "ActiveUCS",
        "ActiveViewport",
        "GetVariable",
        "SetVariable",
        "SendCommand",
        "HandleToObject",
        "ObjectIdToObject",
        "StartUndoMark",
        "EndUndoMark",
        "SaveAs",
        "Save",
        "Close",
        "Regen",
        "PurgeAll",
        "Export",
        "SummaryInfo",
        "PickfirstSelectionSet",
        "Name",
        "FullName",
        "Saved",
    ],
    "ModelSpace": [
        "AddLine",
        "AddCircle",
        "AddArc",
        "AddEllipse",
        "AddLightWeightPolyline",
        "AddText",
        "AddMText",
        "AddHatch",
        "AddDimAligned",
        "AddDimRotated",
        "AddDimRadial",
        "AddDimDiametric",
        "AddDimAngular",
        "AddTable",
        "AddMLeader",
        "AddPoint",
        "AddSpline",
        "AddSolid",
        "AddXline",
        "AddRegion",
        "AddRaster",
        "InsertBlock",
        "AttachExternalReference",
        "AddAttribute",
        "Count",
        "Item",
    ],
    "PaperSpace": ["AddPViewport", "AddLine", "InsertBlock"],
    "Linetypes": ["Load", "Item", "Count", "Add"],
    "Layers": ["Add", "Item", "Count"],
    "Layout": [
        "ConfigName",
        "CanonicalMediaName",
        "GetCanonicalMediaNames",
        "GetPlotDeviceNames",
        "RefreshPlotDeviceInfo",
        "StyleSheet",
        "PlotRotation",
        "PaperUnits",
        "GetPaperSize",
        "GetPaperMargins",
        "SetCustomScale",
        "GetCustomScale",
        "CenterPlot",
        "PlotType",
        "Block",
        "Name",
    ],
    "Plot": ["PlotToFile"],
    "Utility": ["GetPoint", "GetEntity", "Prompt", "TranslateCoordinates"],
    "PreferencesFiles": ["SupportPath", "PrinterStyleSheetPath"],
}


def _members(obj) -> set[str] | None:
    """Member names from the object's own type info (None if it has none)."""
    try:
        info = obj._oleobj_.GetTypeInfo()
    except Exception:
        return None
    names: set[str] = set()
    try:
        attr = info.GetTypeAttr()
        for i in range(attr.cFuncs):
            desc = info.GetFuncDesc(i)
            names.add(info.GetNames(desc.memid)[0])
        for i in range(attr.cVars):
            desc = info.GetVarDesc(i)
            names.add(info.GetNames(desc.memid)[0])
    except Exception:
        return names or None
    return names


def _err(exc: BaseException) -> str:
    text = f"{type(exc).__name__}: {exc}"
    return text[:500]


def surface(report: dict) -> object | None:
    import win32com.client

    import config

    progid = config.settings.cad_progid
    app = None
    tried = []
    for candidate in (
        progid,
        "nanoCAD.Application.24.1",
        "nanoCAD.Application.24",
        "nanoCAD.Application",
    ):
        if candidate in tried:
            continue
        tried.append(candidate)
        try:
            app = win32com.client.GetActiveObject(candidate)
            report["progid_attached"] = candidate
            break
        except Exception as exc:
            report.setdefault("attach_errors", {})[candidate] = _err(exc)
    if app is None:
        return None

    for key in ("Name", "Version", "FullName", "HWND"):
        try:
            report.setdefault("application", {})[key] = str(getattr(app, key))
        except Exception as exc:
            report.setdefault("application", {})[key] = "ERROR " + _err(exc)

    objects = {"Application": app}
    try:
        doc = app.ActiveDocument
        objects["Document"] = doc
        objects["ModelSpace"] = doc.ModelSpace
        objects["PaperSpace"] = doc.PaperSpace
        objects["Linetypes"] = doc.Linetypes
        objects["Layers"] = doc.Layers
        objects["Layout"] = doc.ActiveLayout
        objects["Plot"] = doc.Plot
        objects["Utility"] = doc.Utility
    except Exception as exc:
        report["surface_error"] = _err(exc)
    try:
        objects["PreferencesFiles"] = app.Preferences.Files
    except Exception as exc:
        report["preferences_error"] = _err(exc)

    out = {}
    for label, required in REQUIRED.items():
        obj = objects.get(label)
        if obj is None:
            out[label] = {"unreachable": True}
            continue
        names = _members(obj)
        if names is None:
            # No type info: probe by attribute access (methods are not called).
            missing = [m for m in required if not _hasattr(obj, m)]
            out[label] = {"typeinfo": False, "missing": missing}
        else:
            low = {n.lower() for n in names}
            out[label] = {
                "typeinfo": True,
                "missing": [m for m in required if m.lower() not in low],
                "member_count": len(names),
            }
    report["surface"] = out

    extra = {}
    try:
        extra["plot_devices"] = [str(n) for n in objects["Layout"].GetPlotDeviceNames()]
    except Exception as exc:
        extra["plot_devices"] = "ERROR " + _err(exc)
    try:
        extra["media_sample"] = [str(n) for n in objects["Layout"].GetCanonicalMediaNames()][:40]
    except Exception as exc:
        extra["media_sample"] = "ERROR " + _err(exc)
    try:
        support = str(objects["PreferencesFiles"].SupportPath)
        extra["support_path"] = support.split(";")
        lins = []
        for folder in support.split(";"):
            try:
                lins += [str(p) for p in Path(folder).glob("*.lin")]
            except OSError:
                pass
        extra["lin_files"] = lins
    except Exception as exc:
        extra["support_path"] = "ERROR " + _err(exc)
    for var in (
        "ACADVER",
        "MEASUREMENT",
        "INSUNITS",
        "LUNITS",
        "FILEDIA",
        "BACKGROUNDPLOT",
        "DWGPREFIX",
        "DIMSCALE",
        "DIMTXT",
        "DIMASZ",
        "DIMSTYLE",
        "TEXTSTYLE",
        "LTSCALE",
        "CELTSCALE",
    ):
        try:
            extra.setdefault("sysvars", {})[var] = str(objects["Document"].GetVariable(var))
        except Exception as exc:
            extra.setdefault("sysvars", {})[var] = "ERROR " + _err(exc)
    report["environment"] = extra
    return app


def _hasattr(obj, name) -> bool:
    try:
        getattr(obj, name)
        return True
    except Exception:
        return False


async def smoke(report: dict) -> None:
    from backends.com_backend import ComBackend

    b = ComBackend()
    await b.connect()
    steps: list[dict] = []
    out = Path(tempfile.gettempdir()) / "nanocad_probe"
    out.mkdir(exist_ok=True)

    async def step(name, coro_factory):
        t0 = time.perf_counter()
        try:
            result = await coro_factory()
            if isinstance(result, (bytes, bytearray)):
                result = {"bytes": len(result)}
            text = json.dumps(result, default=str, ensure_ascii=False)
            steps.append(
                {
                    "step": name,
                    "ok": True,
                    "ms": round((time.perf_counter() - t0) * 1000),
                    "result": json.loads(text[:2000]) if len(text) <= 2000 else text[:2000],
                }
            )
            return result
        except Exception as exc:
            steps.append(
                {
                    "step": name,
                    "ok": False,
                    "error": _err(exc),
                    "trace": traceback.format_exc()[-1500:],
                }
            )
            return None

    await step("system_status", b.system_status)
    created = await step("drawing_new", lambda: b.drawing_new())
    if created is None:
        report["smoke"] = steps
        return
    await step(
        "layer_create AXES CENTER", lambda: b.layer_create("AXES", color=1, linetype="CENTER")
    )
    await step("linetype_load DASHED", lambda: b.linetype_load("DASHED"))
    await step("linetype_load ACAD_ISO04W100", lambda: b.linetype_load("ACAD_ISO04W100"))
    await step("linetype_load native осевая", lambda: b.linetype_load("осевая"))
    await step("linetype_load GOST 2.303 4", lambda: b.linetype_load("ГОСТ 2.303 4"))
    await step("linetype_list", lambda: b.linetype_list())
    await step("line", lambda: b.entity_create_line(0, 0, 200, 0))
    await step("line on AXES", lambda: b.entity_create_line(0, -20, 0, 120, layer="AXES"))
    await step(
        "line linetype HIDDEN", lambda: b.entity_create_line(0, 100, 200, 100, linetype="HIDDEN")
    )
    await step("circle", lambda: b.entity_create_circle(100, 50, 25))
    await step(
        "polyline",
        lambda: b.entity_create_polyline([[0, 0], [200, 0], [200, 100], [0, 100]], closed=True),
    )
    await step("text (кириллица)", lambda: b.entity_create_text("Исполнительная схема", 10, 110, 5))
    await step("mtext", lambda: b.entity_create_mtext("Отм. +0.000\\PОсь 1", 10, 140, 60, 3.5))
    await step(
        "hatch ANSI31",
        lambda: b.entity_create_hatch(
            "ANSI31", [[150, 10], [190, 10], [190, 40], [150, 40]], scale=1.0
        ),
    )
    await step(
        "hatch GOST_GROUND",
        lambda: b.entity_create_hatch("GOST_GROUND", [[150, 50], [190, 50], [190, 80], [150, 80]]),
    )
    await step("dimension_linear", lambda: b.dimension_linear(0, 0, 200, 0, 100, -15))
    await step(
        "table",
        lambda: b.entity_create_table(
            250, 100, [["1", "Труба Ø110"], ["2", "Колодец"]], headers=["№", "Наименование"]
        ),
    )
    await step("screenshot", lambda: b.view_screenshot())
    await step("save_as dxf", lambda: b.drawing_save_as(str(out / "probe.dxf"), "dxf"))
    await step("export_pdf model", lambda: b.drawing_export_pdf(str(out / "probe.pdf")))
    await step("close new drawing", lambda: b.drawing_close(save=False))
    report["smoke"] = steps
    report["smoke_output_folder"] = str(out)


def main() -> int:
    report: dict = {
        "python": sys.version.split()[0],
        "os": platform.platform(),
        "backend_env": os.environ.get("AUTOCAD_MCP_BACKEND"),
    }
    if not sys.platform.startswith("win"):
        print("Run this on Windows with nanoCAD open.")
        return 2
    try:
        import win32com.client  # noqa: F401
    except ImportError:
        print('pywin32 is missing: pip install -e ".[com]"')
        return 2

    import pythoncom

    pythoncom.CoInitialize()
    app = surface(report)
    pythoncom.CoUninitialize()
    if app is None:
        REPORT.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print("nanoCAD is not reachable over COM. Is it running? Report:", REPORT)
        return 2
    del app

    try:
        asyncio.run(smoke(report))
    except Exception as exc:
        report["smoke_crash"] = _err(exc)

    missing = {
        k: v.get("missing") for k, v in report.get("surface", {}).items() if v.get("missing")
    }
    failed = [s["step"] for s in report.get("smoke", []) if not s["ok"]]
    report["summary"] = {"missing_members": missing, "failed_steps": failed}
    REPORT.write_text(
        json.dumps(report, ensure_ascii=False, indent=2, default=str), encoding="utf-8"
    )

    print(
        f"nanoCAD: {report.get('application', {}).get('Version')} via {report.get('progid_attached')}"
    )
    print(f"Missing ActiveX members: {sum(len(v) for v in missing.values())}")
    for step_ in report.get("smoke", []):
        print(
            ("  OK   " if step_["ok"] else "  FAIL ")
            + step_["step"]
            + ("" if step_["ok"] else "  -> " + step_["error"][:120])
        )
    print("Report:", REPORT)
    return 0 if not missing and not failed and "smoke_crash" not in report else 1


if __name__ == "__main__":
    sys.exit(main())
