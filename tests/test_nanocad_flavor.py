"""nanoCAD as the live COM seat — the content differences, tested without COM.

Nothing here connects to a CAD: every function under test is a pure function of
the environment and of values the COM backend would have read. The AutoCAD
cases pin that selecting nanoCAD changed nothing for an AutoCAD seat.
"""

from __future__ import annotations

import pytest

import config
from backends import cad_flavor as cf


@pytest.fixture(autouse=True)
def _clean_env(monkeypatch):
    for name in ("AUTOCAD_MCP_BACKEND", "CAD_PROGID", "CAD_LIN_FILE", "CAD_PDF_DEVICE"):
        monkeypatch.delenv(name, raising=False)


@pytest.fixture
def nanocad(monkeypatch):
    monkeypatch.setenv("AUTOCAD_MCP_BACKEND", "nanocad")


# -- selection ---------------------------------------------------------------


def test_nanocad_backend_attaches_to_nanocad_not_autocad(nanocad):
    assert config.settings.cad_progid == "nanoCAD.Application"
    assert config.settings.cad_flavor == "nanocad"


def test_explicit_progid_still_wins_under_nanocad(nanocad, monkeypatch):
    monkeypatch.setenv("CAD_PROGID", "nanoCAD.Application.24.1")
    assert config.settings.cad_progid == "nanoCAD.Application.24.1"


def test_a_nanocad_progid_on_the_com_backend_is_the_same_seat(monkeypatch):
    monkeypatch.setenv("AUTOCAD_MCP_BACKEND", "com")
    monkeypatch.setenv("CAD_PROGID", "nanoCAD.Application.24.1")
    assert config.settings.cad_flavor == "nanocad"


def test_autocad_defaults_are_untouched():
    assert config.settings.cad_progid == "AutoCAD.Application"
    assert config.settings.cad_flavor == "autocad"
    assert cf.product_name() == "AutoCAD"
    assert cf.window_title_keyword() == "AutoCAD"


def test_nanocad_names_itself(nanocad):
    assert cf.product_name() == "nanoCAD"
    assert cf.window_title_keyword() == "nanoCAD"


@pytest.mark.asyncio
async def test_nanocad_backend_never_falls_back_to_ezdxf_off_windows(nanocad, monkeypatch):
    import server

    monkeypatch.setattr(server, "_WIN32", False)
    with pytest.raises(RuntimeError, match="nanocad backend requires Windows"):
        await server._make_backend()


# -- linetypes ---------------------------------------------------------------


def test_autocad_keeps_its_single_measurement_file():
    assert cf.linetype_file_candidates(1) == ["acadiso.lin"]
    assert cf.linetype_file_candidates(0) == ["acad.lin"]


def test_nanocad_tries_the_support_path_after_autocads_names(nanocad, tmp_path):
    (tmp_path / "ncad.lin").write_text("*X,x\nA,1,-1\n")
    (tmp_path / "ncadiso.lin").write_text("*X,x\nA,1,-1\n")
    out = cf.linetype_file_candidates(1, [tmp_path])
    assert out[:2] == ["acadiso.lin", "acad.lin"]
    # metric file first, full paths so the CAD need not resolve a bare name
    assert out[2:] == [str(tmp_path / "ncadiso.lin"), str(tmp_path / "ncad.lin")]


def test_lin_override_goes_first(nanocad, monkeypatch):
    monkeypatch.setenv("CAD_LIN_FILE", r"C:\spds\gost.lin")
    assert cf.linetype_file_candidates(1)[0] == r"C:\spds\gost.lin"


def test_missing_support_folder_is_skipped(nanocad, tmp_path):
    assert cf.linetype_file_candidates(1, [tmp_path / "nope"]) == ["acadiso.lin", "acad.lin"]


def test_loader_stops_at_first_file_that_works():
    tried = []

    def load(f):
        tried.append(f)
        if f != "b.lin":
            raise OSError("File system error")

    assert cf.load_linetype_from_candidates(load, ["a.lin", "b.lin", "c.lin"], str) == "b.lin"
    assert tried == ["a.lin", "b.lin"]


def test_loader_names_every_failure():
    def load(f):
        raise OSError(f"no {f}")

    with pytest.raises(RuntimeError, match=r"a\.lin: no a\.lin; b\.lin: no b\.lin"):
        cf.load_linetype_from_candidates(load, ["a.lin", "b.lin"], str)


# -- plot device -------------------------------------------------------------


def test_autocad_pdf_device_is_passed_through_without_a_com_call():
    def boom():
        raise AssertionError("device list must not be read on AutoCAD")

    assert cf.resolve_plot_device("DWG To PDF.pc3", boom) == "DWG To PDF.pc3"


def test_nanocad_translates_the_pdf_default(nanocad):
    names = ["Нет", "Microsoft Print to PDF", "Встроенный PDF", "Принтер"]
    assert cf.resolve_plot_device("DWG To PDF.pc3", lambda: names) == "Встроенный PDF"


def test_windows_pdf_printer_is_only_the_last_resort(nanocad):
    assert cf.pick_pdf_device(["None", "Microsoft Print to PDF"]) == "Microsoft Print to PDF"


def test_a_named_device_is_never_translated(nanocad):
    assert cf.resolve_plot_device("Плоттер A1", lambda: []) == "Плоттер A1"


def test_pdf_override_applies_on_any_cad(monkeypatch):
    monkeypatch.setenv("CAD_PDF_DEVICE", "My PDF")
    assert cf.resolve_plot_device("DWG To PDF.pc3", lambda: []) == "My PDF"


def test_no_pdf_device_is_a_readable_error(nanocad):
    with pytest.raises(RuntimeError, match="CAD_PDF_DEVICE"):
        cf.resolve_plot_device("DWG To PDF.pc3", lambda: ["None"])


# -- paper media -------------------------------------------------------------

A3 = "ISO_A3_(420.00_x_297.00_MM)"


def test_exact_media_wins_everywhere():
    assert cf.match_media(A3, [A3, "ISO A3 (420 x 297 мм)"]) == A3


def test_autocad_never_guesses_media():
    assert cf.match_media(A3, ["ISO A3 (420 x 297 мм)"]) is None


def test_nanocad_matches_media_by_size(nanocad):
    names = ["ISO A4 (210.00 x 297.00 мм)", "ISO A3 (420.00 x 297.00 мм)"]
    assert cf.match_media(A3, names) == "ISO A3 (420.00 x 297.00 мм)"


def test_nanocad_does_not_take_the_rotated_sheet(nanocad):
    assert cf.match_media(A3, ["ISO A3 (297.00 x 420.00 мм)"]) is None
