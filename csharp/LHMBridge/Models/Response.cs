using System.Text.Json.Serialization;

namespace LHMBridge.Models;

public class Response<T>
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    public static Response<T> Ok(T data) => new()
    {
        Success = true,
        Data = data,
        Error = null
    };

    public static Response<T> Fail(string error) => new()
    {
        Success = false,
        Data = default,
        Error = error
    };
}
