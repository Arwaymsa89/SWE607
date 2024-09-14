﻿using Contracts.Colors;
using Contracts.Graph;

namespace CodeParser.Export;

public class DgmlExport
{
    /// <summary>
    ///     Exports the given nodes and edges to a dgml file.
    ///     Note that the "Contains" dependency is treated as hierarchy information
    ///     to build sub-graphs in the output file.
    /// </summary>
    public void Export(string fileName, CodeGraph graph)
    {
        var builder = new DgmlFileBuilder();

        WriteCategories(builder);

        // Nodes and groups.
        var containsRelationships = new List<(string sourceId, string targetId)>();
        HashSet<string> containers = [];
        foreach (var node in graph.Nodes.Values)
        {
            if (node.Children.Any())
            {
                builder.AddGroup(node.Id, node.Name, node.ElementType.ToString());
                containers.Add(node.Id);
            }
            else
            {
                builder.AddNodeById(node.Id, node.Name, node.ElementType.ToString());
            }

            containsRelationships.AddRange(node.Children.Select(c => (node.Id, c.Id)));
        }

        // Regular dependencies
        var normal = new List<Dependency>();
        graph.DfsHierarchy(e => normal.AddRange(e.Dependencies));
        foreach (var edge in normal)
        {
            // Omit the calls label for better readability.
            var edgeLabel = GetEdgeLabel(edge);
            builder.AddEdgeById(edge.SourceId, edge.TargetId, edgeLabel);
        }

        // Containment relationships
        foreach (var edge in containsRelationships)
        {
            if (containers.Contains(edge.targetId))
            {
                builder.AddGroupToGroup(edge.sourceId, edge.targetId);
            }
            else
            {
                builder.AddNodeToGroup(edge.sourceId, edge.targetId);
            }
        }

        builder.WriteOutput(fileName);
    }

    private static string GetEdgeLabel(Dependency dependency)
    {
        // Omit the label text for now. The color makes it clear that it is a call dependency
        if (dependency.Type == DependencyType.Calls || dependency.Type ==  DependencyType.Invokes)
        {
            return string.Empty;
        }

        // We can see this by the dotted line
        if (dependency.Type == DependencyType.Implements || dependency.Type == DependencyType.Inherits)
        {
            return string.Empty;
        }

        if (dependency.Type == DependencyType.Uses)
        {
            return string.Empty;
        }

        if (dependency.Type == DependencyType.UsesAttribute)
        {
            return string.Empty;
        }

        return dependency.Type.ToString();
    }

    private static void WriteCategories(DgmlFileBuilder writer)
    {
        var elementTypes = Enum.GetValues(typeof(CodeElementType)).Cast<CodeElementType>();
        foreach (var type in elementTypes)
        {
            writer.AddCategory(type.ToString(), "Background", $"#{ColorDefinitions.GetRbgOf(type):X}");
        }
    }
}