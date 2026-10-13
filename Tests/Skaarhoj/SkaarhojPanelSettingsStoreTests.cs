using System.Diagnostics;
using System.Text.Json;
using PtzJoystickControl.SkaarhojPanel;
using Xunit;

namespace PtzJoystickControl.Tests.Skaarhoj;

public sealed class SkaarhojPanelSettingsStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly TraceRecorder _trace = new();

    public SkaarhojPanelSettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PtzSkaarhojSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "SkaarhojPanel.json");
    }

    public void Dispose()
    {
        _trace.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private IEnumerable<(TraceEventType Type, string Message)> OwnEvents => _trace.Events.Where(e => e.Message.Contains(_dir));

    [Fact]
    public void MissingFile_LoadsUnconfiguredDefaults_WritesAnEditableTemplate_AndLogsNothing()
    {
        var settings = new SkaarhojPanelSettingsStore(_path).Load();

        Assert.False(settings.IsConfigured);
        Assert.Equal(9923, settings.Port);
        Assert.True(File.Exists(_path));
        var json = File.ReadAllText(_path);
        Assert.DoesNotContain("IsConfigured", json);
        var template = JsonSerializer.Deserialize<SkaarhojPanelSettings>(json)!;
        Assert.False(template.IsConfigured);
        Assert.Equal(9923, template.Port);
        Assert.Equal(new[] { "SkaarhojPanel.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
        Assert.Empty(OwnEvents);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var store = new SkaarhojPanelSettingsStore(_path);

        Assert.True(store.Save(new SkaarhojPanelSettings { Address = "192.168.1.163", Port = 9924 }));
        var loaded = new SkaarhojPanelSettingsStore(_path).Load();

        Assert.True(loaded.IsConfigured);
        Assert.Equal("192.168.1.163", loaded.Address);
        Assert.Equal(9924, loaded.Port);
        Assert.Equal(new[] { "SkaarhojPanel.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void FileWithOnlyAnAddress_UsesTheDefaultPort()
    {
        File.WriteAllText(_path, "{ \"Address\": \"10.0.0.5\" }");

        var settings = new SkaarhojPanelSettingsStore(_path).Load();

        Assert.True(settings.IsConfigured);
        Assert.Equal("10.0.0.5", settings.Address);
        Assert.Equal(9923, settings.Port);
    }

    [Theory]
    [InlineData("{ \"Address\": \"   \" }")]
    [InlineData("{ \"Address\": null }")]
    [InlineData("{ }")]
    public void BlankAddress_IsNotConfigured(string json)
    {
        File.WriteAllText(_path, json);

        Assert.False(new SkaarhojPanelSettingsStore(_path).Load().IsConfigured);
        Assert.Empty(OwnEvents);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("{ \"Port\": \"abc\" }")]
    public void CorruptFile_LogsAnError_LoadsUnconfigured_AndIsLeftForTheUserToFix(string json)
    {
        File.WriteAllText(_path, json);

        var settings = new SkaarhojPanelSettingsStore(_path).Load();

        Assert.False(settings.IsConfigured);
        Assert.Contains(OwnEvents, e => e.Type == TraceEventType.Error);
        Assert.Equal(json, File.ReadAllText(_path));
    }
}
