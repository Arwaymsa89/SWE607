﻿namespace Contracts.Graph;

public class CodeGraph
{
    public Dictionary<string, CodeElement> Nodes = new();

    public CodeElement? TryGetCodeElement(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        Nodes.TryGetValue(id, out var codeElement);
        return codeElement;
    }

    /// <summary>
    ///     Note: Remove a parent for a method leads to a method without a parent.
    ///     That's what we want.
    /// </summary>
    public void RemoveCodeElement(string elementId)
    {
        RemoveCodeElements(new HashSet<string>() { elementId });
    }

    /// <summary>
    ///     Note: Remove a parent for a method leads to a method without a parent.
    ///     That's what we want.
    /// </summary>
    public void RemoveCodeElements(HashSet<string> elementIds)
    {
        foreach (var elementId in elementIds)
        { 
            Nodes.Remove(elementId); 
        }
        DfsHierarchy(CleanupFunc);
        return;

        void CleanupFunc(CodeElement element)
        {
            if (element.Parent != null && elementIds.Contains(element.Parent.Id))
            {
                element.Parent = null;
            }

            element.Children.RemoveWhere(e => elementIds.Contains(e.Id));
            element.Dependencies.RemoveWhere(d => elementIds.Contains(d.SourceId) || elementIds.Contains(d.TargetId));
        }
    }

    public void DfsHierarchy(Action<CodeElement> handler)
    {
        HashSet<string> visited = [];
        foreach (var element in Nodes.Values)
        {
            if (!visited.Contains(element.Id))
            {
                DfsHierarchy(element, visited, handler);
            }
        }
    }

    private void DfsHierarchy(CodeElement element, HashSet<string> visited, Action<CodeElement> handler)
    {
        visited.Add(element.Id);

        foreach (var child in element.Children)
        {
            if (!visited.Contains(child.Id))
            {
                DfsHierarchy(child, visited, handler);
            }
        }

        handler(element);
    }

    public CodeElement IntegrateCodeElementFromOriginal(CodeElement originalElement)
    {
        var existingElement = TryGetCodeElement(originalElement.Id);
        if (existingElement is not null)
        {
            // Code element is already integrated.
            return existingElement;
        }

        // Check if the parent of the new element is already in the graph
        var newElement = originalElement.CloneSimple();
        if (originalElement.Parent != null &&
            Nodes.TryGetValue(originalElement.Parent.Id, out var parent))
        {
            // Grab the parent reference. The parent is already in the graph.
            newElement.Parent = originalElement.Parent;
            parent.Children.Add(newElement);
        }

        // Check if children of the new element are already in the graph.
        var intersect = Nodes.Values.Intersect(originalElement.Children);
        foreach (var child in intersect)
        {
            newElement.Children.Add(child);
            child.Parent = newElement;
        }

        Nodes.TryAdd(newElement.Id, newElement);
        return newElement;
    }

    public IEnumerable<Dependency> GetAllDependencies()
    {
        return Nodes.Values.SelectMany(n => n.Dependencies).ToList();
    }
}