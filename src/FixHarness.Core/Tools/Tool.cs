using System.Text.Json;
using FixHarness.Model;

namespace FixHarness.Tools;

/// <summary>A tool is a description for the model plus ordinary code the harness runs.</summary>
public sealed record Tool(ToolDefinition Definition, Func<JsonElement, CancellationToken, Task<string>> Execute);

public sealed class ToolRegistry(IEnumerable<Tool> tools)
{
    private readonly Dictionary<string, Tool> _byName = tools.ToDictionary(t => t.Definition.Name);

    public IReadOnlyList<ToolDefinition> Definitions => _byName.Values.Select(t => t.Definition).ToList();

    /// <summary>Runs a tool call. Errors become text for the model; they never crash the loop.</summary>
    public async Task<string> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
    {
        if (!_byName.TryGetValue(call.Name, out var tool))
        {
            return $"Unknown tool: \"{call.Name}\". Available tools: {string.Join(", ", _byName.Keys)}.";
        }

        try
        {
            using var arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            return await tool.Execute(arguments.RootElement, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return $"Error: {error.Message}";
        }
    }
}
