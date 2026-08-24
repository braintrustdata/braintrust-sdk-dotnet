// Braintrust OpenAPI spec normalizer.
//
// Runs as an MSBuild inline task (RoslynCodeTaskFactory, Type="Class") before
// NSwag. Every transform here is driven by schema *shape*, never by schema name,
// so a new union in the spec is handled without touching this file.
//
// The rules, in order:
//   1. collapse allOf:[$ref, anyOf] back to the plain $ref
//   2. drop inline titles that would clobber a component POJO
//   3. drop nullish union arms; a union of one real arm becomes that arm
//   4. a union of string enums becomes one enum
//   5. T | array<T> in a parameter becomes a repeatable param
//   6. objects sharing a required single-value string enum property become
//      base + allOf subclasses + an OpenAPI discriminator
//  12. arms with pairwise-distinct JSON kinds become a wrapper with one accessor
//      per kind (AsString / AsObject / AsArray), dispatched by JsonKindUnionConverter
//   9. any union none of the above can type becomes free-form (C# object),
//      inlined at each use site
//   7. drop components that are now bare $ref aliases
//  10. name inline request/response bodies from their operationId
//  11. name inline property schemas whose generated name would be numbered
//   8. make schemas colliding with the extension-data member free-form
//
// Rules are numbered in the order they were added; the list above is execution
// order. Every one of them matches on shape, so nothing here needs editing when
// the spec grows a new union or a new endpoint.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Braintrust.Build
{
    public sealed class SpecNormalizer : Microsoft.Build.Utilities.Task
    {
        public string InputPath { get; set; }
        public string OutputPath { get; set; }
        public string UnionShellsPath { get; set; }
        public string Namespace { get; set; }

        public override bool Execute()
        {
            foreach (var line in SpecNormalization.Run(InputPath, OutputPath, UnionShellsPath, Namespace))
            {
                // Indented lines are per-node detail; keep them off a normal build.
                var importance = line.StartsWith("    ", StringComparison.Ordinal)
                    ? Microsoft.Build.Framework.MessageImportance.Normal
                    : Microsoft.Build.Framework.MessageImportance.High;
                Log.LogMessage(importance, "  " + line);
            }
            return true;
        }
    }

    public static class SpecNormalization
    {
        // ── yaml node helpers ────────────────────────────────────────────────
        static IDictionary<object, object> Map(object o) => o as IDictionary<object, object>;
        static IList<object> Lst(object o) => o as IList<object>;
        static string Str(object o) => o as string;
        static object Get(object m, string k)
        {
            var d = Map(m);
            return d != null && d.TryGetValue(k, out var v) ? v : null;
        }
        static IDictionary<object, object> NewMap() => new Dictionary<object, object>();
        static string RefName(string r) => r?.Substring(r.LastIndexOf('/') + 1);
        static object MakeRef(string name)
        {
            var m = NewMap();
            m["$ref"] = "#/components/schemas/" + name;
            return m;
        }

        static IDictionary<object, object> _schemas;

        /// <summary>Follow a chain of $refs to the underlying schema node.</summary>
        static object Resolve(object node)
        {
            for (var i = 0; i < 8; i++)
            {
                var r = Str(Get(node, "$ref"));
                if (r == null) return node;
                node = Get(_schemas, RefName(r));
            }
            return node;
        }

        /// <summary>Resolve, then flatten allOf into a single view of type/properties/
        /// required. Rule 6 rewrites variants into allOf wrappers, so shape tests have
        /// to see through that or a union reached twice looks different the second time.
        /// </summary>
        static IDictionary<object, object> Effective(object node)
        {
            var acc = NewMap();
            var props = NewMap();
            var required = new List<object>();

            void Merge(object n, int depth)
            {
                if (depth > 8) return;
                var m = Map(Resolve(n));
                if (m == null) return;
                foreach (var kv in m)
                {
                    var k = Str(kv.Key);
                    if (k == "allOf" || k == "properties" || k == "required") continue;
                    if (!acc.ContainsKey(kv.Key)) acc[kv.Key] = kv.Value;
                }
                var p = Map(Get(m, "properties"));
                if (p != null) foreach (var kv in p) props[kv.Key] = kv.Value;
                var r = Lst(Get(m, "required"));
                if (r != null) foreach (var v in r) if (!required.Contains(v)) required.Add(v);
                var all = Lst(Get(m, "allOf"));
                if (all != null) foreach (var part in all) Merge(part, depth + 1);
            }
            Merge(node, 0);

            if (props.Count > 0) acc["properties"] = props;
            if (required.Count > 0) acc["required"] = required;
            return acc;
        }

        /// <summary>A union arm carrying no type information (the zod-to-OpenAPI
        /// "nullable" artifact): {}, {nullable:true} or {type:null}.</summary>
        static bool IsNullish(object b)
        {
            var m = Map(b);
            if (m == null) return false;
            if (Str(Get(m, "type")) == "null") return true;
            return Get(m, "type") == null && !m.ContainsKey("properties") && !m.ContainsKey("$ref")
                && !m.ContainsKey("anyOf") && !m.ContainsKey("oneOf")
                && !m.ContainsKey("items") && !m.ContainsKey("enum") && !m.ContainsKey("allOf");
        }

        static bool IsObjectish(object b)
        {
            var e = Effective(b);
            return Str(Get(e, "type")) == "object" || Get(e, "properties") != null;
        }

        /// <summary>Canonical string for a node, used for structural equality.</summary>
        static string Canon(object node)
        {
            var sb = new StringBuilder();
            void W(object n)
            {
                var m = Map(n);
                if (m != null)
                {
                    sb.Append('{');
                    foreach (var k in m.Keys.Select(Str).Where(x => x != null).OrderBy(x => x, StringComparer.Ordinal))
                    { sb.Append(k).Append(':'); W(m[k]); sb.Append(','); }
                    sb.Append('}');
                    return;
                }
                var l = Lst(n);
                if (l != null) { sb.Append('['); foreach (var v in l) { W(v); sb.Append(','); } sb.Append(']'); return; }
                sb.Append(n == null ? "~" : Convert.ToString(n, System.Globalization.CultureInfo.InvariantCulture));
            }
            W(node);
            return sb.ToString();
        }

        // Keys that describe a schema rather than constrain it. Two arms that differ
        // only in these are the same type, so they are dropped before comparing.
        static readonly HashSet<string> AnnotationKeys = new HashSet<string>
        {
            "title", "description", "example", "examples", "default",
            "deprecated", "readOnly", "writeOnly", "externalDocs",
        };

        /// <summary>Canonical string for a node's *type*: resolved, allOf-flattened,
        /// with annotations removed.</summary>
        static string CanonShape(object node) => Canon(StripAnnotations(Effective(node), 0));

        static object StripAnnotations(object node, int depth)
        {
            if (depth > 12) return node;
            var m = Map(node);
            if (m != null)
            {
                var copy = NewMap();
                foreach (var kv in m)
                {
                    var k = Str(kv.Key);
                    if (k != null && AnnotationKeys.Contains(k)) continue;
                    copy[kv.Key] = StripAnnotations(kv.Value, depth + 1);
                }
                return copy;
            }
            var l = Lst(node);
            if (l != null) return l.Select(v => StripAnnotations(v, depth + 1)).ToList();
            return node;
        }

        static string Pascal(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder();
            foreach (var p in Regex.Split(s, @"[^A-Za-z0-9]+"))
                if (p.Length > 0) sb.Append(char.ToUpperInvariant(p[0])).Append(p.Substring(1));
            return sb.ToString();
        }

        // ── entry point ──────────────────────────────────────────────────────
        // The rules thread the schema table and the shell list through static state, so
        // two concurrent runs in one MSBuild node would interleave. Only this project uses
        // the task today, but serializing costs nothing and removes the hazard.
        static readonly object Gate = new object();

        public static List<string> Run(string inputPath, string outputPath,
                                      string unionShellsPath = null, string nameSpace = null)
        {
            lock (Gate) return RunCore(inputPath, outputPath, unionShellsPath, nameSpace);
        }

        static List<string> RunCore(string inputPath, string outputPath,
                                    string unionShellsPath, string nameSpace)
        {
            var report = new List<string>();
            UnionShells.Clear();

            // Pass 0 (string level): the /v1/proxy/{path+} greedy param's '+' is not
            // a legal C# identifier. Only such path in the spec; no-op once gone.
            var text = System.IO.File.ReadAllText(inputPath)
                .Replace("/v1/proxy/{path+}", "/v1/proxy/{path}")
                .Replace("name: path+", "name: path")
                .Replace("operationId: proxy{path+}", "operationId: proxyFallback")
                .Replace("operationId: optionsProxyproxy{path+}", "operationId: optionsProxyFallback");

            var root = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<object>(text);
            var rootMap = Map(root);
            _schemas = Map(Get(Get(rootMap, "components"), "schemas"));

            report.Add(CollapseAllOfOverRef(root));
            report.Add(StripAmbiguousTitles(root));
            report.AddRange(ApplyUnionRules(rootMap));
            report.Add(CollapseDegenerateAllOf(root));
            report.Add(DropRefAliases(rootMap));
            report.Add(NameInlineBodies(rootMap));
            report.Add(NameCollidingInlineSchemas(rootMap));
            report.Add(CloseExtensionDataCollisions(root));

            var outText = new YamlDotNet.Serialization.SerializerBuilder()
                .WithMaximumRecursion(200).Build().Serialize(root);
            System.IO.File.WriteAllText(outputPath, outText);

            if (unionShellsPath != null)
                report.Add(WriteUnionShellCompanion(unionShellsPath, nameSpace));

            return report;
        }

        // ── Rule 1: collapse allOf:[$ref, anyOf/oneOf] -> $ref ───────────────
        // The spec expresses "a SavedFunctionId, narrowed" as
        // allOf:[$ref SavedFunctionId, anyOf:[...]]. The ref already declares those
        // branches, so generators flatten both sides into one broken POJO. Collapsing
        // back to the plain $ref restores the union. Fires only when the ref covers
        // every inline branch and adds nothing but a nullish arm.
        static string CollapseAllOfOverRef(object root)
        {
            var n = 0;
            if (_schemas == null) return "collapsed 0 allOf-over-$ref node(s)";

            string Title(object node) => Str(Get(node, "title"));

            void Visit(object node)
            {
                var m = Map(node);
                if (m == null)
                {
                    var l = Lst(node);
                    if (l != null) foreach (var v in l) Visit(v);
                    return;
                }

                var allOf = Lst(Get(m, "allOf"));
                if (allOf != null && allOf.Count == 2)
                {
                    IDictionary<object, object> refPart = null, compPart = null;
                    foreach (var el in allOf)
                    {
                        var em = Map(el);
                        if (em == null) continue;
                        if (em.ContainsKey("$ref")) refPart = em;
                        if (em.ContainsKey("anyOf") || em.ContainsKey("oneOf")) compPart = em;
                    }
                    if (refPart != null && compPart != null)
                    {
                        var refStr = Str(Get(refPart, "$ref"));
                        var target = Get(_schemas, RefName(refStr));
                        var declared = Lst(Get(target, "anyOf")) ?? Lst(Get(target, "oneOf"));
                        var inlineList = Lst(Get(compPart, "anyOf")) ?? Lst(Get(compPart, "oneOf"));

                        if (declared != null && inlineList != null)
                        {
                            var inlineTitles = new HashSet<string>(inlineList.Select(Title).Where(t => t != null));
                            var declaredTitles = new HashSet<string>(declared.Select(Title).Where(t => t != null));
                            var extrasAllNullish = declared
                                .Where(b => Title(b) == null || !inlineTitles.Contains(Title(b)))
                                .All(IsNullish);

                            if (inlineTitles.Count > 0 && declaredTitles.IsSupersetOf(inlineTitles) && extrasAllNullish)
                            {
                                m.Remove("allOf");
                                m["$ref"] = refStr;
                                n++;
                            }
                        }
                    }
                }
                foreach (var v in m.Values.ToList()) Visit(v);
            }
            Visit(root);
            return $"collapsed {n} allOf-over-$ref node(s)";
        }

        // ── Rule 2: drop inline titles that would clobber a component POJO ───
        // Titles become class names, so an inline schema titled 'function' overwrites
        // the real Function component. When the title backs more than one shape a
        // name mapping cannot fix it; dropping it lets the schema be named from its
        // (unique) path instead. Runs after Rule 1, which reads branch titles.
        static string StripAmbiguousTitles(object root)
        {
            var n = 0;
            if (_schemas == null) return "dropped 0 ambiguous title(s)";

            bool IsTopLevel(string path) => Regex.IsMatch(path, @"^components\.schemas\.[^.]+$");
            var shapesByTitle = new Dictionary<string, HashSet<string>>();

            void Collect(object node, string path)
            {
                var m = Map(node);
                if (m == null)
                {
                    var l = Lst(node);
                    if (l != null) for (var i = 0; i < l.Count; i++) Collect(l[i], path + "[" + i + "]");
                    return;
                }
                var title = Str(Get(m, "title"));
                if (!string.IsNullOrEmpty(title) && !IsTopLevel(path))
                {
                    var props = Map(Get(m, "properties"));
                    var shape = props != null
                        ? string.Join(",", props.Keys.Select(Str).OrderBy(x => x, StringComparer.Ordinal))
                        : "type:" + Str(Get(m, "type"));
                    if (!shapesByTitle.TryGetValue(title, out var set)) shapesByTitle[title] = set = new HashSet<string>();
                    set.Add(shape);
                }
                foreach (var kv in m) Collect(kv.Value, path.Length > 0 ? path + "." + Str(kv.Key) : Str(kv.Key));
            }
            Collect(root, "");

            var pojoNames = new HashSet<string>(
                _schemas.Where(kv => Get(kv.Value, "properties") != null).Select(kv => Str(kv.Key)));

            void Strip(object node, string path)
            {
                var m = Map(node);
                if (m == null)
                {
                    var l = Lst(node);
                    if (l != null) for (var i = 0; i < l.Count; i++) Strip(l[i], path + "[" + i + "]");
                    return;
                }
                var title = Str(Get(m, "title"));
                if (!string.IsNullOrEmpty(title) && !IsTopLevel(path)
                    && shapesByTitle.TryGetValue(title, out var set) && set.Count > 1
                    && pojoNames.Contains(Pascal(title)))
                { m.Remove("title"); n++; }

                foreach (var kv in m.ToList()) Strip(kv.Value, path.Length > 0 ? path + "." + Str(kv.Key) : Str(kv.Key));
            }
            Strip(root, "");
            return $"dropped {n} ambiguous title(s)";
        }

        // ── Rules 3-6: unions ────────────────────────────────────────────────
        sealed class UnionSite
        {
            public IDictionary<object, object> Node;
            public string Path;
            public List<object> Arms;
            public string Discriminator;
            public string ShapeKey;
            public string DesiredName;
            public bool IsTopLevelComponent;
        }

        static List<string> ApplyUnionRules(IDictionary<object, object> rootMap)
        {
            var report = new List<string>();
            if (_schemas == null) return report;

            var paramSchemas = ParameterPositionSchemas(rootMap);
            int nullish = 0, singles = 0, enums = 0, repeatables = 0;
            var residueShapes = new SortedDictionary<string, int>();
            var residuePaths = new List<string>();
            var freeFormComponents = new HashSet<string>();
            var kindUnions = new List<UnionSite>();
            var sites = new List<UnionSite>();
            var pending = new List<(IDictionary<object, object> node, string path, bool inParam)>();

            // Collect first: Rule 6 mutates components.schemas as it runs.
            void Collect(object node, string path, bool inParam)
            {
                var m = Map(node);
                if (m == null)
                {
                    var l = Lst(node);
                    if (l != null) for (var i = 0; i < l.Count; i++) Collect(l[i], path + "[" + i + "]", inParam);
                    return;
                }
                if (m.ContainsKey("anyOf") || m.ContainsKey("oneOf")) pending.Add((m, path, inParam));
                foreach (var kv in m.ToList())
                {
                    var key = Str(kv.Key);
                    Collect(kv.Value, path.Length > 0 ? path + "." + key : key,
                            inParam || key == "parameters");
                }
            }
            foreach (var kv in _schemas.ToList())
            {
                var name = Str(kv.Key);
                Collect(kv.Value, "components.schemas." + name, paramSchemas.Contains(name));
            }
            Collect(Get(rootMap, "paths"), "paths", false);
            Collect(Get(Get(rootMap, "components"), "parameters"), "components.parameters", true);

            // ── Phase 1: local rules; queue Rule 6 candidates ────────────────
            foreach (var (node, path, inParam) in pending)
            {
                var key = node.ContainsKey("anyOf") ? "anyOf" : "oneOf";
                var branches = Lst(Get(node, key));
                if (branches == null) continue;

                // Rule 3 - nullish arms carry no type information.
                var real = branches.Where(b => !IsNullish(b)).ToList();
                if (real.Count != branches.Count) nullish++;

                if (real.Count == 0) { node.Remove(key); continue; }
                if (real.Count == 1)
                {
                    node.Remove(key);
                    foreach (var kv in Map(real[0]) ?? NewMap()) node[kv.Key] = kv.Value;
                    singles++;
                    continue;
                }

                // Rule 4 - a union of string enums is one enum.
                if (real.All(b => Str(Get(b, "type")) == "string" && Lst(Get(b, "enum")) != null))
                {
                    var merged = new List<object>();
                    foreach (var b in real)
                        foreach (var v in Lst(Get(b, "enum")))
                            if (!merged.Contains(v)) merged.Add(v);
                    node.Remove(key);
                    node["type"] = "string";
                    node["enum"] = merged;
                    enums++;
                    continue;
                }

                // Rule 5 - T | array<T> in a parameter is a repeatable param.
                if (real.Count == 2 && inParam)
                {
                    var arr = real.FirstOrDefault(b => Str(Get(Effective(b), "type")) == "array");
                    var sca = real.FirstOrDefault(b => Str(Get(Effective(b), "type")) != "array");
                    if (arr != null && sca != null
                        && CanonShape(Get(Effective(arr), "items")) == CanonShape(sca))
                    {
                        node.Remove(key);
                        foreach (var kv in Map(Effective(arr))) node[kv.Key] = kv.Value;
                        // Keep the scalar arm's (simpler) form as the element type;
                        // the array arm often wraps it in allOf:[$ref, {title}].
                        node["items"] = sca;
                        repeatables++;
                        continue;
                    }
                }

                // Rule 6 - discriminated object union. Queue; naming needs the
                // whole set so structurally identical unions can share a base.
                var disc = FindDiscriminator(real);
                if (disc != null)
                {
                    var desired = DeriveBaseName(path);
                    sites.Add(new UnionSite
                    {
                        Node = node, Path = path, Arms = real, Discriminator = disc,
                        ShapeKey = Canon(real), DesiredName = desired,
                        IsTopLevelComponent = path == "components.schemas." + desired,
                    });
                    continue;
                }

                // Rule 12 - arms with pairwise-distinct JSON kinds (string | object,
                // string | array, array | object). The kind of the incoming token picks
                // the arm with no ambiguity, so this can be a typed wrapper. Queued;
                // naming and deduping need the whole set.
                // Several string-enum arms (tool_choice is "auto" | "none" | "required" |
                // {...}) are one string arm as far as C# is concerned, so fold them
                // together before testing whether the kinds are distinct.
                var coalesced = CoalesceStringEnums(real);
                var kinds = coalesced.Select(JsonKindOf).ToList();
                if (coalesced.Count >= 2 && kinds.All(k => k != null)
                    && kinds.Distinct().Count() == kinds.Count)
                {
                    kindUnions.Add(new UnionSite
                    {
                        Node = node, Path = path, Arms = coalesced,
                        ShapeKey = string.Join("|", coalesced.Select(CanonShape)),
                        DesiredName = DeriveBaseName(path),
                        IsTopLevelComponent = path == "components.schemas." + DeriveBaseName(path),
                    });
                    continue;
                }

                // Rule 9 - nothing above matched, so this union cannot be typed. Left
                // alone, NSwag emits an empty pass-through class that can hold neither
                // a scalar nor an array, so *every* form of the value fails to
                // deserialize. A free-form object round-trips all of them.
                var shapes = string.Join("|", real.Select(b =>
                    IsObjectish(b) ? "object" : (Str(Get(Effective(b), "type")) ?? "?")).Distinct().OrderBy(x => x));
                residueShapes.TryGetValue(shapes, out var c);
                residueShapes[shapes] = c + 1;
                residuePaths.Add($"[{shapes}] {path}");

                var componentName = path.StartsWith("components.schemas.", StringComparison.Ordinal)
                    ? path.Substring("components.schemas.".Length)
                    : null;
                if (componentName != null && !componentName.Contains('.'))
                    freeFormComponents.Add(componentName);   // inlined at each use site below
                else
                    MakeFreeForm(node);
            }

            // ── Phase 2: one base per distinct shape, deterministically named ──
            var groups = sites.GroupBy(s => s.ShapeKey).ToList();
            var taken = new HashSet<string>(_schemas.Keys.Select(Str));
            int hierarchies = 0, shared = 0;

            foreach (var group in groups)
            {
                var members = group.ToList();
                // Prefer a name a top-level component already carries, then the
                // shortest, then alphabetical - never source-file order, so
                // SavedFunctionId wins over NullableSavedFunctionId.
                var holder = members
                    .Where(m => m.IsTopLevelComponent)
                    .OrderBy(m => m.DesiredName.Length).ThenBy(m => m.DesiredName, StringComparer.Ordinal)
                    .FirstOrDefault();
                var desired = holder?.DesiredName ?? members
                    .OrderBy(m => m.DesiredName.Length).ThenBy(m => m.DesiredName, StringComparer.Ordinal)
                    .First().DesiredName;

                var baseName = holder != null ? desired : Allocate(desired, taken);
                var representative = holder ?? members[0];

                BuildHierarchy(baseName, representative.Arms, representative.Discriminator, taken);
                hierarchies++;

                foreach (var m in members)
                {
                    if (ReferenceEquals(m, holder))
                    {
                        // The component itself becomes the base.
                        m.Node.Clear();
                        foreach (var kv in Map(_schemas[baseName])) m.Node[kv.Key] = kv.Value;
                        _schemas[baseName] = m.Node;
                    }
                    else
                    {
                        m.Node.Clear();
                        m.Node["$ref"] = "#/components/schemas/" + baseName;
                        if (!ReferenceEquals(m, representative)) shared++;
                    }
                }
            }

            // ── Phase 3: typed wrappers for kind-distinct unions ──────────────
            int wrappers = 0, wrapperSites = 0;
            var kindGroups = kindUnions.GroupBy(s => s.ShapeKey).ToList();

            // Two unions at the same property of different parents want the same name
            // (five `content` properties, three distinct arm types). Qualify by the arm
            // type rather than letting them become Content2 and Content3.
            var desiredCounts = kindGroups
                .GroupBy(g => g.First().DesiredName)
                .ToDictionary(g => g.Key, g => g.Count());

            foreach (var group in kindGroups)
            {
                var members = group.ToList();
                var holder = members
                    .Where(m => m.IsTopLevelComponent)
                    .OrderBy(m => m.DesiredName.Length).ThenBy(m => m.DesiredName, StringComparer.Ordinal)
                    .FirstOrDefault();
                var desired = holder?.DesiredName ?? members
                    .OrderBy(m => m.DesiredName.Length).ThenBy(m => m.DesiredName, StringComparer.Ordinal)
                    .First().DesiredName;

                if (holder == null && desiredCounts.TryGetValue(desired, out var sharing) && sharing > 1
                    && ArmQualifier(members[0].Arms) is { } qualifier)
                    desired = qualifier + LeafName(members[0].Path);

                var shellName = holder != null ? desired : Allocate(desired, taken);
                var hasPlainStringArm = members[0].Arms.Any(a =>
                    JsonKindOf(a) == "String" && Lst(Get(Effective(a), "enum")) == null);
                BuildKindUnionShell(shellName, members[0].Arms, taken);
                UnionShells.Add((shellName, hasPlainStringArm));
                wrappers++;

                foreach (var m in members)
                {
                    if (ReferenceEquals(m, holder))
                    {
                        m.Node.Clear();
                        foreach (var kv in Map(_schemas[shellName])) m.Node[kv.Key] = kv.Value;
                        _schemas[shellName] = m.Node;
                    }
                    else
                    {
                        m.Node.Clear();
                        m.Node["$ref"] = "#/components/schemas/" + shellName;
                    }
                    wrapperSites++;
                }
            }

            var inlined = InlineFreeFormComponents(rootMap, freeFormComponents);

            report.Add($"unions: {hierarchies} discriminated hierarchies covering {sites.Count} site(s) "
                     + $"({shared} sharing a base), {repeatables} repeatable params, {enums} merged enums, "
                     + $"{singles} single-arm collapses, {nullish} nullish arms dropped");
            report.Add($"unions: {wrappers} kind-dispatch wrapper(s) covering {wrapperSites} site(s)");
            report.Add($"unions: {residuePaths.Count} residual, made free-form "
                     + $"({string.Join("  ", residueShapes.Select(kv => kv.Key + " x" + kv.Value))})"
                     + (inlined > 0 ? $"; {inlined} reference(s) to {freeFormComponents.Count} component(s) inlined" : ""));
            foreach (var r in residuePaths) report.Add("    residual " + r);
            return report;
        }

        // ── Rule 13: collapse allOf members that carry no type ───────────────
        // Rule 9 replaces a reference to an untypeable union with an inline free-form
        // schema. Where that reference sat inside an allOf, the result is
        // allOf:[{}, {description: ...}] - which the generator renders as an empty class
        // rather than as `object`, reintroducing exactly the pass-through bag Rule 9 set
        // out to avoid. Dropping the members that say nothing leaves a free-form node.
        static string CollapseDegenerateAllOf(object root)
        {
            var n = 0;

            void Visit(object node)
            {
                var m = Map(node);
                if (m == null)
                {
                    var l = Lst(node);
                    if (l != null) foreach (var v in l) Visit(v);
                    return;
                }

                var allOf = Lst(Get(m, "allOf"));
                if (allOf != null)
                {
                    var meaningful = allOf.Where(member => !IsNullish(member)).ToList();
                    if (meaningful.Count != allOf.Count)
                    {
                        if (meaningful.Count == 0)
                        {
                            m.Remove("allOf");   // free-form; description is kept
                            n++;
                        }
                        else
                        {
                            m["allOf"] = meaningful;
                            n++;
                        }
                    }
                }
                foreach (var v in m.Values.ToList()) Visit(v);
            }
            Visit(root);
            return $"collapsed {n} degenerate allOf node(s)";
        }

        /// <summary>The kind-dispatch wrapper shells, for the companion file.</summary>
        static readonly List<(string Name, bool HasPlainStringArm)> UnionShells =
            new List<(string, bool)>();

        /// <summary>The last meaningful path segment - the property the union sits on.</summary>
        static string LeafName(string path)
        {
            var segments = path.Split('.').Select(x => Regex.Replace(x, @"\[\d+\]$", ""))
                .Where(x => x.Length > 0 && x != "properties" && x != "items"
                            && x != "additionalProperties" && x != "allOf" && x != "anyOf" && x != "oneOf")
                .ToList();
            return segments.Count == 0 ? "" : Pascal(Singular(segments[segments.Count - 1]));
        }

        /// <summary>A name for what distinguishes this union from another with the same
        /// desired name: the type of its structured arm.</summary>
        static string ArmQualifier(List<object> arms)
        {
            foreach (var arm in arms)
            {
                switch (JsonKindOf(arm))
                {
                    case "Array":
                        var itemRef = Str(Get(Get(Effective(arm), "items"), "$ref"));
                        if (itemRef != null) return RefName(itemRef);
                        break;
                    case "Object":
                        var armRef = Str(Get(arm, "$ref"));
                        if (armRef != null) return RefName(armRef);
                        var title = Str(Get(arm, "title"));
                        if (IsUsableTypeName(title)) return Pascal(title);
                        break;
                }
            }
            return null;
        }

        /// <summary>Fold every string-enum arm into one arm carrying the union of their
        /// values. Distinct enum arms are distinct in the spec but indistinguishable in
        /// C#, where they are all just a string.</summary>
        static List<object> CoalesceStringEnums(List<object> arms)
        {
            bool IsStringEnum(object a) =>
                Str(Get(Effective(a), "type")) == "string" && Lst(Get(Effective(a), "enum")) != null;

            var enumArms = arms.Where(IsStringEnum).ToList();
            if (enumArms.Count < 2) return arms;

            var values = new List<object>();
            foreach (var a in enumArms)
                foreach (var v in Lst(Get(Effective(a), "enum")))
                    if (!values.Contains(v)) values.Add(v);

            var merged = NewMap();
            merged["type"] = "string";
            merged["enum"] = values;

            var result = new List<object>();
            var inserted = false;
            foreach (var a in arms)
            {
                if (IsStringEnum(a))
                {
                    if (inserted) continue;
                    result.Add(merged);
                    inserted = true;
                    continue;
                }
                result.Add(a);
            }
            return result;
        }

        /// <summary>The single JSON kind an arm can appear as, or null if it has no
        /// definite kind (so kind alone could not pick it).</summary>
        static string JsonKindOf(object arm)
        {
            var e = Effective(arm);
            if (Get(e, "properties") != null) return "Object";
            switch (Str(Get(e, "type")))
            {
                case "string": return "String";
                case "number": return "Number";
                case "integer": return "Number";
                case "boolean": return "Boolean";
                case "array": return "Array";
                case "object": return "Object";
                default: return null;
            }
        }

        /// <summary>Build a wrapper whose properties are the arms, one per JSON kind.
        /// The properties are declared in the spec so the *generator* decides their C#
        /// types - the normalizer never has to model C# type names. At runtime
        /// JsonKindUnionConverter reads the token kind and fills the matching one.</summary>
        static void BuildKindUnionShell(string shellName, List<object> arms, HashSet<string> taken)
        {
            var properties = NewMap();

            foreach (var arm in arms)
            {
                var kind = JsonKindOf(arm);
                var armMap = Map(arm);
                object schema;

                if (kind == "Object" && armMap != null && !armMap.ContainsKey("$ref"))
                {
                    // Hoist an inline object arm so its class is named from the spec's
                    // title rather than from the wrapper property.
                    var title = Str(Get(armMap, "title"));
                    var componentName = Allocate(
                        IsUsableTypeName(title) ? Pascal(title) : shellName + "Object", taken);
                    _schemas[componentName] = arm;
                    schema = MakeRef(componentName);
                }
                else if (armMap != null && armMap.ContainsKey("default"))
                {
                    // A default on an arm makes the generator emit a property initializer,
                    // so that arm would never read as null and would always win on write.
                    // The default describes the union, not this one arm.
                    var copy = NewMap();
                    foreach (var kv in armMap) if (Str(kv.Key) != "default") copy[kv.Key] = kv.Value;
                    schema = copy;
                }
                else
                {
                    schema = arm;
                }
                properties["as" + kind] = schema;
            }

            var shell = NewMap();
            shell["type"] = "object";
            shell["properties"] = properties;
            // Closed: the converter owns serialization, so an extension-data member
            // would only add a member that never sees a value.
            shell["additionalProperties"] = false;
            _schemas[shellName] = shell;
            taken.Add(shellName);
        }

        /// <summary>Emit the partial declarations that attach the converter to each
        /// wrapper. Only type names are written - no C# type mapping.</summary>
        public static string WriteUnionShellCompanion(string path, string nameSpace)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated />");
            sb.AppendLine("// Generated by build/SpecNormalizer.cs. Attaches JsonKindUnionConverter to");
            sb.AppendLine("// each union wrapper: attributes merge across partial declarations, so this");
            sb.AppendLine("// adds the converter to the class NSwag generates without touching its output.");
            sb.AppendLine("#nullable disable");
            sb.AppendLine();
            sb.AppendLine("namespace " + nameSpace);
            sb.AppendLine("{");
            foreach (var shell in UnionShells.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                sb.AppendLine($"    [System.Text.Json.Serialization.JsonConverter(typeof(JsonKindUnionConverter<{shell.Name}>))]");
                sb.AppendLine($"    public partial class {shell.Name}");
                sb.AppendLine("    {");
                if (shell.HasPlainStringArm)
                {
                    // The string arm is the shorthand form callers reach for most, so let
                    // them assign it directly: message.Content = "hello". Only emitted for
                    // a plain string arm, where the C# type is certain.
                    sb.AppendLine($"        public static implicit operator {shell.Name}(string value)");
                    sb.AppendLine($"            => value is null ? null : new {shell.Name} {{ AsString = value }};");
                }
                sb.AppendLine("    }");
                sb.AppendLine();
            }
            sb.AppendLine("}");
            System.IO.File.WriteAllText(path, sb.ToString());
            return $"wrote {UnionShells.Count} union wrapper declaration(s)";
        }

        /// <summary>Empty the node so the generator treats it as free-form (C# object),
        /// keeping only the description.</summary>
        static void MakeFreeForm(IDictionary<object, object> node)
        {
            var description = Get(node, "description");
            node.Clear();
            if (description != null) node["description"] = description;
        }

        /// <summary>A free-form *component* still gets an empty class of its own, and a
        /// property typed as that class can hold neither a scalar nor an array. Inline
        /// the free-form at every use site instead and drop the component.</summary>
        static int InlineFreeFormComponents(IDictionary<object, object> rootMap, HashSet<string> names)
        {
            if (names.Count == 0) return 0;
            var replaced = 0;

            void Visit(object node)
            {
                var m = Map(node);
                if (m == null)
                {
                    var l = Lst(node);
                    if (l != null) foreach (var v in l) Visit(v);
                    return;
                }
                var r = Str(Get(m, "$ref"));
                if (r != null && r.StartsWith("#/components/schemas/", StringComparison.Ordinal)
                    && names.Contains(RefName(r)))
                {
                    MakeFreeForm(m);
                    replaced++;
                    return;
                }
                foreach (var v in m.Values.ToList()) Visit(v);
            }
            Visit(rootMap);

            foreach (var name in names) _schemas.Remove(name);
            return replaced;
        }

        /// <summary>Component schemas reached from a parameter object.</summary>
        static HashSet<string> ParameterPositionSchemas(IDictionary<object, object> rootMap)
        {
            var found = new HashSet<string>();
            void Visit(object node)
            {
                var m = Map(node);
                if (m == null)
                {
                    var l = Lst(node);
                    if (l != null) foreach (var v in l) Visit(v);
                    return;
                }
                var inVal = Str(Get(m, "in"));
                if (inVal == "query" || inVal == "path" || inVal == "header")
                {
                    var r = Str(Get(Get(m, "schema"), "$ref"));
                    if (r != null) found.Add(RefName(r));
                }
                foreach (var v in m.Values.ToList()) Visit(v);
            }
            Visit(rootMap);
            return found;
        }

        /// <summary>The property name that discriminates these arms: required in
        /// every arm, a single-value string enum in every arm, distinct across arms.
        /// This is what finds 'type', 'event_type', 'role' and 'provider' without
        /// any of them being named here.</summary>
        static string FindDiscriminator(List<object> arms)
        {
            if (arms.Count < 2 || !arms.All(IsObjectish)) return null;

            var perArm = new List<Dictionary<string, string>>();
            foreach (var arm in arms)
            {
                var e = Effective(arm);
                var props = Map(Get(e, "properties"));
                var required = new HashSet<string>((Lst(Get(e, "required")) ?? new List<object>()).Select(Str));
                var found = new Dictionary<string, string>();
                if (props != null)
                {
                    foreach (var kv in props)
                    {
                        var name = Str(kv.Key);
                        if (name == null || !required.Contains(name)) continue;
                        var s = Effective(kv.Value);
                        var en = Lst(Get(s, "enum"));
                        if (en != null && en.Count == 1 && Str(en[0]) != null) found[name] = Str(en[0]);
                    }
                }
                perArm.Add(found);
            }

            var common = new HashSet<string>(perArm[0].Keys);
            foreach (var m in perArm.Skip(1)) common.IntersectWith(m.Keys);

            return common
                .Where(c => perArm.Select(m => m[c]).Distinct().Count() == arms.Count)
                .OrderBy(c => c, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        /// <summary>Create the base schema plus one allOf subclass per arm. This is
        /// the only spec shape NJsonSchema turns into a real C# class hierarchy.</summary>
        static void BuildHierarchy(string baseName, List<object> arms, string disc, HashSet<string> taken)
        {
            var mapping = NewMap();
            var baseSchema = NewMap();
            baseSchema["type"] = "object";
            _schemas[baseName] = baseSchema;
            taken.Add(baseName);

            foreach (var arm in arms)
            {
                var effective = Effective(arm);
                var discSchema = Get(Map(Get(effective, "properties")), disc);
                var value = Str((Lst(Get(Effective(discSchema), "enum")) ?? new List<object>()).FirstOrDefault());
                if (value == null) continue;

                var armRef = Str(Get(arm, "$ref"));
                string variantName;
                object body;

                if (armRef != null)
                {
                    // An existing component: give it the base as a parent, in place,
                    // so everything else already referencing it keeps working.
                    variantName = RefName(armRef);
                    body = _schemas[variantName];
                }
                else
                {
                    variantName = Allocate(Pascal(value) + baseName, taken);
                    body = arm;
                }

                var inner = NewMap();
                foreach (var kv in Map(body) ?? NewMap()) inner[kv.Key] = kv.Value;
                StripDiscriminator(inner, disc);

                var wrapper = NewMap();
                wrapper["allOf"] = new List<object> { MakeRef(baseName), inner };
                _schemas[variantName] = wrapper;
                mapping[value] = "#/components/schemas/" + variantName;
            }

            var discriminator = NewMap();
            discriminator["propertyName"] = disc;
            discriminator["mapping"] = mapping;
            baseSchema["discriminator"] = discriminator;
        }

        /// <summary>The discriminator lives on the base and is written by the
        /// converter; leaving it on the subclass duplicates the JSON key. Copies
        /// properties/required before editing so sibling arms are not disturbed.</summary>
        static void StripDiscriminator(IDictionary<object, object> schema, string disc)
        {
            var props = Map(Get(schema, "properties"));
            if (props != null && props.ContainsKey(disc))
            {
                var copy = NewMap();
                foreach (var kv in props) if (Str(kv.Key) != disc) copy[kv.Key] = kv.Value;
                schema["properties"] = copy;
            }
            var required = Lst(Get(schema, "required"));
            if (required != null)
            {
                var keep = required.Where(r => Str(r) != disc).ToList();
                if (keep.Count == 0) schema.Remove("required"); else schema["required"] = keep;
            }
        }

        /// <summary>Name a hoisted base from its location: the enclosing component
        /// plus the nearest property name. Container/composition segments carry no
        /// meaning, so they are skipped.</summary>
        static string DeriveBaseName(string path)
        {
            // Structural keywords carry no meaning in a name. `schema` and `content` are
            // structural only in a path/parameter position - under components.schemas
            // they are ordinary property names, so they must survive there.
            bool Structural(string s) =>
                s == "properties" || s == "items" || s == "additionalProperties"
                || s == "allOf" || s == "anyOf" || s == "oneOf" || s.Length == 0;
            bool Noise(string s) => Structural(s) || s == "schema" || s == "content";

            var segments = path.Split('.').Select(s => Regex.Replace(s, @"\[\d+\]$", "")).ToList();

            if (segments.Count >= 3 && segments[0] == "components" && segments[1] == "schemas")
            {
                var name = segments[2];
                for (var i = 3; i < segments.Count; i++)
                    if (!Structural(segments[i])) name += Pascal(Singular(segments[i]));
                return name;
            }
            var tail = segments.AsEnumerable().Reverse().Where(s => !Noise(s)).Take(2).Reverse();
            return Pascal(string.Join("-", tail));
        }

        // Endings where a trailing "s" is part of the word, not a plural: status,
        // analysis, address. Without these, `status` singularizes to `statu`.
        static readonly string[] NotPlural = { "ss", "us", "is", "os" };

        static string Singular(string s) =>
            s.Length > 3
            && s.EndsWith("s", StringComparison.Ordinal)
            && !NotPlural.Any(e => s.EndsWith(e, StringComparison.Ordinal))
                ? s.Substring(0, s.Length - 1) : s;

        // A title or property name that says nothing about the type, or that would
        // shadow a framework type if used as a class name. `title: object` on a union
        // arm produced a class called Object, which hides System.Object for anything
        // written in the generated namespace.
        static readonly HashSet<string> UninformativeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "object", "string", "array", "number", "integer", "boolean", "null", "value",
            "type", "task", "exception", "enum", "delegate", "guid", "uri", "decimal",
            "double", "single", "byte", "char", "int32", "int64", "attribute", "tuple",
        };

        static bool IsUsableTypeName(string candidate) =>
            !string.IsNullOrEmpty(candidate) && !UninformativeNames.Contains(candidate);

        static string Allocate(string desired, HashSet<string> taken)
        {
            if (taken.Add(desired)) return desired;
            for (var i = 2; ; i++)
                if (taken.Add(desired + i)) return desired + i;
        }

        // ── Rule 10: name inline request/response bodies from the operation ───
        // An inline body schema has no name of its own, so the generator falls back to
        // Response, Response2 ... Body2, which says nothing about what it holds. The
        // operationId does, and it is unique per operation.
        static string NameInlineBodies(IDictionary<object, object> rootMap)
        {
            var paths = Map(Get(rootMap, "paths"));
            if (paths == null) return "named 0 inline body schema(s)";

            var methods = new HashSet<string> { "get", "put", "post", "delete", "patch", "options", "head", "trace" };
            var taken = new HashSet<string>(_schemas.Keys.Select(Str));
            var n = 0;

            // Only an inline object is worth a name; a $ref already has one.
            bool Nameable(object schema) =>
                Map(schema) is { } m && !m.ContainsKey("$ref") && m.ContainsKey("properties");

            void Hoist(IDictionary<object, object> container, string desired)
            {
                var schema = Get(container, "schema");
                if (!Nameable(schema)) return;
                var name = Allocate(desired, taken);
                _schemas[name] = schema;
                container["schema"] = MakeRef(name);
                n++;
            }

            foreach (var pathItem in paths.Values.ToList())
            {
                var operations = Map(pathItem);
                if (operations == null) continue;

                foreach (var kv in operations.ToList())
                {
                    if (!methods.Contains(Str(kv.Key) ?? "")) continue;
                    var operation = Map(kv.Value);
                    var operationId = Str(Get(operation, "operationId"));
                    if (operationId == null) continue;
                    var prefix = Pascal(operationId);

                    foreach (var media in Map(Get(Get(operation, "requestBody"), "content"))?.Values.ToList()
                                          ?? new List<object>())
                        Hoist(Map(media), prefix + "Request");

                    foreach (var response in Map(Get(operation, "responses"))?.Values.ToList()
                                             ?? new List<object>())
                        foreach (var media in Map(Get(response, "content"))?.Values.ToList() ?? new List<object>())
                            Hoist(Map(media), prefix + "Response");
                }
            }
            return $"named {n} inline body schema(s) from their operationId";
        }

        // ── Rule 11: name inline property schemas that collide ───────────────
        // Several components declare an inline object under the same property name
        // (eight `position` schemas, six `metadata`, ...). The generator names the first
        // from the property and numbers the rest - Position2 through Position8. Hoisting
        // them to components fixes the names, and identical shapes collapse onto one
        // type instead of eight copies. Only colliding names are touched; a unique
        // inline schema already gets a sensible name from its property.
        static string NameCollidingInlineSchemas(IDictionary<object, object> rootMap)
        {
            var sites = new List<(IDictionary<object, object> Parent, object Key, string Name,
                                 string Owner, string Shape, bool NeedsName)>();
            var componentNames = new HashSet<string>(_schemas.Keys.Select(Str));

            // An inline object worth naming, in a value position. Members of
            // allOf/anyOf/oneOf lists are excluded - the union rules own those.
            // needsName: a position the generator cannot name from anything, so it falls
            // back to Anonymous, Anonymous2, ... Those must be hoisted even when the name
            // is unique and clashes with nothing.
            void Collect(IDictionary<object, object> parent, object key, string name, string owner,
                         bool needsName = false)
            {
                var schema = Map(Get(parent, Str(key)));
                if (schema == null || name == null) return;
                if (schema.ContainsKey("$ref") || !schema.ContainsKey("properties")) return;
                sites.Add((parent, key, name, owner, Canon(schema), needsName));
            }

            void Walk(IDictionary<object, object> m, string owner, string naturalName)
            {
                var properties = Map(Get(m, "properties"));
                if (properties != null)
                {
                    foreach (var kv in properties.ToList())
                    {
                        var name = Str(kv.Key);
                        Collect(properties, kv.Key, name, owner);
                        if (Map(kv.Value) is { } child) Walk(child, owner, name);
                    }
                }

                // A collection's element and a map's value inherit the enclosing
                // property's name; these are the positions the generator cannot name at
                // all, and they surface as Anonymous, Anonymous2, ...
                foreach (var position in new[] { "items", "additionalProperties" })
                {
                    if (Map(Get(m, position)) is not { } nested) continue;
                    var elementName = naturalName == null ? null : Singular(naturalName);
                    Collect(m, position, elementName, owner, needsName: true);
                    Walk(nested, owner, elementName);
                }

                foreach (var composition in new[] { "allOf", "anyOf", "oneOf" })
                    if (Lst(Get(m, composition)) is { } list)
                        foreach (var member in list)
                            if (Map(member) is { } m2) Walk(m2, owner, naturalName);
            }

            foreach (var kv in _schemas.ToList())
                if (Map(kv.Value) is { } body) Walk(body, Str(kv.Key), null);

            var taken = new HashSet<string>(componentNames);
            int named = 0, merged = 0;

            foreach (var group in sites.GroupBy(s => s.Name))
            {
                var shapes = group.GroupBy(s => s.Shape).ToList();

                // A name used once is already generated sensibly from its property -
                // unless it clashes with a component (which produces Function2) or sits in
                // a position the generator cannot name at all (which produces Anonymous).
                if (group.Count() == 1
                    && !componentNames.Contains(Pascal(group.Key))
                    && !group.Any(s => s.NeedsName)) continue;

                foreach (var shape in shapes)
                {
                    var members = shape.ToList();
                    // One shape under this name keeps the bare name; several get
                    // qualified by their owning component so each is predictable.
                    var bareNameIsFine = shapes.Count == 1
                        && !componentNames.Contains(Pascal(group.Key))
                        && IsUsableTypeName(group.Key);
                    var desired = bareNameIsFine
                        ? Pascal(group.Key)
                        : Pascal(members[0].Owner) + Pascal(group.Key);
                    var componentName = Allocate(desired, taken);
                    _schemas[componentName] = Get(members[0].Parent, Str(members[0].Key));

                    foreach (var site in members)
                    {
                        site.Parent[site.Key] = MakeRef(componentName);
                        merged++;
                    }
                    named++;
                }
            }
            return $"named {named} colliding inline schema(s), collapsing {merged} site(s)";
        }

        // ── Rule 8: free-form schemas that collide with extension data ───────
        // NJsonSchema gives every open object an `[JsonExtensionData] AdditionalProperties`
        // member, and there is no setting to rename it. A schema that also declares a
        // *property* named additionalProperties therefore emits that member twice and
        // will not compile. Such a schema is always a JSON-Schema meta-schema, for which
        // IDictionary<string, object> is the better C# shape than a generated POJO, so
        // replace it with a free-form object.
        static string CloseExtensionDataCollisions(object root)
        {
            var n = 0;
            bool Collides(string name) =>
                name != null && name.Replace("_", "").Equals("additionalproperties", StringComparison.OrdinalIgnoreCase);

            void Visit(object node)
            {
                var m = Map(node);
                if (m == null)
                {
                    var l = Lst(node);
                    if (l != null) foreach (var v in l) Visit(v);
                    return;
                }
                var props = Map(Get(m, "properties"));
                if (props != null && props.Keys.Select(Str).Any(Collides))
                {
                    var description = Get(m, "description");
                    m.Clear();
                    m["type"] = "object";
                    m["additionalProperties"] = NewMap();
                    if (description != null) m["description"] = description;
                    n++;
                    return;
                }
                foreach (var v in m.Values.ToList()) Visit(v);
            }
            Visit(root);
            return $"made {n} extension-data-colliding schema(s) free-form";
        }

        // ── Rule 7: drop bare $ref aliases ───────────────────────────────────
        // Sharing a base leaves components whose whole body is {$ref: Base}.
        // NSwag emits an empty class for those, so redirect every reference to the
        // target and delete the alias.
        static string DropRefAliases(IDictionary<object, object> rootMap)
        {
            var alias = new Dictionary<string, string>();
            foreach (var kv in _schemas)
            {
                var m = Map(kv.Value);
                if (m == null) continue;
                if (m.ContainsKey("$ref") && m.Keys.Select(Str).All(k => k == "$ref" || k == "description"))
                    alias[Str(kv.Key)] = RefName(Str(m["$ref"]));
            }
            if (alias.Count == 0) return "dropped 0 $ref alias component(s)";

            string Final(string name)
            {
                for (var i = 0; i < 8 && alias.TryGetValue(name, out var next); i++) name = next;
                return name;
            }

            void Redirect(object node)
            {
                var m = Map(node);
                if (m == null)
                {
                    var l = Lst(node);
                    if (l != null) foreach (var v in l) Redirect(v);
                    return;
                }
                var r = Str(Get(m, "$ref"));
                if (r != null && r.StartsWith("#/components/schemas/", StringComparison.Ordinal))
                {
                    var target = Final(RefName(r));
                    m["$ref"] = "#/components/schemas/" + target;
                }
                foreach (var v in m.Values.ToList()) Redirect(v);
            }

            foreach (var name in alias.Keys.ToList()) _schemas.Remove(name);
            Redirect(rootMap);
            return $"dropped {alias.Count} $ref alias component(s): {string.Join(", ", alias.Keys.OrderBy(x => x))}";
        }
    }
}
