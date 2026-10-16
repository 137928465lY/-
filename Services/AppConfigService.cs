using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace 叶片缺陷审核.Services;

public class AppConfigService 
{
    private static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    private readonly IConfiguration _configuration;

    public AppConfigService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public IReadOnlyList<string> DomainFields =>
        _configuration.GetSection("DomainFields").GetChildren()
            .Select(c => c.Value ?? "")
            .Where(v => v.Length > 0)
            .ToList();

    public string? this[string key] => _configuration[key];

    public void SetDomainFields(IReadOnlyList<string> fields)
    {
        var root = File.Exists(ConfigPath)
            ? JsonNode.Parse(File.ReadAllText(ConfigPath)) as JsonObject ?? new JsonObject()
            : new JsonObject();

        root["DomainFields"] = new JsonArray(fields.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());
        File.WriteAllText(ConfigPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
