using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Sim.DeviceAgent.App.ConfigEditor;
using Sim.DeviceAgent.App.Devices;
using Sim.DeviceAgent.App.Shell;
using Sim.DeviceAgent.Configuration;
using Sim.DeviceAgent.Hosting;

namespace Sim.DeviceAgent.App;

public sealed partial class MainWindow : Window
{
    private readonly AgentRuntimeHost? _host;
    private readonly StartupGuardService? _startupGuard;
    private readonly string _configPath;
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueueTimer _noticeTimer;
    private readonly ObservableCollection<string> _devices = [];
    private readonly ObservableCollection<string> _sinks = [];
    private readonly ObservableCollection<DeviceEntry> _deviceEntries = [];
    private readonly ObservableCollection<SinkEntry> _sinkEntries = [];

    private AgentOptions? _editorAgent;
    private DeviceEntry? _currentDevice;
    private SinkEntry? _currentSink;
    private string? _editorSnapshot;
    private bool _configReady;
    private bool _suppressDeviceSelection;
    private bool _suppressSinkSelection;
    private bool _allowClose;
    private bool _suppressStartupToggle;
    private DateTimeOffset? _lastReloadSuccessAt;
    private string? _lastReloadError;

    internal MainWindow(
        AgentRuntimeHost? host,
        string configPath,
        string? startupError,
        string? startupNotice = null,
        StartupGuardService? startupGuard = null)
    {
        InitializeComponent();
        TitleBarChrome.Attach(this, TitleBarRoot, TitleBarDragRegion, CaptionInsetColumn);

        _startupGuard = startupGuard;
        _suppressStartupToggle = true;
        StartWithWindowsToggle.IsOn = startupGuard?.StartWithWindows ?? false;
        StartWithWindowsToggle.IsEnabled = startupGuard is not null;
        _suppressStartupToggle = false;

        _noticeTimer = DispatcherQueue.CreateTimer();
        _noticeTimer.Interval = TimeSpan.FromSeconds(3);
        _noticeTimer.IsRepeating = false;
        _noticeTimer.Tick += (_, _) => ConfigInfoBar.IsOpen = false;

        _host = host;
        _configPath = Path.GetFullPath(configPath);
        Title = "DeviceAgent";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1220, 820));
        AppWindow.Closing += OnClosing;
        DeviceList.ItemsSource = _devices;
        SinkList.ItemsSource = _sinks;
        ConfigDeviceList.ItemsSource = _deviceEntries;
        ConfigSinkList.ItemsSource = _sinkEntries;
        ConfigText.Text = $"配置文件：{_configPath}";

        if (startupError is not null)
        {
            ShowError(startupError);
        }

        ConfigureTextInputs();
        RefreshPorts();
        InitializeConfigEditor();

        if (startupNotice is not null)
        {
            ShowConfigMessage(startupNotice, InfoBarSeverity.Success);
        }

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        SyncReloadBaseline();
        Refresh();
    }

    public void ShowFromTray()
    {
        if (!AppWindow.IsVisible)
        {
            AppWindow.Show();
        }

        Activate();
    }

    private void ConfigureTextInputs()
    {
        foreach (var textBox in new[]
                 {
                     DeviceIdBox,
                     DeviceNameBox,
                     TcpHostBox,
                     FileDirectoryBox,
                     FilePatternBox,
                     ArchiveDirectoryBox,
                     ErrorDirectoryBox,
                     DelimiterBox,
                     ParsePatternBox,
                     FilterPatternBox,
                     CsvDelimiterBox,
                     CsvColumnsBox,
                     SinkIdBox,
                     WsBindBox,
                     WsOriginsBox,
                     CenterUrlBox,
                     MqHostBox,
                     MqVirtualHostBox,
                     MqUserNameBox,
                     MqExchangeBox,
                     MqRoutingKeyBox,
                     SpoolDirectoryBox,
                     AgentIdText,
                     AgentDisplayNameBox,
                     AgentStateDirectoryText
                 })
        {
            textBox.IsSpellCheckEnabled = false;
            textBox.IsTextPredictionEnabled = false;
        }
    }

    public void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    public void AllowClose() => _allowClose = true;

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            _timer.Stop();
            return;
        }

        args.Cancel = true;
        AppWindow.Hide();
    }

    private void Refresh()
    {
        if (_host is null)
        {
            return;
        }

        var snapshot = _host.GetSnapshot();
        UpdateReloadNotice(snapshot.Config);

        UpdateItems(
            _devices,
            snapshot.Devices.Select(device =>
                $"{device.DeviceName}（{device.DeviceId}）· {device.Type} · {device.State} · " +
                $"帧 {device.ReceivedFrames} · 丢弃 {device.DroppedFrames} · 解析错误 {device.ParseErrors}"));

        UpdateItems(
            _sinks,
            snapshot.Sinks.Select(sink =>
            {
                var endpoint = string.IsNullOrWhiteSpace(sink.Endpoint) ? string.Empty : $" · {sink.Endpoint}";
                return string.Equals(sink.Type, "wsServer", StringComparison.OrdinalIgnoreCase)
                    ? $"{sink.Id} · {sink.Type} · {sink.State}{endpoint} · 客户端 {sink.Clients}"
                    : $"{sink.Id} · {sink.Type} · {sink.State}{endpoint} · 待发送 {sink.PendingEntries}";
            }));
    }

    private void SyncReloadBaseline()
    {
        if (_host is null)
        {
            return;
        }

        var status = _host.GetSnapshot().Config;
        _lastReloadSuccessAt = status.LastSuccessAt;
        _lastReloadError = status.LastError;
    }

    private void UpdateReloadNotice(ConfigReloadStatus status)
    {
        if (status.LastSuccessAt != _lastReloadSuccessAt)
        {
            _lastReloadSuccessAt = status.LastSuccessAt;
            _lastReloadError = status.LastError;
            if (!TrySyncEditorWithExternalReload())
            {
                ShowConfigMessage(status.LastMessage, InfoBarSeverity.Success);
            }

            return;
        }

        if (string.Equals(status.LastError, _lastReloadError, StringComparison.Ordinal))
        {
            return;
        }

        _lastReloadError = status.LastError;
        if (status.LastError is not null)
        {
            ShowConfigMessage($"配置重载失败：{status.LastError}", InfoBarSeverity.Error);
        }
    }

    private static void UpdateItems(ObservableCollection<string> target, IEnumerable<string> values)
    {
        var items = values.ToArray();
        if (target.Count == items.Length && target.SequenceEqual(items))
        {
            return;
        }

        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private bool TrySyncEditorWithExternalReload()
    {
        if (!_configReady)
        {
            InitializeConfigEditor("检测到配置文件已更新，配置页已自动同步。");
            return true;
        }

        AgentConfig disk;
        string diskSnapshot;
        try
        {
            disk = AgentConfigLoader.Load(_configPath);
            NormalizeDisplayTokens(disk, ConfigFileTokens.Read(_configPath));
            diskSnapshot = AgentConfigWriter.Serialize(disk, Path.GetDirectoryName(_configPath)!);
        }
        catch (Exception)
        {
            return false;
        }

        if (_editorSnapshot is not null &&
            string.Equals(diskSnapshot, _editorSnapshot, StringComparison.Ordinal))
        {
            return false;
        }

        if (IsEditorDirty())
        {
            ShowConfigMessage(
                "配置文件已在外部更新；配置页保留了未保存的修改，点击“从磁盘重新载入”可载入最新配置。",
                InfoBarSeverity.Warning);
            return true;
        }

        InitializeConfigEditor("检测到配置文件已更新，配置页已自动同步。");
        return true;
    }

    private bool IsEditorDirty()
    {
        if (_editorSnapshot is null)
        {
            return true;
        }

        ApplyAgentForm();
        ApplyCurrentDeviceForm();
        ApplyCurrentSinkForm();

        try
        {
            return !string.Equals(SerializeEditorModel(), _editorSnapshot, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return true;
        }
    }

    private void CaptureEditorSnapshot()
    {
        ApplyAgentForm();
        ApplyCurrentDeviceForm();
        ApplyCurrentSinkForm();

        try
        {
            _editorSnapshot = SerializeEditorModel();
        }
        catch (Exception)
        {
            _editorSnapshot = null;
        }
    }

    private string SerializeEditorModel()
    {
        var config = new AgentConfig
        {
            Agent = _editorAgent ?? new AgentOptions(),
            Sinks = _sinkEntries.ToDictionary(
                entry => entry.Id,
                entry => entry.Config,
                StringComparer.OrdinalIgnoreCase),
            Devices = _deviceEntries.Select(entry => entry.Config).ToList()
        };

        return AgentConfigWriter.Serialize(config, Path.GetDirectoryName(_configPath)!);
    }

    private void InitializeConfigEditor(string? loadedNotice = null)
    {
        var previousDeviceId = _currentDevice?.Config.DeviceId;
        var previousSinkId = _currentSink?.Id;
        var previousNavIndex = ConfigNavList.SelectedIndex;

        _suppressDeviceSelection = true;
        _suppressSinkSelection = true;
        _deviceEntries.Clear();
        _sinkEntries.Clear();
        _suppressDeviceSelection = false;
        _suppressSinkSelection = false;
        _currentDevice = null;
        _currentSink = null;
        _editorAgent = null;
        _editorSnapshot = null;
        _configReady = false;

        try
        {
            var config = AgentConfigLoader.Load(_configPath);
            NormalizeDisplayTokens(config, ConfigFileTokens.Read(_configPath));

            _editorAgent = config.Agent;
            foreach (var device in config.Devices)
            {
                _deviceEntries.Add(new DeviceEntry(device, isNew: false));
            }

            foreach (var (sinkId, sink) in config.Sinks)
            {
                if (sink is null)
                {
                    continue;
                }

                _sinkEntries.Add(new SinkEntry(sinkId, sink));
            }

            _configReady = true;

            var errors = AgentConfigValidator.Validate(config);
            ShowConfigMessage(
                errors.Count == 0
                    ? loadedNotice ?? "配置文件已载入。修改后点击“保存并应用”。"
                    : $"配置文件已载入，但校验未通过：{string.Join("；", errors)}",
                errors.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowConfigMessage(
                $"无法读取配置：{ex.Message}。可点击“新建默认配置”从头创建。",
                InfoBarSeverity.Warning);
        }

        NewConfigButton.Visibility = _configReady ? Visibility.Collapsed : Visibility.Visible;
        SaveConfigButton.IsEnabled = _configReady;
        SaveStatusText.Text = _configReady ? string.Empty : "配置未载入";

        LoadAgentForm();
        ConfigNavList.SelectedIndex = previousNavIndex >= 0 ? previousNavIndex : 0;
        SelectInitialEntries(previousDeviceId, previousSinkId);

        if (_configReady)
        {
            CaptureEditorSnapshot();
        }
    }

    private static void NormalizeDisplayTokens(
        AgentConfig config,
        (string? LocalToken, Dictionary<string, string> SinkTokens) rawTokens)
    {
        var wsEnvTokenActive = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(AgentConfigLoader.TokenEnvironmentVariable));
        var centerEnvTokenActive = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(AgentConfigLoader.CenterTokenEnvironmentVariable));

        foreach (var (sinkId, sink) in config.Sinks)
        {
            if (sink is null)
            {
                continue;
            }

            var isWsServer = string.Equals(sink.Type, "wsServer", StringComparison.OrdinalIgnoreCase);
            var isWsClient = string.Equals(sink.Type, "wsClient", StringComparison.OrdinalIgnoreCase);
            var envOverrideActive = isWsServer ? wsEnvTokenActive : isWsClient && centerEnvTokenActive;
            if (envOverrideActive)
            {
                sink.Token = rawTokens.SinkTokens.TryGetValue(sinkId, out var rawToken)
                    ? rawToken
                    : isWsServer && rawTokens.LocalToken is not null ? rawTokens.LocalToken : string.Empty;
            }
        }
    }

    private void SelectInitialEntries(string? preferredDeviceId = null, string? preferredSinkId = null)
    {
        _suppressDeviceSelection = true;
        _suppressSinkSelection = true;
        ConfigDeviceList.SelectedIndex = ResolveSelectionIndex(
            _deviceEntries,
            entry => entry.Config.DeviceId,
            preferredDeviceId);
        ConfigSinkList.SelectedIndex = ResolveSelectionIndex(
            _sinkEntries,
            entry => entry.Id,
            preferredSinkId);
        _suppressDeviceSelection = false;
        _suppressSinkSelection = false;

        _currentDevice = ConfigDeviceList.SelectedItem as DeviceEntry;
        _currentSink = ConfigSinkList.SelectedItem as SinkEntry;
        LoadDeviceForm(_currentDevice);
        LoadSinkForm(_currentSink);
    }

    private static int ResolveSelectionIndex<T>(
        IReadOnlyList<T> items,
        Func<T, string?> keySelector,
        string? preferredKey)
    {
        if (!string.IsNullOrWhiteSpace(preferredKey))
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (string.Equals(keySelector(items[index]), preferredKey, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
        }

        return items.Count > 0 ? 0 : -1;
    }

    private void OnConfigNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = ConfigNavList.SelectedIndex;
        ConfigDevicesPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConfigSinksPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        ConfigAgentPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadAgentForm()
    {
        if (_editorAgent is null)
        {
            AgentIdText.Text = string.Empty;
            AgentDisplayNameBox.Text = string.Empty;
            AgentStateDirectoryText.Text = string.Empty;
            AgentTrayToggle.IsOn = false;
            return;
        }

        AgentIdText.Text = _editorAgent.Id;
        AgentDisplayNameBox.Text = _editorAgent.DisplayName;
        AgentStateDirectoryText.Text = _editorAgent.StateDirectory;
        AgentTrayToggle.IsOn = _editorAgent.Tray.Enabled;
    }

    private void ApplyAgentForm()
    {
        if (_editorAgent is null)
        {
            return;
        }

        _editorAgent.DisplayName = AgentDisplayNameBox.Text.Trim();
        _editorAgent.Tray.Enabled = AgentTrayToggle.IsOn;
    }

    private void OnConfigDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceSelection)
        {
            return;
        }

        ApplyCurrentDeviceForm();
        _currentDevice = ConfigDeviceList.SelectedItem as DeviceEntry;
        LoadDeviceForm(_currentDevice);
    }

    private void LoadDeviceForm(DeviceEntry? entry)
    {
        DeviceForm.IsHitTestVisible = entry is not null;
        DeviceForm.Opacity = entry is null ? 0.6 : 1;

        if (entry is null)
        {
            DeviceIdBox.Text = string.Empty;
            DeviceNameBox.Text = string.Empty;
            DeviceSinkChecks.Children.Clear();
            return;
        }

        var config = entry.Config;
        DeviceIdBox.Text = config.DeviceId;
        DeviceIdBox.IsReadOnly = !entry.IsNew;
        DeviceNameBox.Text = config.DeviceName;
        SelectComboByText(TransportTypeCombo, NormalizeTransportType(config.Type));
        QueueCapacityBox.Value = config.QueueCapacity;
        DeviceEnabledToggle.IsOn = config.Enabled;

        SelectPort(config.Port);
        SelectComboByText(BaudRateCombo, config.BaudRate.ToString(CultureInfo.InvariantCulture));
        SelectComboByText(DataBitsCombo, config.DataBits.ToString(CultureInfo.InvariantCulture));
        SelectComboByText(ParityCombo, config.Parity);
        SelectComboByText(StopBitsCombo, config.StopBits);
        ReadTimeoutBox.Value = config.ReadTimeoutMs;
        DtrToggle.IsOn = config.DtrEnable;
        RtsToggle.IsOn = config.RtsEnable;

        TcpHostBox.Text = config.Host ?? string.Empty;
        TcpPortBox.Value = int.TryParse(config.Port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tcpPort)
            ? tcpPort
            : double.NaN;
        TcpReconnectMinBox.Value = config.ReconnectMinSeconds;
        TcpReconnectMaxBox.Value = config.ReconnectMaxSeconds;

        FileDirectoryBox.Text = config.Directory ?? string.Empty;
        FilePatternBox.Text = config.Pattern;
        StableSecondsBox.Value = config.StableSeconds;
        PollIntervalBox.Value = config.PollIntervalSeconds;
        ArchiveDirectoryBox.Text = config.ArchiveDirectory ?? string.Empty;
        ErrorDirectoryBox.Text = config.ErrorDirectory ?? string.Empty;

        var codec = config.Codec ?? new CodecConfig();
        SelectComboByText(CodecTypeCombo, codec.Type);
        SelectComboByText(EncodingCombo, codec.Encoding);
        MaxFrameBytesBox.Value = codec.MaxFrameBytes;
        DelimiterBox.Text = EscapeDelimiter(codec.Delimiter);
        ParsePatternBox.Text = codec.Parse?.Pattern ?? string.Empty;
        ParseIgnoreCaseToggle.IsOn = codec.Parse?.IgnoreCase ?? false;
        FilterPatternBox.Text = codec.Filter?.Pattern ?? string.Empty;
        FilterIgnoreCaseToggle.IsOn = codec.Filter?.IgnoreCase ?? false;
        CsvDelimiterBox.Text = codec.CsvDelimiter;
        CsvColumnsBox.Text = FormatColumns(codec.Columns);

        RebuildDeviceSinkChecks(config.Sinks ?? []);
        UpdateTransportVisibility();
        UpdateCodecVisibility();
    }

    private void ApplyCurrentDeviceForm()
    {
        if (_currentDevice is null)
        {
            return;
        }

        var config = _currentDevice.Config;
        config.DeviceId = DeviceIdBox.Text.Trim();
        config.DeviceName = DeviceNameBox.Text.Trim();
        config.Type = NormalizeTransportType(GetComboText(TransportTypeCombo));
        config.QueueCapacity = IntValue(QueueCapacityBox, 1024);
        config.Enabled = DeviceEnabledToggle.IsOn;

        switch (config.Type)
        {
            case "serial":
                config.Port = ResolveSelectedPort();
                break;

            case "tcp":
                config.Port = double.IsNaN(TcpPortBox.Value)
                    ? null
                    : ((int)Math.Round(TcpPortBox.Value)).ToString(CultureInfo.InvariantCulture);
                break;
        }

        config.BaudRate = ParseIntText(GetComboText(BaudRateCombo), 9600);
        config.DataBits = ParseIntText(GetComboText(DataBitsCombo), 8);
        config.Parity = DefaultIfEmpty(GetComboText(ParityCombo), "None");
        config.StopBits = DefaultIfEmpty(GetComboText(StopBitsCombo), "One");
        config.ReadTimeoutMs = IntValue(ReadTimeoutBox, 200);
        config.DtrEnable = DtrToggle.IsOn;
        config.RtsEnable = RtsToggle.IsOn;

        config.Host = TcpHostBox.Text.Trim();
        config.ReconnectMinSeconds = IntValue(TcpReconnectMinBox, 1);
        config.ReconnectMaxSeconds = IntValue(TcpReconnectMaxBox, 30);

        config.Directory = FileDirectoryBox.Text.Trim();
        config.Pattern = FilePatternBox.Text.Trim();
        config.StableSeconds = DoubleValue(StableSecondsBox, 5);
        config.PollIntervalSeconds = DoubleValue(PollIntervalBox, 5);
        config.ArchiveDirectory = ArchiveDirectoryBox.Text.Trim();
        config.ErrorDirectory = ErrorDirectoryBox.Text.Trim();

        var codec = config.Codec ??= new CodecConfig();
        codec.Type = NormalizeCodecType(GetComboText(CodecTypeCombo));
        codec.Encoding = DefaultIfEmpty(GetComboText(EncodingCombo), "utf-8");
        codec.MaxFrameBytes = IntValue(MaxFrameBytesBox, 65536);
        codec.Delimiter = UnescapeDelimiter(DelimiterBox.Text);
        codec.Parse = string.IsNullOrWhiteSpace(ParsePatternBox.Text)
            ? null
            : new RegexParseOptions
            {
                Pattern = ParsePatternBox.Text.Trim(),
                IgnoreCase = ParseIgnoreCaseToggle.IsOn
            };
        codec.Filter = string.IsNullOrWhiteSpace(FilterPatternBox.Text)
            ? null
            : new RegexParseOptions
            {
                Pattern = FilterPatternBox.Text.Trim(),
                IgnoreCase = FilterIgnoreCaseToggle.IsOn
            };
        codec.CsvDelimiter = DefaultIfEmpty(CsvDelimiterBox.Text, ",");
        codec.HasHeader = true;
        codec.Columns = ParseColumns(CsvColumnsBox.Text);

        config.Sinks = DeviceSinkChecks.Children
            .OfType<CheckBox>()
            .Where(check => check.IsChecked == true && check.Tag is string)
            .Select(check => (string)check.Tag)
            .ToList();

        _currentDevice.NotifyDisplayChanged();
    }

    private void OnAddDeviceClick(object sender, RoutedEventArgs e)
    {
        ApplyCurrentDeviceForm();

        var entry = new DeviceEntry(CreateDefaultDevice(), isNew: true);
        _deviceEntries.Add(entry);

        _suppressDeviceSelection = true;
        ConfigDeviceList.SelectedItem = entry;
        _suppressDeviceSelection = false;

        _currentDevice = entry;
        LoadDeviceForm(entry);
        DeviceNameBox.Focus(FocusState.Programmatic);
    }

    private async void OnDeleteDeviceClick(object sender, RoutedEventArgs e)
    {
        if (ConfigDeviceList.SelectedItem is not DeviceEntry entry)
        {
            return;
        }

        var label = string.IsNullOrWhiteSpace(entry.Config.DeviceId) ? "新设备" : entry.Config.DeviceId;
        if (!await ConfirmAsync("删除设备", $"确定删除设备 {label}？"))
        {
            return;
        }

        ApplyCurrentDeviceForm();
        _deviceEntries.Remove(entry);
        _currentDevice = null;

        _suppressDeviceSelection = true;
        ConfigDeviceList.SelectedIndex = _deviceEntries.Count > 0 ? 0 : -1;
        _suppressDeviceSelection = false;

        _currentDevice = ConfigDeviceList.SelectedItem as DeviceEntry;
        LoadDeviceForm(_currentDevice);
    }

    private DeviceConfig CreateDefaultDevice()
    {
        return new DeviceConfig
        {
            DeviceId = string.Empty,
            Enabled = true,
            Type = "serial",
            DeviceName = "新设备",
            Port = string.Empty,
            BaudRate = 9600,
            DataBits = 8,
            Parity = "None",
            StopBits = "One",
            ReadTimeoutMs = 200,
            QueueCapacity = 1024,
            Codec = new CodecConfig
            {
                Type = "delimiter",
                Delimiter = "\r\n",
                Encoding = "utf-8"
            },
            Sinks = _sinkEntries.Count > 0 ? [_sinkEntries[0].Id] : []
        };
    }

    private void OnDeviceTransportChanged(object sender, SelectionChangedEventArgs e) => UpdateTransportVisibility();

    private void OnCodecTypeChanged(object sender, SelectionChangedEventArgs e) => UpdateCodecVisibility();

    private void UpdateTransportVisibility()
    {
        var type = NormalizeTransportType(GetComboText(TransportTypeCombo));
        SerialFields.Visibility = type == "serial" ? Visibility.Visible : Visibility.Collapsed;
        TcpFields.Visibility = type == "tcp" ? Visibility.Visible : Visibility.Collapsed;
        FileFields.Visibility = type == "file" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCodecVisibility()
    {
        var type = NormalizeCodecType(GetComboText(CodecTypeCombo));
        DelimiterPanel.Visibility = type == "delimiter" ? Visibility.Visible : Visibility.Collapsed;
        DelimiterFields.Visibility = type == "delimiter" ? Visibility.Visible : Visibility.Collapsed;
        PassthroughFields.Visibility = type == "passthrough" ? Visibility.Visible : Visibility.Collapsed;
        CsvFields.Visibility = type == "csv" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RebuildDeviceSinkChecks(IReadOnlyCollection<string> selectedIds)
    {
        var selected = new HashSet<string>(selectedIds, StringComparer.OrdinalIgnoreCase);
        DeviceSinkChecks.Children.Clear();

        foreach (var sink in _sinkEntries)
        {
            DeviceSinkChecks.Children.Add(new CheckBox
            {
                Content = sink.Id,
                Tag = sink.Id,
                IsChecked = selected.Contains(sink.Id)
            });
        }
    }

    private void OnRefreshPortsClick(object sender, RoutedEventArgs e)
    {
        var current = ResolveSelectedPort();
        RefreshPorts();
        SelectPort(current);
    }

    private void RefreshPorts()
    {
        try
        {
            PortCombo.ItemsSource = SerialPortCatalog.Enumerate()
                .Select(info => new PortChoice(info.PortName, info.DisplayName))
                .ToArray();
        }
        catch (Exception)
        {
            // Port enumeration is best effort; users can still type a port name.
        }
    }

    private void SelectPort(string? portName)
    {
        var choice = PortCombo.Items
            .OfType<PortChoice>()
            .FirstOrDefault(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase));
        if (choice is not null)
        {
            PortCombo.SelectedItem = choice;
            return;
        }

        PortCombo.SelectedIndex = -1;
        PortCombo.Text = portName ?? string.Empty;
    }

    private string ResolveSelectedPort()
    {
        if (PortCombo.SelectedItem is PortChoice choice &&
            string.Equals(GetComboText(PortCombo), choice.DisplayName, StringComparison.Ordinal))
        {
            return choice.PortName;
        }

        var text = GetComboText(PortCombo);
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var match = System.Text.RegularExpressions.Regex.Match(text, @"^COM\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToUpperInvariant() : text;
    }

    private sealed record PortChoice(string PortName, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private void OnConfigSinkSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSinkSelection)
        {
            return;
        }

        ApplyCurrentSinkForm();
        _currentSink = ConfigSinkList.SelectedItem as SinkEntry;
        LoadSinkForm(_currentSink);
    }

    private void LoadSinkForm(SinkEntry? entry)
    {
        SinkForm.IsHitTestVisible = entry is not null;
        SinkForm.Opacity = entry is null ? 0.6 : 1;

        if (entry is null)
        {
            SinkIdBox.Text = string.Empty;
            SinkTypeCombo.SelectedIndex = -1;
            SinkTypeCombo.IsEnabled = false;
            UpdateSinkTypeVisibility(string.Empty);
            return;
        }

        SinkIdBox.Text = entry.Id;
        SinkTypeCombo.IsEnabled = false;
        SelectComboByText(SinkTypeCombo, entry.Config.Type);

        var config = entry.Config;
        WsBindBox.Text = config.Bind ?? "127.0.0.1";
        WsPortBox.Value = config.Port > 0 ? config.Port : double.NaN;
        WsTokenBox.Password = config.Token;
        WsOriginsBox.Text = string.Join(Environment.NewLine, config.AllowedOrigins);

        CenterUrlBox.Text = config.Url;
        CenterTokenBox.Password = config.Token;

        MqHostBox.Text = DefaultIfEmpty(config.Host, "127.0.0.1");
        MqPortBox.Value = config.Port > 0 ? config.Port : 5672;
        MqVirtualHostBox.Text = DefaultIfEmpty(config.VirtualHost, "/");
        MqUserNameBox.Text = DefaultIfEmpty(config.UserName, "guest");
        MqPasswordBox.Password = config.Password;
        MqExchangeBox.Text = config.Exchange;
        SelectComboByText(MqExchangeTypeCombo, DefaultIfEmpty(config.ExchangeType, "topic"));
        MqRoutingKeyBox.Text = DefaultIfEmpty(config.RoutingKey, "device.{deviceId}");
        MqDurableToggle.IsOn = config.Durable;
        MqPersistentToggle.IsOn = config.Persistent;

        KafkaBootstrapBox.Text = DefaultIfEmpty(config.BootstrapServers, "127.0.0.1:9092");
        KafkaTopicBox.Text = config.Topic;
        KafkaMessageKeyBox.Text = DefaultIfEmpty(config.MessageKey, "{agentId}:{deviceId}");
        SelectComboByText(KafkaSecurityProtocolCombo, DefaultIfEmpty(config.SecurityProtocol, "plaintext"));
        SelectComboByText(KafkaSaslMechanismCombo, DefaultIfEmpty(config.SaslMechanism, "plain"));
        KafkaSaslUsernameBox.Text = config.SaslUsername;
        KafkaSaslPasswordBox.Password = config.SaslPassword;
        KafkaSslCaLocationBox.Text = config.SslCaLocation ?? string.Empty;

        SinkReconnectMinBox.Value = config.ReconnectMinSeconds;
        SinkReconnectMaxBox.Value = config.ReconnectMaxSeconds;
        SpoolSegmentBox.Value = config.SpoolSegmentMegabytes;
        SpoolMaxBox.Value = config.SpoolMaxMegabytes;
        SpoolDirectoryBox.Text = config.SpoolDirectory ?? string.Empty;

        UpdateSinkTypeVisibility(entry.Config.Type);
    }

    private void ApplyCurrentSinkForm()
    {
        if (_currentSink is null)
        {
            return;
        }

        var config = _currentSink.Config;
        switch (NormalizeSinkType(config.Type))
        {
            case "wsserver":
                config.Bind = DefaultIfEmpty(WsBindBox.Text, "127.0.0.1");
                config.Port = IntValue(WsPortBox, 0);
                config.Token = WsTokenBox.Password;
                config.AllowedOrigins = ParseLines(WsOriginsBox.Text);
                break;

            case "rabbitmq":
                config.Host = DefaultIfEmpty(MqHostBox.Text, "127.0.0.1");
                config.Port = IntValue(MqPortBox, 5672);
                config.VirtualHost = DefaultIfEmpty(MqVirtualHostBox.Text, "/");
                config.UserName = DefaultIfEmpty(MqUserNameBox.Text, "guest");
                config.Password = MqPasswordBox.Password;
                config.Exchange = MqExchangeBox.Text.Trim();
                config.ExchangeType = DefaultIfEmpty(GetComboText(MqExchangeTypeCombo), "topic").ToLowerInvariant();
                config.RoutingKey = DefaultIfEmpty(MqRoutingKeyBox.Text, "device.{deviceId}");
                config.Durable = MqDurableToggle.IsOn;
                config.Persistent = MqPersistentToggle.IsOn;
                ApplyReliableSinkForm(config);
                break;

            case "kafka":
                config.BootstrapServers = DefaultIfEmpty(KafkaBootstrapBox.Text, "127.0.0.1:9092");
                config.Topic = KafkaTopicBox.Text.Trim();
                config.MessageKey = DefaultIfEmpty(KafkaMessageKeyBox.Text, "{agentId}:{deviceId}");
                config.SecurityProtocol = DefaultIfEmpty(GetComboText(KafkaSecurityProtocolCombo), "plaintext");
                config.SaslMechanism = DefaultIfEmpty(GetComboText(KafkaSaslMechanismCombo), "plain");
                config.SaslUsername = KafkaSaslUsernameBox.Text.Trim();
                config.SaslPassword = KafkaSaslPasswordBox.Password;
                config.SslCaLocation = string.IsNullOrWhiteSpace(KafkaSslCaLocationBox.Text)
                    ? null
                    : KafkaSslCaLocationBox.Text.Trim();
                ApplyReliableSinkForm(config);
                break;

            default:
                config.Url = CenterUrlBox.Text.Trim();
                config.Token = CenterTokenBox.Password;
                ApplyReliableSinkForm(config);
                break;
        }

        _currentSink.NotifyDisplayChanged();
    }

    private void ApplyReliableSinkForm(SinkConfig config)
    {
        config.ReconnectMinSeconds = IntValue(SinkReconnectMinBox, 1);
        config.ReconnectMaxSeconds = IntValue(SinkReconnectMaxBox, 30);
        config.SpoolSegmentMegabytes = IntValue(SpoolSegmentBox, 64);
        config.SpoolMaxMegabytes = IntValue(SpoolMaxBox, 1024);
        config.SpoolDirectory = string.IsNullOrWhiteSpace(SpoolDirectoryBox.Text)
            ? null
            : SpoolDirectoryBox.Text.Trim();
    }

    private void UpdateSinkTypeVisibility(string type)
    {
        var normalized = NormalizeSinkType(type);
        WsServerSinkFields.Visibility = normalized == "wsserver" ? Visibility.Visible : Visibility.Collapsed;
        WsClientSinkFields.Visibility = normalized == "wsclient" ? Visibility.Visible : Visibility.Collapsed;
        RabbitMqSinkFields.Visibility = normalized == "rabbitmq" ? Visibility.Visible : Visibility.Collapsed;
        KafkaSinkFields.Visibility = normalized == "kafka" ? Visibility.Visible : Visibility.Collapsed;
        ReliableSinkFields.Visibility = normalized == "wsserver" ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnAddSinkClick(object sender, RoutedEventArgs e)
    {
        ApplyCurrentDeviceForm();
        ApplyCurrentSinkForm();

        var created = await PromptForSinkAsync();
        if (created is null)
        {
            return;
        }

        var (id, type, bind, port) = created.Value;
        var config = type switch
        {
            "wsServer" => new SinkConfig
            {
                Type = "wsServer",
                Bind = bind,
                Port = port
            },
            "rabbitmq" => new SinkConfig
            {
                Type = "rabbitmq",
                Host = "127.0.0.1",
                Port = 5672,
                VirtualHost = "/",
                UserName = "guest",
                Password = "guest",
                Exchange = "sim.device-agent",
                ExchangeType = "topic",
                RoutingKey = "device.{deviceId}"
            },
            "kafka" => new SinkConfig
            {
                Type = "kafka",
                BootstrapServers = "127.0.0.1:9092",
                Topic = "devices.{deviceId}",
                MessageKey = "{agentId}:{deviceId}"
            },
            _ => new SinkConfig
            {
                Type = "wsClient",
                Url = string.Empty
            }
        };

        var entry = new SinkEntry(id, config);
        _sinkEntries.Add(entry);

        _suppressSinkSelection = true;
        ConfigSinkList.SelectedItem = entry;
        _suppressSinkSelection = false;

        _currentSink = entry;
        LoadSinkForm(entry);

        if (_currentDevice is not null)
        {
            RebuildDeviceSinkChecks(_currentDevice.Config.Sinks ?? []);
        }
    }

    private async Task<(string Id, string Type, string? Bind, int Port)?> PromptForSinkAsync()
    {
        var idBox = new TextBox
        {
            Text = NextSinkId(),
            PlaceholderText = "出口编号"
        };
        var typeCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        typeCombo.Items.Add(new ComboBoxItem { Content = "wsServer" });
        typeCombo.Items.Add(new ComboBoxItem { Content = "wsClient" });
        typeCombo.Items.Add(new ComboBoxItem { Content = "rabbitmq" });
        typeCombo.Items.Add(new ComboBoxItem { Content = "kafka" });
        typeCombo.SelectedIndex = 0;
        ConfigureDropDownPopup(typeCombo);

        var bindBox = new TextBox
        {
            Text = "127.0.0.1",
            PlaceholderText = "127.0.0.1"
        };
        var portBox = new NumberBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Minimum = 1,
            Maximum = 65535,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Value = NextWsPort()
        };

        var wsPanel = new StackPanel { Spacing = 8 };
        wsPanel.Children.Add(new TextBlock
        {
            Text = "监听地址（仅回环）",
            FontSize = 12,
            Opacity = 0.7
        });
        wsPanel.Children.Add(bindBox);
        wsPanel.Children.Add(new TextBlock
        {
            Text = "监听端口（1–65535）",
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(0, 8, 0, 0)
        });
        wsPanel.Children.Add(portBox);

        var panel = new StackPanel { Spacing = 8, MinWidth = 340 };
        panel.Children.Add(new TextBlock
        {
            Text = "出口编号（字母、数字、. _ -）",
            FontSize = 12,
            Opacity = 0.7
        });
        panel.Children.Add(idBox);
        panel.Children.Add(new TextBlock
        {
            Text = "类型",
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(0, 8, 0, 0)
        });
        panel.Children.Add(typeCombo);
        panel.Children.Add(wsPanel);

        typeCombo.SelectionChanged += (_, _) =>
        {
            wsPanel.Visibility = string.Equals(GetComboText(typeCombo), "wsServer", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        };

        var dialog = new ContentDialog
        {
            Title = "新增出口",
            Content = panel,
            PrimaryButtonText = "新增",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        dialog.Opened += (_, _) =>
        {
            idBox.Focus(FocusState.Programmatic);
            idBox.SelectAll();
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var id = idBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            ShowConfigMessage("出口编号不能为空。", InfoBarSeverity.Error);
            return null;
        }

        if (!id.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
        {
            ShowConfigMessage("出口编号只能使用字母、数字、'.'、'_' 或 '-'。", InfoBarSeverity.Error);
            return null;
        }

        if (_sinkEntries.Any(sink => string.Equals(sink.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            ShowConfigMessage($"出口编号 '{id}' 已存在。", InfoBarSeverity.Error);
            return null;
        }

        var type = NormalizeSinkType(GetComboText(typeCombo)) switch
        {
            "wsserver" => "wsServer",
            "rabbitmq" => "rabbitmq",
            "kafka" => "kafka",
            _ => "wsClient"
        };

        if (type != "wsServer")
        {
            return (id, type, null, 0);
        }

        var bind = bindBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(bind))
        {
            ShowConfigMessage("wsServer 出口必须填写监听地址。", InfoBarSeverity.Error);
            return null;
        }

        if (double.IsNaN(portBox.Value) || portBox.Value < 1 || portBox.Value > 65535)
        {
            ShowConfigMessage("wsServer 出口必须填写 1–65535 的监听端口。", InfoBarSeverity.Error);
            return null;
        }

        return (id, type, bind, (int)Math.Round(portBox.Value));
    }

    private static void ConfigureDropDownPopup(ComboBox combo)
    {
        combo.Loaded += (_, _) => ApplyDropDownPlacement(combo);
        combo.DropDownOpened += (_, _) => ApplyDropDownPlacement(combo);
    }

    private static void ApplyDropDownPlacement(ComboBox combo)
    {
        combo.ApplyTemplate();
        var popup = FindDescendant<Popup>(combo) ?? combo.FindName("Popup") as Popup;
        if (popup is null)
        {
            return;
        }

        popup.ShouldConstrainToRootBounds = false;
        popup.DesiredPlacement = PopupPlacementMode.Bottom;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private async void OnDeleteSinkClick(object sender, RoutedEventArgs e)
    {
        if (ConfigSinkList.SelectedItem is not SinkEntry entry)
        {
            return;
        }

        if (!await ConfirmAsync("删除出口", $"确定删除出口 {entry.Id}？引用它的设备会同步移除该出口。"))
        {
            return;
        }

        ApplyCurrentDeviceForm();
        ApplyCurrentSinkForm();

        _sinkEntries.Remove(entry);
        foreach (var device in _deviceEntries)
        {
            device.Config.Sinks = device.Config.Sinks
                .Where(sinkId => !string.Equals(sinkId, entry.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        _currentSink = null;
        _suppressSinkSelection = true;
        ConfigSinkList.SelectedIndex = _sinkEntries.Count > 0 ? 0 : -1;
        _suppressSinkSelection = false;

        _currentSink = ConfigSinkList.SelectedItem as SinkEntry;
        LoadSinkForm(_currentSink);

        if (_currentDevice is not null)
        {
            RebuildDeviceSinkChecks(_currentDevice.Config.Sinks ?? []);
        }
    }

    private string NextSinkId()
    {
        const string baseName = "center";
        var index = 1;

        while (true)
        {
            var candidate = index == 1 ? baseName : baseName + index.ToString(CultureInfo.InvariantCulture);
            if (_sinkEntries.All(sink => !string.Equals(sink.Id, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }

            index++;
        }
    }

    private int NextWsPort()
    {
        var port = 18080;
        while (_sinkEntries.Any(entry =>
                   string.Equals(entry.Config.Type, "wsServer", StringComparison.OrdinalIgnoreCase) &&
                   entry.Config.Port == port))
        {
            port++;
        }

        return port;
    }

    private bool ValidateSinkEntries()
    {
        if (_sinkEntries.Any(sink => string.IsNullOrWhiteSpace(sink.Id)))
        {
            ShowConfigMessage("出口编号不能为空。", InfoBarSeverity.Error);
            return false;
        }

        var duplicate = _sinkEntries
            .GroupBy(sink => sink.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            ShowConfigMessage($"出口编号 '{duplicate.Key}' 重复。", InfoBarSeverity.Error);
            return false;
        }

        return true;
    }

    private async void OnSaveConfigClick(object sender, RoutedEventArgs e)
    {
        if (!_configReady || _editorAgent is null)
        {
            return;
        }

        ApplyAgentForm();
        ApplyCurrentDeviceForm();
        ApplyCurrentSinkForm();

        if (!ValidateSinkEntries())
        {
            return;
        }

        var config = new AgentConfig
        {
            Agent = _editorAgent,
            Sinks = _sinkEntries.ToDictionary(
                entry => entry.Id,
                entry => entry.Config,
                StringComparer.OrdinalIgnoreCase),
            Devices = _deviceEntries.Select(entry => entry.Config).ToList()
        };

        var errors = AgentConfigValidator.Validate(config);
        if (errors.Count > 0)
        {
            ShowConfigMessage($"配置校验失败：{string.Join("；", errors)}", InfoBarSeverity.Error);
            return;
        }

        try
        {
            AgentConfigWriter.Save(_configPath, config);
        }
        catch (Exception ex)
        {
            ShowConfigMessage($"配置保存失败：{ex.Message}", InfoBarSeverity.Error);
            return;
        }

        foreach (var entry in _deviceEntries)
        {
            entry.IsNew = false;
        }

        DeviceIdBox.IsReadOnly = true;
        SaveStatusText.Text = $"已保存 {DateTimeOffset.Now:HH:mm:ss}";
        CaptureEditorSnapshot();

        if (_host is null)
        {
            ShowConfigMessage(
                "配置已保存；当前没有运行中的实例，请重启 DeviceAgent 后生效。",
                InfoBarSeverity.Success);
            return;
        }

        try
        {
            var result = await _host.Reloader.ReloadAsync(CancellationToken.None);
            ShowConfigMessage(
                result.Success
                    ? "配置已保存并应用。"
                    : $"配置已保存，但应用失败：{result.Message}",
                result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowConfigMessage($"配置已保存，但应用失败：{ex.Message}", InfoBarSeverity.Warning);
        }

        SyncReloadBaseline();
        Refresh();
    }

    private async void OnNewConfigClick(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_configPath) &&
            !await ConfirmAsync("新建默认配置", "当前配置文件无法载入，继续将用新配置覆盖它。是否继续？"))
        {
            return;
        }

        var defaults = AgentConfigDefaults.Create(
            Environment.MachineName,
            Path.GetDirectoryName(_configPath)!);
        _editorAgent = defaults.Agent;

        _suppressDeviceSelection = true;
        _suppressSinkSelection = true;
        _deviceEntries.Clear();
        _sinkEntries.Clear();

        foreach (var device in defaults.Devices)
        {
            _deviceEntries.Add(new DeviceEntry(device, isNew: true));
        }

        foreach (var (sinkId, sink) in defaults.Sinks)
        {
            _sinkEntries.Add(new SinkEntry(sinkId, sink));
        }

        _suppressDeviceSelection = false;
        _suppressSinkSelection = false;

        _currentDevice = null;
        _currentSink = null;
        _configReady = true;
        _editorSnapshot = null;
        NewConfigButton.Visibility = Visibility.Collapsed;
        SaveConfigButton.IsEnabled = true;
        SaveStatusText.Text = string.Empty;

        LoadAgentForm();
        ConfigNavList.SelectedIndex = 0;
        SelectInitialEntries();
        ShowConfigMessage(
            "已创建默认配置草稿：包含本机出口和一个示例文件设备，确认后保存。",
            InfoBarSeverity.Informational);
    }

    private async void OnReloadEditorClick(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("从磁盘重新载入", "未保存的界面修改会丢失。是否继续？", "重新载入"))
        {
            return;
        }

        InitializeConfigEditor();
    }

    private void OnOpenConfigClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = File.Exists(_configPath)
                ? _configPath
                : Path.GetDirectoryName(_configPath)!;
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowConfigMessage($"无法打开配置文件：{ex.Message}", InfoBarSeverity.Warning);
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primaryText = "确定")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void OnStartWithWindowsToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressStartupToggle || _startupGuard is null)
        {
            return;
        }

        var enabled = StartWithWindowsToggle.IsOn;
        try
        {
            _startupGuard.SetStartWithWindows(enabled);
            ShowConfigMessage(
                enabled
                    ? "已开启开机自启：登录 Windows 后由守护进程拉起并驻留托盘。"
                    : "已关闭开机自启；当前运行的实例不受影响。",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowConfigMessage($"开机自启设置失败：{ex.Message}", InfoBarSeverity.Error);
            _suppressStartupToggle = true;
            StartWithWindowsToggle.IsOn = !enabled;
            _suppressStartupToggle = false;
        }
    }

    private void ShowConfigMessage(string message, InfoBarSeverity severity)
    {
        ConfigInfoBar.Severity = severity;
        ConfigInfoBar.Message = message;
        ConfigInfoBar.IsOpen = true;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    private static string NormalizeTransportType(string? type)
        => type?.Trim().ToLowerInvariant() switch
        {
            "tcp" => "tcp",
            "file" => "file",
            _ => "serial"
        };

    private static string NormalizeSinkType(string? type)
        => type?.Trim().ToLowerInvariant() switch
        {
            "wsserver" => "wsserver",
            "rabbitmq" => "rabbitmq",
            "kafka" => "kafka",
            _ => "wsclient"
        };

    private static string NormalizeCodecType(string? type)
        => type?.Trim().ToLowerInvariant() switch
        {
            "passthrough" => "passthrough",
            "csv" => "csv",
            _ => "delimiter"
        };

    private static string GetComboText(ComboBox combo)
    {
        if (combo.IsEditable && !string.IsNullOrWhiteSpace(combo.Text))
        {
            return combo.Text.Trim();
        }

        return combo.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? string.Empty
            : combo.SelectedItem?.ToString() ?? string.Empty;
    }

    private static void SelectComboByText(ComboBox combo, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            combo.SelectedIndex = -1;
            if (combo.IsEditable)
            {
                combo.Text = string.Empty;
            }

            return;
        }

        foreach (var item in combo.Items)
        {
            var itemText = item is ComboBoxItem comboItem
                ? comboItem.Content?.ToString()
                : item?.ToString();
            if (string.Equals(itemText, text, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = -1;
        if (combo.IsEditable)
        {
            combo.Text = text;
        }
    }

    private static int IntValue(NumberBox box, int fallback)
        => double.IsNaN(box.Value) ? fallback : (int)Math.Round(box.Value);

    private static double DoubleValue(NumberBox box, double fallback)
        => double.IsNaN(box.Value) ? fallback : box.Value;

    private static int ParseIntText(string? text, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static string DefaultIfEmpty(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static List<string> ParseLines(string text)
        => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static string FormatColumns(IReadOnlyDictionary<string, string>? columns)
        => columns is null
            ? string.Empty
            : string.Join(Environment.NewLine, columns.Select(pair => $"{pair.Key}={pair.Value}"));

    private static Dictionary<string, string> ParseColumns(string text)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                continue;
            }

            columns[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return columns;
    }

    private static string EscapeDelimiter(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                '\\' => "\\\\",
                _ => character.ToString()
            });
        }

        return builder.ToString();
    }

    private static string UnescapeDelimiter(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' && index + 1 < value.Length)
            {
                index++;
                builder.Append(value[index] switch
                {
                    'r' => '\r',
                    'n' => '\n',
                    't' => '\t',
                    _ => value[index]
                });
            }
            else
            {
                builder.Append(value[index]);
            }
        }

        return builder.ToString();
    }
}
