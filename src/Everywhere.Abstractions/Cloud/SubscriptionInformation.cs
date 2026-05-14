using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Serialization;

namespace Everywhere.Cloud;

public sealed partial class SubscriptionInformation : ObservableObject
{
    [ObservableProperty]
    [JsonPropertyName("plan")]
    [JsonConverter(typeof(JsonStringEnumConverter<SubscriptionPlan>))]
    public partial SubscriptionPlan Plan { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanCreditsUsageRatio))]
    [JsonPropertyName("planCredits")]
    public partial long PlanCredits { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanCreditsUsageRatio))]
    [JsonPropertyName("totalPlanCredits")]
    public partial long TotalPlanCredits { get; set; }

    [JsonIgnore]
    public double PlanCreditsUsageRatio => TotalPlanCredits > 0 ? (double)PlanCredits / TotalPlanCredits : 0;

    [ObservableProperty]
    [JsonPropertyName("bonusCredits")]
    public partial long BonusCredits { get; set; }

    [ObservableProperty]
    [JsonPropertyName("periodStart")]
    [JsonConverter(typeof(JsonISOStringDateTimeOffsetFormatter))]
    public partial DateTimeOffset? PeriodStart { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpiredDaysAgo))]
    [JsonPropertyName("periodEnd")]
    [JsonConverter(typeof(JsonISOStringDateTimeOffsetFormatter))]
    public partial DateTimeOffset? PeriodEnd { get; set; }

    [JsonIgnore]
    public int ExpiredDaysAgo
    {
        get
        {
            if (!PeriodEnd.HasValue) return 0;
            var expiredDays = (DateTimeOffset.UtcNow - PeriodEnd.Value).TotalDays;
            return expiredDays > 0 ? (int)expiredDays : 0;
        }
    }

    [ObservableProperty]
    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter<SubscriptionStatus>))]
    public partial SubscriptionStatus? Status { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FreeWebSearchUsageRatio))]
    [JsonPropertyName("freeWebSearchCount")]
    public partial int UsedFreeWebSearchCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FreeWebSearchUsageRatio))]
    [JsonPropertyName("totalFreeWebSearchCount")]
    public partial int TotalFreeWebSearchCount { get; set; }

    [JsonIgnore]
    public double FreeWebSearchUsageRatio => TotalFreeWebSearchCount > 0 ? (double)UsedFreeWebSearchCount / TotalFreeWebSearchCount : 0;
}

[JsonSerializable(typeof(ApiPayload<SubscriptionInformation>))]
public sealed partial class SubscriptionInformationJsonSerializerContext : JsonSerializerContext;