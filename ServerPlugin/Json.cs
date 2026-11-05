using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace ServerPlugin;

// The JSON of the companion operations and of the index, loss log and
// restore records: camelCase names, no nulls, UTC times
public static class Json
{
    public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
    };

    public static string Write(object value) => JsonConvert.SerializeObject(value, Settings);

    public static T Read<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);

    // Atomic for readers on other nodes: a temporary file renamed into place
    public static void WriteFile(string path, object value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, Write(value));
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    // Null when the file is gone or not readable, as files of other nodes
    // may be deleted by retention at any time
    public static T ReadFile<T>(string path)
        where T : class
    {
        try
        {
            return Read<T>(File.ReadAllText(path));
        }
        catch (Exception ex)
            when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public sealed class Position
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}
