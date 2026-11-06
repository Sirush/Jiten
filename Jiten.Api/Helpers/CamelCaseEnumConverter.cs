using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiten.Api.Helpers;

public sealed class CamelCaseEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase)
    where TEnum : struct, Enum;
