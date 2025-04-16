#pragma warning disable CS8618
using System.Text.Json;

public static class OpenAPISchemaTransformer
{
    public class OpenApiRequestBody
    {
        public Dictionary<string, OpenApiContent> Content { get; set; }
    }

    public class OpenApiContent
    {
        public OpenApiSchema Schema { get; set; }
        public object Example { get; set; }
    }

    public class OpenApiSchema
    {
        public string Type { get; set; }
        public Dictionary<string, OpenApiProperty> Properties { get; set; }
    }

    public class OpenApiProperty
    {
        public string Type { get; set; }
        public string Example { get; set; }
    }

    public class OpenApiResponse
    {
        public string Description { get; set; }
        public OpenApiContent Content { get; set; }
    }

    public static string TransformSchemaToOpenAPI(Dictionary<string, List<RequestSchema>> requestSchemasByFile, string serviceName)
    {
        var paths = new Dictionary<string, object>();

        // Build paths from the requestSchemas
        foreach (var kvp in requestSchemasByFile)
        {
            foreach (var request in kvp.Value)
            {
                var pathItem = new
                {
                    summary = request.MethodName,
                    description = $"Automatically generated endpoint for {request.MethodName}",
                    operationId = request.MethodName.ToLower(),
                    parameters = new List<object>
                {
                    new {
                        name = "hostname",
                        @in = "query",
                        description = "Hostname of the API",
                        required = true,
                        schema = new { type = "string" }
                    }
                },
                    requestBody = new OpenApiRequestBody
                    {
                        Content = new Dictionary<string, OpenApiContent>
                    {
                        {
                            "application/json", new OpenApiContent
                            {
                                Schema = new OpenApiSchema
                                {
                                    Type = "object",
                                    Properties = new Dictionary<string, OpenApiProperty>
                                    {
                                        { "sample", new OpenApiProperty { Type = "string", Example = "sample" } }
                                    }
                                },
                                Example = new { sample = request.SampleJson }
                            }
                        }
                    }
                    },
                    responses = new Dictionary<string, OpenApiResponse>
                {
                    {
                        "200", new OpenApiResponse
                        {
                            Description = "Successful operation",
                            Content = new OpenApiContent
                            {
                                Schema = new OpenApiSchema
                                {
                                    Type = "object"
                                }
                            }
                        }
                    }
                }
                };

                paths[request.Endpoint] = new
                {
                    post = pathItem
                };
            }
        }

        // Create the OpenAPI document
        var openApiDoc = new
        {
            openapi = "3.0.1",
            info = new
            {
                title = $"Hudl.{serviceName} API",
                description = $"API for {serviceName} services",
                version = "1.0.0"
            },
            paths = paths,
            components = new
            {
                schemas = new { }
            }
        };

        var json = JsonSerializer.Serialize(openApiDoc, new JsonSerializerOptions { WriteIndented = true });

        return json;
    }
}