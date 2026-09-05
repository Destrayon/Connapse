using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests;

/// <summary>
/// Connapse reads who may read what; it never decides it. These tests make that a build-time
/// property rather than a convention.
/// </summary>
/// <remarks>
/// Scans the compiled assemblies' type-reference tables, so a writer cannot come back through any
/// door — not a service, not a page, not a background job — without this failing. A grant is an
/// administrator's decision made in AWS; the one time Connapse could author one (#462) the only
/// grant it could write was a whole group on a whole bucket, and it was removed in #463.
/// <para>
/// Type references rather than a policy document, because the policy is only what Connapse
/// <i>asks</i> for. An over-broad role handed to it by an operator would let any SDK call through;
/// the guarantee has to be that no such call exists to make.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public class GrantMutationGuardTests
{
    /// <summary>Every assembly that ships in the web host.</summary>
    private static readonly string[] ShippedAssemblies =
    [
        "Connapse.Core", "Connapse.Storage", "Connapse.Identity", "Connapse.Ingestion",
        "Connapse.Search", "Connapse.Agents", "Connapse.Background", "Connapse.Web"
    ];

    /// <summary>
    /// SDK request types whose presence would mean Connapse can change a permission: every S3
    /// Access Grants mutation, and the Identity Store membership mutations that change who a
    /// group-held grant reaches.
    /// </summary>
    private static readonly string[] ForbiddenRequestTypes =
    [
        "Amazon.S3Control.Model.CreateAccessGrantRequest",
        "Amazon.S3Control.Model.DeleteAccessGrantRequest",
        "Amazon.S3Control.Model.CreateAccessGrantsLocationRequest",
        "Amazon.S3Control.Model.DeleteAccessGrantsLocationRequest",
        "Amazon.S3Control.Model.UpdateAccessGrantsLocationRequest",
        "Amazon.S3Control.Model.CreateAccessGrantsInstanceRequest",
        "Amazon.S3Control.Model.DeleteAccessGrantsInstanceRequest",
        "Amazon.S3Control.Model.PutAccessGrantsInstanceResourcePolicyRequest",
        "Amazon.S3Control.Model.DeleteAccessGrantsInstanceResourcePolicyRequest",
        "Amazon.S3Control.Model.AssociateAccessGrantsIdentityCenterRequest",
        "Amazon.S3Control.Model.DissociateAccessGrantsIdentityCenterRequest",
        "Amazon.S3Control.Model.TagResourceRequest",
        "Amazon.S3Control.Model.UntagResourceRequest",
        "Amazon.IdentityStore.Model.CreateGroupRequest",
        "Amazon.IdentityStore.Model.DeleteGroupRequest",
        "Amazon.IdentityStore.Model.CreateGroupMembershipRequest",
        "Amazon.IdentityStore.Model.DeleteGroupMembershipRequest",
        "Amazon.IdentityStore.Model.CreateUserRequest",
        "Amazon.IdentityStore.Model.DeleteUserRequest",
        "Amazon.IdentityStore.Model.UpdateUserRequest",
        "Amazon.IdentityStore.Model.UpdateGroupRequest"
    ];

    [Fact]
    public void NoShippedAssembly_ReferencesAnAwsCallThatChangesAPermission()
    {
        foreach (string assembly in ShippedAssemblies)
        {
            var referenced = TypeReferencesOf(assembly);

            referenced.Should().NotContain(ForbiddenRequestTypes,
                $"{assembly} must only be able to read permissions, never change them");
        }
    }

    [Fact]
    public void TheScanSeesSdkTypes_SoASilentPassIsNotAnEmptyScan()
    {
        // The read path really does call ListAccessGrants; if this stopped being visible the guard
        // above would pass on nothing, which is the one way it could fail open.
        TypeReferencesOf("Connapse.Storage")
            .Should().Contain("Amazon.S3Control.Model.ListAccessGrantsRequest");
    }

    /// <summary>Every external type the compiled assembly refers to, as <c>Namespace.Name</c>.</summary>
    private static IReadOnlyList<string> TypeReferencesOf(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);

        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();

        var names = new List<string>();
        foreach (var handle in metadata.TypeReferences)
        {
            var reference = metadata.GetTypeReference(handle);
            names.Add(metadata.GetString(reference.Namespace) + "." + metadata.GetString(reference.Name));
        }

        return names;
    }
}
