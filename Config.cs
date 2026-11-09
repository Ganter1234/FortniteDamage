using CounterStrikeSharp.API.Core;
using System.Text.Json.Serialization;

namespace FortniteDamage;

public class FortniteDamageConfig : BasePluginConfig
{
    [JsonPropertyName("Distance")]
    public float Distance { get; set; } = 40.0f;

    [JsonPropertyName("Commands")]
    public List<string> Commands { get; set; } = new() { "css_hits", "css_fh", "css_fortnite_hits" };
}
