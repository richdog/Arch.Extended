using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;

namespace Arch.System.SourceGenerator;

[Generator]
public class QueryGenerator : IIncrementalGenerator
{
    private static Dictionary<ISymbol, List<IMethodSymbol>> _classToMethods { get; set; }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        //if (!Debugger.IsAttached) Debugger.Launch();

        // Register the generic attributes 
        string attributes = $$"""
            namespace Arch.System.SourceGenerator
            {
            #if NET7_0_OR_GREATER
                {{new StringBuilder().AppendGenericAttributes("All", "All", 25)}}
                {{new StringBuilder().AppendGenericAttributes("Any", "Any", 25)}}
                {{new StringBuilder().AppendGenericAttributes("None", "None", 25)}}
                {{new StringBuilder().AppendGenericAttributes("Exclusive", "Exclusive", 25)}}
            #endif
            }
        """;
        context.RegisterPostInitializationOutput(ctx => ctx.AddSource("Attributes.g.cs", SourceText.From(attributes, Encoding.UTF8)));

        // Do a simple filter for methods marked with update
        IncrementalValuesProvider<MethodDeclarationSyntax> methodDeclarations = context.SyntaxProvider.CreateSyntaxProvider(
                 static (s, _) => s is MethodDeclarationSyntax { AttributeLists.Count: > 0 },
                 static (ctx, _) => GetMethodSymbolIfAttributeof(ctx, "Arch.System.QueryAttribute")
        ).Where(static m => m is not null)!; // filter out attributed methods that we don't care about

        // Combine the selected enums with the `Compilation`
        IncrementalValueProvider<(Compilation, ImmutableArray<MethodDeclarationSyntax>)> compilationAndMethods = context.CompilationProvider.Combine(methodDeclarations.WithComparer(Comparer.Instance).Collect());
        context.RegisterSourceOutput(compilationAndMethods, static (spc, source) =>
        {
            Generate(source.Item1, source.Item2, spc);
        });
    }

    /// <summary>
    ///     Adds a <see cref="IMethodSymbol"/> to its class.
    ///     Stores them in <see cref="_classToMethods"/>.
    /// </summary>
    /// <param name="methodSymbol">The <see cref="IMethodSymbol"/> which will be added/mapped to its class.</param>
    private static void AddMethodToClass(IMethodSymbol methodSymbol)
    {
        if (!_classToMethods.TryGetValue(methodSymbol.ContainingSymbol, out List<IMethodSymbol>? list))
        {
            list = new List<IMethodSymbol>();
            _classToMethods[methodSymbol.ContainingSymbol] = list;
        }
        list.Add(methodSymbol);
    }

    /// <summary>
    ///     Returns a <see cref="MethodDeclarationSyntax"/> if its annocated with a attribute of <see cref="name"/>.
    /// </summary>
    /// <param name="context">Its <see cref="GeneratorSyntaxContext"/>.</param>
    /// <param name="name">The attributes name.</param>
    /// <returns></returns>
    private static MethodDeclarationSyntax? GetMethodSymbolIfAttributeof(GeneratorSyntaxContext context, string name)
    {
        // we know the node is a EnumDeclarationSyntax thanks to IsSyntaxTargetForGeneration
        MethodDeclarationSyntax enumDeclarationSyntax = (MethodDeclarationSyntax)context.Node;

        // loop through all the attributes on the method
        foreach (AttributeListSyntax attributeListSyntax in enumDeclarationSyntax.AttributeLists)
        {
            foreach (AttributeSyntax attributeSyntax in attributeListSyntax.Attributes)
            {
                if (ModelExtensions.GetSymbolInfo(context.SemanticModel, attributeSyntax).Symbol is not IMethodSymbol attributeSymbol) continue;

                INamedTypeSymbol attributeContainingTypeSymbol = attributeSymbol.ContainingType;
                string fullName = attributeContainingTypeSymbol.ToDisplayString();

                // Is the attribute the [EnumExtensions] attribute?
                if (fullName != name) continue;
                return enumDeclarationSyntax;
            }
        }

        // we didn't find the attribute we were looking for
        return null;
    }

    /// <summary>
    ///     Generates queries and partial classes for the found marked methods.
    /// </summary>
    /// <param name="compilation">The <see cref="Compilation"/>.</param>
    /// <param name="methods">The <see cref="ImmutableArray{MethodDeclarationSyntax}"/> array, the methods which we will generate queries and classes for.</param>
    /// <param name="context">The <see cref="SourceProductionContext"/>.</param>
    private static void Generate(Compilation compilation, ImmutableArray<MethodDeclarationSyntax> methods, SourceProductionContext context)
    {
        if (methods.IsDefaultOrEmpty) return;

        // Generate Query methods and map them to their classes
        _classToMethods = new(512);
        foreach (MethodDeclarationSyntax methodSyntax in methods)
        {
            IMethodSymbol? methodSymbol = null;
            try
            {
                SemanticModel semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                methodSymbol = ModelExtensions.GetDeclaredSymbol(semanticModel, methodSyntax) as IMethodSymbol;
                //context.AddSource(methodSymbol.ToString(), "lol");
            }
            catch
            {
                //not update,skip
                continue;
            }

            try
            {
                AddMethodToClass(methodSymbol);
            }
            catch (Exception e)
            {
                throw new Exception(methodSymbol?.ToString());
            }

            StringBuilder sb = new();
            StringBuilder method = sb.AppendQueryMethod(methodSymbol);
            string fileName = methodSymbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat).Replace('<', '{').Replace('>', '}');
            try
            {
                //throw new Exception(method.ToString());
                context.AddSource($"{fileName}.g.cs", CSharpSyntaxTree.ParseText(method.ToString()).GetRoot().NormalizeWhitespace().ToFullString());
            }
            catch
            {
                throw new Exception(method.ToString());
            }
        }

        // Creating class that calls the created methods after another.
        foreach (KeyValuePair<ISymbol, List<IMethodSymbol>> classToMethod in _classToMethods)
        {
            string template = new StringBuilder().AppendBaseSystem(classToMethod).ToString();
            if (string.IsNullOrEmpty(template)) continue;

            try
            {
                string fileName = (classToMethod.Key as INamedTypeSymbol).ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat).Replace('<', '{').Replace('>', '}');
                context.AddSource($"{fileName}.g.cs",
                    CSharpSyntaxTree.ParseText(template).GetRoot().NormalizeWhitespace().ToFullString());
            }
            catch
            {
                throw new Exception(template);
            }
        }
    }

    /// <summary>
    /// Compares <see cref="MethodDeclarationSyntax"/>s to remove duplicates. 
    /// </summary>
    class Comparer : IEqualityComparer<MethodDeclarationSyntax>
    {
        public static readonly Comparer Instance = new();

        public bool Equals(MethodDeclarationSyntax x, MethodDeclarationSyntax y)
        {
            return x.Equals(y);
        }

        public int GetHashCode(MethodDeclarationSyntax obj)
        {
            return obj.GetHashCode();
        }
    }
}