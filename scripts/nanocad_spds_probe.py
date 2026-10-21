# scripts/nanocad_spds_probe.py
"""What nanoCAD СПДС exposes to the outside — read-only, one JSON report.

СПДС objects (формат, штамп, отметки, оси, выноски...) are MultiCAD custom
objects, not plain lines and text. This script finds out how much of them a
COM client such as this MCP server can see and drive. It changes nothing.

1. **COM classes** — every class registered in Windows whose server lives in
   the Nanosoft folder (ProgID, DLL, type library). Any of them may be the
   door to СПДС objects (``McCOM2.dll`` is the prime suspect).
2. **Objects in the open drawing** — counts every entity type in model and
   paper space; for each type that is not a plain AutoCAD-style entity it
   lists the members its COM interface declares and reads the properties
   that take no arguments (value types and short values only).
3. **Named dictionaries** — where СПДС keeps its drawing-wide settings.

Open nanoCAD СПДС with a drawing that already HAS СПДС objects — one of your
исполнительные схемы is ideal (a formats/stamp, level marks, axes, leaders).
Nothing is modified and the drawing is not saved. Then:

    .venv\\Scripts\\python scripts\\nanocad_spds_probe.py

Writes ``nanocad_spds_report.json`` next to the project. The report contains
the drawing's object types and property values (e.g. the stamp's text): look
through it before sharing if the drawing is confidential.
"""

from __future__ import annotations

import json
import re
import sys
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REPORT = ROOT / "nanocad_spds_report.json"

#: Plain AutoCAD-style types: counted, not dissected.
_PLAIN = re.compile(
    r"^AcDb(Line|Circle|Arc|Ellipse|Polyline|2dPolyline|3dPolyline|Text|MText|Hatch|"
    r"BlockReference|AttributeDefinition|Attribute|Point|Spline|Solid|Face|Xline|Ray|"
    r"RotatedDimension|AlignedDimension|RadialDimension|DiametricDimension|"
    r"2LineAngularDimension|3PointAngularDimension|OrdinateDimension|ArcDimension|"
    r"Leader|MLeader|Table|Viewport|Region|Trace|Wipeout|RasterImage|OleFrame|Ole2Frame)$"
)

_MAX_TYPES = 40
_MAX_EXAMPLES = 2
_MAX_ENTITIES = 20000
_MAX_VALUE = 200

_DISPATCH_PROPERTYGET = 2


def _err(exc: BaseException) -> str:
    return f"{type(exc).__name__}: {exc}"[:300]


# -- 1. COM classes ----------------------------------------------------------


def nanosoft_com_classes() -> list[dict]:
    import winreg

    out = []
    root = winreg.OpenKey(winreg.HKEY_CLASSES_ROOT, "CLSID")
    i = 0
    while True:
        try:
            clsid = winreg.EnumKey(root, i)
        except OSError:
            break
        i += 1
        try:
            key = winreg.OpenKey(root, clsid)
        except OSError:
            continue
        server = None
        for sub in ("InprocServer32", "LocalServer32"):
            try:
                with winreg.OpenKey(key, sub) as s:
                    server = winreg.QueryValueEx(s, "")[0]
                    break
            except OSError:
                pass
        if not server or "nanosoft" not in str(server).lower():
            continue
        entry = {"clsid": clsid, "server": server}
        for sub, field in (("ProgID", "progid"), ("VersionIndependentProgID", "progid_vi")):
            try:
                with winreg.OpenKey(key, sub) as s:
                    entry[field] = winreg.QueryValueEx(s, "")[0]
            except OSError:
                pass
        try:
            entry["name"] = winreg.QueryValueEx(key, "")[0]
        except OSError:
            pass
        try:
            with winreg.OpenKey(key, "TypeLib") as s:
                entry["typelib"] = winreg.QueryValueEx(s, "")[0]
        except OSError:
            pass
        out.append(entry)
    out.sort(key=lambda e: (Path(str(e["server"]).strip('"')).name.lower(), e.get("progid", "")))
    return out


def nanosoft_typelibs() -> list[dict]:
    """Registered type libraries whose file lives in the Nanosoft folder."""
    import winreg

    out = []
    root = winreg.OpenKey(winreg.HKEY_CLASSES_ROOT, "TypeLib")
    i = 0
    while True:
        try:
            libid = winreg.EnumKey(root, i)
        except OSError:
            break
        i += 1
        try:
            lib = winreg.OpenKey(root, libid)
            j = 0
            while True:
                try:
                    version = winreg.EnumKey(lib, j)
                except OSError:
                    break
                j += 1
                with winreg.OpenKey(lib, version) as v:
                    name = winreg.QueryValueEx(v, "")[0] if v else ""
                    for arch in ("0\\win64", "0\\win32"):
                        try:
                            with winreg.OpenKey(v, arch) as a:
                                path = winreg.QueryValueEx(a, "")[0]
                        except OSError:
                            continue
                        if "nanosoft" in str(path).lower():
                            out.append(
                                {"libid": libid, "version": version, "name": name, "path": path}
                            )
        except OSError:
            continue
    return out


def typelib_interfaces(path: str) -> dict:
    """Interface and coclass names (with member names) declared by a type library."""
    import pythoncom

    result: dict = {}
    try:
        lib = pythoncom.LoadTypeLib(path)
    except Exception as exc:
        return {"error": _err(exc)}
    for i in range(lib.GetTypeInfoCount()):
        try:
            info = lib.GetTypeInfo(i)
            name = lib.GetDocumentation(i)[0]
            attr = info.GetTypeAttr()
            members = []
            for k in range(attr.cFuncs):
                desc = info.GetFuncDesc(k)
                members.append(info.GetNames(desc.memid)[0])
            result[name] = {"kind": int(attr.typekind), "members": sorted(set(members))[:300]}
        except Exception as exc:
            result[f"#{i}"] = {"error": _err(exc)}
    return result


# -- 2. objects in the drawing -----------------------------------------------


def _short(value) -> object:
    import pythoncom

    if isinstance(value, pythoncom.TypeIIDs[pythoncom.IID_IDispatch]):
        try:
            return "<object " + value.GetTypeInfo().GetDocumentation(-1)[0] + ">"
        except Exception:
            return "<object>"
    if isinstance(value, (tuple, list)):
        items = [_short(v) for v in value[:12]]
        return items + (["..."] if len(value) > 12 else [])
    if isinstance(value, (int, float, bool)) or value is None:
        return value
    text = str(value)
    return text if len(text) <= _MAX_VALUE else text[:_MAX_VALUE] + "..."


def dissect(obj) -> dict:
    """The COM interface of ``obj``: its name, members, and zero-arg property values."""
    import pythoncom

    out: dict = {}
    try:
        info = obj._oleobj_.GetTypeInfo()
        out["interface"] = info.GetDocumentation(-1)[0]
        attr = info.GetTypeAttr()
    except Exception as exc:
        out["typeinfo_error"] = _err(exc)
        return out
    methods, props, values = set(), set(), {}
    for k in range(attr.cFuncs):
        try:
            desc = info.GetFuncDesc(k)
            name = info.GetNames(desc.memid)[0]
        except Exception:
            continue
        if desc.invkind == _DISPATCH_PROPERTYGET:
            props.add(name)
            if len(desc.args) == 0 and name not in values:
                try:
                    raw = obj._oleobj_.Invoke(desc.memid, 0, pythoncom.DISPATCH_PROPERTYGET, True)
                    values[name] = _short(raw)
                except Exception as exc:
                    values[name] = "ERROR " + _err(exc)[:120]
        elif desc.invkind == 1:
            methods.add(name)
    out["methods"] = sorted(methods)
    out["properties"] = sorted(props)
    out["values"] = values
    return out


def scan_drawing(doc) -> dict:
    counts: Counter[str] = Counter()
    examples: dict[str, list] = {}
    seen = 0
    spaces = [("Model", doc.ModelSpace)]
    try:
        for i in range(doc.Layouts.Count):
            layout = doc.Layouts.Item(i)
            if str(layout.Name).lower() != "model":
                spaces.append((str(layout.Name), layout.Block))
    except Exception as exc:
        counts["!layouts_error " + _err(exc)[:80]] += 1
    for space_name, space in spaces:
        if space is None:
            continue
        try:
            n = space.Count
        except Exception:
            continue
        for i in range(n):
            if seen >= _MAX_ENTITIES:
                break
            seen += 1
            try:
                ent = space.Item(i)
                kind = str(ent.ObjectName)
            except Exception as exc:
                counts["!unreadable " + _err(exc)[:80]] += 1
                continue
            counts[kind] += 1
            if _PLAIN.match(kind):
                continue
            bucket = examples.setdefault(kind, [])
            if len(bucket) < _MAX_EXAMPLES and len(examples) <= _MAX_TYPES:
                item = {"space": space_name}
                try:
                    item["handle"] = str(ent.Handle)
                    item["layer"] = str(ent.Layer)
                except Exception:
                    pass
                item.update(dissect(ent))
                bucket.append(item)
    return {
        "entities_scanned": seen,
        "types": dict(counts.most_common()),
        "non_plain_examples": examples,
    }


def dictionaries(doc) -> list[str]:
    names = []
    try:
        nod = doc.Dictionaries
        for i in range(nod.Count):
            try:
                names.append(str(nod.Item(i).Name))
            except Exception as exc:
                names.append("? " + _err(exc)[:80])
    except Exception as exc:
        names.append("ERROR " + _err(exc))
    return names


# -- main --------------------------------------------------------------------


def main() -> int:
    if not sys.platform.startswith("win"):
        print("Run this on Windows with nanoCAD СПДС open.")
        return 2
    import pythoncom
    import win32com.client

    pythoncom.CoInitialize()
    report: dict = {}
    try:
        app = win32com.client.GetActiveObject("nanoCAD.Application")
    except Exception as exc:
        print("nanoCAD is not running (or not reachable over COM):", _err(exc))
        return 2
    report["application"] = {"FullName": str(app.FullName), "Version": str(app.Version)}
    doc = app.ActiveDocument
    report["document"] = {"Name": str(doc.Name)}

    print("Scanning registered COM classes...")
    classes = nanosoft_com_classes()
    report["com_classes"] = classes
    libs = nanosoft_typelibs()
    report["typelibs"] = libs
    spds_libs = {}
    for lib in libs:
        low = (lib["path"] + lib["name"]).lower()
        if any(k in low for k in ("mccom", "spds", "mcs", "multicad", "mapi")):
            spds_libs[lib["path"]] = typelib_interfaces(lib["path"])
    report["spds_typelib_contents"] = spds_libs

    print("Scanning the open drawing (read-only)...")
    report["drawing"] = scan_drawing(doc)
    report["dictionaries"] = dictionaries(doc)
    pythoncom.CoUninitialize()

    REPORT.write_text(
        json.dumps(report, ensure_ascii=False, indent=2, default=str), encoding="utf-8"
    )
    types = report["drawing"]["types"]
    special = [t for t in types if not _PLAIN.match(t)]
    print(f"COM classes from Nanosoft: {len(classes)}; type libraries: {len(libs)}")
    print(f"Entity types in drawing: {len(types)}; non-standard: {len(special)}")
    for t in special[:20]:
        print(f"  {types[t]:5d}  {t}")
    print("Report:", REPORT)
    return 0


if __name__ == "__main__":
    sys.exit(main())
