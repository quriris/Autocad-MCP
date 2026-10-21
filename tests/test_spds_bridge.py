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
    }
