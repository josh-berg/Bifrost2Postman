namespace Bifrost2Postman;

class Program
{
    static void Main(string[] args)
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
        var dllFolderPath = Path.Combine(clientRootFolderPath, "bin", "Debug", "netstandard2.0");
        if (!Directory.Exists(dllFolderPath))
        {
            Console.WriteLine("Error: The project hasn't been built. Please open the service and build before running this tool.");
            return;
        }

        var options = Enum.GetValues(typeof(SchemaSpecification))
            .Cast<SchemaSpecification>()
            .Select(x => $"Generate {x} Schema")
            .ToList();
        var selected = ConsoleSelector.SelectFromList(options, "Select one or more output formats:");
        var selectedSchemas = selected.Select(x => (SchemaSpecification)Enum.Parse(typeof(SchemaSpecification), x.ToString())).ToList();
        Console.Clear();
        Console.WriteLine($"Generating Schemas");

        // Get service name
        var folderName = new DirectoryInfo(clientRootFolderPath).Name;
        string serviceName = "Service";
        if (folderName.StartsWith("Hudl.") && folderName.EndsWith(".Client"))
        {
            serviceName = folderName.Substring("Hudl.".Length, folderName.Length - "Hudl.".Length - ".Client".Length);
        }

        var requests = SchemaGenerator.Generate(servicesFolderPath, dllFolderPath);

        if (selectedSchemas.Contains(SchemaSpecification.Postman))
        {
            var output = PostmanSchemaTransformer.TransformSchemaToPostman(requests, serviceName);
            var outputFileName = $"hudl_{serviceName.ToLowerInvariant()}_postman_generated.json";
            File.WriteAllText(outputFileName, output);
        }

        if (selectedSchemas.Contains(SchemaSpecification.OpenAPI))
        {
            var output = OpenAPISchemaTransformer.TransformSchemaToOpenAPI(requests, serviceName);
            var outputFileName = $"hudl_{serviceName.ToLowerInvariant()}_openapi_generated.json";
            File.WriteAllText(outputFileName, output);
        }

        var schemaNames = string.Join(", ", selectedSchemas.Select(s => s.ToString()));
        Console.WriteLine($"Schema files generated successfully (Types: {schemaNames}).");
    }

}
