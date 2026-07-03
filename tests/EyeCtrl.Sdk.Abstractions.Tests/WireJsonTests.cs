namespace EyeCtrl.Sdk.Abstractions.Tests;

using System.Text.Json;
using EyeCtrl.Contracts;

public class WireJsonTests
{
    [Fact]
    public void Options_SerializeCamelCase_WithTypeDiscriminator()
    {
        WireMessage msg = new RegisterMessage { Id = "a", Rect = new SdkRect(1, 2, 3, 4), Hwnd = 5 };
        var json = JsonSerializer.Serialize(msg, WireJson.Options);
        Check.That(json).Contains("\"type\":\"register\"");
        Check.That(json).Contains("\"id\":\"a\"");
        Check.That(json).Contains("\"hwnd\":5");
    }

    [Fact]
    public void Options_RoundTripActivated()
    {
        var json = JsonSerializer.Serialize<WireMessage>(new ActivatedMessage { Id = "z" }, WireJson.Options);
        var back = JsonSerializer.Deserialize<WireMessage>(json, WireJson.Options);
        Check.That(back).IsInstanceOf<ActivatedMessage>();
        Check.That(((ActivatedMessage)back!).Id).IsEqualTo("z");
    }
}
