using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Application.Approvals;
using CsCheck;
using FluentAssertions;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// How an approval request reads a call's arguments (CON-185): secret fields never survive redaction at any depth,
/// everything else does, and an identical call reads the same whatever its key order.
/// </summary>
[Trait("Category", "Unit")]
public class McpCallArgumentsTests
{
    private const string Marker = "SECRET-VALUE-";

    // Secret names in the casings and separators real request bodies use, and ordinary names that look close.
    private static readonly Gen<string> SecretName = Gen.OneOfConst(
        "Secret", "secret", "clientSecret", "client_secret", "Token", "token", "Passphrase", "Password",
        "ClientCertificatePassword", "PrivateKey", "PrivateKeyPem", "OpenAiApiKey", "x-api-key", "Authorization",
        "ConnectionString", "credentials", "Cookie");

    private static readonly Gen<string> PlainName = Gen.OneOfConst(
        "operation", "name", "containerId", "BaseUrl", "CertificatePem", "TokenKey", "secretName", "tokens", "keyId", "Region", "body", "parameters");

    private static readonly Gen<JsonNode?> Scalar = Gen.OneOf(
        Gen.String[Gen.Char["ab\" \\é"], 0, 4].Select(s => (JsonNode?)JsonValue.Create(s)),
        Gen.Int[-3, 3].Select(i => (JsonNode?)JsonValue.Create(i)),
        Gen.Bool.Select(b => (JsonNode?)JsonValue.Create(b)),
        Gen.Const((JsonNode?)null));

    private static Gen<JsonNode?> Value(int depth, bool secrets) => depth == 0
        ? Scalar
        : Gen.Frequency(
            (3, Scalar),
            (2, Gen.Int[0, 3].SelectMany(n => Value(depth - 1, secrets).Array[n]).Select(items => (JsonNode?)new JsonArray(items))),
            (2, Object(depth - 1, secrets).Select(o => (JsonNode?)o)));

    // Every secret field's value is a numbered marker (an object holding one when it isn't a string), so a leak
    // anywhere in the redacted text is found by searching for the marker.
    private static Gen<JsonObject> Object(int depth, bool secrets = true) =>
        Gen.Int[0, 4].SelectMany(n => Gen.Select(
                secrets ? Gen.Frequency((1, SecretName.Select(name => (name, true))), (2, PlainName.Select(name => (name, false))))
                        : PlainName.Select(name => (name, false)),
                Value(depth, secrets), Gen.Int[0, 999], Gen.Bool)
            .Array[n])
        .Select(fields =>
        {
            var obj = new JsonObject();
            foreach (var ((name, secret), value, id, nested) in fields)
                obj[name] = secret
                    ? nested ? new JsonObject { ["inner"] = $"{Marker}{id}" } : JsonValue.Create($"{Marker}{id}")
                    : value?.DeepClone();
            return obj;
        });

    private static Dictionary<string, JsonElement> Arguments(JsonObject obj) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(obj.ToJsonString())!;

    [Fact]
    public void From_RandomArguments_RedactedHoldsNoSecretValueAndCanonicalHoldsThemAll() =>
        Object(3).Sample(obj =>
        {
            var call = McpCallArguments.From(Arguments(obj));

            call.Redacted.Should().NotContain(Marker);
            int secrets = obj.ToJsonString().Split(Marker).Length - 1;
            (call.Canonical.Split(Marker).Length - 1).Should().Be(secrets, "the real arguments keep every value");
            call.HasSecrets.Should().Be(secrets > 0);
            JsonNode.Parse(call.Redacted).Should().NotBeNull("the redacted form is still JSON");
        }, iter: 2000);

    [Fact]
    public void From_RandomArgumentsWithoutSecrets_RedactedIsTheCanonicalForm() =>
        Object(3, secrets: false).Sample(obj =>
        {
            var call = McpCallArguments.From(Arguments(obj));

            call.Redacted.Should().Be(call.Canonical);
            call.HasSecrets.Should().BeFalse();
            JsonNode.DeepEquals(JsonNode.Parse(call.Canonical), obj).Should().BeTrue("nothing is lost or changed");
        }, iter: 2000);

    [Fact]
    public void From_RandomArgumentsInReversedKeyOrder_ReadTheSame() =>
        Object(3).Sample(obj =>
        {
            var reversed = Reverse(obj);

            McpCallArguments.From(Arguments(reversed)).Should().Be(McpCallArguments.From(Arguments(obj)));
        }, iter: 1000);

    [Fact]
    public void From_ConnectionCreateThroughTheGenericTool_HidesTheSecretAndKeepsTheRest()
    {
        var call = McpCallArguments.From(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""
            {"operation":"connections.create","body":{"Name":"prod","Provider":"S3","Secret":"hunter2","ConfigJson":"{}"}}
            """)!);

        call.Redacted.Should().Be("""{"body":{"ConfigJson":"{}","Name":"prod","Provider":"S3","Secret":"[hidden]"},"operation":"connections.create"}""");
        call.Canonical.Should().Contain("hunter2");
    }

    [Fact]
    public void From_NullSecret_StaysNull()
    {
        var call = McpCallArguments.From(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"Secret":null,"Name":"a"}""")!);

        call.Redacted.Should().Be("""{"Name":"a","Secret":null}""");
        call.HasSecrets.Should().BeFalse();
    }

    [Theory]
    [InlineData("Secret", true)]
    [InlineData("client_secret", true)]
    [InlineData("x-api-key", true)]
    [InlineData("PrivateKeyPem", true)]
    [InlineData("TokenKey", false)]
    [InlineData("secretName", false)]
    [InlineData("CertificatePem", false)]
    [InlineData("tokens", false)]
    public void IsSecret_Name_MatchesBySuffix(string name, bool secret)
    {
        McpCallArguments.IsSecret(name).Should().Be(secret);
    }

    private static JsonObject Reverse(JsonObject obj)
    {
        var copy = new JsonObject();
        foreach (var (key, value) in obj.Reverse())
            copy[key] = value switch
            {
                JsonObject inner => Reverse(inner),
                JsonArray array => new JsonArray(array.Select(item => item is JsonObject o ? Reverse(o) : item?.DeepClone()).ToArray()),
                _ => value?.DeepClone(),
            };
        return copy;
    }
}
