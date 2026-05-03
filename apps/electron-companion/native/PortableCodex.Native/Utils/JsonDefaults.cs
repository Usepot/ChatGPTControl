using System.Text.Json;
using System.Text.Json.Serialization;

namespace PortableCodex.Native.Utils;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Transport = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static readonly JsonSerializerOptions Storage = new(Transport)
    {
        WriteIndented = true,
    };
}
