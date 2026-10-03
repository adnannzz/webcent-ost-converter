using OstConverter.Core.Conversion;

namespace OstConverterFree.Accounts;

/// <summary>What the UI asks for what the user may do; the conversion pipeline only sees the <see cref="Entitlement"/>.</summary>
public interface IEntitlementProvider
{
    Entitlement Current { get; }
    string PlanName { get; }
    string? AccountEmail { get; }
    event EventHandler? Changed;
}
