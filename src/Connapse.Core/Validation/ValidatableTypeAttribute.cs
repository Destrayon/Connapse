namespace Microsoft.Extensions.Validation.Embedded;

// The validation source generator only finds request records in this library through this
// attribute. The package's own [ValidatableType] is experimental in .NET 10 (ASP0029); the Web
// and Razor SDKs generate this same internal copy, and a plain library declares it itself.
// Delete it once Connapse targets .NET 11, where the package attribute is stable.
[AttributeUsage(AttributeTargets.Class)]
internal sealed class ValidatableTypeAttribute : Attribute
{
}
