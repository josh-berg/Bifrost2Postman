using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public static class SchemaGenerator
{
    public static Dictionary<string, List<RequestSchema>> Generate(string servicesRootPath, string dllFolderPath)
    {
        try
        {
            var dllFiles = Directory.GetFiles(dllFolderPath, "*.dll");
            if (dllFiles.Length == 0)
            {
                throw new FileNotFoundException("No DLL files found in the bin folder. Ensure the project is correctly built in the service you are running against.");
            }

            // Grabs all DLLs from target, needed to load up so roslyn can get types
            var dynamicClientReferences = dllFiles.Select(dll => MetadataReference.CreateFromFile(dll)).ToList();

            var csFiles = Directory.GetFiles(servicesRootPath, "*.cs", SearchOption.AllDirectories);
            var trees = csFiles.Select(file =>
            {
                var syntaxTree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
                return new { FilePath = file, SyntaxTree = syntaxTree };
            }).ToList();

            // Load up all types
            var compilation = CSharpCompilation.Create("BifrostGen", [.. trees.Select(x => x.SyntaxTree)])
                .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
                .AddReferences(MetadataReference.CreateFromFile(typeof(Console).Assembly.Location))
                .AddReferences(dynamicClientReferences);

            // Grouping endpoints by their service
            var endpointsByFile = new Dictionary<string, List<RequestSchema>>();

            foreach (var tree in trees)
            {
                try
                {
                    var semanticModel = compilation.GetSemanticModel(tree.SyntaxTree);
                    var root = tree.SyntaxTree.GetCompilationUnitRoot();

                    var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
                    var interfaces = root.DescendantNodes().OfType<InterfaceDeclarationSyntax>();
                    var allTypes = classes.Cast<SyntaxNode>().Concat(interfaces.Cast<SyntaxNode>());

                    foreach (var type in allTypes)
                    {
                        // Grabs class name (eg. ITicketedEventService)
                        var className = type is ClassDeclarationSyntax classDecl
                            ? classDecl.Identifier.ToString()
                            : (type as InterfaceDeclarationSyntax)?.Identifier.ToString() ?? "UnknownService";

                        foreach (var method in type.DescendantNodes().OfType<MethodDeclarationSyntax>())
                        {
                            // Grabs bifrost route (eg. /bifrost/ticketed-event-service/get-ticketed-event)
                            var bifrostAttr = method.AttributeLists
                                .SelectMany(a => a.Attributes)
                                .FirstOrDefault(attr => attr.Name.ToString().Contains("BifrostPath"));

                            if (bifrostAttr == null) continue;

                            var pathExpr = bifrostAttr.ArgumentList?.Arguments.First().ToString().Trim('"');
                            if (pathExpr == null) continue;

                            // Grabs method name (eg. GetTicketedEvent)
                            var methodName = method.Identifier.ToString();
                            // Grabs parameter
                            var paramSymbol = method.ParameterList.Parameters.FirstOrDefault();
                            // Grabs parameter type
                            var paramTypeSyntax = paramSymbol?.Type;
                            var paramTypeInfo = paramTypeSyntax != null ? semanticModel.GetTypeInfo(paramTypeSyntax) : default;
                            var paramTypeName = paramTypeInfo.Type?.ToString() ?? "Unknown";

                            // Generates the sample JSON
                            var sampleJson = GenerateSampleJsonForType(paramTypeInfo.Type);

                            if (!endpointsByFile.ContainsKey(className))
                            {
                                endpointsByFile[className] = new List<RequestSchema>();
                            }

                            endpointsByFile[className].Add(new RequestSchema
                            {
                                MethodName = methodName,
                                Endpoint = pathExpr,
                                SampleJson = sampleJson
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"Error processing file {tree.FilePath}: {ex.Message}", ex);
                }
            }
            return endpointsByFile;
        }
        catch (Exception ex)
        {
            throw new Exception($"Error generating schema: {ex.Message}", ex);
        }
    }

    static string GenerateSampleJsonForType(ITypeSymbol? typeSymbol)
    {
        try
        {
            // Stack is needed to keep track of things we've referenced to not get stuck in a loop
            var path = new Stack<ITypeSymbol>();
            var sample = GenerateSampleForType(typeSymbol, path);
            return JsonSerializer.Serialize(sample, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error generating sample JSON for type: {ex.Message}");
            return "{}";
        }
    }

    static object? GenerateSampleForType(ITypeSymbol? typeSymbol, Stack<ITypeSymbol> path)
    {
        if (typeSymbol == null) return null;

        // We don't want all the nullable properties (HasValue and such) in postman, so we grab the underlying type (eg. int? -> int) if nullable
        if (IsNullable(typeSymbol, out var underlyingType))
        {
            typeSymbol = underlyingType;
        }

        if (typeSymbol == null) return null;

        // Checks if simple type
        if (IsSimple(typeSymbol))
        {
            return GetSampleValue(typeSymbol);
        }

        // Checks if collection, if so return a list with one sample value of list type
        if (IsCollection(typeSymbol, out var elementType))
        {
            return new[] { GenerateSampleForType(elementType, path) };
        }

        // Checks if circular reference, if so return a message
        if (path.Contains(typeSymbol, SymbolEqualityComparer.Default))
        {
            return $"!!CircularReference {typeSymbol.Name}!!>";
        }

        path.Push(typeSymbol);

        // Grab all public properties of the type
        var props = typeSymbol?.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic) ?? Enumerable.Empty<IPropertySymbol>();

        var dict = new Dictionary<string, object?>();

        // Loop through all properties and generate sample values for them
        foreach (var prop in props)
        {
            dict[prop.Name] = GenerateSampleForType(prop.Type, path);
        }

        if (typeSymbol != null)
        {
            path.Pop();
        }
        return dict;
    }

    // Checks if type is a simple type (eg. int, string, etc)
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

    // Magic code that checks if type is nullable (eg. int?)
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

    // Checks if type is a collection (eg. IEnumerable<T>, List<T>, etc)
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

    // Gets a sample value for the type (eg. int -> 1, string -> "", etc)
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