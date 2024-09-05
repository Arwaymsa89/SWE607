﻿using System.Diagnostics;
using CodeParser.Parser.Config;
using Contracts.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeParser.Parser;

public class ParserProgressArg : EventArgs
{
    public int NumberOfParsedElements { get; set; }
}

public partial class Parser(ParserConfig config)
{
    private readonly CodeGraph _codeGraph = new();

    private readonly ParserConfig _config = config;
    private readonly Dictionary<string, ISymbol> _elementIdToSymbolMap = new();
    private readonly HashSet<string> _projectFilePaths = [];
    private readonly Dictionary<string, CodeElement> _symbolKeyToElementMap = new();

    public event EventHandler<ParserProgressArg>? ParserProgress;

    public async Task<CodeGraph> ParseSolution(string solutionPath)
    {
        Clear();

        var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(solutionPath);

        CollectAllFilePathInSolution(solution);

        // First Pass: Build Hierarchy
        await BuildHierarchy(solution);

        // Second Pass: Build Dependencies
        // We don't need to iterate over the projects
        AnalyzeDependencies(solution);

        Clear();

        // Makes the cycle detection easier because I never get to the assembly as shared ancestor
        // for a nested dependency.
        InsertGlobalNamespaceIfUsed();
        return _codeGraph;
    }

    private void Clear()
    {
        _symbolKeyToElementMap.Clear();
        _elementIdToSymbolMap.Clear();
        _projectFilePaths.Clear();
    }

    /// <summary>
    ///     If any assembly uses the global namespace we add the global namespace to all assemblies.
    ///     For example a unit test assembly may have the autogenerated Main.
    /// </summary>
    private void InsertGlobalNamespaceIfUsed()
    {
        var global = "global";
        var assemblies = _codeGraph.Nodes.Values.Where(n => n.Parent is null).ToList();
        Debug.Assert(assemblies.All(a => a.ElementType == CodeElementType.Assembly));
        var isGlobalNsUsed = assemblies.Any(a => a.Children.Any(c => c.ElementType != CodeElementType.Namespace));

        var newGlobalNamespaces = new List<CodeElement>();
        if (isGlobalNsUsed)
        {
            foreach (var assembly in assemblies)
            {
                var children = assembly.Children.ToList();

                var id = Guid.NewGuid().ToString();
                var fullName = assembly.FullName + "." + global;
                var globalNs = new CodeElement(id, CodeElementType.Namespace, global, fullName, assembly);
                newGlobalNamespaces.Add(globalNs);

                assembly.Children.Add(globalNs);

                // Move elements
                foreach (var child in children)
                {
                    child.MoveTo(globalNs);
                }
            }

            // Don't modify collection during iteration
            foreach (var globalNs in newGlobalNamespaces)
            {
                _codeGraph.Nodes[globalNs.Id] = globalNs;
            }
        }
    }

    private void CollectAllFilePathInSolution(Solution solution)
    {
        foreach (var project in solution.Projects)
        {
            if (_config.IsProjectIncluded(project.Name) is false)
            {
                continue;
            }

            foreach (var document in project.Documents)
            {
                if (document.FilePath != null)
                {
                    _projectFilePaths.Add(document.FilePath);
                }
            }
        }
    }

    private string GetSymbolKey(ISymbol symbol)
    {
        var fullName = BuildSymbolName(symbol);

        if (symbol is IMethodSymbol methodSymbol)
        {
            // Overloaded methods should have a unique key
            var parameters = string.Join("_ ", methodSymbol.Parameters.Select(p => p.Type.ToString()));
            return $"{fullName}_{parameters}_{symbol.Kind}";
        }

        return $"{fullName}_{symbol.Kind}";
    }

    /// <summary>
    ///     Since I iterate over the compilation units (to get rid of external code)
    ///     any seen namespace, even "namespace X.Y.Z;", ends up as
    ///     namespace Z directly under the assembly node.
    ///     So If I see namespace X.Y.Z I create X, Y, Z and set them as parent child.
    /// </summary>
    private CodeElement GetOrCreateCodeElementWithNamespaceHierarchy(ISymbol symbol,
        CodeElementType elementType, CodeElement initialParent, SourceLocation? location)
    {
        if (symbol is INamespaceSymbol namespaceSymbol)
        {
            var namespaces = new Stack<INamespaceSymbol>();
            var current = namespaceSymbol;

            // Build the stack of nested namespaces
            while (current is { IsGlobalNamespace: false })
            {
                namespaces.Push(current);
                current = current.ContainingNamespace;
            }

            var parent = initialParent;

            // Create or get each namespace in the hierarchy
            while (namespaces.Count > 0)
            {
                // We create the whole chain when encountering namespace X.Y.Z;
                // So I give all the same source location. Right?
                var ns = namespaces.Pop();

                // The location is only valid for the input namespace symbol.
                var nsLocation = ReferenceEquals(ns, namespaceSymbol) ? location : null;
                var nsElement = GetOrCreateCodeElement(ns, CodeElementType.Namespace, parent, nsLocation);
                parent = nsElement;
            }

            return parent;
        }

        // For non-namespace symbols, use the original logic
        return GetOrCreateCodeElement(symbol, elementType, initialParent, location);
    }

    private CodeElement GetOrCreateCodeElement(ISymbol symbol, CodeElementType elementType, CodeElement? parent,
        SourceLocation? location)
    {
        var symbolKey = GetSymbolKey(symbol);

        // We may encounter namespace declarations in many files.
        if (_symbolKeyToElementMap.TryGetValue(symbolKey, out var existingElement))
        {
            if (location != null)
            {
                existingElement.SourceLocations.Add(location);
            }

            return existingElement;
        }

        var name = symbol.Name;
        var fullName = BuildSymbolName(symbol);
        var newId = Guid.NewGuid().ToString();

        var element = new CodeElement(newId, elementType, name, fullName, parent);
        if (location != null)
        {
            element.SourceLocations.Add(location);
        }

        parent?.Children.Add(element);
        _codeGraph.Nodes[element.Id] = element;
        _symbolKeyToElementMap[symbolKey] = element;
        _elementIdToSymbolMap[element.Id] = symbol;
        return element;
    }

    /// <summary>
    ///     Sometimes when walking up the parent chain:
    ///     After the global namespace the containing symbol is not reliable.
    ///     If we do not end up at an assembly it is added manually.
    /// </summary>
    public static string BuildSymbolName(ISymbol symbol)
    {
        var parts = new List<string>();

        var currentSymbol = symbol;
        ISymbol? lastKnownSymbol = null;

        while (currentSymbol != null)
        {
            if (currentSymbol is IModuleSymbol)
            {
                // Skip the module symbol
                currentSymbol = currentSymbol.ContainingSymbol;
            }

            lastKnownSymbol = currentSymbol;

            var name = currentSymbol.Name;
            parts.Add(name);
            currentSymbol = currentSymbol.ContainingSymbol;
        }

        if (lastKnownSymbol is not IAssemblySymbol)
        {
            // global namespace has the ContainingCompilation set.
            Debug.Assert(lastKnownSymbol is INamespaceSymbol { IsGlobalNamespace: true });
            var namespaceSymbol = lastKnownSymbol as INamespaceSymbol;
            var assemblySymbol = namespaceSymbol.ContainingCompilation.Assembly;
            parts.Add(assemblySymbol.Name);
        }

        parts.Reverse();
        var fullName = string.Join(".", parts.Where(p => !string.IsNullOrEmpty(p)));
        return fullName;
    }

    /// <summary>
    ///     Get the source location of a syntax node
    /// </summary>
    private static SourceLocation GetLocation(SyntaxNode node)
    {
        var location = new SourceLocation(
            node.SyntaxTree.FilePath,
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            node.GetLocation().GetLineSpan().StartLinePosition.Character + 1);
        return location;
    }

    /// <summary>
    ///     Gets the source locations of a semantic symbol. We may have more than one location if
    ///     the symbol is defined over several files (i.e. partial classes)
    /// </summary>
    private static List<SourceLocation> GetLocations(ISymbol symbol)
    {
        return symbol.Locations.Select(l => new SourceLocation
        {
            File = l.SourceTree?.FilePath ?? "",
            Line = l.GetLineSpan().StartLinePosition.Line + 1,
            Column = l.GetLineSpan().StartLinePosition.Character + 1
        }).ToList();
    }


    private static void AddDependency(CodeElement source, DependencyType type,
        CodeElement target,
        List<SourceLocation> sourceLocations)
    {
        var existingDependency = source.Dependencies.FirstOrDefault(d =>
            d.TargetId == target.Id && d.Type == type);

        if (existingDependency != null)
        {
            // Note we may read some dependencies more than once through different ways but that's fine.
            // For example identifier and member access of field.
            var newLocations = sourceLocations.Except(existingDependency.SourceLocations);
            existingDependency.SourceLocations.AddRange(newLocations);
        }
        else
        {
            var newDependency = new Dependency(source.Id, target.Id, type);
            newDependency.SourceLocations.AddRange(sourceLocations);
            source.Dependencies.Add(newDependency);
        }
    }
}