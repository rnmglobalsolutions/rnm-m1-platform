namespace RNM.Platform.Domain.Tenancy;

public readonly record struct TenantId(string Value)
{
    public override string ToString() => Value;
}
