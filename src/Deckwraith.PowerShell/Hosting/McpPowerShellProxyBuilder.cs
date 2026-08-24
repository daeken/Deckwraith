using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deckwraith.Mcp;

namespace Deckwraith.PowerShell.Hosting;

internal static partial class McpPowerShellProxyBuilder
{
    public static string BuildModuleImport(
        string module,
        IReadOnlyList<McpCatalogEntry> tools)
    {
        var functions = string.Join(
            Environment.NewLine + Environment.NewLine,
            tools.Select(BuildFunction));
        var exports = string.Join(
            ", ",
            tools.Select(tool => Quote(tool.PowerShellCommand)));
        return $$"""
            $deckwraithMcpModule = New-Module -Name {{Quote(module)}} -ScriptBlock {
            {{Indent(functions, 4)}}

                Export-ModuleMember -Function {{exports}}
            }
            Import-Module $deckwraithMcpModule -Global -Force
            """;
    }

    private static string BuildFunction(McpCatalogEntry tool)
    {
        var properties = tool.InputSchema.ValueKind is JsonValueKind.Object &&
            tool.InputSchema.TryGetProperty("properties", out var propertiesElement) &&
            propertiesElement.ValueKind is JsonValueKind.Object
            ? propertiesElement.EnumerateObject().ToArray()
            : [];
        var required = tool.InputSchema.ValueKind is JsonValueKind.Object &&
            tool.InputSchema.TryGetProperty("required", out var requiredElement) &&
            requiredElement.ValueKind is JsonValueKind.Array
            ? requiredElement.EnumerateArray()
                .Where(item => item.ValueKind is JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var parameterNames = BuildParameterNames(properties.Select(property => property.Name));
        var parameters = properties.Select(property => BuildParameter(
            property.Name,
            parameterNames[property.Name],
            property.Value,
            required.Contains(property.Name))).ToArray();
        var help = BuildHelp(tool, properties, parameterNames);
        var assignments = properties.Select(property => $$"""
            if ($PSBoundParameters.ContainsKey({{Quote(parameterNames[property.Name])}})) {
                $__deckwraithArguments[{{Quote(property.Name)}}] = ${{parameterNames[property.Name]}}
            }
            """);
        return $$"""
            function {{tool.PowerShellCommand}} {
            {{Indent(help, 4)}}
                [CmdletBinding()]
                param(
            {{Indent(string.Join("," + Environment.NewLine, parameters), 8)}}
                )

                $__deckwraithArguments = [ordered]@{}
            {{Indent(string.Join(Environment.NewLine, assignments), 4)}}
                Invoke-DwMcpTool -Server {{Quote(tool.ServerId)}} -Tool {{Quote(tool.ToolName)}} -Arguments $__deckwraithArguments
            }
            """;
    }

    private static string BuildParameter(
        string jsonName,
        string parameterName,
        JsonElement schema,
        bool required)
    {
        var attributes = new List<string>
        {
            required
                ? "[Parameter(Mandatory=$true, ValueFromPipelineByPropertyName=$true)]"
                : "[Parameter(ValueFromPipelineByPropertyName=$true)]",
        };
        if (!StringComparer.OrdinalIgnoreCase.Equals(jsonName, parameterName))
        {
            attributes.Add($"[Alias({Quote(jsonName)})]");
        }

        if (schema.ValueKind is JsonValueKind.Object &&
            schema.TryGetProperty("enum", out var enumElement) &&
            enumElement.ValueKind is JsonValueKind.Array)
        {
            var values = enumElement.EnumerateArray()
                .Where(item => item.ValueKind is JsonValueKind.String)
                .Select(item => Quote(item.GetString()!))
                .ToArray();
            if (values.Length > 0)
            {
                attributes.Add($"[ValidateSet({string.Join(", ", values)})]");
            }
        }

        AddRangeValidation(attributes, schema);
        attributes.Add(PowerShellType(schema));
        return string.Join(Environment.NewLine, attributes) + $" ${parameterName}";
    }

    private static void AddRangeValidation(List<string> attributes, JsonElement schema)
    {
        if (schema.ValueKind is not JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("minimum", out var minimum) &&
            minimum.ValueKind is JsonValueKind.Number &&
            schema.TryGetProperty("maximum", out var maximum) &&
            maximum.ValueKind is JsonValueKind.Number)
        {
            attributes.Add($"[ValidateRange({minimum.GetRawText()}, {maximum.GetRawText()})]");
        }

        if (schema.TryGetProperty("minLength", out var minLength) &&
            minLength.TryGetInt32(out var minimumLength) &&
            schema.TryGetProperty("maxLength", out var maxLength) &&
            maxLength.TryGetInt32(out var maximumLength))
        {
            attributes.Add($"[ValidateLength({minimumLength}, {maximumLength})]");
        }

        if (schema.TryGetProperty("pattern", out var pattern) &&
            pattern.ValueKind is JsonValueKind.String)
        {
            attributes.Add($"[ValidatePattern({Quote(pattern.GetString()!)})]");
        }
    }

    private static string PowerShellType(JsonElement schema)
    {
        string? type = null;
        if (schema.ValueKind is JsonValueKind.Object &&
            schema.TryGetProperty("type", out var typeElement))
        {
            if (typeElement.ValueKind is JsonValueKind.String)
            {
                type = typeElement.GetString();
            }
            else if (typeElement.ValueKind is JsonValueKind.Array)
            {
                foreach (var candidate in typeElement.EnumerateArray())
                {
                    if (candidate.ValueKind is JsonValueKind.String &&
                        !StringComparer.Ordinal.Equals(candidate.GetString(), "null"))
                    {
                        type = candidate.GetString();
                        break;
                    }
                }
            }
        }

        if (StringComparer.Ordinal.Equals(type, "array"))
        {
            var itemType = schema.TryGetProperty("items", out var items)
                ? PowerShellType(items)
                : "[object]";
            return itemType.TrimEnd(']') + "[]]";
        }

        return type switch
        {
            "string" => "[string]",
            "integer" => "[long]",
            "number" => "[double]",
            "boolean" => "[bool]",
            "object" => "[hashtable]",
            _ => "[object]",
        };
    }

    private static string BuildHelp(
        McpCatalogEntry tool,
        IReadOnlyList<JsonProperty> properties,
        Dictionary<string, string> parameterNames)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<#");
        builder.AppendLine(".SYNOPSIS");
        builder.AppendLine(SanitizeHelp(tool.Description));
        builder.AppendLine(".DESCRIPTION");
        builder.Append("Assigned MCP tool ").Append(tool.QualifiedName)
            .AppendLine(". Execution is explicit and host-journaled.");
        foreach (var property in properties)
        {
            builder.Append(".PARAMETER ").AppendLine(parameterNames[property.Name]);
            var description = property.Value.ValueKind is JsonValueKind.Object &&
                property.Value.TryGetProperty("description", out var descriptionElement)
                ? descriptionElement.GetString()
                : null;
            builder.AppendLine(SanitizeHelp(description ?? $"MCP argument '{property.Name}'."));
        }

        builder.AppendLine(".NOTES");
        builder.Append("Use Get-DwToolSchema ").Append(tool.PowerShellCommand)
            .AppendLine(" to inspect the complete original JSON Schema.");
        builder.Append("Module: ").AppendLine(tool.PowerShellModule);
        builder.AppendLine("#>");
        return builder.ToString().TrimEnd();
    }

    private static Dictionary<string, string> BuildParameterNames(
        IEnumerable<string> properties)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties)
        {
            var normalized = string.Concat(TokenPattern().Matches(property)
                .Select(match => PascalCase(match.Value)));
            if (normalized.Length == 0 || char.IsDigit(normalized[0]))
            {
                normalized = "Value" + normalized;
            }

            var candidate = normalized;
            var suffix = 2;
            while (!used.Add(candidate))
            {
                candidate = normalized + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }

            result.Add(property, candidate);
        }

        return result;
    }

    private static string SanitizeHelp(string value) =>
        value.Replace("#>", "# >", StringComparison.Ordinal).Trim();

    private static string Indent(string value, int spaces)
    {
        var prefix = new string(' ', spaces);
        return string.Join(
            Environment.NewLine,
            value.ReplaceLineEndings("\n").Split('\n').Select(line => prefix + line));
    }

    private static string Quote(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string PascalCase(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

    [GeneratedRegex("[A-Za-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
