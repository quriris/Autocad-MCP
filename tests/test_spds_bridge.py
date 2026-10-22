"""The server's half of the nanoCAD СПДС bridge, tested with a fake plugin.

``FakeDoc.SendCommand`` plays the McpSpds plugin: it reads ``request.json``,
deletes it, and writes ``response-<id>.json`` exactly as the C# side does, so
the whole round trip runs on Linux without nanoCAD.
"""

from __future__ import annotations

import json

import pytest

import config
from backends import spds_bridge as sb


class FakeDoc:
    def __init__(self, folder, answer=None, cmdactive=0, respond=True):
        self.folder = folder
        self.answer = answer or (lambda op, args: {"echo": op, "args": args})
        self.vars = {"CMDACTIVE": cmdactive, "FILEDIA": 1}
        self.sent: list[str] = []
        self.respond = respond
        self.filedia_at_send = None

    def GetVariable(self, name):
        return self.vars[name]

    def SetVariable(self, name, value):
        self.vars[name] = value

    def SendCommand(self, text):
        self.sent.append(text)
        self.filedia_at_send = self.vars["FILEDIA"]
        if not self.respond or not text.startswith(sb.COMMAND):
            return
        request = self.folder / "request.json"
        data = json.loads(request.read_text(encoding="utf-8"))
        request.unlink()
        try:
            reply = {"ok": True, "result": self.answer(data["op"], data["args"])}
        except Exception as exc:
            reply = {"ok": False, "error": f"{type(exc).__name__}: {exc}"}
        (self.folder / f"response-{data['id']}.json").write_text(
            json.dumps(reply, ensure_ascii=False), encoding="utf-8"
        )


def test_round_trip_returns_the_plugins_result(tmp_path):
    doc = FakeDoc(tmp_path)
    out = sb.call(doc, "describe", {"handles": ["1A"]}, timeout=2, folder=tmp_path)
    assert out == {"echo": "describe", "args": {"handles": ["1A"]}}
    assert doc.sent == ["MCP_SPDS\n"]
    # nothing left behind for a later stray MCP_SPDS to answer
    assert list(tmp_path.iterdir()) == []


def test_cyrillic_survives_both_ways(tmp_path):
    doc = FakeDoc(tmp_path, answer=lambda op, args: {"Наименование": args["text"]})
    out = sb.call(doc, "set", {"text": "Исполнительная схема"}, timeout=2, folder=tmp_path)
    assert out == {"Наименование": "Исполнительная схема"}


def test_plugin_error_becomes_runtime_error(tmp_path):
    def boom(op, args):
        raise ValueError("unknown property 'Лист'")

    with pytest.raises(RuntimeError, match="СПДС plugin: ValueError: unknown property 'Лист'"):
        sb.call(FakeDoc(tmp_path, answer=boom), "set", {}, timeout=2, folder=tmp_path)


def test_silent_plugin_times_out_with_load_hint_and_cleans_its_request(tmp_path):
    doc = FakeDoc(tmp_path, respond=False)
    with pytest.raises(TimeoutError, match="spds_plugin_load"):
        sb.call(doc, "ping", timeout=0.2, folder=tmp_path, poll=0.01)
    assert not (tmp_path / "request.json").exists()


def test_busy_nanocad_is_refused_before_anything_is_written(tmp_path):
    doc = FakeDoc(tmp_path, cmdactive=1)
    with pytest.raises(RuntimeError, match="CMDACTIVE"):
        sb.call(doc, "ping", timeout=1, folder=tmp_path)
    assert doc.sent == []
    assert list(tmp_path.iterdir()) == []


def test_request_is_written_atomically(tmp_path):
    rid = sb.write_request("ping", None, tmp_path)
    assert json.loads((tmp_path / "request.json").read_text(encoding="utf-8")) == {
        "id": rid,
        "op": "ping",
        "args": {},
    }
    assert [p.name for p in tmp_path.iterdir()] == ["request.json"]


def test_netload_holds_filedia_at_zero_then_restores_it(tmp_path):
    plugin = tmp_path / "McpSpds.dll"
    plugin.write_bytes(b"MZ")
    doc = FakeDoc(tmp_path)
    assert sb.netload(doc, plugin) == str(plugin)
    assert doc.sent == [f'_NETLOAD "{plugin}"\n']
    assert doc.filedia_at_send == 0
    assert doc.vars["FILEDIA"] == 1


def test_netload_refuses_a_missing_plugin(tmp_path):
    with pytest.raises(FileNotFoundError):
        sb.netload(FakeDoc(tmp_path), tmp_path / "nope.dll")


def test_default_plugin_ships_with_the_repository():
    assert sb.DEFAULT_PLUGIN.name == "McpSpds.dll"
    assert sb.DEFAULT_PLUGIN.is_file(), "nanocad_plugin/bin/McpSpds.dll must be committed"


def test_bridge_dir_override(monkeypatch, tmp_path):
    monkeypatch.setenv("SPDS_BRIDGE_DIR", str(tmp_path))
    assert sb.bridge_dir() == tmp_path


# -- the backend and tool layer ------------------------------------------------


@pytest.mark.asyncio
async def test_com_backend_refuses_spds_on_autocad(monkeypatch):
    from backends.com_backend import ComBackend

    monkeypatch.delenv("AUTOCAD_MCP_BACKEND", raising=False)
    monkeypatch.delenv("CAD_PROGID", raising=False)
    assert config.settings.cad_flavor == "autocad"
    with pytest.raises(RuntimeError, match="СПДС tools need nanoCAD"):
        await ComBackend().spds_call("ping")


def test_spds_tools_form_their_own_pack():
    import server

    assert "spds" in server.TOOL_PACK_NAMES
    assert server.PACK_TOOL_NAMES["spds"] == {
        "spds_status",
        "spds_plugin_load",
        "spds_list",
        "spds_get",
        "spds_set",
        "spds_format_create",
        "spds_leader_create",
        "spds_break_create",
        "spds_clone",
        "spds_create",
        "spds_level_mark_create",
        "spds_inspect",
        "spds_stamp_text_height",
        "spds_classes",
        "spds_native_classes",
        "spds_object_create",
        "spds_invoke",
        "spds_axis_create",
        "spds_axis_grid_create",
        "spds_room_create",
    }


def test_wait_loaded_reads_the_plugins_marker(tmp_path):
    (tmp_path / "loaded.json").write_text('{"plugin_version": "0.1.1"}', encoding="utf-8")
    assert sb.wait_loaded(tmp_path, timeout=0.1) == {"plugin_version": "0.1.1"}


def test_wait_loaded_gives_up_when_nanocad_never_loaded_it(tmp_path):
    assert sb.wait_loaded(tmp_path, timeout=0.05, sleep=lambda s: None) is None


def test_log_tail_returns_the_last_lines(tmp_path):
    (tmp_path / "plugin.log").write_text("\n".join(f"line {i}" for i in range(30)), "utf-8")
    assert sb.log_tail(tmp_path, lines=2) == "line 28\nline 29"
    assert sb.log_tail(tmp_path / "missing") == ""


@pytest.mark.parametrize(
    ("value", "text"),
    [(3, "+3,000"), (-0.45, "-0,450"), (0, "0,000"), (21.0004, "+21,000"), (-0.0001, "0,000")],
)
def test_level_text_is_spelled_like_nanocad(value, text):
    import server

    assert server.spds_level_text(value) == text


# -- the generic object tools: what reaches the plugin ---------------------------


def _tool_fn(tool):
    return getattr(tool, "fn", tool)


@pytest.fixture
def sent(monkeypatch):
    import server

    calls: list = []

    async def fake(ctx, op, args=None, timeout=None):
        calls.append((op, args, timeout))
        return {"handle": "1A"}

    monkeypatch.setattr(server, "_spds", fake)
    return calls


@pytest.mark.asyncio
async def test_axis_grid_sends_spacings_and_names(sent):
    import server

    await _tool_fn(server.spds_axis_grid_create)(
        base_x=100, base_y=200, x_spacings=[4500, 4500], y_spacings=[8600],
        y_names=["А", "Б"], dim_chain=True, markers_position="bottom", scale=100,
    )
    op, args, _ = sent[0]
    assert op == "axis_grid_create"
    assert args["base"] == [100.0, 200.0, 0.0]
    assert args["x_spacings"] == [4500.0, 4500.0] and args["y_spacings"] == [8600.0]
    assert args["y_names"] == ["А", "Б"] and "x_names" not in args
    assert args["dim_chain"] is True and args["markers_position"] == "bottom"
    assert args["scale"] == 100.0


@pytest.mark.asyncio
async def test_axis_grid_refuses_an_unknown_marker_position(sent):
    import server
    from fastmcp.exceptions import ToolError

    with pytest.raises(ToolError, match="markers_position"):
        await _tool_fn(server.spds_axis_grid_create)(
            base_x=0, base_y=0, x_spacings=[], y_spacings=[], markers_position="up"
        )
    assert sent == []


@pytest.mark.asyncio
async def test_object_create_passes_class_init_steps(sent):
    import server

    steps = [{"call": "Leader.AddSegment", "args": [[0, 0], [10, 10]]}, {"get": "Origin"}]
    await _tool_fn(server.spds_object_create)(
        class_name="McNoteChain", init={"Origin": [5, 5]}, steps=steps, layer="АР_Текст"
    )
    op, args, _ = sent[0]
    assert op == "new"
    assert args == {
        "class": "McNoteChain",
        "steps": steps,
        "init": {"Origin": [5, 5]},
        "layer": "АР_Текст",
    }


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "bad", [[{"value": 1}], [{"set": "Text"}], ["Text"]], ids=["no-verb", "set-no-value", "not-a-dict"]
)
async def test_steps_are_checked_before_nanocad_sees_them(sent, bad):
    import server
    from fastmcp.exceptions import ToolError

    with pytest.raises(ToolError, match="step 0"):
        await _tool_fn(server.spds_invoke)(handle="1A", steps=bad)
    assert sent == []


@pytest.mark.asyncio
async def test_native_class_scan_gets_a_long_timeout(sent):
    import server

    await _tool_fn(server.spds_native_classes)()
    assert sent[0][0] == "native_classes" and sent[0][2] >= 120


@pytest.mark.asyncio
async def test_room_sends_its_contour(sent):
    import server

    await _tool_fn(server.spds_room_create)(
        points=[[0, 0], [5000, 0], [5000, 4000], [0, 4000]], number="2", name="Гараж",
        text_x=2500, text_y=2000,
    )
    op, args, _ = sent[0]
    assert op == "room_create"
    assert args["points"][2] == [5000.0, 4000.0, 0.0]
    assert args["number"] == "2" and args["name"] == "Гараж"
    assert args["text"] == [2500.0, 2000.0, 0.0] and args["plain_area"] is False
