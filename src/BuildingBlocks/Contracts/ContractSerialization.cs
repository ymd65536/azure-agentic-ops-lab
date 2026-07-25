using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureAgenticOps.Contracts;

/// <summary>
/// Provides the canonical <see cref="JsonSerializerOptions"/> used for all public contracts.
/// Serialization behavior is explicit so that JSON output stays stable across releases:
/// camelCase property names, string enum values, and omission of null values.
/// </summary>
public static class ContractSerialization
{
    /// <summary>Gets the canonical serializer options for contract JSON.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Serializes a contract to canonical JSON.</summary>
    /// <typeparam name="T">The contract type.</typeparam>
    /// <param name="value">The contract instance.</param>
    /// <returns>The canonical JSON representation.</returns>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Deserializes canonical contract JSON.</summary>
    /// <typeparam name="T">The contract type.</typeparam>
    /// <param name="json">The JSON text.</param>
    /// <returns>The deserialized contract.</returns>
    /// <exception cref="JsonException">The JSON is not valid for the contract.</exception>
    public static T Deserialize<T>(string json)
    {
        T? value = JsonSerializer.Deserialize<T>(json, Options);
        return value is null
            ? throw new JsonException($"JSON deserialized to null for contract type '{typeof(T).Name}'.")
            : value;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
