using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RouteDeck.Core.Models;

namespace RouteDeck.Core.Serialization;

/// <summary>Shared JSON settings for config files and the pipe protocol.</summary>
public static class RouteDeckJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new IPAddressJsonConverter());
        options.Converters.Add(new IpPrefixJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public sealed class IPAddressJsonConverter : JsonConverter<IPAddress>
{
    public override IPAddress? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : IPAddress.Parse(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

public sealed class IpPrefixJsonConverter : JsonConverter<IpPrefix>
{
    public override IpPrefix Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        IpPrefix.Parse(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, IpPrefix value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
