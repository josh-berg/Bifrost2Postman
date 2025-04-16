using System.Text.Json;

public static class PostmanSchemaTransformer
{
    public static string TransformSchemaToPostman(Dictionary<string, List<RequestSchema>> requestSchemasByFile, string serviceName)
    {
        var postmanItems = requestSchemasByFile.Select(kvp => new
        {
            name = kvp.Key,
            item = kvp.Value.Select(request => new
            {
                name = request.MethodName,
                request = new
                {
                    method = "POST",
                    header = new List<object>
                                    {
                                        new { key = "Content-Type", value = "application/json" }
                                    },
                    url = new
                    {
                        raw = request.Endpoint,
                        host = new List<string> { "{{Hostname}}:{{Port}}" },
                        path = request.Endpoint.Split('/')
                    },
                    body = new
                    {
                        mode = "raw",
                        raw = request.SampleJson
                    }
                }
            })
        }).ToList();

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

        return json;
    }
}