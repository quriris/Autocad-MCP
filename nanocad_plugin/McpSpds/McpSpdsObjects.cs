// McpSpds, part 2: every СПДС object, not only the ones with their own op.
//
// MultiCAD.NET has a class for most СПДС objects (axes and axis grids,
// section and view marks, node marks, chain/comb/knot leaders, rooms, walls,
// windows, doors, stairs, tables...). Rather than one hand-written op per
// class, three generic ops reach all of them by reflection:
//
//   classes  — the catalogue: every creatable MultiCAD entity class with its
//              writable members (name, type, enum values)
//   new      — create one by class name: members set before it enters the
//              drawing (`init`), then `steps` (set / call / get by dotted path),
//              then nanoCAD's property list (`props`)
//   invoke   — the same `steps` on an object already in the drawing
//
// Objects with no MultiCAD.NET class at all (the level mark, …) exist only as
// native classes; `native_classes` lists the ones nanoCAD has registered by
// reading class ids out of the СПДС modules and asking nanoCAD for each name.
//
// axis_create / axis_grid_create are typed shortcuts for the building axes,
// which need a few calls in a fixed order.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Multicad;
using Multicad.DatabaseServices;
using Multicad.Geometry;
using Multicad.Symbols;

namespace McpSpds
{
    public partial class Commands
    {
        const BindingFlags Inst = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

        // -- the catalogue ------------------------------------------------------

        static IEnumerable<Type> MulticadTypes()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = asm.GetName().Name ?? "";
                if (!n.StartsWith("mapi", StringComparison.OrdinalIgnoreCase))
                    continue;
                Type[] types;
                try { types = asm.GetExportedTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
                catch { continue; }
                foreach (var t in types)
                    yield return t;
            }
        }

        static bool Creatable(Type t) =>
            typeof(McEntity).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null;

        static Type FindType(string name)
        {
            Type? hit = null;
            foreach (var t in MulticadTypes())
            {
                if (string.Equals(t.FullName, name, StringComparison.OrdinalIgnoreCase))
                    return t;
                if (hit == null && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                    hit = t;
            }
            return hit ?? throw new ArgumentException("no MultiCAD class '" + name + "' (see the classes op)");
        }

        static object Classes(JsonElement a)
        {
            string filter = Str(a, "filter", "").ToLowerInvariant();
            bool members = Bool(a, "members", true);
            bool all = Bool(a, "all_types", false);
            var list = new List<object>();
            foreach (var t in MulticadTypes().OrderBy(t => t.FullName))
            {
                if (!(all ? !t.IsNested || t.IsNestedPublic : Creatable(t)))
                    continue;
                if (filter.Length > 0 && !(t.FullName ?? "").ToLowerInvariant().Contains(filter))
                    continue;
                var d = new Dictionary<string, object>
                {
                    ["class"] = t.Name,
                    ["full_name"] = t.FullName ?? "",
                    ["base"] = t.BaseType?.Name ?? "",
                    ["creatable"] = Creatable(t),
                };
                if (members)
                    d["members"] = Members(t, Bool(a, "methods", false));
                list.Add(d);
            }
            return new Dictionary<string, object> { ["count"] = list.Count, ["classes"] = list };
        }

        static List<object> Members(Type t, bool methods)
        {
            var list = new List<object>();
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0 || p.DeclaringType == typeof(object))
                    continue;
                // The McEntity plumbing (DbEntity, ID...) is the same on every class.
                if (p.DeclaringType != null && (p.DeclaringType == typeof(McEntity) || p.DeclaringType == typeof(McObject)))
                    continue;
                var m = new Dictionary<string, object>
                {
                    ["name"] = p.Name,
                    ["type"] = TypeName(p.PropertyType),
                    ["writable"] = p.CanWrite && p.GetSetMethod() != null,
                };
                if (p.PropertyType.IsEnum)
                    m["values"] = Enum.GetNames(p.PropertyType);
                list.Add(m);
            }
            if (methods)
                foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (mi.IsSpecialName)
                        continue;
                    list.Add(new Dictionary<string, object>
                    {
                        ["method"] = mi.Name,
                        ["returns"] = TypeName(mi.ReturnType),
                        ["params"] = mi.GetParameters().Select(p => TypeName(p.ParameterType) + " " + p.Name).ToArray(),
                    });
                }
            return list;
        }

        static string TypeName(Type t)
        {
            if (t.IsByRef)
                return "ref " + TypeName(t.GetElementType()!);
            if (t.IsGenericType)
                return t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(TypeName)) + ">";
            return t.Name;
        }

        // -- class ids nanoCAD has registered -------------------------------------

        // A class id is a GUID inside the native module that implements the
        // class (measured: the level mark's id sits in SpdsSymbols.dll four
        // times). Every random (version 4) GUID-shaped run of 16 bytes in the
        // СПДС modules is offered to McObjectManager.GetClassName; nanoCAD
        // names the ones it registered and nothing else.
        static object NativeClasses(JsonElement a)
        {
            string cache = Path.Combine(Dir, "native_classes-" + PluginVersion + ".json");
            if (!Bool(a, "refresh", false) && File.Exists(cache))
                return JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(cache, Encoding.UTF8));

            string bin = Path.GetDirectoryName(typeof(McObjectManager).Assembly.Location) ?? "";
            var found = new Dictionary<Guid, Dictionary<string, object>>();
            var tried = new HashSet<Guid>();
            int files = 0;
            foreach (var file in Directory.GetFiles(bin, "*.dll").Concat(Directory.GetFiles(bin, "*.tx")))
            {
                var info = new FileInfo(file);
                if (info.Length > 16_000_000)
                    continue;
                byte[] bytes;
                try { bytes = File.ReadAllBytes(file); } catch { continue; }
                files++;
                var g = new byte[16];
                for (int i = 0; i + 16 <= bytes.Length; i += 4)
                {
                    // version nibble 4, RFC 4122 variant
                    if ((bytes[i + 7] >> 4) != 4 || (bytes[i + 8] & 0xC0) != 0x80)
                        continue;
                    Array.Copy(bytes, i, g, 0, 16);
                    var guid = new Guid(g);
                    if (!tried.Add(guid))
                        continue;
                    string name;
                    try { name = McObjectManager.GetClassName(guid) ?? ""; } catch { continue; }
                    if (name.Length == 0)
                        continue;
                    found[guid] = new Dictionary<string, object>
                    {
                        ["class_id"] = guid.ToString(),
                        ["name"] = name,
                        ["module"] = Path.GetFileName(file),
                    };
                }
            }
            // The .NET classes say which ids they wrap (static TypeID).
            foreach (var t in MulticadTypes())
            {
                try
                {
                    var p = t.GetProperty("TypeID", BindingFlags.Public | BindingFlags.Static);
                    if (p?.GetValue(null) is Guid id && found.TryGetValue(id, out var d))
                        d["net_class"] = t.FullName ?? t.Name;
                }
                catch { }
            }
            var result = new Dictionary<string, object>
            {
                ["modules_scanned"] = files,
                ["ids_tried"] = tried.Count,
                ["count"] = found.Count,
                ["classes"] = found.Values.OrderBy(d => (string)d["name"]).ToList(),
            };
            try { File.WriteAllText(cache, JsonSerializer.Serialize(result), new UTF8Encoding(false)); } catch { }
            return result;
        }

        // -- create any class / run steps -----------------------------------------

        static object NewObject(JsonElement a)
        {
            var type = FindType(Str(a, "class", ""));
            if (!Creatable(type))
                throw new ArgumentException(type.FullName + " is not a creatable MultiCAD entity");
            var obj = (McEntity)Activator.CreateInstance(type)!;
            if (a.TryGetProperty("init", out var init) && init.ValueKind == JsonValueKind.Object)
                foreach (var kv in init.EnumerateObject())
                    SetPath(obj, kv.Name, kv.Value);
            if (!obj.DbEntity.AddToCurrentDocument())
                throw new InvalidOperationException("nanoCAD did not add the " + type.Name);
            return Finish(obj, a);
        }

        static object Invoke(JsonElement a)
        {
            string handle = a.GetProperty("handle").GetString() ?? "";
            return Finish(Resolve(handle), a);
        }

        // Shared tail of new / invoke / the typed creators: layer, scale,
        // steps, property list, each followed by an update.
        static Dictionary<string, object> Finish(McObject obj, JsonElement a)
        {
            if (obj is McEntity e && a.TryGetProperty("layer", out var layer) && layer.GetString() is string ln && ln.Length > 0)
                e.DbEntity.Layer = ln;
            ApplyScale(obj, a);
            Commit(obj);
            var results = new List<object>();
            if (a.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var step in steps.EnumerateArray())
                {
                    try { results.Add(RunStep(obj, step)); }
                    catch (Exception ex)
                    {
                        var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                        throw new ArgumentException("step " + i + " (" + step.ToString() + "): "
                            + inner.GetType().Name + ": " + inner.Message);
                    }
                    i++;
                }
                Commit(obj);
            }
            var changed = ApplyProps(obj, a.TryGetProperty("props", out var p) ? p : default);
            if (changed.Count > 0)
                Commit(obj);
            var d = Bool(a, "describe", true) ? DescribeOne(obj, Hex(obj.ID)) : new Dictionary<string, object> { ["handle"] = Hex(obj.ID) };
            d["type"] = obj.GetType().FullName ?? "";
            if (results.Count > 0)
                d["steps"] = results;
            return d;
        }

        // {"set": "a.b", "value": v} | {"call": "a.b", "args": [...]} | {"get": "a.b"}
        // A path walks properties, fields, no-argument methods and [index].
        static object RunStep(object root, JsonElement step)
        {
            if (step.TryGetProperty("set", out var set))
            {
                SetPath(root, set.GetString() ?? "", step.GetProperty("value"));
                return "ok";
            }
            if (step.TryGetProperty("get", out var get))
                return Out(GetPath(root, get.GetString() ?? ""), 0);
            if (step.TryGetProperty("call", out var call))
            {
                string path = call.GetString() ?? "";
                int dot = path.LastIndexOf('.');
                object target = dot < 0 ? root : GetPath(root, path.Substring(0, dot))
                    ?? throw new ArgumentException("'" + path.Substring(0, dot) + "' is null");
                var args = step.TryGetProperty("args", out var ar) && ar.ValueKind == JsonValueKind.Array
                    ? ar.EnumerateArray().ToList() : new List<JsonElement>();
                return CallMethod(target, dot < 0 ? path : path.Substring(dot + 1), args);
            }
            throw new ArgumentException("a step needs set, get or call");
        }

        static object? GetPath(object root, string path)
        {
            object? cur = root;
            if (path.Length == 0)
                return cur;
            foreach (var raw in path.Split('.'))
            {
                if (cur == null)
                    throw new ArgumentException("null before '" + raw + "' in '" + path + "'");
                string name = raw;
                int? index = null;
                int br = raw.IndexOf('[');
                if (br >= 0)
                {
                    name = raw.Substring(0, br);
                    index = int.Parse(raw.Substring(br + 1).TrimEnd(']'), CultureInfo.InvariantCulture);
                }
                if (name.Length > 0)
                    cur = GetMember(cur, name);
                if (index != null)
                    cur = GetIndex(cur!, index.Value);
            }
            return cur;
        }

        static object? GetMember(object obj, string name)
        {
            var t = obj.GetType();
            var p = t.GetProperties(Inst).FirstOrDefault(x =>
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && x.GetIndexParameters().Length == 0);
            if (p != null)
                return p.GetValue(obj);
            var f = t.GetField(name, Inst);
            if (f != null)
                return f.GetValue(obj);
            var m = t.GetMethods(Inst).FirstOrDefault(x =>
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && x.GetParameters().Length == 0);
            if (m != null)
                return m.Invoke(obj, null);
            throw new ArgumentException(t.Name + " has no member '" + name + "'");
        }

        static object? GetIndex(object obj, int i)
        {
            if (obj is IList list)
                return list[i];
            var item = obj.GetType().GetProperties(Inst).FirstOrDefault(x =>
                x.GetIndexParameters().Length == 1 && x.GetIndexParameters()[0].ParameterType == typeof(int));
            if (item != null)
                return item.GetValue(obj, new object[] { i });
            var gi = obj.GetType().GetMethod("get_Item", new[] { typeof(int) });
            if (gi != null)
                return gi.Invoke(obj, new object[] { i });
            if (obj is IEnumerable en)
                return en.Cast<object>().ElementAt(i);
            throw new ArgumentException(obj.GetType().Name + " cannot be indexed");
        }

        static void SetPath(object root, string path, JsonElement value)
        {
            int dot = path.LastIndexOf('.');
            object target = dot < 0 ? root : GetPath(root, path.Substring(0, dot))
                ?? throw new ArgumentException("'" + path.Substring(0, dot) + "' is null");
            string name = dot < 0 ? path : path.Substring(dot + 1);
            var t = target.GetType();
            var p = t.GetProperties(Inst).FirstOrDefault(x =>
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && x.GetIndexParameters().Length == 0);
            if (p != null && p.GetSetMethod() != null)
            {
                p.SetValue(target, ToNet(value, p.PropertyType));
                return;
            }
            var f = t.GetField(name, Inst);
            if (f != null && !f.IsInitOnly)
            {
                f.SetValue(target, ToNet(value, f.FieldType));
                return;
            }
            throw new ArgumentException(t.Name + " has no writable member '" + name + "'");
        }

        static object CallMethod(object target, string name, List<JsonElement> args)
        {
            bool isType = target is Type;
            var t = isType ? (Type)target : target.GetType();
            var flags = isType ? BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase : Inst;
            var candidates = t.GetMethods(flags)
                .Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.GetParameters().Length)
                .ToList();
            if (candidates.Count == 0)
                throw new ArgumentException(t.Name + " has no method '" + name + "'");
            Exception? last = null;
            foreach (var m in candidates)
            {
                var ps = m.GetParameters();
                int required = ps.Count(p => !p.IsOut);
                if (args.Count < required || args.Count > ps.Length)
                    continue;
                object?[] values;
                try
                {
                    values = new object?[ps.Length];
                    for (int i = 0; i < ps.Length; i++)
                    {
                        var pt = ps[i].ParameterType.IsByRef ? ps[i].ParameterType.GetElementType()! : ps[i].ParameterType;
                        values[i] = i < args.Count ? ToNet(args[i], pt) : (pt.IsValueType ? Activator.CreateInstance(pt) : null);
                    }
                }
                catch (Exception ex) { last = ex; continue; }
                object? ret = m.Invoke(isType ? null : target, values);
                var outs = new List<object>();
                for (int i = 0; i < ps.Length; i++)
                    if (ps[i].ParameterType.IsByRef)
                        outs.Add(Out(values[i], 0));
                if (outs.Count == 0)
                    return Out(ret, 0);
                return new Dictionary<string, object> { ["return"] = Out(ret, 0), ["out"] = outs };
            }
            throw new ArgumentException("no overload of " + t.Name + "." + name + " takes "
                + args.Count + " argument(s)" + (last != null ? ": " + last.Message : "")
                + " — overloads: " + string.Join("; ", candidates.Select(m =>
                    "(" + string.Join(", ", m.GetParameters().Select(p => TypeName(p.ParameterType))) + ")")));
        }

        // JSON -> a .NET value of type t.
        static object? ToNet(JsonElement v, Type t)
        {
            if (v.ValueKind == JsonValueKind.Null)
                return null;
            var u = Nullable.GetUnderlyingType(t) ?? t;
            if (u == typeof(string)) return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
            if (u == typeof(bool)) return v.ValueKind == JsonValueKind.String ? bool.Parse(v.GetString()!) : v.GetBoolean();
            if (u == typeof(Point3d)) return P3(v);
            if (u == typeof(Point2d)) { var p = P3(v); return new Point2d(p.X, p.Y); }
            if (u == typeof(Vector3d)) { var p = P3(v); return new Vector3d(p.X, p.Y, p.Z); }
            if (u == typeof(Guid)) return Guid.Parse(v.GetString() ?? "");
            if (u == typeof(McObjectId)) return Resolve(v.GetString() ?? "").ID;
            if (u.IsEnum) return EnumValue(v, u);
            if (u.IsPrimitive || u == typeof(decimal))
            {
                double d = v.ValueKind == JsonValueKind.String
                    ? double.Parse(v.GetString()!, CultureInfo.InvariantCulture) : v.GetDouble();
                return System.Convert.ChangeType(d, u, CultureInfo.InvariantCulture);
            }
            if (typeof(McObject).IsAssignableFrom(u) && v.ValueKind == JsonValueKind.String)
            {
                var o = Resolve(v.GetString() ?? "");
                if (u.IsInstanceOfType(o))
                    return o;
                var cast = u.GetMethod("CastFrom", BindingFlags.Public | BindingFlags.Static);
                if (cast != null)
                    return cast.Invoke(null, new object[] { o });
                throw new ArgumentException("object " + v.GetString() + " is a " + o.GetType().Name + ", not a " + u.Name);
            }
            if (v.ValueKind == JsonValueKind.Array)
            {
                if (u.IsArray)
                {
                    var et = u.GetElementType()!;
                    var items = v.EnumerateArray().Select(x => ToNet(x, et)).ToArray();
                    var arr = Array.CreateInstance(et, items.Length);
                    for (int i = 0; i < items.Length; i++) arr.SetValue(items[i], i);
                    return arr;
                }
                // List<T>, Collection<T>, McPointCollection...: anything with Add(T).
                var coll = u.GetInterfaces().Concat(new[] { u }).FirstOrDefault(i =>
                    i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICollection<>));
                if (coll != null && !u.IsInterface && u.GetConstructor(Type.EmptyTypes) != null)
                {
                    var et = coll.GetGenericArguments()[0];
                    var inst = Activator.CreateInstance(u)!;
                    var add = coll.GetMethod("Add")!;
                    foreach (var x in v.EnumerateArray())
                        add.Invoke(inst, new[] { ToNet(x, et) });
                    return inst;
                }
            }
            throw new ArgumentException("cannot make a " + TypeName(u) + " from " + v.ToString());
        }

        static Point3d P3(JsonElement v)
        {
            if (v.ValueKind == JsonValueKind.Object)
                return new Point3d(Num(v, "x", 0), Num(v, "y", 0), Num(v, "z", 0));
            return P(v);
        }

        // "X" finds AxisGridKind_X; a number is the raw value.
        static object EnumValue(JsonElement v, Type t)
        {
            if (v.ValueKind == JsonValueKind.Number)
                return Enum.ToObject(t, v.GetInt64());
            string s = v.GetString() ?? "";
            foreach (var n in Enum.GetNames(t))
                if (string.Equals(n, s, StringComparison.OrdinalIgnoreCase))
                    return Enum.Parse(t, n);
            var tail = Enum.GetNames(t).Where(n => n.EndsWith("_" + s, StringComparison.OrdinalIgnoreCase)).ToList();
            if (tail.Count == 1)
                return Enum.Parse(t, tail[0]);
            throw new ArgumentException("'" + s + "' is not a " + t.Name + " (" + string.Join(", ", Enum.GetNames(t)) + ")");
        }

        // .NET value -> JSON, two levels deep for objects.
        static object Out(object? v, int depth)
        {
            switch (v)
            {
                case null: return "";
                case string or bool or Enum or Point3d or McObjectId: return ToJson(v);
                case int or long or short or byte or uint or ulong or ushort or double or float or decimal: return ToJson(v);
                case Vector3d vec: return new[] { vec.X, vec.Y, vec.Z };
                case Point2d p2: return new[] { p2.X, p2.Y };
                case Guid g: return g.ToString();
                case McObject mo when depth > 0 || mo.ID.Handle != 0:
                    return new Dictionary<string, object> { ["handle"] = mo.ID.IsNull ? "" : Hex(mo.ID), ["type"] = mo.GetType().Name };
            }
            if (v is IEnumerable en)
            {
                var list = new List<object>();
                foreach (var x in en)
                {
                    if (list.Count >= 200) break;
                    list.Add(Out(x, depth + 1));
                }
                return list;
            }
            if (depth >= 1)
                return v.GetType().Name;
            var d = new Dictionary<string, object> { ["$type"] = v.GetType().Name };
            foreach (var p in v.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                try { d[p.Name] = Out(p.GetValue(v), depth + 1); } catch (Exception ex) { d[p.Name] = "!" + ex.GetType().Name; }
            }
            return d;
        }

        // -- building axes ----------------------------------------------------------

        // One axis line from start to end with its marker text(s).
        static object AxisCreate(JsonElement a)
        {
            var ent = NewAxis(P(a.GetProperty("start")), P(a.GetProperty("end")), MarkerTexts(a));
            var d = Finish(ent, a);
            d["markers"] = Out(((McBasicAxis)ent.Axis).Methods.Markers.ValueMarkers, 0);
            return d;
        }

        static List<string> MarkerTexts(JsonElement a)
        {
            var list = new List<string>();
            if (a.TryGetProperty("markers", out var m) && m.ValueKind == JsonValueKind.Array)
                foreach (var x in m.EnumerateArray()) list.Add(x.GetString() ?? "");
            else if (a.TryGetProperty("name", out var n) && (n.GetString() ?? "").Length > 0)
                list.Add(n.GetString()!);
            return list;
        }

        static McAxisEntity NewAxis(Point3d start, Point3d end, List<string> markers)
        {
            var ent = new McAxisEntity();
            McBasicAxis axis = ent.Axis;
            axis.TypeAxis = Axis_type.Axis_type_linear;
            McBasicAxis.McAxisSpecificLinear lin = axis;
            lin.setPoints(start, end);
            if (!ent.DbEntity.AddToCurrentDocument())
                throw new InvalidOperationException("nanoCAD did not add the axis");
            if (markers.Count > 0)
            {
                // re-read after it is in the drawing: the wrapper of a fresh
                // object may not reach the stored one
                McBasicAxis.McAxisSpecificLinear lin2 = ent.Axis;
                lin2.setPoints(start, end);
                lin2.Markers.ValueMarkers = markers;
            }
            ent.DbEntity.Update();
            return ent;
        }

        // A rectangular grid: x axes (vertical lines) at base.x + cumulative
        // x_spacings, y axes (horizontal) at base.y + cumulative y_spacings,
        // each overshooting the grid by `overhang`. Names default to 1, 2, 3...
        // across and А, Б, В... up, as nanoCAD numbers them.
        static object AxisGridCreate(JsonElement a)
        {
            var b = P(a.GetProperty("base"));
            var xs = Cumulative(b.X, a, "x_spacings");
            var ys = Cumulative(b.Y, a, "y_spacings");
            if (xs.Count < 1 || ys.Count < 1)
                throw new ArgumentException("x_spacings and y_spacings make at least one axis each");
            double over = Num(a, "overhang", 1500);
            double y0 = ys.First() - over, y1 = ys.Last() + over;
            double x0 = xs.First() - over, x1 = xs.Last() + over;
            var xNames = Names(a, "x_names", xs.Count, false);
            var yNames = Names(a, "y_names", ys.Count, true);

            var grid = new McSpdsGridRect();
            if (!grid.DbEntity.AddToCurrentDocument())
                throw new InvalidOperationException("nanoCAD did not add the axis grid");
            var log = new List<object>();
            var pure = grid.getAxisMethods();
            for (int i = 0; i < xs.Count; i++)
            {
                var ax = NewAxis(new Point3d(xs[i], y0, 0), new Point3d(xs[i], y1, 0), new List<string> { xNames[i] });
                AxisGridKind kind;
                bool ok = pure.Add(ax, true, out kind);
                log.Add(new Dictionary<string, object> { ["axis"] = xNames[i], ["added"] = ok, ["kind"] = kind.ToString() });
            }
            for (int i = 0; i < ys.Count; i++)
            {
                var ax = NewAxis(new Point3d(x0, ys[i], 0), new Point3d(x1, ys[i], 0), new List<string> { yNames[i] });
                AxisGridKind kind;
                bool ok = pure.Add(ax, true, out kind);
                log.Add(new Dictionary<string, object> { ["axis"] = yNames[i], ["added"] = ok, ["kind"] = kind.ToString() });
            }
            grid.DbEntity.Update();
            // texts by kind, after the grid has sorted its axes
            foreach (var kind in new[] { AxisGridKind.AxisGridKind_X, AxisGridKind.AxisGridKind_Y })
            {
                var m = grid.getAxisMethods(kind);
                if (a.TryGetProperty("dim_chain", out var dc)) m.DimChain = dc.GetBoolean();
                if (a.TryGetProperty("dim_total", out var dt)) m.DimTotal = dt.GetBoolean();
                if (a.TryGetProperty("markers_position", out var mp) && mp.ValueKind == JsonValueKind.String)
                    m.AbsoluteMarkersPosition = (Absolute_markers_position)EnumValue(mp, typeof(Absolute_markers_position));
            }
            var d = Finish(grid, a);
            d["axes"] = log;
            d["grid"] = GridSummary(grid);
            return d;
        }

        static object GridSummary(McEntitySpdsGrid grid)
        {
            var d = new Dictionary<string, object>();
            foreach (var kind in new[] { AxisGridKind.AxisGridKind_X, AxisGridKind.AxisGridKind_Y })
            {
                try
                {
                    var m = grid.getAxisMethods(kind);
                    var names = new List<object>();
                    var dist = new List<object>();
                    for (uint i = 0; i < m.Count; i++)
                    {
                        names.Add(m.getText(i));
                        if (i > 0) dist.Add(m.getDistance(i));
                    }
                    d[kind.ToString()] = new Dictionary<string, object> { ["count"] = (long)m.Count, ["names"] = names, ["distances"] = dist };
                }
                catch (Exception ex) { d[kind.ToString()] = "!" + ex.Message; }
            }
            return d;
        }

        static List<double> Cumulative(double start, JsonElement a, string name)
        {
            var list = new List<double> { start };
            if (a.TryGetProperty(name, out var arr))
                foreach (var v in arr.EnumerateArray())
                    list.Add(list[list.Count - 1] + v.GetDouble());
            return list;
        }

        // ГОСТ 21.101: letters without Ё З Й О Х Ц Ч Щ Ъ Ы Ь
        const string AxisLetters = "АБВГДЕЖИКЛМНПРСТУФШЭЮЯ";

        static List<string> Names(JsonElement a, string name, int count, bool letters)
        {
            var list = new List<string>();
            if (a.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var v in arr.EnumerateArray()) list.Add(v.GetString() ?? "");
            for (int i = list.Count; i < count; i++)
                list.Add(letters
                    ? (i < AxisLetters.Length ? AxisLetters[i].ToString() : AxisLetters[i % AxisLetters.Length] + (i / AxisLetters.Length).ToString(CultureInfo.InvariantCulture))
                    : (i + 1).ToString(CultureInfo.InvariantCulture));
            return list;
        }
    }
}
