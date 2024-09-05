﻿using System.Diagnostics;
using Contracts.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeParser.Parser;

public partial class Parser
{
    /// <summary>
    ///     Entry for dependency analysis
    /// </summary>
    private void AnalyzeDependencies(Solution solution)
    {
        var loop = 0;
        foreach (var element in _codeGraph.Nodes.Values)
        {
            var symbol = _elementIdToSymbolMap[element.Id];

            if (symbol is IEventSymbol eventSymbol)
            {
                AnalyzeEventDependencies(solution, element, eventSymbol);
            }
            else if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateSymbol)
            {
                // Handle before the type dependencies.
                AnalyzeDelegateDependencies(element, delegateSymbol);
            }
            else if (symbol is INamedTypeSymbol typeSymbol)
            {
                AnalyzeInheritanceDependencies(element, typeSymbol);
            }
            else if (symbol is IMethodSymbol methodSymbol)
            {
                AnalyzeMethodDependencies(solution, element, methodSymbol);
            }
            else if (symbol is IPropertySymbol propertySymbol)
            {
                AnalyzePropertyDependencies(solution, element, propertySymbol);
            }
            else if (symbol is IFieldSymbol fieldSymbol)
            {
                AnalyzeFieldDependencies(element, fieldSymbol);
            }

            // For all type of symbols check if decorated with an attribute.
            AnalyzeAttributeDependencies(element, symbol);

            if (loop % 10 == 0)
            {
                ParserProgress?.Invoke(this, new ParserProgressArg
                {
                    NumberOfParsedElements = loop
                });
            }

            ++loop;
        }

        // Analyze global statements for each assembly
        AnalyzeGlobalStatementsForAssembly(solution);
    }


    private void AnalyzeGlobalStatementsForAssembly(Solution solution)
    {
        foreach (var statement in _globalStatementsByAssembly)
        {
            var assemblySymbol = statement.Key;
            var globalStatements = statement.Value;
            if (globalStatements.Count == 0)
            {
                continue;
            }

            // Find the existing assembly element
            var symbolKey = GetSymbolKey(assemblySymbol);
            var assemblyElement = _symbolKeyToElementMap[symbolKey];

            // Create a dummy class for this assembly's global statements
            var dummyClassId = Guid.NewGuid().ToString();
            var dummyClassName = "GlobalStatements";
            var dummyClassFullName = BuildSymbolName(assemblySymbol) + "." + dummyClassName;
            var dummyClass = new CodeElement(dummyClassId, CodeElementType.Class, dummyClassName, dummyClassFullName,
                assemblyElement);
            _codeGraph.Nodes[dummyClassId] = dummyClass;
            assemblyElement.Children.Add(dummyClass);

            // Create a dummy method to contain global statements
            var dummyMethodId = Guid.NewGuid().ToString();
            var dummyMethodName = "Execute";
            var dummyMethodFullName = $"{dummyClassName}.{dummyMethodName}";
            var dummyMethod = new CodeElement(dummyMethodId, CodeElementType.Method, dummyMethodName,
                dummyMethodFullName, dummyClass);
            _codeGraph.Nodes[dummyMethodId] = dummyMethod;
            dummyClass.Children.Add(dummyMethod);

            // Analyze global statements within the context of the dummy method
            foreach (var globalStatement in globalStatements)
            {
                var document = solution.GetDocument(globalStatement.SyntaxTree);
                var semanticModel = document?.GetSemanticModelAsync().Result;
                if (semanticModel != null)
                {
                    AnalyzeMethodBody(dummyMethod, globalStatement, semanticModel);
                }
            }
        }
    }
    private void AnalyzeAttributeDependencies(CodeElement element, ISymbol symbol)
    {
        foreach (var attributeData in symbol.GetAttributes())
        {
            if (attributeData.AttributeClass != null)
            {
                var location = attributeData.ApplicationSyntaxReference != null
                    ? GetLocation(attributeData.ApplicationSyntaxReference.GetSyntax())
                    : null;

                element.Attributes.Add(attributeData.AttributeClass.Name);
                AddTypeDependency(element, attributeData.AttributeClass, DependencyType.UsesAttribute, location);
            }
        }
    }

    private void AnalyzeDelegateDependencies(CodeElement delegateElement, INamedTypeSymbol delegateSymbol)
    {
        var methodSymbol = delegateSymbol.DelegateInvokeMethod;
        if (methodSymbol is null)
        {
            Trace.WriteLine("Method symbol not available for delegate");
            return;
        }

        // Analyze return type
        AddTypeDependency(delegateElement, methodSymbol.ReturnType, DependencyType.Uses);

        // Analyze parameter types
        foreach (var parameter in methodSymbol.Parameters)
        {
            AddTypeDependency(delegateElement, parameter.Type, DependencyType.Uses);
        }
    }

    private void AnalyzeEventDependencies(Solution solution, CodeElement eventElement, IEventSymbol eventSymbol)
    {
        // Analyze event type (usually a delegate type)
        AddTypeDependency(eventElement, eventSymbol.Type, DependencyType.Uses);

        // If the event has add/remove accessors, analyze them
        if (eventSymbol.AddMethod != null)
        {
            AnalyzeMethodDependencies(solution, eventElement, eventSymbol.AddMethod);
        }

        if (eventSymbol.RemoveMethod != null)
        {
            AnalyzeMethodDependencies(solution, eventElement, eventSymbol.RemoveMethod);
        }
    }

    /// <summary>
    ///     Use solution, not the compilation. The syntax tree may not be found.
    /// </summary>
    private void AnalyzeMethodDependencies(Solution solution, CodeElement methodElement, IMethodSymbol methodSymbol)
    {
        // Analyze parameter types
        foreach (var parameter in methodSymbol.Parameters)
        {
            AddTypeDependency(methodElement, parameter.Type, DependencyType.Uses);
        }

        // Analyze return type
        if (!methodSymbol.ReturnsVoid)
        {
            AddTypeDependency(methodElement, methodSymbol.ReturnType, DependencyType.Uses);
        }

        //if (methodSymbol.IsExtensionMethod)
        //{
        //    // The first parameter of an extension method is the extended type
        //    var extendedType = methodSymbol.Parameters[0].Type;
        //    AddTypeDependency(methodElement, extendedType, DependencyType.Uses);
        //}

        // If this method is an interface method or an abstract method, find its implementations
        if (methodSymbol.IsAbstract || methodSymbol.ContainingType.TypeKind == TypeKind.Interface)
        {
            FindImplementations(methodElement, methodSymbol);
        }

        // Check for method override
        if (methodSymbol.IsOverride)
        {
            var overriddenMethod = methodSymbol.OverriddenMethod;
            if (overriddenMethod != null)
            {
                var locations = GetLocations(methodSymbol);
                AddMethodOverrideDependency(methodElement, overriddenMethod, locations);
            }
        }

        // Analyze method body for object creations and method calls
        foreach (var syntaxReference in methodSymbol.DeclaringSyntaxReferences)
        {
            var syntax = syntaxReference.GetSyntax();
            var document = solution.GetDocument(syntax.SyntaxTree);

            var semanticModel = document?.GetSemanticModelAsync().Result;
            if (semanticModel == null)
            {
                continue;
            }

            AnalyzeMethodBody(methodElement, syntax, semanticModel);
        }
    }


    private void FindImplementations(CodeElement methodElement, IMethodSymbol methodSymbol)
    {
        var implementingTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        // If it's an interface method, find all types implementing the interface
        if (methodSymbol.ContainingType.TypeKind == TypeKind.Interface)
        {
            implementingTypes.UnionWith(FindTypesImplementingInterface(methodSymbol.ContainingType));
        }
        // If it's an abstract method, find all types deriving from the containing type
        else if (methodSymbol.IsAbstract)
        {
            implementingTypes.UnionWith(FindTypesDerivedFrom(methodSymbol.ContainingType));
        }

        foreach (var implementingType in implementingTypes)
        {
            var implementingMethod = implementingType.FindImplementationForInterfaceMember(methodSymbol);
            if (implementingMethod != null)
            {
                var implementingElement = _symbolKeyToElementMap.GetValueOrDefault(GetSymbolKey(implementingMethod));
                if (implementingElement != null)
                {
                    // Note: Implementations for external methods are not in our map
                    var locations = GetLocations(implementingMethod);
                    AddDependency(implementingElement, DependencyType.Implements, methodElement, locations);
                }
            }
        }
    }

    private IEnumerable<INamedTypeSymbol> FindTypesImplementingInterface(INamedTypeSymbol interfaceSymbol)
    {
        return _allNamedTypesInSolution
            .Where(type => type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, interfaceSymbol)));
    }

    private IEnumerable<INamedTypeSymbol> FindTypesDerivedFrom(INamedTypeSymbol baseType)
    {
        return _allNamedTypesInSolution
            .Where(type => IsTypeDerivedFrom(type, baseType));
    }

    private bool IsTypeDerivedFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        var currentType = type.BaseType;
        while (currentType != null)
        {
            if (SymbolEqualityComparer.Default.Equals(currentType, baseType))
            {
                return true;
            }

            currentType = currentType.BaseType;
        }

        return false;
    }

    /// <summary>
    ///     Overrides
    /// </summary>
    private void AddMethodOverrideDependency(CodeElement sourceElement, IMethodSymbol methodSymbol,
        List<SourceLocation> locations)
    {
        if (_symbolKeyToElementMap.TryGetValue(GetSymbolKey(methodSymbol), out var targetElement))
        {
            AddDependency(sourceElement, DependencyType.Overrides, targetElement, locations);
        }
        else if (_symbolKeyToElementMap.TryGetValue(GetSymbolKey(methodSymbol.ContainingType),
                     out var containingTypeElement))
        {
            // Trace.WriteLine("Method override not captured. It is likely that the base method is generic or external code.");

            // If we don't have the method itself in our map, add a dependency to its containing type
            // Maybe we override a framework method. Happens also if the base method is a generic one.
            // In this case the GetSymbolKey is different. One uses T, the overriding method uses the actual type.
            AddDependency(sourceElement, DependencyType.Overrides, containingTypeElement, locations);
        }
    }

    private void AnalyzeFieldDependencies(CodeElement fieldElement, IFieldSymbol fieldSymbol)
    {
        AddTypeDependency(fieldElement, fieldSymbol.Type, DependencyType.Uses);
    }

    /// <summary>
    ///     For method and property bodies.
    /// </summary>
    private void AnalyzeMethodBody(CodeElement sourceElement, SyntaxNode node, SemanticModel semanticModel)
    {
        foreach (var descendantNode in node.DescendantNodesAndSelf())
        {
            switch (descendantNode)
            {
                case ObjectCreationExpressionSyntax objectCreationSyntax:
                    var typeInfo = semanticModel.GetTypeInfo(objectCreationSyntax);
                    if (typeInfo.Type != null)
                    {
                        var location = GetLocation(objectCreationSyntax);
                        AddTypeDependency(sourceElement, typeInfo.Type, DependencyType.Creates, location);
                    }

                    break;

                case InvocationExpressionSyntax invocationSyntax:
                    var symbolInfo = semanticModel.GetSymbolInfo(invocationSyntax);
                    if (symbolInfo.Symbol is IMethodSymbol calledMethod)
                    {
                        var location = GetLocation(invocationSyntax);
                        AddCallsDependency(sourceElement, calledMethod, location);

                        // Handle generic method invocations
                        if (calledMethod.IsGenericMethod)
                        {
                            foreach (var typeArg in calledMethod.TypeArguments)
                            {
                                AddTypeDependency(sourceElement, typeArg, DependencyType.Uses, location);
                            }
                        }
                    }

                    // Handle event invocations
                    var invokedSymbol = semanticModel.GetSymbolInfo(invocationSyntax.Expression).Symbol;
                    if (invokedSymbol is IMethodSymbol { AssociatedSymbol: IEventSymbol eventSymbol2 })
                    {
                        // Capture cases where an event is directly invoked 
                        AddEventUsageDependency(sourceElement, eventSymbol2);
                    }

                    break;

                case IdentifierNameSyntax identifierSyntax:
                    AnalyzeIdentifier(sourceElement, identifierSyntax, semanticModel);
                    break;

                case MemberAccessExpressionSyntax memberAccessSyntax:
                    AnalyzeMemberAccess(sourceElement, memberAccessSyntax, semanticModel);
                    break;

                case AssignmentExpressionSyntax assignmentExpression:

                    // Event registration and un-registration.
                    if (assignmentExpression.IsKind(SyntaxKind.AddAssignmentExpression) ||
                        assignmentExpression.IsKind(SyntaxKind.SubtractAssignmentExpression))
                    {
                        var leftSymbol = semanticModel.GetSymbolInfo(assignmentExpression.Left).Symbol;
                        if (leftSymbol is IEventSymbol eventSymbol)
                        {
                            AddEventUsageDependency(sourceElement, eventSymbol);
                        }
                    }

                    break;
            }
        }
    }

    private void AddEventUsageDependency(CodeElement sourceElement, IEventSymbol eventSymbol)
    {
        if (_symbolKeyToElementMap.TryGetValue(GetSymbolKey(eventSymbol), out var eventElement))
        {
            AddDependency(sourceElement, DependencyType.Uses, eventElement, []);
        }
        else if (_symbolKeyToElementMap.TryGetValue(GetSymbolKey(eventSymbol.ContainingType),
                     out var containingTypeElement))
        {
            // If we don't have the event itself in our map, add a dependency to its containing type
            AddDependency(sourceElement, DependencyType.Uses, containingTypeElement, []);
        }
    }

    private void AddCallsDependency(CodeElement sourceElement, IMethodSymbol methodSymbol, SourceLocation location)
    {
        //Trace.WriteLine($"Adding call dependency: {sourceElement.Name} -> {methodSymbol.Name}");

        if (methodSymbol.IsExtensionMethod)
        {
            // Handle calls to extension methods
            methodSymbol = methodSymbol.ReducedFrom ?? methodSymbol;
        }

        if (_symbolKeyToElementMap.TryGetValue(GetSymbolKey(methodSymbol), out var targetElement))
        {
            AddDependency(sourceElement, DependencyType.Calls, targetElement, [location]);
        }
        // If the method is not in our map, we might want to add a dependency to its containing type
        else if (_symbolKeyToElementMap.TryGetValue(GetSymbolKey(methodSymbol.ContainingType),
                     out var containingTypeElement))
        {
            AddDependency(sourceElement, DependencyType.Calls, containingTypeElement, [location]);
        }
    }


    /// <summary>
    ///     Handle also List_T. Where List is not a code element of our project
    /// </summary>
    private void AnalyzeInheritanceDependencies(CodeElement element, INamedTypeSymbol typeSymbol)
    {
        // Analyze base class
        if (typeSymbol.BaseType != null && typeSymbol.BaseType.SpecialType != SpecialType.System_Object)
        {
            AddTypeDependency(element, typeSymbol.BaseType, DependencyType.Inherits);
        }

        // Analyze implemented interfaces
        foreach (var @interface in typeSymbol.Interfaces)
        {
            AddTypeDependency(element, @interface, DependencyType.Implements);
        }
    }

    private void AddTypeDependency(CodeElement sourceElement, ITypeSymbol typeSymbol, DependencyType dependencyType,
        SourceLocation? location = null)
    {
        switch (typeSymbol)
        {
            case IArrayTypeSymbol arrayType:
                // For arrays, we add an "Uses" dependency to the element type. Even if we create them.
                AddTypeDependency(sourceElement, arrayType.ElementType, DependencyType.Uses, location);
                break;

            case INamedTypeSymbol namedTypeSymbol:
                var symbolKey = GetSymbolKey(namedTypeSymbol);
                if (_symbolKeyToElementMap.TryGetValue(symbolKey, out var targetElement))
                {
                    // The type is internal (part of our codebase)
                    AddDependency(sourceElement, dependencyType, targetElement, location != null ? [location] : []);

                    if (namedTypeSymbol.IsGenericType)
                    {
                        // Add "Uses" dependencies to type arguments
                        foreach (var typeArg in namedTypeSymbol.TypeArguments)
                        {
                            AddTypeDependency(sourceElement, typeArg, DependencyType.Uses, location);
                        }
                    }
                }
                else
                {
                    // The type is external

                    // Optionally, you might want to track external dependencies
                    // AddExternalDependency(sourceElement, namedTypeSymbol, dependencyType, location);
                    if (namedTypeSymbol.IsGenericType)
                    {
                        // For example List<MyType>
                        // Add "Uses" dependencies to type arguments, which might be internal
                        foreach (var typeArg in namedTypeSymbol.TypeArguments)
                        {
                            AddTypeDependency(sourceElement, typeArg, DependencyType.Uses, location);
                        }
                    }
                }

                break;

            case IPointerTypeSymbol pointerTypeSymbol:
                AddTypeDependency(sourceElement, pointerTypeSymbol.PointedAtType, DependencyType.Uses, location);
                break;
            case IFunctionPointerTypeSymbol functionPointerType:

                // The function pointer has a return type and parameters.
                // we could add these dependencies here.

                break;
            case IDynamicTypeSymbol:
                // Noting to gain on this branch
                // For example: Dictionary<string, dynamic>
                break;
            default:
                // Handle other type symbols (e.g., type parameters)
                symbolKey = GetSymbolKey(typeSymbol);
                if (_symbolKeyToElementMap.TryGetValue(symbolKey, out targetElement))
                {
                    AddDependency(sourceElement, dependencyType, targetElement, location != null ? [location] : []);
                }

                break;
        }
    }

    private void AnalyzeIdentifier(CodeElement sourceElement, IdentifierNameSyntax identifierSyntax,
        SemanticModel semanticModel)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(identifierSyntax);

        // Note: I treat accessing a property as a call to the getter / setter.
        // The dependency is added in AnalyzeMemberAccess.
        //
        //if (symbolInfo.Symbol is IPropertySymbol propertySymbol)
        //{
        //    AddPropertyCallDependency(sourceElement, propertySymbol, DependencyType.Uses);
        //}
        //else

        if (symbolInfo.Symbol is IFieldSymbol fieldSymbol)
        {
            AddFieldDependency(sourceElement, fieldSymbol, DependencyType.Uses);
        }
    }

    private void AnalyzeMemberAccess(CodeElement sourceElement, MemberAccessExpressionSyntax memberAccessSyntax,
        SemanticModel semanticModel)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(memberAccessSyntax);
        if (symbolInfo.Symbol is IPropertySymbol propertySymbol)
        {
            // I treat accessing a property as a call to the getter / setter.
            var location = GetLocation(memberAccessSyntax);
            AddPropertyCallDependency(sourceElement, propertySymbol, [location]);
        }
        else if (symbolInfo.Symbol is IFieldSymbol fieldSymbol)
        {
            AddFieldDependency(sourceElement, fieldSymbol, DependencyType.Uses);
        }
    }

    /// <summary>
    ///     Calling a property is treated like calling a method.
    /// </summary>
    private void AddPropertyCallDependency(CodeElement sourceElement, IPropertySymbol propertySymbol,
        List<SourceLocation> locations)
    {
        if (_symbolKeyToElementMap.TryGetValue(GetSymbolKey(propertySymbol), out var targetElement))
        {
            AddDependency(sourceElement, DependencyType.Calls, targetElement, locations);
        }
        else if (_symbolKeyToElementMap.TryGetValue(GetSymbolKey(propertySymbol.ContainingType),
                     out var containingTypeElement))
        {
            AddDependency(sourceElement, DependencyType.Calls, containingTypeElement, locations);
        }
    }

    private void AddFieldDependency(CodeElement sourceElement, IFieldSymbol fieldSymbol, DependencyType dependencyType)
    {
        if (_symbolKeyToElementMap.TryGetValue(GetSymbolKey(fieldSymbol), out var targetElement))
        {
            AddDependency(sourceElement, dependencyType, targetElement, []);
        }
        else if (_symbolKeyToElementMap.TryGetValue(GetSymbolKey(fieldSymbol.ContainingType),
                     out var containingTypeElement))
        {
            AddDependency(sourceElement, dependencyType, containingTypeElement, []);
        }
    }
}