using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace VegasReborn.Core.Tests.Profiles;

/// <summary>In-memory account service with the profile revision semantics of the real one. No network.</summary>
public sealed class FakeCloud : HttpMessageHandler
{
    public static readonly string[] OwnSlots = ["VegasRetailPEC_v1", "VegasRetailEquipment_v1"];

    public FakeCloud(string identity, string token)
    {
        Identity = identity;
        Token = token;
        Profile = new JsonObject
        {
            ["revision"] = 0,
            ["data"] = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["files"] = new JsonObject(),
                ["preferences"] = new JsonObject { ["remakeSetting"] = true },
                ["progression"] = new JsonObject { ["schemaVersion"] = 1, ["totalExperience"] = 0 },
            },
        };
    }

    public string Identity { get; set; }
    public string Token { get; }
    public JsonObject Profile { get; }
    public JsonObject Data => Profile["data"]!.AsObject();
    public JsonObject Files => Data["files"]!.AsObject();
    public List<(string Method, string Path, JsonNode? Body)> Calls { get; } = [];
    public bool Conflict { get; set; }
    public long AckXp { get; set; }
    public Action OnPut { get; set; } = () => { };

    public void SetExperience(JsonNode? value) => Data["progression"]!["totalExperience"] = value;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization?.Parameter != Token)
        {
            throw new InvalidOperationException("Unexpected bearer");
        }
        var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var body = text is null ? null : JsonNode.Parse(text);
        var path = request.RequestUri!.AbsolutePath;
        Calls.Add((request.Method.Method, path, body?.DeepClone()));
        if (path == "/api/v1/account")
        {
            return Reply(200, new JsonObject { ["id"] = Identity, ["name"] = "Tester" });
        }
        if (request.Method == HttpMethod.Get)
        {
            return Reply(200, Profile.DeepClone());
        }
        if (Conflict || L(body!["revision"]) != L(Profile["revision"]))
        {
            return Reply(409, new JsonObject { ["error"] = "revision_conflict" });
        }
        OnPut();
        var preserved = new JsonObject();
        foreach (var (key, value) in Files)
        {
            if (!OwnSlots.Contains(key))
            {
                preserved[key] = value?.DeepClone();
            }
        }
        foreach (var (key, value) in body["data"]!["files"]!.AsObject())
        {
            preserved[key] = value?.DeepClone();
        }
        Data["files"] = preserved;
        var total = Math.Max(Math.Max(L(Data["progression"]!["totalExperience"]), L(body["data"]!["progression"]!["totalExperience"])), AckXp);
        Data["progression"]!["totalExperience"] = total;
        Profile["revision"] = L(Profile["revision"]) + 1;
        return Reply(200, Profile.DeepClone());
    }

    private static long L(JsonNode? node) => long.Parse(node!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    private static HttpResponseMessage Reply(int status, JsonNode body) => new((HttpStatusCode)status)
    {
        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString())),
    };
}
