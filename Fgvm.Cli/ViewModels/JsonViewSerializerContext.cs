using System.Text.Json.Serialization;
using Fgvm.Cli.Command;

namespace Fgvm.Cli.ViewModels;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ListView))]
[JsonSerializable(typeof(List<ListView>))]
[JsonSerializable(typeof(RemoteReleaseView))]
[JsonSerializable(typeof(List<RemoteReleaseView>))]
[JsonSerializable(typeof(LogEntryView))]
[JsonSerializable(typeof(List<LogEntryView>))]
internal partial class JsonViewSerializerContext : JsonSerializerContext;
