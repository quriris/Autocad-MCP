// McpSpds — the nanoCAD-side half of the MCP server's СПДС tools.
//
// nanoCAD СПДС objects (format + stamp, leaders, level marks, breaks...) are
// MultiCAD objects: plain ActiveX/COM sees them only as an anonymous
// IAcadEntity (measured on nanoCAD 24.1). This plugin runs inside nanoCAD and
// uses the MultiCAD.NET API, which does know them.
//
// Protocol (file based, so it needs no network port and runs on nanoCAD's
// main thread, where MultiCAD calls are safe):
//   1. the MCP server writes %TEMP%\acadmcp-spds\request.json
//        {"id": "...", "op": "...", "args": {...}}
//   2. it sends the command MCP_SPDS to nanoCAD over COM
//   3. this command reads and deletes the request, runs it, and writes
//        %TEMP%\acadmcp-spds\response-<id>.json  {"ok": true, ...} | {"ok": false, "error": "..."}
//   4. the server picks the response up.
//
// Objects are addressed by their DWG handle (hex), the same handle the COM
// side of the server reports, so both halves talk about the same object.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Multicad;
using Multicad.DatabaseServices;
using Multicad.Geometry;
using Multicad.Projects;
using Multicad.Symbols;

// nanoCAD's own .NET loader (NETLOAD) registers commands declared with the
// platform attribute, Teigha.Runtime.CommandMethod — the MultiCAD one alone was
// never registered (measured: nanoCAD 24.1 never answered MCP_SPDS).
[assembly: Teigha.Runtime.ExtensionApplication(typeof(McpSpds.App))]
[assembly: Teigha.Runtime.CommandClass(typeof(McpSpds.Commands))]

namespace McpSpds
{
    // Runs once when NETLOAD loads the assembly: leaves a marker the server
    // reads to tell "never loaded" from "loaded but the command did not run".
    public class App : Teigha.Runtime.IExtensionApplication
    {
        public void Initialize()
        {
            try
            {
                Directory.CreateDirectory(Commands.Dir);
                File.WriteAllText(
                    Path.Combine(Commands.Dir, "loaded.json"),
                    JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["plugin_version"] = Commands.PluginVersion,
                        ["loaded_at"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
                        ["runtime"] = Environment.Version.ToString(),
                    }),
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Commands.Log("Initialize marker: " + ex);
            }
            try
            {
                HostMgd.ApplicationServices.Application.DocumentManager.MdiActiveDocument?.Editor
                    .WriteMessage("\nMcpSpds " + Commands.PluginVersion + " загружен (команда MCP_SPDS).\n");
            }
            catch { }
        }

        public void Terminate() { }
    }

    public partial class Commands
    {
        public const string PluginVersion = "0.3.1";

        internal static string Dir => Path.Combine(Path.GetTempPath(), "acadmcp-spds");

        internal static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(Path.Combine(Dir, "plugin.log"),
                    DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        [Teigha.Runtime.CommandMethod("MCP_SPDS", Teigha.Runtime.CommandFlags.Modal)]
        public static void McpSpds()
        {
            try
            {
                Run();
            }
            catch (Exception ex)
            {
                Log("MCP_SPDS: " + ex);
            }
        }

        static void Run()
        {
            string request = Path.Combine(Dir, "request.json");
            if (!File.Exists(request))
            {
                Log("MCP_SPDS ran with no request.json in " + Dir);
                return;
            }
            string text;
            try
            {
                text = File.ReadAllText(request, Encoding.UTF8);
            }
            finally
            {
                TryDelete(request);
            }

            string id = "unknown";
            var reply = new Dictionary<string, object>();
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                id = root.GetProperty("id").GetString() ?? "unknown";
                string op = root.GetProperty("op").GetString() ?? "";
                JsonElement args = root.TryGetProperty("args", out var a) ? a : default;
                object result = Dispatch(op, args);
                reply["ok"] = true;
                reply["result"] = result;
            }
            catch (Exception ex)
            {
                reply["ok"] = false;
                reply["error"] = ex.GetType().Name + ": " + ex.Message;
            }

            string target = Path.Combine(Dir, "response-" + Sanitize(id) + ".json");
            string tmp = target + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(reply), new UTF8Encoding(false));
            if (File.Exists(target))
                File.Delete(target);
            File.Move(tmp, target);
        }

        // -- dispatch -----------------------------------------------------------

        static object Dispatch(string op, JsonElement a)
        {
            switch (op)
            {
                case "ping":
                    return new Dictionary<string, object>
                    {
                        ["plugin_version"] = PluginVersion,
                        ["document"] = McDocument.ActiveDocument?.Name ?? "",
                    };
                case "list":
                    return ListObjects(a);
                case "describe":
                    return Describe(a);
                case "set":
                    return SetProps(a);
                case "formats":
                    return Formats();
                case "format_create":
                    return FormatCreate(a);
                case "note_position_create":
                    return NotePositionCreate(a);
                case "note_multilayer_create":
                    return NoteMultilayerCreate(a);
                case "break_create":
                    return BreakCreate(a);
                case "clone":
                    return Clone(a);
                case "create":
                    return CreateByClass(a);
                case "project_info":
                    return ProjectInfo(a);
                case "inspect":
                    return Inspect(a);
                case "cell_text_height":
                    return CellTextHeight(a);
                case "classes":
                    return Classes(a);
                case "native_classes":
                    return NativeClasses(a);
                case "new":
                    return NewObject(a);
                case "invoke":
                    return Invoke(a);
                case "axis_create":
                    return AxisCreate(a);
                case "axis_grid_create":
                    return AxisGridCreate(a);
                case "room_create":
                    return RoomCreate(a);
                default:
                    throw new ArgumentException("unknown op '" + op + "'");
            }
        }

        // -- reading ------------------------------------------------------------

        // Every MultiCAD object of the drawing (model and sheets), found inside
        // nanoCAD: a COM walk over a 340k-entity model takes minutes, this
        // does not touch plain AutoCAD-style entities at all.
        static object ListObjects(JsonElement a)
        {
            string contains = Str(a, "class_contains", "").ToLowerInvariant();
            int limit = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("limit", out var l) ? l.GetInt32() : 2000;
            // The active drawing only — model plus its sheets and layouts.
            // (AllObjects reached every open drawing and returned blocks and
            // sheets as class-less entries; measured on nanoCAD 24.1.)
            var filter = ObjectFilter.Create(false);
            foreach (var doc in DrawingDocs())
                filter.AddDoc(doc);
            var list = new List<object>();
            int total = 0;
            var seen = new HashSet<long>();
            foreach (var id in filter.GetObjects())
            {
                if (!seen.Add(id.Handle))
                    continue;
                McObject? obj;
                try { obj = id.GetObject(); } catch { continue; }
                if (obj is not McEntity e)
                    continue;
                string cls = id.ClassName ?? "";
                string type = obj.GetType().Name;
                if (contains.Length > 0
                    && !cls.ToLowerInvariant().Contains(contains)
                    && !type.ToLowerInvariant().Contains(contains))
                    continue;
                total++;
                if (list.Count >= limit)
                    continue;
                var item = new Dictionary<string, object>
                {
                    ["handle"] = Hex(id),
                    ["mc_class"] = cls,
                    ["type"] = type,
                    ["class_id"] = obj.ClassID.ToString(),
                };
                try
                {
                    item["layer"] = e.DbEntity.Layer;
                    var bb = e.DbEntity.BoundingBox;
                    item["bbox"] = new object[] { Pt(bb.MinPoint), Pt(bb.MaxPoint) };
                }
                catch { }
                list.Add(item);
            }
            return new Dictionary<string, object> { ["total"] = total, ["objects"] = list };
        }

        static List<McDocument> DrawingDocs()
        {
            var docs = new List<McDocument>();
            var top = McDocument.ActiveDocument?.TopDoc ?? McDocument.ActiveDocument;
            if (top == null)
                return docs;
            docs.Add(top);
            var ids = new List<McObjectId>();
            try { ids.AddRange(top.GetSheets()); } catch { }
            try { ids.AddRange(top.GetLayouts()); } catch { }
            foreach (var id in ids)
            {
                try
                {
                    var d = McDocument.GetDocument(id);
                    if (d != null && !docs.Contains(d))
                        docs.Add(d);
                }
                catch { }
            }
            return docs;
        }

        static object Describe(JsonElement a)
        {
            var list = new List<object>();
            foreach (var h in a.GetProperty("handles").EnumerateArray())
            {
                string handle = h.GetString() ?? "";
                try
                {
                    list.Add(DescribeOne(Resolve(handle), handle));
                }
                catch (Exception ex)
                {
                    list.Add(new Dictionary<string, object> { ["handle"] = handle, ["error"] = ex.Message });
                }
            }
            return list;
        }

        static Dictionary<string, object> DescribeOne(McObject obj, string handle)
        {
            var d = new Dictionary<string, object>
            {
                ["handle"] = handle,
                ["class"] = obj.GetType().FullName ?? "",
                ["mc_class"] = obj.ID.ClassName ?? "",
                ["class_id"] = obj.ClassID.ToString(),
            };
            var ent = obj as McEntity;
            if (ent != null)
            {
                var db = ent.DbEntity;
                d["layer"] = db.Layer;
                try
                {
                    var bb = db.BoundingBox;
                    d["bbox"] = new object[] { Pt(bb.MinPoint), Pt(bb.MaxPoint) };
                }
                catch { }
            }
            var props = new Dictionary<string, object>();
            var src = Source(obj);
            if (src != null)
            {
                var bag = src.ObjectProperties;
                foreach (var name in bag.GetNames())
                {
                    try
                    {
                        var p = bag.GetProperty(name);
                        props[name] = new Dictionary<string, object>
                        {
                            ["value"] = ToJson(p.GetValue()),
                            ["type"] = p.PropertyType?.Name ?? "",
                            ["readonly"] = p.IsReadOnly,
                        };
                    }
                    catch (Exception ex)
                    {
                        props[name] = new Dictionary<string, object> { ["error"] = ex.Message };
                    }
                }
            }
            d["properties"] = props;
            return d;
        }

        static object Formats()
        {
            var list = new List<object>();
            foreach (var f in McFormat.GetAllFormats(false))
                list.Add(DescribeOne(f, Hex(f.ID)));
            return list;
        }

        // -- writing ------------------------------------------------------------

        static object SetProps(JsonElement a)
        {
            string handle = a.GetProperty("handle").GetString() ?? "";
            var obj = Resolve(handle);
            var applied = ApplyProps(obj, a.TryGetProperty("props", out var p) ? p : default);
            Commit(obj, applied.Count > 0);
            return new Dictionary<string, object> { ["handle"] = handle, ["changed"] = applied };
        }

        static object FormatCreate(JsonElement a)
        {
            var f = new McFormat();
            f.Template = TemplateValue(Str(a, "template", "SPDS"));
            string size = Str(a, "size", "A3").ToUpperInvariant();
            f.Format = (McFormat.FormatSize)Enum.Parse(typeof(McFormat.FormatSize), size, true);
            if (a.TryGetProperty("multiplicity", out var m))
                f.Multiplicity = m.GetInt32();
            f.Horizontal = Bool(a, "horizontal", true);
            if (a.TryGetProperty("scale", out var s))
                f.DimScale = s.GetDouble();
            if (a.TryGetProperty("sheet", out var sh))
                f.Sheet = sh.GetInt32();
            if (a.TryGetProperty("sheet_count", out var sc))
                f.SheetCount = sc.GetInt32();
            if (a.TryGetProperty("name", out var n))
                f.Name = n.GetString() ?? "";
            f.Origin = P(a.GetProperty("origin"));
            f.DbEntity.AddToCurrentDocument();
            ApplyScale(f, a);
            // nanoCAD fills a new format's stamp from the drawing on its first
            // update, overwriting anything written before it (measured, nanoCAD
            // 24.1: Designation written at creation was lost, the same write a
            // moment later stuck). Let that happen first, then write.
            Commit(f);
            var stamp = ApplyProps(f, a.TryGetProperty("props", out var p) ? p : default);
            Commit(f, stamp.Count > 0);
            if (a.TryGetProperty("address_height", out var ah))
            {
                try
                {
                    var rebuilt = RebuildWithCellHeight(f, "ComplexName", ah.GetDouble(), out _, out double after);
                    var d = Created(rebuilt);
                    d["address_height"] = double.IsNaN(after) ? "unreadable" : after;
                    return d;
                }
                catch (ArgumentException ex)
                {
                    // An empty address or a template without the cell: the
                    // format itself is fine, so report rather than fail.
                    var d = Created(f);
                    d["address_height_skipped"] = ex.Message;
                    return d;
                }
            }
            return Created(f);
        }

        static object NotePositionCreate(JsonElement a)
        {
            var n = new McNotePosition();
            n.Origin = P(a.GetProperty("origin"));
            n.FirstLine = Str(a, "first_line", "");
            n.SecondLine = Str(a, "second_line", "");
            if (a.TryGetProperty("text_size", out var ts))
                n.TextSize = ts.GetDouble();
            if (a.TryGetProperty("arrow", out var ar))
                n.Arrow = (Arrows)Enum.Parse(typeof(Arrows), ar.GetString() ?? "Arrow", true);
            n.DbEntity.AddToCurrentDocument();
            ApplyScale(n, a);
            var points = new List<Point3d>();
            if (a.TryGetProperty("targets", out var targets))
                foreach (var t in targets.EnumerateArray())
                {
                    points.Add(P(t));
                    n.Leader.AddSegment(n.Origin, P(t));
                }
            // AddSegment makes the leader lines but leaves each arrow on the
            // shelf (measured, nanoCAD 24.1: Leader1X/Y == Origin); the arrow
            // point is the LeaderN X/Y parameter, so write it.
            if (points.Count > 0)
            {
                Commit(n);
                var bag = n.DbObject.ObjectProperties;
                for (int i = 0; i < points.Count; i++)
                {
                    var px = bag.GetProperty("Leader" + (i + 1) + "X");
                    var py = bag.GetProperty("Leader" + (i + 1) + "Y");
                    if (px == null || py == null)
                        break;
                    px.SetValue(points[i].X);
                    py.SetValue(points[i].Y);
                }
            }
            Commit(n);
            ApplyProps(n, a.TryGetProperty("props", out var p) ? p : default);
            Commit(n);
            return Created(n);
        }

        static object NoteMultilayerCreate(JsonElement a)
        {
            var n = new McNoteMultilayer();
            n.Start = P(a.GetProperty("start"));
            n.End = P(a.GetProperty("end"));
            if (a.TryGetProperty("text_size", out var ts))
                n.TextSize = ts.GetDouble();
            if (a.TryGetProperty("lines", out var lines))
                foreach (var line in lines.EnumerateArray())
                    n.Units.Add(line.GetString() ?? "");
            n.DbEntity.AddToCurrentDocument();
            ApplyScale(n, a);
            Commit(n);
            ApplyProps(n, a.TryGetProperty("props", out var p) ? p : default);
            Commit(n);
            return Created(n);
        }

        static object BreakCreate(JsonElement a)
        {
            var b = new McConnectionBreak();
            b.StartPoint = P(a.GetProperty("start"));
            b.EndPoint = P(a.GetProperty("end"));
            b.BreakType = (McConnectionBreak.BreakTypes)Enum.Parse(
                typeof(McConnectionBreak.BreakTypes), Str(a, "type", "Linear"), true);
            if (a.TryGetProperty("length", out var l))
                b.Length = l.GetDouble();
            if (a.TryGetProperty("width", out var w))
                b.Width = w.GetDouble();
            // nanoCAD picked Offset -120 on its own, putting the break beside
            // the two points rather than between them (measured, nanoCAD 24.1).
            b.Offset = Num(a, "offset", 0);
            b.DbEntity.AddToCurrentDocument();
            ApplyScale(b, a);
            Commit(b);
            ApplyProps(b, a.TryGetProperty("props", out var p) ? p : default);
            Commit(b);
            return Created(b);
        }

        // Any MultiCAD object, including those MultiCAD.NET has no class for
        // (the level mark): copy an existing one, move it, change properties.
        static object Clone(JsonElement a)
        {
            string handle = a.GetProperty("handle").GetString() ?? "";
            var src = Resolve(handle) as McEntity
                ?? throw new ArgumentException("object " + handle + " is not a MultiCAD entity");
            var copy = (McDbObject)src.DbEntity.Clone(false);
            copy.AddToCurrentDocument();
            var copyEnt = copy.Entity;
            ApplyScale(copyEnt, a);
            double dx = Num(a, "dx", 0), dy = Num(a, "dy", 0);
            if (dx != 0 || dy != 0)
                copyEnt.DbEntity.Transform(Matrix3d.Displacement(new Vector3d(dx, dy, 0)));
            Commit(copyEnt);
            ApplyProps(copyEnt, a.TryGetProperty("props", out var p) ? p : default);
            Commit(copyEnt);
            return Created(copyEnt);
        }

        // Any MultiCAD entity by its class id (from spds_list / describe) or by
        // the class of an existing object, e.g. a level mark, which has no
        // MultiCAD.NET class of its own. Created at the origin, then moved so
        // its insertion lands at `at` (the move is measured, not assumed).
        static object CreateByClass(JsonElement a)
        {
            Guid classId;
            if (a.TryGetProperty("like_handle", out var like))
                classId = Resolve(like.GetString() ?? "").ClassID;
            else
                classId = Guid.Parse(Str(a, "class_id", ""));
            var created = McObjectManager.Create(classId) as McEntity
                ?? throw new ArgumentException("class " + classId + " is not a MultiCAD entity");
            created.DbEntity.AddToCurrentDocument();
            ApplyScale(created, a);
            if (a.TryGetProperty("at", out var at))
            {
                var p = P(at);
                if (p.X != 0 || p.Y != 0)
                    created.DbEntity.Transform(Matrix3d.Displacement(new Vector3d(p.X, p.Y, 0)));
            }
            Commit(created);
            ApplyProps(created, a.TryGetProperty("props", out var pr) ? pr : default);
            Commit(created);
            return Created(created);
        }

        // "Scale" is the annotation scale every СПДС object carries; a new
        // object otherwise takes the drawing's current one (1:100 in the СПДС
        // template, which drew an A3 format 42000 wide on nanoCAD 24.1).
        static void ApplyScale(McObject obj, JsonElement a)
        {
            if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("scale", out var s))
                return;
            var src = Source(obj);
            var p = src?.ObjectProperties.GetProperty("Scale");
            if (p != null && !p.IsReadOnly)
                p.SetValue(s.GetDouble());
        }

        // -- helpers ------------------------------------------------------------

        static McObject Resolve(string handle)
        {
            long h = long.Parse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var id = McObjectId.FromHandle(h);
            if (id.IsNull)
                throw new ArgumentException("no object with handle " + handle);
            return id.GetObject() ?? throw new ArgumentException("handle " + handle + " is not a MultiCAD object");
        }

        static McPropertySource? Source(McObject obj)
        {
            if (obj is McEntity e)
                return e.DbObject;
            return obj as McPropertySource;
        }

        static List<string> ApplyProps(McObject obj, JsonElement props)
        {
            var changed = new List<string>();
            if (props.ValueKind != JsonValueKind.Object)
                return changed;
            var src = Source(obj) ?? throw new ArgumentException("object has no properties");
            var bag = src.ObjectProperties;
            foreach (var kv in props.EnumerateObject())
            {
                var p = bag.GetProperty(kv.Name)
                    ?? throw new ArgumentException("unknown property '" + kv.Name + "'");
                if (p.IsReadOnly)
                    throw new ArgumentException("property '" + kv.Name + "' is read-only");
                var value = FromJson(kv.Value, p.PropertyType);
                p.SetValue(value);
                if (!Same(bag.GetProperty(kv.Name)?.GetValue(), value))
                    WriteThroughProject(obj, kv.Name, value);
                changed.Add(kv.Name);
            }
            return changed;
        }

        // A stamp field bound to the drawing's project data (mode Get /
        // GetCustom) does not keep a value written on the format: nanoCAD 24.1
        // reverted Designation and Drawing type. Write the bound node property
        // (the link name, else the same name) on the object's node and up its
        // parents, then refresh the object from the node.
        static void WriteThroughProject(McObject obj, string name, object? value)
        {
            if (obj is not McEntity e)
                return;
            McObjNode? node = null;
            try { node = e.DbObject.ProjectNode; } catch { }
            if (node == null)
                return;
            string link = "";
            try { node.GetPropertyMode(name, ref link); } catch { }
            string target = string.IsNullOrEmpty(link) ? name : link;
            McBasePrjNode? current = node;
            for (int depth = 0; current != null && depth < 6; depth++)
            {
                try
                {
                    var np = current.ObjectProperties.GetProperty(target);
                    if (np != null && !np.IsReadOnly)
                    {
                        np.SetValue(value);
                        break;
                    }
                }
                catch (Exception ex) { Log("project write " + target + ": " + ex.Message); }
                current = current is McObjNode on ? on.Parent : (current as McInsertionNode)?.Parent;
            }
            try { node.UpdateLinkedProperties(); } catch { }
            try { node.UpdateObject(e.DbObject, true); } catch (Exception ex) { Log("UpdateObject: " + ex.Message); }
        }

        static bool Same(object? a, object? b)
        {
            if (a == null || b == null)
                return a == b;
            return string.Equals(Convert.ToString(a, CultureInfo.InvariantCulture),
                Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        // Diagnostics: how each property of an object is bound to the project.
        static object ProjectInfo(JsonElement a)
        {
            string handle = a.GetProperty("handle").GetString() ?? "";
            var obj = Resolve(handle);
            var d = new Dictionary<string, object> { ["handle"] = handle };
            if (obj is not McEntity e)
                return d;
            McObjNode? node = null;
            try { node = e.DbObject.ProjectNode; } catch (Exception ex) { d["node_error"] = ex.Message; }
            d["has_node"] = node != null;
            if (node == null)
                return d;
            var modes = new Dictionary<string, object>();
            foreach (var name in e.DbObject.ObjectProperties.GetNames())
            {
                try
                {
                    string link = "";
                    var mode = node.GetPropertyMode(name, ref link);
                    modes[name] = new[] { mode.ToString(), link };
                }
                catch { }
            }
            d["modes"] = modes;
            d["node_props"] = Bag(node);
            var chain = new List<object>();
            McBasePrjNode? parent = node.Parent;
            for (int depth = 0; parent != null && depth < 6; depth++)
            {
                chain.Add(new Dictionary<string, object>
                {
                    ["class"] = parent.GetType().Name,
                    ["props"] = Bag(parent),
                });
                parent = parent is McInsertionNode ins ? ins.Parent : null;
            }
            d["parents"] = chain;
            return d;
        }

        // Diagnostics for what the property list does not show — e.g. a stamp
        // cell's text height, set in nanoCAD's "Настройка текста" dialog and
        // stored per format (measured: a second format kept 3.5 mm while the
        // first had 1.5 mm), yet absent from ObjectProperties.
        static object Inspect(JsonElement a)
        {
            string handle = a.GetProperty("handle").GetString() ?? "";
            var obj = Resolve(handle);
            var d = new Dictionary<string, object> { ["handle"] = handle, ["class"] = obj.GetType().FullName ?? "" };
            if (obj is not McEntity e)
                return d;
            d["bags"] = Bag3(e.DbObject);
            d["children"] = Children(e.DbEntity, 0);
            try
            {
                var bytes = e.DbEntity.SerializeToBytes();
                d["bytes_len"] = bytes.Length;
                var hits = new Dictionary<string, object>();
                foreach (var v in new[] { 1.5, 2.5, 3.5, 5.0, 7.0 })
                {
                    var offs = new List<int>();
                    var pat = BitConverter.GetBytes(v);
                    var patf = BitConverter.GetBytes((float)v);
                    for (int i = 0; i + 8 <= bytes.Length; i++)
                        if (Match(bytes, i, pat)) offs.Add(i);
                    var offsf = new List<int>();
                    for (int i = 0; i + 4 <= bytes.Length; i++)
                        if (Match(bytes, i, patf)) offsf.Add(i);
                    hits[v.ToString(CultureInfo.InvariantCulture)] = new Dictionary<string, object>
                    {
                        ["double"] = offs.Count > 40 ? offs.GetRange(0, 40) : offs,
                        ["float"] = offsf.Count > 40 ? offsf.GetRange(0, 40) : offsf,
                    };
                }
                d["value_offsets"] = hits;
                if (Bool(a, "bytes", false))
                    d["bytes_b64"] = Convert.ToBase64String(bytes);
            }
            catch (Exception ex) { d["serialize_error"] = ex.Message; }
            return d;
        }

        // A stamp cell's text height (nanoCAD's "Настройка текста" dialog) has
        // no API. It is kept in the format's own data as a cell record right
        // after the cell's text: 0B 02 0D 26 02 7D, then three doubles each
        // tagged 06 — the cell's y, x and text height (measured on nanoCAD
        // 24.1: ComplexName 37.5 / 116.81 / 3.5, the same format set to 1.5 mm
        // by hand stored 1.5). The format is serialized, that double patched,
        // a new format created from the bytes and the old one erased — so the
        // handle changes and is returned.
        static readonly byte[] CellMark = { 0x0B, 0x02, 0x0D, 0x26, 0x02, 0x7D };

        static object CellTextHeight(JsonElement a)
        {
            string handle = a.GetProperty("handle").GetString() ?? "";
            string field = Str(a, "field", "ComplexName");
            double height = a.GetProperty("height").GetDouble();
            var obj = Resolve(handle) as McEntity
                ?? throw new ArgumentException("object " + handle + " is not a MultiCAD entity");
            var rebuilt = RebuildWithCellHeight(obj, field, height, out double before, out double after);
            return new Dictionary<string, object>
            {
                ["old_handle"] = handle,
                ["handle"] = Hex(rebuilt.ID),
                ["field"] = field,
                ["height_before"] = before,
                ["height_after"] = double.IsNaN(after) ? "unreadable" : after,
            };
        }

        // Serialize, patch the cell's height, rebuild, erase the original only
        // once the rebuilt object is in the drawing. Verified live on nanoCAD
        // 24.1: the address cell went 3.5 -> 2.5 mm and nothing else changed.
        static McEntity RebuildWithCellHeight(McEntity obj, string field, double height,
            out double before, out double after)
        {
            if (height <= 0 || height > 20)
                throw new ArgumentException("height must be between 0 and 20 mm");
            var prop = obj.DbObject.ObjectProperties.GetProperty(field)
                ?? throw new ArgumentException("unknown field '" + field + "'");
            string value = Convert.ToString(prop.GetValue(), CultureInfo.InvariantCulture) ?? "";
            if (value.Length == 0)
                throw new ArgumentException("field '" + field + "' is empty; fill it first");

            byte[] bytes = obj.DbEntity.SerializeToBytes();
            int at = FindCellHeight(bytes, value, out before);
            if (at < 0)
                throw new ArgumentException("no cell record for '" + field + "' in this format "
                    + "(nanoCAD keeps one only for cells with their own text settings)");
            after = before;
            if (Math.Abs(before - height) < 1e-9)
                return obj;
            Array.Copy(BitConverter.GetBytes(height), 0, bytes, at, 8);

            var copy = McDbEntity.DeserializeNewFromBytes(bytes)
                ?? throw new InvalidOperationException("nanoCAD did not rebuild the format from its data");
            if (!copy.AddToCurrentDocument())
                throw new InvalidOperationException("nanoCAD did not add the rebuilt format");
            var copyEnt = copy.Entity;
            Commit(copyEnt);
            after = double.NaN;
            try { FindCellHeight(copyEnt.DbEntity.SerializeToBytes(), value, out after); } catch { }
            McObjectManager.Erase(obj.ID);
            McObjectManager.UpdateAll();
            return copyEnt;
        }

        static int FindCellHeight(byte[] bytes, string text, out double height)
        {
            height = double.NaN;
            var pat = Encoding.Unicode.GetBytes(text);
            for (int i = IndexOf(bytes, pat, 0); i >= 0; i = IndexOf(bytes, pat, i + 1))
            {
                int end = i + pat.Length;
                int mark = IndexOf(bytes, CellMark, end);
                if (mark < 0 || mark - end > 160)
                    continue;
                int k = mark + CellMark.Length, found = 0;
                while (k + 9 <= bytes.Length && k < mark + 80)
                {
                    if (bytes[k] == 0x06)
                    {
                        found++;
                        if (found == 3)
                        {
                            height = BitConverter.ToDouble(bytes, k + 1);
                            return k + 1;
                        }
                        k += 9;
                    }
                    else
                        k++;
                }
            }
            return -1;
        }

        static int IndexOf(byte[] hay, byte[] needle, int from)
        {
            for (int i = Math.Max(0, from); i + needle.Length <= hay.Length; i++)
                if (Match(hay, i, needle))
                    return i;
            return -1;
        }

        static bool Match(byte[] b, int at, byte[] pat)
        {
            for (int k = 0; k < pat.Length; k++)
                if (b[at + k] != pat[k]) return false;
            return true;
        }

        static Dictionary<string, object> Bag3(McPropertySource src)
        {
            var d = new Dictionary<string, object>();
            foreach (var kind in new[] { "object", "custom", "system" })
            {
                var bag = new Dictionary<string, object>();
                try
                {
                    var props = kind == "object" ? src.ObjectProperties
                        : kind == "custom" ? src.CustomProperties : src.SystemProperties;
                    foreach (var n in props.GetNames())
                    {
                        try
                        {
                            var p = props.GetProperty(n);
                            bag[n] = kind == "object" ? (p?.PropertyType?.Name ?? "") : ToJson(p?.GetValue());
                        }
                        catch (Exception ex) { bag[n] = "ERROR " + ex.Message; }
                    }
                }
                catch (Exception ex) { bag["!"] = ex.Message; }
                d[kind] = bag;
            }
            return d;
        }

        static List<object> Children(McDbEntity db, int depth)
        {
            var list = new List<object>();
            if (depth > 3)
                return list;
            var ids = new List<McObjectId>();
            try { ids.AddRange(db.ChildrenIds); } catch { }
            try
            {
                foreach (var id in db.GetChildrenIdsEx(ChildrenSysFilterType.ChildrenSysFilter_All))
                    if (!ids.Contains(id)) ids.Add(id);
            }
            catch { }
            foreach (var id in ids)
            {
                var c = new Dictionary<string, object> { ["mc_class"] = id.ClassName ?? "", ["handle"] = Hex(id) };
                try
                {
                    var o = id.GetObject();
                    c["class"] = o?.GetType().FullName ?? "";
                    if (o is McTText tt)
                    {
                        c["text_height"] = tt.TextHeight;
                        c["content"] = tt.GetContent();
                    }
                    if (o is McEntity ce)
                    {
                        c["props"] = Bag(ce.DbObject);
                        c["children"] = Children(ce.DbEntity, depth + 1);
                    }
                }
                catch (Exception ex) { c["error"] = ex.Message; }
                list.Add(c);
            }
            return list;
        }

        static Dictionary<string, object> Bag(McPropertySource src)
        {
            var bag = new Dictionary<string, object>();
            foreach (var kind in new[] { "object", "custom" })
            {
                try
                {
                    var props = kind == "object" ? src.ObjectProperties : src.CustomProperties;
                    foreach (var n in props.GetNames())
                    {
                        try { bag[kind + ":" + n] = ToJson(props.GetProperty(n)?.GetValue()); } catch { }
                    }
                }
                catch { }
            }
            return bag;
        }

        static void Commit(McObject obj, bool pushToProject = false)
        {
            if (obj is McEntity e)
            {
                // Shared stamp fields (обозначение, объект, организация...) live
                // in the drawing's project node; Update pulls them back from it
                // and silently undid a fresh Designation (measured, nanoCAD 24.1).
                // Push what was just written to the node first, as nanoCAD's own
                // stamp editor does — it then reaches every format sharing it.
                if (pushToProject)
                {
                    try { e.DbObject.UpdateProjectData(ProjectDataUpdateDirection.FromObjectToNode); }
                    catch (Exception ex) { Log("UpdateProjectData: " + ex.Message); }
                }
                e.DbObject.Update();
            }
            McObjectManager.UpdateAll();
        }

        static Dictionary<string, object> Created(McObject obj)
        {
            var d = DescribeOne(obj, Hex(obj.ID));
            d["created"] = true;
            return d;
        }

        static string Hex(McObjectId id) => id.Handle.ToString("X", CultureInfo.InvariantCulture);

        static double TemplateValue(string name)
        {
            switch (name.ToUpperInvariant())
            {
                case "GOST": return McFormat.Template_GOST;
                case "ISO": return McFormat.Template_ISO;
                case "DIN": return McFormat.Template_DIN;
                case "NONE": return McFormat.Template_NONE;
                default: return McFormat.Template_SPDS;
            }
        }

        static Point3d P(JsonElement e)
        {
            var arr = new List<double>();
            foreach (var v in e.EnumerateArray())
                arr.Add(v.GetDouble());
            return new Point3d(arr[0], arr[1], arr.Count > 2 ? arr[2] : 0);
        }

        static object Pt(Point3d p) => new[] { p.X, p.Y, p.Z };

        static string Str(JsonElement a, string name, string def) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) ? v.GetString() ?? def : def;

        static bool Bool(JsonElement a, string name, bool def) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) ? v.GetBoolean() : def;

        static double Num(JsonElement a, string name, double def) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) ? v.GetDouble() : def;

        static object ToJson(object? v)
        {
            switch (v)
            {
                case null: return "";
                case string s: return s;
                case bool b: return b;
                case int or long or short or byte or uint: return Convert.ToInt64(v, CultureInfo.InvariantCulture);
                case double or float or decimal: return Convert.ToDouble(v, CultureInfo.InvariantCulture);
                case Point3d p: return Pt(p);
                case McObjectId id: return id.IsNull ? "" : Hex(id);
                case Enum e: return e.ToString();
                default: return v.ToString() ?? "";
            }
        }

        static object? FromJson(JsonElement v, Type t)
        {
            if (t == typeof(string)) return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
            if (t == typeof(double)) return v.ValueKind == JsonValueKind.String ? double.Parse(v.GetString()!, CultureInfo.InvariantCulture) : v.GetDouble();
            if (t == typeof(float)) return (float)v.GetDouble();
            if (t == typeof(int)) return v.ValueKind == JsonValueKind.String ? int.Parse(v.GetString()!, CultureInfo.InvariantCulture) : v.GetInt32();
            if (t == typeof(long)) return v.GetInt64();
            if (t == typeof(bool)) return v.GetBoolean();
            if (t == typeof(Point3d)) return P(v);
            if (t.IsEnum) return Enum.Parse(t, v.GetString() ?? v.ToString(), true);
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            if (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) return v.GetBoolean();
            return v.ToString();
        }

        static string Sanitize(string id)
        {
            var sb = new StringBuilder();
            foreach (char c in id)
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                    sb.Append(c);
            return sb.Length > 0 ? sb.ToString() : "unknown";
        }

        static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }
    }
}
