using System.ComponentModel;
using Sim.DeviceAgent.Configuration;

namespace Sim.DeviceAgent.App.ConfigEditor;

public sealed class DeviceEntry : INotifyPropertyChanged
{
    public DeviceEntry(DeviceConfig config, bool isNew)
    {
        Config = config;
        IsNew = isNew;
    }

    public DeviceConfig Config { get; }

    public bool IsNew { get; set; }

    public string Display => $"{Config.DeviceName}（{Config.DeviceId}）· {Config.Type}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyDisplayChanged()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
}

public sealed class SinkEntry : INotifyPropertyChanged
{
    public SinkEntry(string id, SinkConfig config)
    {
        Id = id;
        Config = config;
    }

    public string Id { get; }

    public SinkConfig Config { get; }

    public string Display => Config.Type.ToLowerInvariant() switch
    {
        "wsserver" => $"{Id} · wsServer · {Config.Bind}:{Config.Port}",
        "rabbitmq" => $"{Id} · rabbitmq · {Config.Host}:{Config.Port}",
        _ => $"{Id} · {Config.Type}"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyDisplayChanged()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
}
