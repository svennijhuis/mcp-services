using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;

namespace McpServices.Roslyn;

/// <summary>
/// Discovers <see cref="CodeFixProvider"/>s the same way an IDE does: the C# Features assemblies that
/// ship with Roslyn (compiler-diagnostic fixes, IDE fixes) plus the analyzer assemblies referenced by
/// a project (e.g. NetAnalyzers, StyleCop). Loaded lazily and cached per assembly path.
/// </summary>
public sealed class CodeFixCatalog(ILogger<CodeFixCatalog> logger)
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<CodeFixProvider>> _byAssembly = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<IReadOnlyList<CodeFixProvider>> _builtIn = new(() => LoadBuiltIn(logger));

    public IReadOnlyList<CodeFixProvider> BuiltIn => _builtIn.Value;

    /// <summary>Providers applicable to a project: built-in plus those found in the project's analyzer references.</summary>
    public IReadOnlyList<CodeFixProvider> ForProject(Project project)
    {
        var all = new List<CodeFixProvider>(BuiltIn);
        foreach (var reference in project.AnalyzerReferences)
        {
            if (reference is AnalyzerFileReference file)
            {
                all.AddRange(_byAssembly.GetOrAdd(file.FullPath, path => LoadFromPath(path, logger)));
            }
        }

        return all;
    }

    public IReadOnlyList<CodeFixProvider> ForDiagnostic(Project project, string diagnosticId) =>
        ForProject(project).Where(p => p.FixableDiagnosticIds.Contains(diagnosticId, StringComparer.OrdinalIgnoreCase)).ToList();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> FixableIds(Project project)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in ForProject(project))
        {
            foreach (var id in provider.FixableDiagnosticIds)
            {
                if (!map.TryGetValue(id, out var list))
                {
                    map[id] = list = [];
                }

                list.Add(provider.GetType().Name);
            }
        }

        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<CodeFixProvider> LoadBuiltIn(ILogger logger)
    {
        var providers = new List<CodeFixProvider> { new RemoveUnnecessaryUsingFix() };
        foreach (var name in new[] { "Microsoft.CodeAnalysis.CSharp.Features", "Microsoft.CodeAnalysis.Features" })
        {
            try
            {
                providers.AddRange(Instantiate(Assembly.Load(name), logger));
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                logger.LogWarning("Code fix assembly {Assembly} could not be loaded: {Message}", name, ex.Message);
            }
        }

        logger.LogInformation("Loaded {Count} built-in code fix providers", providers.Count);
        return providers;
    }

    private static IReadOnlyList<CodeFixProvider> LoadFromPath(string path, ILogger logger)
    {
        try
        {
            var assembly = Assembly.LoadFrom(path);
            var providers = Instantiate(assembly, logger);
            if (providers.Count > 0)
            {
                logger.LogInformation("Loaded {Count} code fix providers from {Path}", providers.Count, path);
            }

            return providers;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException or NotSupportedException)
        {
            logger.LogDebug("Analyzer assembly {Path} has no loadable code fixes: {Message}", path, ex.Message);
            return [];
        }
    }

    private static List<CodeFixProvider> Instantiate(Assembly assembly, ILogger logger)
    {
        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }

        var result = new List<CodeFixProvider>();
        foreach (var type in types)
        {
            if (type is null || type.IsAbstract || !typeof(CodeFixProvider).IsAssignableFrom(type))
            {
                continue;
            }

            var export = type.GetCustomAttribute<ExportCodeFixProviderAttribute>();
            if (export is not null && export.Languages.Length > 0 && !export.Languages.Contains(LanguageNames.CSharp, StringComparer.Ordinal))
            {
                continue;
            }

            try
            {
                if (type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes) is null)
                {
                    continue;
                }

                if (Activator.CreateInstance(type, nonPublic: true) is CodeFixProvider provider && provider.FixableDiagnosticIds.Length > 0)
                {
                    result.Add(provider);
                }
            }
            catch (Exception ex) when (ex is TargetInvocationException or MissingMethodException or MemberAccessException or TypeInitializationException or NotSupportedException)
            {
                logger.LogDebug("Skipping code fix provider {Type}: {Message}", type.FullName, ex.Message);
            }
        }

        return result;
    }

    /// <summary>Analyzers bundled with the project (never the IDE ones; those need editorconfig options).</summary>
    public static ImmutableArray<DiagnosticAnalyzer> ProjectAnalyzers(Project project) =>
        project.AnalyzerReferences.SelectMany(r => r.GetAnalyzers(LanguageNames.CSharp)).ToImmutableArray();
}
