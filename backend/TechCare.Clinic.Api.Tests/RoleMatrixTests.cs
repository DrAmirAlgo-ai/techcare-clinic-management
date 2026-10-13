using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechCare.Clinic.Api.Features.Auth;
using TechCare.Clinic.Api.Infrastructure;
using TechCare.Clinic.Api.Tests.Infrastructure;

namespace TechCare.Clinic.Api.Tests;

/// <summary>
/// PLAN_ADDENDUM.md 0.3 — the role matrix as tests. Three theories walk <see cref="RoleMatrix.Rows"/>: every role a
/// row allows must get through, every other role must be refused with 403, and a caller with no token must be
/// challenged with 401. The fourth test reads the controllers themselves, so the table cannot quietly stop
/// covering an endpoint (or keep asserting on one that was renamed or removed).
///
/// All three theories run inside the shared API collection, one case at a time, against clinic A. Each case builds
/// the rows it needs through <see cref="RoleMatrixContext"/>, so no case reads or changes a fixture another test
/// depends on — except for the deliberate shape of the table: a single row may only be answered once per run.
/// </summary>
[Collection(ApiCollection.Name)]
public class RoleMatrixTests : IAsyncLifetime
{
    private readonly ApiFactory _factory;

    public RoleMatrixTests(ApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// The change-password row signs in as a throwaway account of each role ("matrixpw…" users). They are deleted
    /// here so no other test — especially the owner panel's "exactly one SuperAdmin" and the clinic staff lists —
    /// ever sees them.
    /// </summary>
    public async Task DisposeAsync()
    {
        await _factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<TechCare.Clinic.Api.Data.ClinicDbContext>();
            var throwaways = await db.Users.IgnoreQueryFilters().Where(u => u.Username.StartsWith("matrixpw")).ToListAsync();
            db.Users.RemoveRange(throwaways);
            await db.SaveChangesAsync();
            return 0;
        });
    }

    [Theory]
    [MemberData(nameof(RoleMatrix.AllowedCases), MemberType = typeof(RoleMatrix))]
    public async Task An_allowed_role_gets_through(int index, string role)
    {
        var row = RoleMatrix.Row(index);
        var context = await RoleMatrixContext.PreparedAsync(_factory);
        // Most rows sign in as the fixture account of that role; a row with its own actor (change-password) signs
        // in as a throwaway account of the same role instead, so it never rewrites a fixture account.
        using var client = row.Actor != null
            ? _factory.CreateAuthenticatedClient(await row.Actor(context, role))
            : await AuthorisedAsync(role);
        using var request = await row.Request(context);
        using var response = await client.SendAsync(request);

        if (!row.Accepts(response.StatusCode))
            Assert.Fail($"{role} is allowed on {row.Route} but got {(int)response.StatusCode} {response.StatusCode}. {await DescribeAsync(response)}");
    }

    [Theory]
    [MemberData(nameof(RoleMatrix.DeniedCases), MemberType = typeof(RoleMatrix))]
    public async Task A_role_the_row_leaves_out_is_refused(int index, string role)
    {
        var row = RoleMatrix.Row(index);
        var context = await RoleMatrixContext.AuthorizationOnlyAsync(_factory);
        using var client = await AuthorisedAsync(role);
        using var request = await row.Request(context);
        using var response = await client.SendAsync(request);

        if (response.StatusCode != HttpStatusCode.Forbidden)
            Assert.Fail($"{role} must be refused on {row.Route} but got {(int)response.StatusCode} {response.StatusCode}. {await DescribeAsync(response)}");
    }

    [Theory]
    [MemberData(nameof(RoleMatrix.EveryRow), MemberType = typeof(RoleMatrix))]
    public async Task A_caller_without_a_token_is_challenged(int index)
    {
        var row = RoleMatrix.Row(index);
        var context = await RoleMatrixContext.AuthorizationOnlyAsync(_factory);
        using var client = _factory.CreateClient();
        using var request = await row.Request(context);
        using var response = await client.SendAsync(request);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
            Assert.Fail($"An anonymous caller must be challenged on {row.Route} but got {(int)response.StatusCode} {response.StatusCode}. {await DescribeAsync(response)}");
    }

    /// <summary>
    /// A client signed in as <paramref name="role"/>. The owner account (PLAN.md 1.1) belongs to no clinic, so it is
    /// registered under <see cref="TestEnvironment.OwnerSlug"/> rather than under a clinic's slug.
    /// </summary>
    private async Task<HttpClient> AuthorisedAsync(string role)
    {
        var slug = role == TestData.SuperAdminRole ? TestEnvironment.OwnerSlug : TestEnvironment.ClinicASlug;
        var token = await _factory.TokenAsync(slug, role);
        return _factory.CreateAuthenticatedClient(token);
    }

    /// <summary>The response body, trimmed: it is usually the one line that explains a status nobody expected.</summary>
    private static async Task<string> DescribeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(body) ? "No body." : body.Length > 500 ? body[..500] : body;
    }

    /// <summary>
    /// Compares the table with the API in both directions, so neither can drift: an action added to a controller
    /// without a row fails here, a row whose route was renamed or deleted fails here, and a row with the wrong
    /// roles fails here too — the roles are read from the same <c>[Authorize]</c> attributes the framework uses.
    /// Public endpoints are left out on purpose: <c>[AllowAnonymous]</c> routes are the isolation suite's business.
    /// </summary>
    [Fact]
    public void The_table_covers_every_protected_action_with_the_roles_the_attributes_give_it()
    {
        var actions = ProtectedActions();
        var table = RoleMatrix.Rows.ToDictionary(row => row.Route, row => string.Join(",", row.AllowedRoles), StringComparer.Ordinal);

        var missing = actions.Keys.Except(table.Keys, StringComparer.Ordinal).OrderBy(route => route, StringComparer.Ordinal).ToList();
        var stale = table.Keys.Except(actions.Keys, StringComparer.Ordinal).OrderBy(route => route, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0, $"Protected endpoints with no RoleMatrix.Rows entry: {string.Join(" | ", missing)}.");
        Assert.True(stale.Count == 0, $"RoleMatrix.Rows entries matching no endpoint any more: {string.Join(" | ", stale)}.");

        foreach (var (route, roles) in actions)
        {
            Assert.True(
                roles == table[route],
                $"{route}: the controller allows [{roles}] but the table says [{table[route]}].");
        }
    }

    /// <summary>
    /// Every action the framework protects, as "VERB api/route/{id:int}" mapped to its allowed roles. Roles come
    /// from all the <c>[Authorize]</c> attributes in play — class and action — intersected, exactly the way the
    /// framework combines them, so an action that narrows its controller's roles is read the same way it runs.
    /// </summary>
    private static Dictionary<string, string> ProtectedActions()
    {
        var actions = new Dictionary<string, string>(StringComparer.Ordinal);

        var controllers = typeof(AuthController).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type));

        foreach (var controller in controllers)
        {
            var prefix = controller.GetCustomAttribute<RouteAttribute>(inherit: true)?.Template?.Trim('/');
            if (string.IsNullOrEmpty(prefix))
                throw new InvalidOperationException($"{controller.Name} has no [Route] attribute, so this test cannot name its actions.");
            if (controller.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) != null) continue;

            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.GetCustomAttribute<HttpMethodAttribute>(inherit: true) is not HttpMethodAttribute http) continue;
                if (method.GetCustomAttribute<NonActionAttribute>(inherit: true) != null) continue;
                if (method.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) != null) continue;

                var authorize = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                    .Concat(method.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
                    .ToList();
                if (authorize.Count == 0)
                    throw new InvalidOperationException($"{controller.Name}.{method.Name} carries neither [Authorize] nor [AllowAnonymous].");

                var allowed = TestData.EveryRole.AsEnumerable();
                foreach (var attribute in authorize)
                {
                    // PLAN.md 1.4: a policy names a permission key, which stands for exactly the roles that carry
                    // the key by default (Permissions.DefaultRoles) — how the fixture accounts, which hold no
                    // explicit set, are measured against the endpoint.
                    var roles = attribute.Roles != null
                        ? TestData.EveryRole.Where(role => attribute.Roles.Split(',').Contains(role)).ToArray()
                        : attribute.Policy != null
                            ? TestData.EveryRole
                                .Where(role => Permissions.DefaultRoles(attribute.Policy).Contains(role))
                                .ToArray()
                            : TestData.EveryRole;
                    allowed = allowed.Intersect(roles);
                }

                var suffix = method.GetCustomAttribute<RouteAttribute>(inherit: true)?.Template?.Trim('/') ?? http.Template?.Trim('/');
                var template = string.IsNullOrEmpty(suffix) ? prefix : $"{prefix}/{suffix}";
                foreach (var verb in http.HttpMethods)
                {
                    var route = $"{verb} {template}";
                    if (!actions.TryAdd(route, string.Join(",", allowed)))
                        throw new InvalidOperationException($"Two actions answer {route}.");
                }
            }
        }

        Assert.NotEmpty(actions);
        return actions;
    }
}

