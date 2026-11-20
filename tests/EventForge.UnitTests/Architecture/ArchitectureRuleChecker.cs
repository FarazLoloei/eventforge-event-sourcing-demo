using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EventForge.UnitTests.Architecture;

/// <summary>A project file addressed by repository-relative path (forward slashes).</summary>
public sealed record ProjectFile(string Path, string Content);

/// <summary>A C# source file addressed by repository-relative path (forward slashes).</summary>
public sealed record SourceFile(string Path, string Text);

public enum ModuleLayer
{
    Unowned,
    Api,
    Workflow,
    Domain,
    Application,
    Infrastructure,
    Simulation,
}

/// <summary>Source layer <paramref name="Layer"/> must not depend on any namespace in <paramref name="Forbidden"/>.</summary>
public sealed record NamespaceRule(ModuleLayer Layer, IReadOnlyList<string> Forbidden);

public sealed record ArchitectureViolation(string Rule, string File, int Line, string Source, string Target, string Detail)
{
    public override string ToString() =>
        $"[{Rule}] {File}{(Line > 0 ? ":" + Line : "")} {Source} -> {Target}: {Detail}";
}

public sealed record AnalysisResult(
    IReadOnlyList<ArchitectureViolation> Violations,
    IReadOnlyDictionary<ModuleLayer, int> FilesByLayer,
    IReadOnlyDictionary<ModuleLayer, int> TypesByLayer);

/// <summary>
/// A NuGet package whose assembly the checker may load as metadata. Anything not listed stays unresolved,
/// so unapproved packages still surface as <c>analysis-incomplete</c>.
/// </summary>
public sealed record ApprovedPackage(string AssemblyFile, string AssemblyName, int MajorVersion);

/// <summary>
/// The approved dependency rules. These are authored independently of the actual project files
/// and sources, which are what the checker compares against them.
/// </summary>
public static class ApprovedRules
{
    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> ProjectReferences { get; } =
        new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["EventForge.Api"] = ["EventForge.Workflow", "EventForge.Infrastructure", "EventForge.Simulation"],
            ["EventForge.Workflow"] = [],
            ["EventForge.Infrastructure"] = ["EventForge.Workflow"],
            ["EventForge.Simulation"] = ["EventForge.Workflow"],
        };

    /// <summary>Package assemblies (beyond the .NET framework) that source analysis may resolve. EVF-21: Npgsql.</summary>
    public static IReadOnlyList<ApprovedPackage> Packages { get; } =
    [
        new("Npgsql.dll", "Npgsql", 10),
    ];

    public static IReadOnlyList<NamespaceRule> Namespaces { get; } =
    [
        new(ModuleLayer.Domain,
        [
            "EventForge.Workflow.Application",
            "EventForge.Api",
            "EventForge.Infrastructure",
            "EventForge.Simulation",
            "Microsoft.AspNetCore",
            "System.Net.Http",
            "System.Data",
            "Npgsql",
            "Microsoft.EntityFrameworkCore",
            "Microsoft.Data.SqlClient",
            "System.Data.SqlClient",
            "Microsoft.Data.Sqlite",
            "MySqlConnector",
            "MySql.Data",
            "Oracle.ManagedDataAccess",
        ]),
        new(ModuleLayer.Application,
        [
            "EventForge.Api",
            "EventForge.Infrastructure",
            "EventForge.Simulation",
            "Npgsql",
        ]),
        new(ModuleLayer.Infrastructure,
        [
            "EventForge.Simulation",
            "EventForge.Api",
        ]),
        new(ModuleLayer.Simulation,
        [
            "EventForge.Infrastructure",
            "EventForge.Api",
            "Npgsql",
        ]),
    ];
}

/// <summary>
/// Static architecture checks over project files and C# sources. It performs no MSBuild evaluation
/// and no reflection; see the limitations documented in README.md.
/// </summary>
public static class ArchitectureRuleChecker
{
    private const string AnalysisIncomplete = "analysis-incomplete";
    private const string NamespaceOwnership = "namespace-ownership";
    private const string WorkflowLocation = "workflow-source-location";

    private static readonly string[] ModuleOwnedNamespaces =
        ["EventForge.Workflow", "EventForge.Infrastructure", "EventForge.Simulation"];

    // Mirrors the SDK implicit usings for ImplicitUsings=enable on Microsoft.NET.Sdk projects.
    private const string ImplicitUsingsSource =
        "global using global::System;\n" +
        "global using global::System.Collections.Generic;\n" +
        "global using global::System.IO;\n" +
        "global using global::System.Linq;\n" +
        "global using global::System.Net.Http;\n" +
        "global using global::System.Threading;\n" +
        "global using global::System.Threading.Tasks;\n";

    private static readonly Lazy<IReadOnlyList<MetadataReference>> FrameworkReferences = new(LoadFrameworkReferences);
    private static readonly Lazy<IReadOnlyList<MetadataReference>> PackageReferences =
        new(() => LoadPackageReferences(ApprovedRules.Packages, AppContext.BaseDirectory));

    // ---------------------------------------------------------------- repository access

    public static string FindRepositoryRoot(string? startDirectory = null)
    {
        var start = startDirectory ?? AppContext.BaseDirectory;
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EventForge.slnx")) &&
                File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Repository root not found: no ancestor of '{start}' contains both EventForge.slnx and global.json.");
    }

    public static IReadOnlyList<ProjectFile> LoadProjectFiles(string repositoryRoot)
    {
        var projects = new List<ProjectFile>();
        foreach (var top in new[] { "src", "tests" })
        {
            foreach (var file in EnumerateFiles(repositoryRoot, top, "*.csproj"))
            {
                projects.Add(new ProjectFile(RelativePath(repositoryRoot, file), File.ReadAllText(file)));
            }
        }

        return projects;
    }

    public static IReadOnlyList<SourceFile> LoadSourceFiles(string repositoryRoot)
    {
        var sources = new List<SourceFile>();
        foreach (var file in EnumerateFiles(repositoryRoot, "src", "*.cs"))
        {
            sources.Add(new SourceFile(RelativePath(repositoryRoot, file), File.ReadAllText(file)));
        }

        return sources;
    }

    private static IEnumerable<string> EnumerateFiles(string repositoryRoot, string folder, string pattern)
    {
        var directory = Path.Combine(repositoryRoot, folder);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Required directory '{folder}' not found under '{repositoryRoot}'.");
        }

        return Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(RelativePath(repositoryRoot, file)))
            .OrderBy(file => file, StringComparer.Ordinal);
    }

    private static string RelativePath(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');

    // ---------------------------------------------------------------- project references

    public static IReadOnlyList<ArchitectureViolation> CheckProjectReferences(
        IReadOnlyCollection<ProjectFile> projects,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> expected)
    {
        var violations = new List<ArchitectureViolation>();
        var byPath = new Dictionary<string, ProjectFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            byPath[NormalizePath(project.Path)] = project;
        }

        var pathByName = new Dictionary<string, string>(StringComparer.Ordinal);
        var graph = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var (normalizedPath, project) in byPath)
        {
            var name = ProjectName(normalizedPath);
            if (!pathByName.TryAdd(name, normalizedPath))
            {
                violations.Add(new("duplicate-project-name", normalizedPath, 0, name, pathByName[name],
                    "two project files share one name, so references cannot be attributed"));
                continue;
            }

            var references = graph[name] = new SortedSet<string>(StringComparer.Ordinal);

            XDocument document;
            try
            {
                document = XDocument.Parse(project.Content);
            }
            catch (XmlException exception)
            {
                violations.Add(new("malformed-project-file", normalizedPath, 0, name, "-", exception.Message));
                continue;
            }

            foreach (var import in document.Descendants().Where(e => e.Name.LocalName == "Import"))
            {
                violations.Add(Unsupported(normalizedPath, name,
                    $"<Import Project=\"{import.Attribute("Project")?.Value}\"> is not evaluated"));
            }

            foreach (var reference in document.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (include is null || include.Contains("$(") || include.Contains('*') ||
                    reference.AncestorsAndSelf().Any(e => e.Attribute("Condition") is not null))
                {
                    violations.Add(Unsupported(normalizedPath, name,
                        "a ProjectReference without a literal Include, or under a Condition, cannot be evaluated"));
                    continue;
                }

                var resolved = Resolve(normalizedPath, include);
                if (!byPath.TryGetValue(resolved, out var target))
                {
                    violations.Add(new("unresolved-project-reference", normalizedPath, 0, name, include,
                        $"referenced project '{resolved}' was not found"));
                    continue;
                }

                references.Add(ProjectName(target.Path));
            }
        }

        bool IsTest(string name) => pathByName[name].StartsWith("tests/", StringComparison.OrdinalIgnoreCase);

        foreach (var (source, targets) in graph)
        {
            foreach (var target in targets.Where(t => !IsTest(source) && IsTest(t)))
            {
                violations.Add(new("production-references-test", pathByName[source], 0, source, target,
                    "production projects must not reference test projects"));
            }
        }

        var production = graph.Keys.Where(name => !IsTest(name)).ToList();
        foreach (var name in expected.Keys.Where(name => !production.Contains(name)))
        {
            violations.Add(new("missing-project", "-", 0, name, "-", "an expected production project was not found"));
        }

        foreach (var name in production.Where(name => !expected.ContainsKey(name)))
        {
            violations.Add(new("unexpected-project", pathByName[name], 0, name, "-",
                "production project is not covered by the approved reference matrix"));
        }

        foreach (var (name, allowed) in expected)
        {
            if (!graph.TryGetValue(name, out var actual))
            {
                continue;
            }

            foreach (var target in actual.Except(allowed, StringComparer.Ordinal).Where(t => !IsTest(t)))
            {
                violations.Add(new("unexpected-reference", pathByName[name], 0, name, target,
                    "reference is not in the approved matrix"));
            }

            foreach (var target in allowed.Except(actual, StringComparer.Ordinal))
            {
                violations.Add(new("missing-reference", pathByName[name], 0, name, target,
                    "approved reference is absent"));
            }
        }

        AddCycles(graph, pathByName, violations);
        return violations;
    }

    private static ArchitectureViolation Unsupported(string path, string name, string detail) =>
        new("unsupported-msbuild-construct", path, 0, name, "-", detail);

    private static void AddCycles(
        Dictionary<string, SortedSet<string>> graph,
        Dictionary<string, string> pathByName,
        List<ArchitectureViolation> violations)
    {
        var visiting = new List<string>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string node)
        {
            if (done.Contains(node))
            {
                return;
            }

            var index = visiting.IndexOf(node);
            if (index >= 0)
            {
                var cycle = visiting.Skip(index).Append(node).ToList();
                var key = string.Join(",", cycle.Take(cycle.Count - 1).Order(StringComparer.Ordinal));
                if (seen.Add(key))
                {
                    violations.Add(new("circular-reference", pathByName[node], 0, node,
                        string.Join(" -> ", cycle), "projects form a reference cycle"));
                }

                return;
            }

            visiting.Add(node);
            foreach (var next in graph[node].Where(graph.ContainsKey))
            {
                Visit(next);
            }

            visiting.RemoveAt(visiting.Count - 1);
            done.Add(node);
        }

        foreach (var node in graph.Keys.Order(StringComparer.Ordinal))
        {
            Visit(node);
        }
    }

    private static string ProjectName(string path) => Path.GetFileNameWithoutExtension(path);

    private static string Resolve(string projectPath, string include)
    {
        var normalized = projectPath.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        var directory = slash < 0 ? "" : normalized[..slash];
        return NormalizePath(directory + "/" + include);
    }

    private static string NormalizePath(string path)
    {
        var parts = new List<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == ".." && parts.Count > 0 && parts[^1] != "..")
            {
                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(segment);
            }
        }

        return string.Join('/', parts);
    }

    // ---------------------------------------------------------------- namespace dependencies

    public static AnalysisResult CheckNamespaceDependencies(
        IReadOnlyCollection<SourceFile> sources,
        IReadOnlyCollection<SourceFile>? supportingSources = null,
        IReadOnlyList<NamespaceRule>? rules = null)
    {
        rules ??= ApprovedRules.Namespaces;
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

        var analyzed = sources
            .Where(source => !IsBuildOutput(source.Path) && !IsGenerated(source))
            .Select(source => (Source: source, Tree: CSharpSyntaxTree.ParseText(source.Text, parseOptions, source.Path)))
            .ToList();

        var trees = analyzed.Select(item => item.Tree)
            .Concat((supportingSources ?? []).Select(s => CSharpSyntaxTree.ParseText(s.Text, parseOptions, s.Path)))
            .Append(CSharpSyntaxTree.ParseText(ImplicitUsingsSource, parseOptions, "obj/ImplicitUsings.g.cs"));

        var compilation = CSharpCompilation.Create(
            "ArchitectureAnalysis",
            trees,
            FrameworkReferences.Value.Concat(PackageReferences.Value),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var violations = new List<ArchitectureViolation>();
        var files = new Dictionary<ModuleLayer, int>();
        var types = new Dictionary<ModuleLayer, int>();
        foreach (var layer in Enum.GetValues<ModuleLayer>())
        {
            files[layer] = 0;
            types[layer] = 0;
        }

        foreach (var (source, tree) in analyzed)
        {
            var layer = LayerOf(source.Path);
            var root = tree.GetRoot();
            files[layer]++;
            types[layer] += root.DescendantNodes().Count(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);

            if (layer == ModuleLayer.Unowned)
            {
                violations.Add(new("unowned-source", source.Path, 0, "-", "-",
                    "source is not under a known src/EventForge.* module folder"));
                continue;
            }

            if (layer == ModuleLayer.Workflow)
            {
                // Hand-written Workflow code belongs in Domain/ or Application/. A file elsewhere in the
                // module would otherwise escape every layer rule, whatever namespace it declares.
                var first = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.GetLocation()
                    ?? ((CompilationUnitSyntax)root).Members.FirstOrDefault()?.GetLocation();
                violations.Add(new(WorkflowLocation, source.Path, first is null ? 0 : Line(first), layer.ToString(), "-",
                    "Workflow source must be under src/EventForge.Workflow/Domain/ or src/EventForge.Workflow/Application/"));
                continue;
            }

            var model = compilation.GetSemanticModel(tree);
            CheckNamespaceOwnership(source.Path, layer, root, model, violations);

            var rule = rules.FirstOrDefault(r => r.Layer == layer);
            if (rule is null)
            {
                continue;
            }

            foreach (var diagnostic in model.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                violations.Add(new(AnalysisIncomplete, source.Path, Line(diagnostic.Location), layer.ToString(),
                    diagnostic.Id, diagnostic.GetMessage()));
            }

            CheckReferences(source.Path, layer, rule, root, model, violations);
        }

        return new AnalysisResult(
            violations.OrderBy(v => v.File, StringComparer.Ordinal).ThenBy(v => v.Line).ThenBy(v => v.Rule, StringComparer.Ordinal)
                .ThenBy(v => v.Target, StringComparer.Ordinal).ToList(),
            files,
            types);
    }

    private static void CheckReferences(
        string path, ModuleLayer layer, NamespaceRule rule, SyntaxNode root, SemanticModel model,
        List<ArchitectureViolation> violations)
    {
        var declared = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        var seen = new HashSet<(int, string, string)>();

        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var info = model.GetSymbolInfo(name);
            var symbols = info.Symbol is not null ? [info.Symbol] : info.CandidateSymbols;
            var inUsing = name.Ancestors().OfType<UsingDirectiveSyntax>().Any();

            foreach (var symbol in symbols)
            {
                if (!TryGetTarget(symbol, inUsing, out var targetNamespace, out var target))
                {
                    continue;
                }

                foreach (var forbidden in rule.Forbidden.Where(f => IsInNamespace(targetNamespace, f)))
                {
                    var line = Line(name.GetLocation());
                    var id = $"{layer}-must-not-depend-on-{forbidden}";
                    if (!seen.Add((line, id, target)))
                    {
                        continue;
                    }

                    var enclosing = model.GetEnclosingSymbol(name.SpanStart);
                    var sourceNamespace = (enclosing as INamespaceSymbol ?? enclosing?.ContainingNamespace)?.ToDisplayString();
                    if (string.IsNullOrEmpty(sourceNamespace) || sourceNamespace.StartsWith('<'))
                    {
                        sourceNamespace = declared ?? "<global>";
                    }

                    violations.Add(new(id, path, line, sourceNamespace, target,
                        $"{layer} code must not depend on '{forbidden}'"));
                }
            }
        }
    }

    private static bool TryGetTarget(ISymbol symbol, bool inUsing, out string targetNamespace, out string target)
    {
        targetNamespace = target = "";
        switch (symbol is IAliasSymbol alias ? alias.Target : symbol)
        {
            case INamespaceSymbol ns when inUsing && !ns.IsGlobalNamespace:
                targetNamespace = ns.ToDisplayString();
                target = targetNamespace + " (namespace import)";
                return true;
            case IArrayTypeSymbol array:
                return TryGetTarget(array.ElementType, inUsing, out targetNamespace, out target);
            case IPointerTypeSymbol pointer:
                return TryGetTarget(pointer.PointedAtType, inUsing, out targetNamespace, out target);
            case INamedTypeSymbol type when type is not IErrorTypeSymbol && !type.ContainingNamespace.IsGlobalNamespace:
                targetNamespace = type.ContainingNamespace.ToDisplayString();
                target = type.ToDisplayString();
                return true;
            case IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol:
                var owner = symbol.ContainingType;
                if (owner is null || owner is IErrorTypeSymbol || owner.ContainingNamespace.IsGlobalNamespace)
                {
                    return false;
                }

                targetNamespace = owner.ContainingNamespace.ToDisplayString();
                target = owner.ToDisplayString() + "." + symbol.Name;
                return true;
            default:
                return false;
        }
    }

    private static bool IsInNamespace(string candidate, string prefix) =>
        candidate == prefix || candidate.StartsWith(prefix + ".", StringComparison.Ordinal);

    private static void CheckNamespaceOwnership(
        string path, ModuleLayer layer, SyntaxNode root, SemanticModel model, List<ArchitectureViolation> violations)
    {
        var owned = OwnedNamespace(layer);
        if (owned is null && layer != ModuleLayer.Api)
        {
            return;
        }

        // Validate the fully resolved namespace of each top-level type (so nested namespace blocks work),
        // reporting once per enclosing namespace declaration.
        var reported = new HashSet<SyntaxNode>();
        foreach (var type in root.DescendantNodes().Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                     .Where(n => n.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax))
        {
            var resolved = model.GetDeclaredSymbol(type)?.ContainingNamespace;
            var name = resolved is null || resolved.IsGlobalNamespace ? null : resolved.ToDisplayString();
            var declaration = type.Parent as BaseNamespaceDeclarationSyntax;

            if (owned is null)
            {
                // API code may use the module namespaces but must not declare them.
                if (name is not null && ModuleOwnedNamespaces.Any(o => IsInNamespace(name, o)) && reported.Add(declaration!))
                {
                    violations.Add(new(NamespaceOwnership, path, Line(declaration!.Name.GetLocation()), layer.ToString(), name,
                        "API files must not declare namespaces owned by another module"));
                }

                continue;
            }

            if (name is null)
            {
                violations.Add(new(NamespaceOwnership, path, Line(type.GetLocation()), layer.ToString(), "<global>",
                    $"types in this folder must be declared in namespace '{owned}' or a child"));
            }
            else if (!IsInNamespace(name, owned) && reported.Add(declaration!))
            {
                violations.Add(new(NamespaceOwnership, path, Line(declaration!.Name.GetLocation()), layer.ToString(), name,
                    $"files in this folder must declare namespace '{owned}' or a child"));
            }
        }
    }

    private static string? OwnedNamespace(ModuleLayer layer) => layer switch
    {
        ModuleLayer.Domain => "EventForge.Workflow.Domain",
        ModuleLayer.Application => "EventForge.Workflow.Application",
        ModuleLayer.Infrastructure => "EventForge.Infrastructure",
        ModuleLayer.Simulation => "EventForge.Simulation",
        _ => null,
    };

    public static ModuleLayer LayerOf(string path)
    {
        var p = path.Replace('\\', '/');
        bool In(string prefix) => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        return
            In("src/EventForge.Workflow/Domain/") ? ModuleLayer.Domain :
            In("src/EventForge.Workflow/Application/") ? ModuleLayer.Application :
            In("src/EventForge.Workflow/") ? ModuleLayer.Workflow :
            In("src/EventForge.Infrastructure/") ? ModuleLayer.Infrastructure :
            In("src/EventForge.Simulation/") ? ModuleLayer.Simulation :
            In("src/EventForge.Api/") ? ModuleLayer.Api :
            ModuleLayer.Unowned;
    }

    private static bool IsBuildOutput(string path) =>
        path.Replace('\\', '/').Split('/').SkipLast(1)
            .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static bool IsGenerated(SourceFile source) =>
        source.Path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
        source.Path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
        source.Path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
        source.Path.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
        source.Text.AsSpan(0, Math.Min(source.Text.Length, 512)).Contains("<auto-generated", StringComparison.Ordinal);

    private static int Line(Location location) => location.GetLineSpan().StartLinePosition.Line + 1;

    private static IReadOnlyList<MetadataReference> LoadFrameworkReferences()
    {
        var frameworkDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)
            ?? throw new InvalidOperationException("The .NET shared framework directory could not be determined.");
        var platformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException("TRUSTED_PLATFORM_ASSEMBLIES is unavailable.");

        var references = platformAssemblies.Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(Path.GetDirectoryName(p), frameworkDirectory, StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        foreach (var required in new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Net.Http.dll", "System.Data.Common.dll" })
        {
            if (!references.Any(r => r.Display!.EndsWith(required, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Framework metadata reference '{required}' not found in '{frameworkDirectory}'; semantic analysis would be unreliable.");
            }
        }

        return references;
    }

    /// <summary>
    /// Loads approved package assemblies from <paramref name="directory"/> (the test output folder, where the
    /// test project's PackageReference places them). Fails closed: a missing or mismatched assembly throws
    /// rather than letting Npgsql symbols silently go unresolved or resolve to something else.
    /// </summary>
    public static IReadOnlyList<MetadataReference> LoadPackageReferences(IEnumerable<ApprovedPackage> packages, string directory)
    {
        var references = new List<MetadataReference>();
        foreach (var package in packages)
        {
            var path = Path.Combine(directory, package.AssemblyFile);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"Approved package assembly '{package.AssemblyFile}' not found in '{directory}'; semantic analysis would be unreliable.");
            }

            var identity = System.Reflection.AssemblyName.GetAssemblyName(path);
            if (!string.Equals(identity.Name, package.AssemblyName, StringComparison.Ordinal) ||
                identity.Version?.Major != package.MajorVersion)
            {
                throw new InvalidOperationException(
                    $"'{path}' is {identity.Name} {identity.Version}, expected {package.AssemblyName} {package.MajorVersion}.x.");
            }

            references.Add(MetadataReference.CreateFromFile(path));
        }

        return references;
    }

    public static string Format(IEnumerable<ArchitectureViolation> violations) =>
        string.Join(Environment.NewLine, violations.Select(v => v.ToString()));
}
