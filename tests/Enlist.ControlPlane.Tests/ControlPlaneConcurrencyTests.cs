using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// What happens when two callers do the same thing at the same instant. Every case here was a
/// check-then-act that read a value, decided on it, and wrote based on a decision that was already
/// stale — the kind of defect that never shows up in a test doing one thing at a time, and that
/// looks like a mystery in production because the second caller's request is perfectly valid.
///
/// Real requests against a real control plane, fired together, because a race is not something a
/// single-threaded test can have an opinion about.
///
/// What these can and cannot prove, stated plainly so nobody reads more into a green run than is
/// there. Two are deterministic and fail outright against the old code: the abandoned upload (the
/// temporary file is either cleaned up or it is not) and the refused enrollment (which guards the
/// ordering the fix introduced - charge the token only after the name is known to be free).
///
/// The other four are OPPORTUNISTIC. They catch the defect only on runs where the requests genuinely
/// overlap inside the server; on a run where they happen to queue up, the old code would have passed
/// them too, because sequentially there is no race to lose. They are worth having anyway - they are
/// the only tests that exercise these endpoints concurrently at all, and a defect that shows up on
/// some fraction of runs is one CI will find - but a single green run of this class is not evidence
/// that a race is fixed. The argument for that lives in the code: an atomic conditional UPDATE, and
/// a unique index consulted rather than a prior read.
/// </summary>
public sealed class ControlPlaneConcurrencyTests : IAsyncLifetime
{
    private const int Racers = 8;
    private static readonly IReadOnlyDictionary<string, string> Required = new Dictionary<string, string> { ["Authentication__Mode"] = "Required" };

    private ControlPlaneTestServer? _server;
    private HttpClient _http = null!;
    private string _operatorKey = "";

    public async Task InitializeAsync()
    {
        _server = await ControlPlaneTestServer.StartAsync(TimeSpan.FromSeconds(30), Required);
        _http = new HttpClient { BaseAddress = _server.BaseUri };
        _operatorKey = await _server.CreateApiKeyAsync("tests", "Operator");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string bearer, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    /// <summary>Fires the same call from several tasks released as close together as this machine can manage.</summary>
    private async Task<List<HttpResponseMessage>> RaceAsync(Func<HttpRequestMessage> request)
    {
        using var startLine = new SemaphoreSlim(0, Racers);
        var runs = Enumerable.Range(0, Racers).Select(async _ =>
        {
            await startLine.WaitAsync();
            return await _http.SendAsync(request());
        }).ToList();

        startLine.Release(Racers);
        return [.. await Task.WhenAll(runs)];
    }

    [Fact]
    public async Task A_single_use_join_token_enrolls_exactly_one_agent_however_many_ask_at_once()
    {
        // The whole purpose of a use count. Read-modify-written in memory, every racer read
        // UsesRemaining = 1 and every racer wrote 0, so one single-use token enrolled as many agents
        // as happened to arrive together - each with a real credential, none of them accounted for.
        var joinToken = await _server!.CreateJoinTokenAsync("--uses", "1");

        var responses = await RaceAsync(() => Request(HttpMethod.Post, "/api/agents/enroll", joinToken, new EnrollAgentRequest("RACE-" + Guid.NewGuid().ToString("N")[..8])));

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var refused = responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized);

        Assert.Equal(1, created);
        Assert.Equal(Racers - 1, refused);

        // And the token is spent exactly once, not eight times into negative territory.
        var tokens = await (await _http.SendAsync(Request(HttpMethod.Get, "/api/join-tokens", _operatorKey))).Content.ReadFromJsonAsync<List<JoinTokenDto>>();
        var used = Assert.Single(tokens!, t => t.UsesRemaining == 0);
        Assert.Equal(CredentialStatuses.UsedUp, used.Status);
    }

    [Fact]
    public async Task Enrolling_one_new_name_from_several_places_at_once_is_a_conflict_not_a_server_error()
    {
        // Both racers find no existing credential and both insert. One wins on the primary key; the
        // loser used to get a 500 for exactly the condition the endpoint answers with 409 when it
        // notices in time. A 500 tells an operator their control plane is broken, when in fact their
        // agent name is taken.
        var joinToken = await _server!.CreateJoinTokenAsync();
        var agentName = "TWIN-" + Guid.NewGuid().ToString("N")[..8];

        var responses = await RaceAsync(() => Request(HttpMethod.Post, "/api/agents/enroll", joinToken, new EnrollAgentRequest(agentName)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(Racers - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.DoesNotContain(responses, r => r.StatusCode == HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task A_refused_enrollment_does_not_spend_a_use_of_the_join_token()
    {
        // The reason the conflict check runs before the token is charged. Otherwise a name that is
        // already taken burns a use on every attempt, and an operator is left holding a single-use
        // token that no longer works and no agent to show for it.
        var joinToken = await _server!.CreateJoinTokenAsync("--uses", "2");
        var agentName = "TAKEN-" + Guid.NewGuid().ToString("N")[..8];

        Assert.Equal(HttpStatusCode.Created, (await _http.SendAsync(Request(HttpMethod.Post, "/api/agents/enroll", joinToken, new EnrollAgentRequest(agentName)))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _http.SendAsync(Request(HttpMethod.Post, "/api/agents/enroll", joinToken, new EnrollAgentRequest(agentName)))).StatusCode);

        var tokens = await (await _http.SendAsync(Request(HttpMethod.Get, "/api/join-tokens", _operatorKey))).Content.ReadFromJsonAsync<List<JoinTokenDto>>();
        Assert.Equal(1, Assert.Single(tokens!).UsesRemaining);
    }

    [Fact]
    public async Task Creating_one_api_key_name_from_several_places_at_once_is_a_conflict_not_a_server_error()
    {
        // AnyAsync then Add: every racer passes the check, and the filtered unique index on live
        // names catches the rest at SaveChanges. The message the losers got promised 409 and
        // delivered 500.
        var name = "race-" + Guid.NewGuid().ToString("N")[..8];

        var responses = await RaceAsync(() => Request(HttpMethod.Post, "/api/api-keys", _operatorKey, new CreateApiKeyRequest(name, "Viewer")));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(Racers - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.DoesNotContain(responses, r => r.StatusCode == HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task Uploading_identical_bytes_from_several_places_at_once_succeeds_every_time()
    {
        // Content addressing means the racers are not really in conflict at all: the file name IS the
        // hash of the bytes, so whoever wins, what ends up on disk is exactly what every one of them
        // was uploading. The loser's File.Move landed on a file that now existed and threw, and the
        // upload reported 500 for a package that had in fact been stored.
        var bytes = TestPackaging.ZipDirectory(RepoPaths.SampleServiceDir());
        var application = "RacePkg" + Guid.NewGuid().ToString("N")[..8];

        var responses = await RaceAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/packages?application={application}") { Content = new ByteArrayContent(bytes) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _operatorKey);
            return request;
        });

        Assert.DoesNotContain(responses, r => r.StatusCode == HttpStatusCode.InternalServerError);
        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, $"an upload of identical bytes failed with {r.StatusCode}"));

        // One digest, one row, whichever racer got there first.
        var digests = new HashSet<string>();
        foreach (var response in responses)
        {
            digests.Add((await response.Content.ReadFromJsonAsync<UploadPackageResponse>())!.Digest);
        }

        Assert.Single(digests);
    }

    [Fact]
    public async Task An_upload_that_dies_mid_body_leaves_no_temporary_file_behind()
    {
        // Nothing sweeps a leftover .tmp: the retention sweep walks package ROWS, and an upload that
        // never finished produced none. On a control plane taking large packages over a flaky link
        // this is an unbounded leak that only a human deleting files would ever reclaim.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/packages?application=Doomed") { Content = new DiesMidStreamContent() };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _operatorKey);

        try
        {
            await _http.SendAsync(request);
        }
        catch (HttpRequestException)
        {
            // Expected: the body stopped arriving.
        }

        // The server needs a moment to notice and unwind before the directory is inspected.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        string[] leftovers;
        do
        {
            leftovers = Directory.GetFiles(_server!.PackageStorageRoot, "*.tmp");
            if (leftovers.Length == 0)
            {
                break;
            }

            await Task.Delay(200);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Empty(leftovers);
    }

    /// <summary>Writes a little and then throws, which is what an aborted upload looks like from the server's side.</summary>
    private sealed class DiesMidStreamContent : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await stream.WriteAsync(new byte[64 * 1024]);
            await stream.FlushAsync();
            throw new IOException("the upload was abandoned partway through, on purpose");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
