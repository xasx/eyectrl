namespace EyeCtrl.Contracts;

using System.Text.Json;

public static class WireJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
