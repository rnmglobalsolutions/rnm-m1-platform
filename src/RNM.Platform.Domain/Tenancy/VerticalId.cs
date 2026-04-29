namespace RNM.Platform.Domain.Tenancy;

public readonly record struct VerticalId(string Value)
{
    public override string ToString() => Value;
}
