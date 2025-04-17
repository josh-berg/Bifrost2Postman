using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using System.Text.Json;

namespace Bifrost2Postman
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    Console.WriteLine("Usage: PostmanGenerator <path to Hudl.Service.Client project root>");
                    return;
                }

                // Ex. Hudl.Ticketing.Client
                var clientRootFolderPath = args[0];

                // Ex. Hudl.Ticketing.Client/Services
                var servicesFolderPath = Path.Combine(clientRootFolderPath, "Services");
                if (!Directory.Exists(servicesFolderPath))
                {
                    Console.WriteLine("Error: There is no 'Services' folder. Make sure you're in the Hudl.Service.Client folder root.");
                    return;
                }

                // TODO: Can we trigger a build from this tool?
                var binFolderPath = Path.Combine(clientRootFolderPath, "bin", "Debug", "netstandard2.0");
                if (!Directory.Exists(binFolderPath))
                {
                    Console.WriteLine("Error: The project hasn't been built. Please open the service and build before running this tool.");
                    return;
                }

                var dllFiles = Directory.GetFiles(binFolderPath, "*.dll");
                if (dllFiles.Length == 0)
                {
                    Console.WriteLine("Error: No DLL files found in the bin folder. Ensure the project is correctly built.");
                    return;
                }

                // Grabs all DLLs from target, needed to load up so roslyn can get types
                var dynamicClientReferences = dllFiles.Select(dll => MetadataReference.CreateFromFile(dll)).ToList();

                // Grabs all custom DLLs from custom_dlls folder
                var customDllsPath = Path.Combine(".", "custom_dlls");
                if (Directory.Exists(customDllsPath))
                {
                    var customDllFiles = Directory.GetFiles(customDllsPath, "*.dll");
                    dynamicClientReferences.AddRange(customDllFiles.Select(dll => MetadataReference.CreateFromFile(dll)));
                }

                var csFiles = Directory.GetFiles(clientRootFolderPath, "*.cs", SearchOption.AllDirectories);
                var trees = csFiles.Select(file =>
                {
                    var syntaxTree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
                    return new { FilePath = file, SyntaxTree = syntaxTree };
                }).ToList();

                // Load up all types
                var compilation = CSharpCompilation.Create("PostmanGen", trees.Select(x => x.SyntaxTree).ToList())
                    .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
                    .AddReferences(MetadataReference.CreateFromFile(typeof(Console).Assembly.Location))
                    .AddReferences(dynamicClientReferences);

                // Grouping endpoints by their service
                var postmanItemsByFile = new Dictionary<string, List<object>>();

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

                                if (!postmanItemsByFile.ContainsKey(className))
                                {
                                    postmanItemsByFile[className] = new List<object>();
                                }

                                // Adds postman json to the list
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
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error processing file {tree.FilePath}: {ex.Message}");
                    }
                }

                var postmanItems = postmanItemsByFile
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => new
                {
                    name = kvp.Key,
                    item = kvp.Value.Cast<dynamic>().OrderBy(entry => (string)entry.name).ToList<object>()
                }).ToList();

                // Builds postman folder
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
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected error: {ex.Message}");
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

            // Grab all public properties of type, include base types
            var props = GetAllPublicInstanceProperties(typeSymbol);

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

        private static Dictionary<string, IPropertySymbol>.ValueCollection GetAllPublicInstanceProperties(ITypeSymbol typeSymbol)
        {
            var typeHierarchy = new Stack<ITypeSymbol>();
            var current = typeSymbol;

            // Walk up the inheritance chain and store types in reverse order
            while (current != null && current.SpecialType == SpecialType.None)
            {
                typeHierarchy.Push(current);
                current = current.BaseType;
            }

            var properties = new Dictionary<string, IPropertySymbol>();

            while (typeHierarchy.Count > 0)
            {
                var type = typeHierarchy.Pop();
                foreach (var member in type.GetMembers().OfType<IPropertySymbol>())
                {
                    if (member.DeclaredAccessibility == Accessibility.Public && !member.IsStatic && !properties.ContainsKey(member.Name))
                    {
                        properties[member.Name] = member;
                    }
                }
            }

            return properties.Values;
        }


    }
}
