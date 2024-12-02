using System.Text.Json.Serialization;
using ModTools.Commands.Banner;

namespace ModTools;

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(BannerConfigFile))]
[JsonSerializable(typeof(SummonBannerOptions))]
[JsonSerializable(typeof(IList<Banner>))]
[JsonSerializable(typeof(Charas))]
[JsonSerializable(typeof(Dragons))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class ModToolsSerializerContext : JsonSerializerContext { }
