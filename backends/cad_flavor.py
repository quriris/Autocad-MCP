"""Which CAD the live COM backend drives, and where nanoCAD differs from AutoCAD.

nanoCAD (Платформа nanoCAD 2x, ProgID ``nanoCAD.Application``) exposes an
ActiveX object model that copies AutoCAD's member names — ``ActiveDocument``,
``ModelSpace.AddLine``, ``Linetypes.Load``, ``Layout.Plot`` and so on — so
almost every COM call in ``com_backend`` works unchanged. What does *not* carry
over is the content AutoCAD ships with and the names it uses for it:

* **Linetype files.** ``acadiso.lin`` / ``acad.lin`` are AutoCAD's files. A
  nanoCAD install carries its own ``.lin`` files on its support path under
  other names, so the loader tries a list of candidates instead of one name.
* **The PDF plotter.** ``DWG To PDF.pc3`` is an AutoCAD device. nanoCAD has
  its own built-in PDF device, found by asking the layout for its device list
  (``GetPlotDeviceNames``) rather than guessing a spelling.
* **The main window title**, used only as the screenshot fallback when
  ``Application.HWND`` cannot be read.

Everything here is a pure function of configuration plus objects the caller
already holds, so it is testable on Linux without COM. Overrides:

* ``CAD_LIN_FILE`` — linetype file tried first (name on the support path or a
  full path).
* ``CAD_PDF_DEVICE`` — plot device used for PDF output.
"""

from __future__ import annotations

import os
import re
from collections.abc import Callable, Iterable
from pathlib import Path

import config

#: AutoCAD's PDF device. Callers and tool defaults still spell it this way;
#: on nanoCAD it is translated by :func:`resolve_plot_device`.
AUTOCAD_PDF_DEVICE = "DWG To PDF.pc3"

#: Substrings that identify nanoCAD's own PDF output device, best first.
_NANOCAD_PDF_HINTS = ("pdf",)


def flavor() -> str:
    """``"nanocad"`` or ``"autocad"`` — read live, like every other setting."""
    return config.settings.cad_flavor


def is_nanocad() -> bool:
    return flavor() == "nanocad"


def product_name() -> str:
    """Human name for messages: never tell a nanoCAD user to fix "AutoCAD"."""
    return "nanoCAD" if is_nanocad() else "AutoCAD"


def window_title_keyword() -> str:
    """Substring of the main window title (EnumWindows fallback only)."""
    return "nanoCAD" if is_nanocad() else "AutoCAD"


# -- linetypes ---------------------------------------------------------------

#: AutoCAD's acad/acadiso.lin names -> the same definition's name in nanoCAD's
#: ncad.lin / ncadiso.lin (read from a nanoCAD 24.1 install). nanoCAD ships the
#: whole AutoCAD set under Russian names, so a tool asking for CENTER gets the
#: seat's own "осевая" definition loaded *as* CENTER — every tool, critique and
#: layer preset that names AutoCAD linetypes keeps working unchanged.
NANOCAD_LINETYPE_ALIASES: dict[str, str] = {
    "BORDER": "рант",
    "BORDER2": "рант2",
    "BORDERX2": "рантX2",
    "CENTER": "осевая",
    "CENTER2": "осевая2",
    "CENTERX2": "осеваяX2",
    "DASHDOT": "штрихпунктирная",
    "DASHDOT2": "штрихпунктирная2",
    "DASHDOTX2": "штрихпунктирнаяX2",
    "DASHED": "штриховая",
    "DASHED2": "штриховая2",
    "DASHEDX2": "штриховаяX2",
    "DIVIDE": "линия_сгиба",
    "DIVIDE2": "линия_сгиба2",
    "DIVIDEX2": "линия_сгибаX2",
    "DOT": "пунктирная",
    "DOT2": "пунктирная2",
    "DOTX2": "пунктирнаяX2",
    "HIDDEN": "невидимая",
    "HIDDEN2": "невидимая2",
    "HIDDENX2": "невидимаяX2",
    "PHANTOM": "фантом",
    "PHANTOM2": "фантом2",
    "PHANTOMX2": "фантомX2",
    "FENCELINE1": "ограждение1",
    "FENCELINE2": "ограждение2",
    "TRACKS": "пути",
    "BATTING": "изоляция",
    "HOT_WATER_SUPPLY": "горячая_вода",
    "GAS_LINE": "газопровод",
    "ZIGZAG": "зигзаг",
    **{f"ACAD_ISO{n:02d}W100": f"NCAD_ISO{n:02d}W100" for n in range(2, 16)},
}


def read_lin_text(path: Path) -> str:
    """A .lin file's text: nanoCAD's own are cp1251, its GOST file is UTF-8."""
    raw = path.read_bytes()
    if raw.startswith(b"\xef\xbb\xbf"):
        return raw[3:].decode("utf-8", errors="replace")
    try:
        return raw.decode("utf-8")
    except UnicodeDecodeError:
        return raw.decode("cp1251", errors="replace")


def parse_lin(text: str) -> dict[str, tuple[str, str, str]]:
    """``{name.lower(): (name, description, definition line)}`` of a .lin text.

    A definition is the ``*NAME,description`` header plus the ``A,...`` line
    after it. Comments (``;;``) and blank lines are skipped.
    """
    out: dict[str, tuple[str, str, str]] = {}
    lines = [ln.rstrip("\r\n") for ln in text.splitlines()]
    i = 0
    while i < len(lines):
        line = lines[i].strip()
        if line.startswith("*"):
            name, _, desc = line[1:].partition(",")
            j = i + 1
            while j < len(lines) and (not lines[j].strip() or lines[j].strip().startswith(";")):
                j += 1
            if j < len(lines) and not lines[j].strip().startswith("*"):
                out[name.strip().lower()] = (name.strip(), desc.strip(), lines[j].strip())
                i = j
        i += 1
    return out


def compat_linetype_file(name: str, lin_files: Iterable[str], out_dir: Path) -> str | None:
    """A one-entry .lin defining AutoCAD's ``name`` with nanoCAD's definition.

    None when ``name`` is not an AutoCAD name we know or no seat file carries
    the nanoCAD counterpart. The file is rewritten on every call: it is tiny,
    and a stale copy from another install must never win.
    """
    native = NANOCAD_LINETYPE_ALIASES.get(name.upper())
    if native is None:
        return None
    for lin_file in lin_files:
        try:
            defs = parse_lin(read_lin_text(Path(lin_file)))
        except OSError:
            continue
        hit = defs.get(native.lower())
        if hit is None:
            continue
        _, desc, definition = hit
        out_dir.mkdir(parents=True, exist_ok=True)
        target = out_dir / f"{name.upper()}.lin"
        content = f"*{name.upper()},{desc}\r\n{definition}\r\n"
        target.write_bytes(content.encode("cp1251", errors="replace"))
        return str(target)
    return None


def _autocad_lin_file(measurement: int | None) -> str:
    return "acad.lin" if measurement == 0 else "acadiso.lin"


def _lin_files_on(folders: Iterable[Path]) -> list[str]:
    """Every ``.lin`` file on the support path, metric (``*iso*``) files first.

    Full paths, so ``Linetypes.Load`` does not depend on how the CAD resolves
    a bare name.
    """
    seen: set[str] = set()
    found: list[Path] = []
    for folder in folders:
        try:
            entries = sorted(folder.glob("*.lin"))
        except OSError:
            continue
        for entry in entries:
            key = entry.name.lower()
            if key in seen or not entry.is_file():
                continue
            seen.add(key)
            found.append(entry)
    found.sort(key=lambda p: (0 if "iso" in p.name.lower() else 1, p.name.lower()))
    return [str(p) for p in found]


def linetype_file_candidates(
    measurement: int | None,
    support_folders: Iterable[Path] = (),
    name: str | None = None,
    compat_dir: Path | None = None,
) -> list[str]:
    """Linetype files to try, in order, for a name the caller did not place.

    AutoCAD keeps its historical single answer (``acadiso.lin`` on a metric
    drawing, ``acad.lin`` on an imperial one). nanoCAD tries, after
    ``CAD_LIN_FILE``: a compat file carrying AutoCAD's ``name`` with the
    seat's own definition (CENTER from "осевая"), the seat's ``.lin`` files
    (for its native names such as "осевая" or "ГОСТ 2.303 4"), and AutoCAD's
    file names last, for a seat where someone copied them in.
    """
    out: list[str] = []
    override = os.environ.get("CAD_LIN_FILE", "").strip()
    if override:
        out.append(override)
    if not is_nanocad():
        out.append(_autocad_lin_file(measurement))
    else:
        seat_files = _lin_files_on(support_folders)
        if name and compat_dir is not None:
            compat = compat_linetype_file(name, seat_files, compat_dir)
            if compat:
                out.append(compat)
        out.extend(seat_files)
        first = _autocad_lin_file(measurement)
        out.extend([first, "acad.lin" if first == "acadiso.lin" else "acadiso.lin"])
    deduped: list[str] = []
    for item in out:
        if item.lower() not in {d.lower() for d in deduped}:
            deduped.append(item)
    return deduped


def load_linetype_from_candidates(
    load: Callable[[str], None],
    candidates: list[str],
    describe_error: Callable[[Exception], str],
) -> str:
    """Call ``load(file)`` for each candidate until one succeeds.

    Returns the file that worked. Raises ``RuntimeError`` naming every file
    tried and why it failed, so "nothing on this seat defines DASHED" is told
    apart from "the file was not found".
    """
    failures: list[str] = []
    for lin_file in candidates:
        try:
            load(lin_file)
            return lin_file
        except Exception as exc:
            failures.append(f"{lin_file}: {describe_error(exc)}")
    raise RuntimeError(
        "no linetype file on this seat could load it ("
        + "; ".join(failures)
        + "). Pass file=<path to a .lin> or set CAD_LIN_FILE."
    )


# -- hatch patterns ------------------------------------------------------------


def hatch_pattern_types() -> tuple[int, ...]:
    """``AddHatch`` PatternType values to try, in order.

    ActiveX numbers them UserDefined=0, PreDefined=1, CustomDefined=2. AutoCAD
    accepts 0 with a named pattern and the backend has always passed it, so
    AutoCAD keeps exactly that. nanoCAD (TeighaX) is strict and answers
    "Bad name for Hatch pattern" to 0 + ANSI31 (measured, nanoCAD 24.1): it
    gets PreDefined for patterns in ncad/ncadiso.pat, then CustomDefined for a
    pattern in its own .pat file on the support path.
    """
    return (1, 2) if is_nanocad() else (0,)


# -- paper media -------------------------------------------------------------

_NUMBER = re.compile(r"\d+(?:[.,]\d+)?")


def _media_size(name: str) -> tuple[float, float] | None:
    """The two sizes a media name spells out, e.g. ``(420.0, 297.0)``.

    Takes the last two numbers, so a leading ``A3`` / ``ISO_A3`` is ignored.
    """
    numbers = [float(n.replace(",", ".")) for n in _NUMBER.findall(name)]
    if len(numbers) < 2:
        return None
    return numbers[-2], numbers[-1]


def match_media(requested: str, available: Iterable[str]) -> str | None:
    """The device's own name for ``requested``, or None.

    An exact name always wins, and on AutoCAD that is the only match (its
    behaviour is unchanged). On nanoCAD the media list is spelled by nanoCAD,
    so a name with the same two sizes in the same order is accepted — the
    orientation is set separately, so a swapped pair is not the same media.
    """
    names = [str(n) for n in available]
    if requested in names:
        return requested
    if not is_nanocad():
        return None
    want = _media_size(requested)
    if want is None:
        return None
    for name in names:
        got = _media_size(name)
        if got and abs(got[0] - want[0]) < 0.6 and abs(got[1] - want[1]) < 0.6:
            return name
    return None


# -- plotting ----------------------------------------------------------------


def pick_pdf_device(device_names: Iterable[str]) -> str | None:
    """nanoCAD's PDF device from the layout's ``GetPlotDeviceNames`` list."""
    names = [str(n) for n in device_names if n]
    for hint in _NANOCAD_PDF_HINTS:
        # Built-in devices before Windows printers ("Microsoft Print to PDF"
        # is a print driver with fixed media, not a vector exporter).
        for name in names:
            low = name.lower()
            if hint in low and "microsoft" not in low:
                return name
        for name in names:
            if hint in name.lower():
                return name
    return None


def resolve_plot_device(requested: str, device_names: Callable[[], Iterable[str]]) -> str:
    """The device to actually write into ``ConfigName`` / ``PlotToFile``.

    ``CAD_PDF_DEVICE`` replaces AutoCAD's PDF device on any CAD. On nanoCAD,
    a request for ``DWG To PDF.pc3`` (every tool's default) is translated to
    the seat's own PDF device; any other name passes through untouched, since
    the user named it deliberately. ``device_names`` is only called when the
    translation is needed — it is a COM round trip.
    """
    is_pdf_default = requested.strip().lower() == AUTOCAD_PDF_DEVICE.lower()
    if not is_pdf_default:
        return requested
    override = os.environ.get("CAD_PDF_DEVICE", "").strip()
    if override:
        return override
    if not is_nanocad():
        return requested
    try:
        picked = pick_pdf_device(device_names())
    except Exception as exc:
        raise RuntimeError(
            f"nanoCAD did not list its plot devices ({exc}); set CAD_PDF_DEVICE to "
            "the name of its PDF device."
        ) from exc
    if picked is None:
        raise RuntimeError(
            "nanoCAD lists no PDF plot device on this seat; set CAD_PDF_DEVICE to "
            "the device name shown in the plot dialog."
        )
    return picked
