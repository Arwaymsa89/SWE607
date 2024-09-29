﻿using Contracts.Graph;

namespace CSharpCodeAnalyst.Project;

[Serializable]
public class ProjectData
{
    public List<SerializableChild> Children { get; set; } = [];

    public List<SerializableCodeElement> CodeElements { get; set; } = [];

    public List<SerializableDependency> Dependencies { get; set; } = [];

    public Dictionary<string, string> Settings { get; set; } = new();

    /// <summary>
    ///     Gallery is already serializable.
    /// </summary>
    public Gallery.Gallery Gallery { get; set; } = new();

    public void SetGallery(Gallery.Gallery gallery)
    {
        // TODO Consider to introduce a serializable Gallery not storing the source locations for dependencies.
        // This would save space. But we have to restore the dependencies.
        Gallery = gallery;
    }

    public Gallery.Gallery GetGallery()
    {
        return Gallery;
    }


    /// <summary>
    ///     Flatten the recursive structures.
    /// </summary>
    public void SetCodeGraph(CodeGraph codeGraph)
    {
        CodeElements = codeGraph.Nodes.Values
            .Select(n =>
                new SerializableCodeElement(n.Id, n.Name, n.FullName, n.ElementType, n.SourceLocations, n.Attributes)).ToList();

        // We iterate over children, so we expect to have a parent
        Children = codeGraph.Nodes.Values
            .SelectMany(element => element.Children)
            .Select(child => new SerializableChild(child.Id, child.Parent!.Id)).ToList();

        Dependencies = codeGraph.Nodes.Values
            .SelectMany(element => element.Dependencies)
            .Select(dependency => new SerializableDependency(dependency.SourceId, dependency.TargetId, dependency.Type,
                dependency.SourceLocations))
            .ToList();
    }

    public CodeGraph GetCodeGraph()
    {
        var codeStructure = new CodeGraph();

        // Pass one: Create elements
        foreach (var se in CodeElements)
        {
            var element = new CodeElement(se.Id, se.ElementType, se.Name, se.FullName, null!);
            element.SourceLocations = se.SourceLocations;
            element.Attributes = se.Attributes;
            codeStructure.Nodes.Add(element.Id, element);
        }

        // Pass two: Create dependencies and parent / child connections
        foreach (var sc in Children)
        {
            var child = codeStructure.Nodes[sc.ChildId];
            var parent = codeStructure.Nodes[sc.ParentId];
            child.Parent = parent;
            parent.Children.Add(child);
        }

        foreach (var sd in Dependencies)
        {
            var source = codeStructure.Nodes[sd.SourceId];
            var dependency = new Dependency(sd.SourceId, sd.TargetId, sd.Type);
            dependency.SourceLocations = sd.SourceLocations;
            source.Dependencies.Add(dependency);
        }


        return codeStructure;
    }
}