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

    public class Commands
    {
        public const string PluginVersion = "0.1.1";

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
            var filter = ObjectFilter.Create(false);
            filter.AllObjects = true;
            var list = new List<object>();
            int total = 0;
            foreach (var id in filter.GetObjects())
            {
                string cls = id.ClassName ?? "";
                if (contains.Length > 0 && !cls.ToLowerInvariant().Contains(contains))
                    continue;
                total++;
                if (list.Count >= limit)
                    continue;
                var item = new Dictionary<string, object> { ["handle"] = Hex(id), ["mc_class"] = cls };
                try
                {
                    if (id.GetObject() is McEntity e)
                    {
                        item["layer"] = e.DbEntity.Layer;
                        var bb = e.DbEntity.BoundingBox;
                        item["bbox"] = new object[] { Pt(bb.MinPoint), Pt(bb.MaxPoint) };
                    }
                }
                catch { }
                list.Add(item);
            }
            return new Dictionary<string, object> { ["total"] = total, ["objects"] = list };
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
            Commit(obj);
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
            ApplyProps(f, a.TryGetProperty("props", out var p) ? p : default);
            Commit(f);
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
            if (a.TryGetProperty("targets", out var targets))
                foreach (var t in targets.EnumerateArray())
                    n.Leader.AddSegment(n.Origin, P(t));
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
            b.DbEntity.AddToCurrentDocument();
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
            double dx = Num(a, "dx", 0), dy = Num(a, "dy", 0);
            if (dx != 0 || dy != 0)
                copyEnt.DbEntity.Transform(Matrix3d.Displacement(new Vector3d(dx, dy, 0)));
            ApplyProps(copyEnt, a.TryGetProperty("props", out var p) ? p : default);
            Commit(copyEnt);
            return Created(copyEnt);
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
                p.SetValue(FromJson(kv.Value, p.PropertyType));
                changed.Add(kv.Name);
            }
            return changed;
        }

        static void Commit(McObject obj)
        {
            if (obj is McEntity e)
                e.DbObject.Update();
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
