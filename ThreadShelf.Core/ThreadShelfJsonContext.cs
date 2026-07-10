using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThreadShelf;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(ThreadShelfDocument))]
[JsonSerializable(typeof(AppPreferences))]
internal sealed partial class ThreadShelfJsonContext : JsonSerializerContext;
