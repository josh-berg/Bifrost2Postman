using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using System.Text.Json;

namespace PostmanGenerator;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: PostmanGenerator <path-to-client-root>");
            return;
        }

        var clientRootFolderPath = args[0];

        var servicesFolderPath = Path.Combine(clientRootFolderPath, "Services");
        var binFolderPath = Path.Combine(clientRootFolderPath, "bin", "Debug", "netstandard2.0");
        var dllFiles = Directory.GetFiles(binFolderPath, "*.dll");
        var dynamicClientReferences = dllFiles.Select(dll => MetadataReference.CreateFromFile(dll)).ToList();

        var csFiles = Directory.GetFiles(clientRootFolderPath, "*.cs", SearchOption.AllDirectories);
        var trees = csFiles.Select(file =>
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
            return new { FilePath = file, SyntaxTree = syntaxTree };
        }).ToList();

        var compilation = CSharpCompilation.Create("PostmanGen", trees.Select(x => x.SyntaxTree).ToList())
            .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddReferences(MetadataReference.CreateFromFile(typeof(Console).Assembly.Location))
            .AddReferences(dynamicClientReferences);

        var postmanItemsByFile = new Dictionary<string, List<object>>();

        foreach (var tree in trees)
        {
            var semanticModel = compilation.GetSemanticModel(tree.SyntaxTree);
            var root = tree.SyntaxTree.GetCompilationUnitRoot();

            var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
            var interfaces = root.DescendantNodes().OfType<InterfaceDeclarationSyntax>();
            var allTypes = classes.Cast<SyntaxNode>().Concat(interfaces.Cast<SyntaxNode>());

            foreach (var type in allTypes)
            {
                var className = type is ClassDeclarationSyntax classDecl
                    ? classDecl.Identifier.ToString()
                    : (type as InterfaceDeclarationSyntax)?.Identifier.ToString() ?? "UnknownService";

                foreach (var method in type.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    var bifrostAttr = method.AttributeLists
                        .SelectMany(a => a.Attributes)
                        .FirstOrDefault(attr => attr.Name.ToString().Contains("BifrostPath"));

                    if (bifrostAttr == null) continue;

                    var pathExpr = bifrostAttr.ArgumentList?.Arguments.First().ToString().Trim('"');
                    if (pathExpr == null) continue;

                    var methodName = method.Identifier.ToString();
                    var paramSymbol = method.ParameterList.Parameters.FirstOrDefault();
                    var paramTypeSyntax = paramSymbol?.Type;
                    var paramTypeInfo = paramTypeSyntax != null ? semanticModel.GetTypeInfo(paramTypeSyntax) : default;
                    var paramTypeName = paramTypeInfo.Type?.ToString() ?? "Unknown";

                    var sampleJson = GenerateSampleJsonForType(paramTypeInfo.Type);

                    if (!postmanItemsByFile.ContainsKey(className))
                    {
                        postmanItemsByFile[className] = new List<object>();
                    }

                    postmanItemsByFile[className].Add(new
                    {
                        name = methodName,
                        request = new
                        {
                            method = "POST",
                            header = new List<object>
                            {
                                new { key = "Content-Type", value = "application/json" }
                            },
                            url = new
                            {
                                raw = pathExpr,
                                host = new List<string> { "{{Hostname}}:{{Port}}" },
                                path = pathExpr.Split('/')
                            },
                            body = new
                            {
                                mode = "raw",
                                raw = sampleJson
                            }
                        }
                    });
                }
            }
        }

        var postmanItems = postmanItemsByFile.Select(kvp => new
        {
            name = kvp.Key,
            item = kvp.Value
        }).ToList();

        var folderName = new DirectoryInfo(clientRootFolderPath).Name;
        string serviceName = "Service";
        if (folderName.StartsWith("Hudl.") && folderName.EndsWith(".Client"))
        {
            serviceName = folderName.Substring("Hudl.".Length, folderName.Length - "Hudl.".Length - ".Client".Length);
        }

        var collection = new
        {
            info = new
            {
                name = $"Hudl.{serviceName} Bifrost Endpoints",
                schema = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
            },
            item = postmanItems
        };

        var json = JsonSerializer.Serialize(collection, new JsonSerializerOptions { WriteIndented = true });
        var outputFileName = $"hudl_{serviceName.ToLowerInvariant()}_postman.json";
        File.WriteAllText(outputFileName, json);
        Console.WriteLine($"Postman collection generated: {outputFileName}");
    }

    static string GenerateSampleJsonForType(ITypeSymbol? typeSymbol)
    {
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var sample = GenerateSampleForType(typeSymbol, visited);
        return JsonSerializer.Serialize(sample, new JsonSerializerOptions { WriteIndented = true });
    }

    static object? GenerateSampleForType(ITypeSymbol? typeSymbol, HashSet<ITypeSymbol> visited)
    {
        if (typeSymbol == null) return null;

        if (IsNullable(typeSymbol, out var underlyingType))
        {
            typeSymbol = underlyingType;
        }

        if (typeSymbol != null && IsSimple(typeSymbol))
        {
            return GetSampleValue(typeSymbol);
        }

        if (typeSymbol != null && visited.Contains(typeSymbol))
        {
            return $"<circular reference to {typeSymbol.Name}>";
        }

        if (typeSymbol != null)
        {
            visited.Add(typeSymbol);
        }

        if (typeSymbol != null && IsCollection(typeSymbol, out var elementType))
        {
            return new[] { GenerateSampleForType(elementType, visited) };
        }

        var props = typeSymbol?.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic) ?? Enumerable.Empty<IPropertySymbol>();

        var dict = new Dictionary<string, object?>();

        foreach (var prop in props)
        {
            dict[prop.Name] = GenerateSampleForType(prop.Type, visited);
        }

        if (typeSymbol != null)
        {
            visited.Remove(typeSymbol);
        }
        return dict;
    }

    static bool IsSimple(ITypeSymbol type)
    {
        return type.SpecialType switch
        {
            SpecialType.System_String => true,
            SpecialType.System_Int32 => true,
            SpecialType.System_Int64 => true,
            SpecialType.System_Boolean => true,
            SpecialType.System_DateTime => true,
            SpecialType.System_Double => true,
            SpecialType.System_Single => true,
            SpecialType.System_Decimal => true,
            SpecialType.System_Char => true,
            _ => type.TypeKind == TypeKind.Enum || type.ToString() == "System.Guid"
        };
    }

    static bool IsNullable(ITypeSymbol type, out ITypeSymbol? underlyingType)
    {
        if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && type is INamedTypeSymbol named && named.TypeArguments.Length == 1)
        {
            underlyingType = named.TypeArguments[0];
            return true;
        }
        underlyingType = null;
        return false;
    }

    static bool IsCollection(ITypeSymbol type, out ITypeSymbol? elementType)
    {
        elementType = null;

        if (type is IArrayTypeSymbol arrayType)
        {
            elementType = arrayType.ElementType;
            return true;
        }

        if (type is INamedTypeSymbol namedType &&
            namedType.AllInterfaces.Any(i => i.ToDisplayString().StartsWith("System.Collections.Generic.IEnumerable")))
        {
            elementType = namedType.TypeArguments.FirstOrDefault();
            return elementType != null;
        }

        return false;
    }

    static object GetSampleValue(ITypeSymbol type) => type.Name switch
    {
        "String" => "",
        "Int32" => 1,
        "Int64" => 1,
        "Boolean" => false,
        "DateTime" => "2025-01-01T01:00:00.000000Z",
        "Double" => 1.0,
        "Single" => 1.0f,
        "Decimal" => 1.0m,
        "Char" => 'A',
        _ when type.TypeKind == TypeKind.Enum => 0,
        _ => "sample"
    };
}
