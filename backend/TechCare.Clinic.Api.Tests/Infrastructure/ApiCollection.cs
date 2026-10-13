using Xunit;

namespace TechCare.Clinic.Api.Tests.Infrastructure;

/// <summary>
/// Every test class joins this collection, so the whole assembly shares one <see cref="ApiFactory"/> — one host,
/// one in-memory database — and runs the classes one after another. Sharing is deliberate: the factory has to
/// write process-wide environment variables before the host starts, which cannot be done twice.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "techcare-api";
}
