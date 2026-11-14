using System.ComponentModel.DataAnnotations;

namespace Ecommerce.BuildingBlocks.Configuration;

public sealed class StoreOptions
{
    public const string SectionName = "Store";

    [Required]
    public string Locale { get; init; } = "fa-IR";

    [Required]
    public string TimeZone { get; init; } = "Asia/Tehran";
}
