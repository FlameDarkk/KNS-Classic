using System.Text;
using KnS.Platform.ActiveUser;
using KnS.Protocol.Crypto;
using KnS.Protocol.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnS.Tests.Protocol;

public sealed class ActiveUserGatewayTests
{
    [Fact]
    public void Get_config_hides_the_terms_screen()
    {
        // Without an "agreement" object the Android module keeps agreement_show = -1 and opens an
        // empty terms screen that closes as declined, leaving the title screen stuck.
        const string timestamp = "1729876543";
        byte[] request = ActiveUserCipher.Encrypt(timestamp, Encoding.UTF8.GetBytes("""{"api":"get_config"}"""));
        var gateway = new ActiveUserGateway(TimeProvider.System, NullLogger<ActiveUserGateway>.Instance);

        ActiveUserReply reply = gateway.Handle(request, timestamp);
        var json = WireJson.ParseObject(ActiveUserCipher.Decrypt(timestamp, reply.Body))!;

        Assert.Equal(0, json["agreement"]!["show"]!.GetValue<int>());
        Assert.Null(json["agreement_ex"]);
    }

    [Fact]
    public void Download_returns_a_non_empty_did()
    {
        // DownloadCheck stores the reply's did; an empty one encrypts to null and crashes the client.
        const string timestamp = "1729876543";
        var gateway = new ActiveUserGateway(TimeProvider.System, NullLogger<ActiveUserGateway>.Instance);

        string Did(string request)
        {
            ActiveUserReply reply = gateway.Handle(ActiveUserCipher.Encrypt(timestamp, Encoding.UTF8.GetBytes(request)), timestamp);
            return WireJson.ParseObject(ActiveUserCipher.Decrypt(timestamp, reply.Body))!["did"]!.GetValue<string>();
        }

        Assert.Equal("abc123", Did("""{"api":"download","did":"abc123"}"""));
        Assert.Matches("^[0-9a-f]{32}$", Did("""{"api":"download","android_id":"x"}"""));
    }
}
