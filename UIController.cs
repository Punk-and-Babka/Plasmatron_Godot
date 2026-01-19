using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Ports;
using System.Linq;

public partial class UIController : Control
{
    #region Экспортируемые элементы
    [ExportGroup("Ссылки")]
    [Export] private CoordinateGrid _grid;
    [Export] private LineEdit _positionInput;
    [Export] private Button _moveButton;
    [Export] private Button _stopButton;
    [Export] private Burner _burner;
    [Export] private HSlider _speedSlider;
    [Export] private Label _positionLabel;
    [Export] private LineEdit _speedLabel;
    [Export] private MenuBar _mainMenu;

    [ExportGroup("Окна")]
    [Export] private PackedScene _aboutWindowScene;
    [Export] private PackedScene _portWindowScene;

    [ExportGroup("Управление")]
    [Export] private Button _startSequenceButton;
    [Export] private SpinBox _cyclesInput;
    [Export] private Label _statusLabel;
    [Export] private Button _emergencyStopButton;
    [Export] private SpeedInputHandler _speedInput;
    [Export] private float DefaultSpeed = 100f;
    [Export] private SpinBox _pauseInput;
    [Export] private Button _pauseButton;

    [ExportGroup("Точки")]
    [Export] private Button _savePoint0Button;
    [Export] private Button _savePoint1Button;
    [Export] private Button _savePoint2Button;
    [Export] private Label _point0Label;
    [Export] private Label _point1Label;
    [Export] private Label _point2Label;
    [Export] private Color[] _pointColors = { Colors.Green, Colors.Red, Colors.Blue };

    [ExportGroup("Меню")]
    [Export] private PopupMenu settingsMenu;
    [Export] private PopupMenu helpMenu;
    #endregion

    private SerialPort _serialPort;
    private PortSelectionWindow _portWindow;

    // ИСПРАВЛЕНО: Единственное объявление Vector2
    private Vector2[] _savedPoints = new Vector2[3];
    private Vector2 _lastPosition;

    private float _lastSpeed;
    private float _updateTimer;
    private string _lastText = string.Empty;
    private float _lastSentSpeed = -1;
    private float _lastSentSliderSpeed = -1;
    private bool _isManualPaused;

    public override void _Ready()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        InitializeEventHandlers();
        InitializeMainMenu();
        ShowPortSelectionWindow();
        CallDeferred(nameof(InitializeDefaultSpeed));
    }

    private void InitializeDefaultSpeed()
    {
        _lastSentSliderSpeed = DefaultSpeed;
        if (_burner != null) _burner.MaxSpeedMM = DefaultSpeed;
        UpdateSpeedSlider(DefaultSpeed);
        SendSpeedCommand(DefaultSpeed, true);
    }

    private void InitializeEventHandlers()
    {
        if (_burner != null)
        {
            _burner.PositionChanged += OnPositionUpdated;
            _burner.SpeedChanged += OnSpeedUpdated;
            _burner.PauseUpdated += OnPauseUpdated;
        }

        if (_speedSlider != null) _speedSlider.ValueChanged += OnSpeedSliderChanged;
        if (_speedInput != null) _speedInput.SpeedChanged += OnSpeedInputChanged;

        _moveButton.Pressed += OnMoveButtonPressed;
        _stopButton.Pressed += OnStopButtonPressed;

        _savePoint0Button.Pressed += () => SavePoint(0);
        _savePoint1Button.Pressed += () => SavePoint(1);
        _savePoint2Button.Pressed += () => SavePoint(2);

        _startSequenceButton.Pressed += OnStartSequencePressed;
        _emergencyStopButton.Pressed += OnEmergencyStopPressed;

        _pauseInput.ValueChanged += OnPauseChanged;
        _pauseButton.Pressed += OnPauseButtonPressed;
    }

    private void InitializeMainMenu()
    {
        if (_mainMenu == null) return;
        if (settingsMenu == null) settingsMenu = _mainMenu.GetChild(0) as PopupMenu;
        if (helpMenu == null) helpMenu = _mainMenu.GetChild(1) as PopupMenu;

        if (settingsMenu != null)
        {
            settingsMenu.Clear();
            settingsMenu.AddItem("Подключение к COM-порту", 0);
            settingsMenu.AddItem("Расчет скорости (Диаметр)", 1);
            settingsMenu.AddItem("Настройки ускорения", 2);
            settingsMenu.AddItem("Добавить деталь", 3);

            if (settingsMenu.IsConnected(PopupMenu.SignalName.IdPressed, new Callable(this, nameof(OnSettingsMenuIdPressed))))
                settingsMenu.Disconnect(PopupMenu.SignalName.IdPressed, new Callable(this, nameof(OnSettingsMenuIdPressed)));
            settingsMenu.IdPressed += OnSettingsMenuIdPressed;
        }

        if (helpMenu != null)
        {
            helpMenu.Clear();
            helpMenu.AddItem("Инструкция к скриптам", 0);
            helpMenu.AddSeparator();
            helpMenu.AddItem("О программе", 1);

            if (helpMenu.IsConnected(PopupMenu.SignalName.IdPressed, new Callable(this, nameof(OnHelpMenuIdPressed))))
                helpMenu.Disconnect(PopupMenu.SignalName.IdPressed, new Callable(this, nameof(OnHelpMenuIdPressed)));
            helpMenu.IdPressed += OnHelpMenuIdPressed;
        }
    }

    private void OnSettingsMenuIdPressed(long id)
    {
        switch (id)
        {
            case 0: ShowPortSelectionWindow(); break;
            case 1: GD.Print("TODO: Calc Speed"); break;
            case 2: GD.Print("TODO: Accel Settings"); break;
            case 3: GD.Print("TODO: Workpiece"); break;
        }
    }

    private void OnHelpMenuIdPressed(long id)
    {
        switch (id)
        {
            case 0: GD.Print("TODO: Help"); break;
            case 1: ShowAboutWindow(); break;
        }
    }

    private void ShowPortSelectionWindow()
    {
        if (_portWindow != null && GodotObject.IsInstanceValid(_portWindow))
        {
            _portWindow.PopupCentered();
            return;
        }
        if (_portWindowScene != null)
        {
            _portWindow = _portWindowScene.Instantiate<PortSelectionWindow>();
            AddChild(_portWindow);
            _portWindow.ConnectionConfirmed += OnPortSelected;
            _portWindow.PopupCentered();
        }
    }

    private void ShowAboutWindow()
    {
        if (_aboutWindowScene != null)
        {
            var win = _aboutWindowScene.Instantiate<Window>();
            AddChild(win);
            win.PopupCentered();
        }
    }

    private void OnPortSelected()
    {
        if (_portWindow.UseMockPort) InitializeMockPort();
        else InitializeSerialPort();
    }

    private void InitializeSerialPort()
    {
        try
        {
            if (_serialPort != null && _serialPort.IsOpen) _serialPort.Close();
            _serialPort = new SerialPort(_portWindow.SelectedPort, _portWindow.SelectedBaudRate, Parity.None, 8, StopBits.One);
            _serialPort.Open();
            _serialPort.DataReceived += SerialDataReceived;
            GD.Print($"Connected to {_portWindow.SelectedPort}");
        }
        catch (Exception ex) { ShowError($"Error: {ex.Message}"); }
    }

    private void InitializeMockPort() { /* Mock Logic */ }

    private void SerialDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try { CallDeferred(nameof(ProcessIncomingData), _serialPort.ReadLine().Trim()); } catch { }
    }

    private void ProcessIncomingData(string data)
    {
        if (data.StartsWith("v") && int.TryParse(data[1..], out int val))
        {
            _lastSpeed = val / 0.8f;
            UpdateUIDisplay();
        }
    }

    public void SendCommand(string command)
    {
        if (_portWindow?.UseMockPort != true && _serialPort?.IsOpen == true)
        {
            _serialPort.WriteLine(command);
            GD.Print($"[SENT] {command}");
        }
    }

    public void SendSpeedCommand(float speedMM, bool forceSend = false)
    {
        if (forceSend || !Mathf.IsEqualApprox(_lastSentSpeed, speedMM))
        {
            int val = (int)(Mathf.Clamp(speedMM, 0, 500) * 0.8f);
            GD.Print($"[SPEED] {speedMM:F0} -> v{val}");
            if (_portWindow?.UseMockPort != true) SendCommand($"v{val}");
            _lastSentSpeed = speedMM;
        }
    }

    private void OnSpeedSliderChanged(double value)
    {
        float newSpeed = (float)Math.Round(value, 0);
        if (Math.Abs(_lastSentSliderSpeed - newSpeed) < 1f) return;
        _lastSentSliderSpeed = newSpeed;
        UpdateSpeedSlider(newSpeed);
        SendSpeedCommand(newSpeed);
        if (_burner != null) _burner.MaxSpeedMM = newSpeed;
    }

    private void OnSpeedUpdated(float speed)
    {
        _lastSpeed = speed;
        UpdateUIDisplay();
    }

    private void OnSpeedInputChanged(float newSpeed)
    {
        _speedSlider.Value = newSpeed;
        if (_burner != null) _burner.MaxSpeedMM = newSpeed;
        SendSpeedCommand(newSpeed);
        UpdateSpeedSlider(newSpeed);
    }

    public void UpdateSpeedSlider(float speed)
    {
        _speedInput.SetSpeed(speed);
        _speedSlider.Value = speed;
        _speedLabel.Text = $"Скорость: {speed:N0} мм/с";
    }

    public override void _Process(double delta)
    {
        _updateTimer += (float)delta;
        if (_burner != null) _lastPosition = _burner.PositionMM; // Читаем Vector2
        if (_updateTimer > 0.05f)
        {
            UpdateUIDisplay();
            _updateTimer = 0;
        }
    }

    private void UpdateUIDisplay()
    {
        string newText = $"Позиция: {_lastPosition.X:N1} мм\n" +
                         $"Скорость: {_lastSpeed:N0} мм/сек\n";
        if (newText != _lastText)
        {
            _positionLabel.Text = newText;
            _lastText = newText;
        }
    }

    private void OnPositionUpdated(Vector2 position)
    {
        _lastPosition = position;
        if (_burner != null && _burner.IsAutoSequenceActive)
            UpdateStatus($"Циклов осталось: {_burner.CyclesRemaining}");
    }

    private void SavePoint(int index)
    {
        _savedPoints[index] = _lastPosition; // Сохраняем Vector2
        Label l = index switch { 0 => _point0Label, 1 => _point1Label, 2 => _point2Label, _ => null };
        if (l != null)
        {
            l.Text = $"{_savedPoints[index].X:F0} мм";
            l.AddThemeColorOverride("font_color", _pointColors[index]);
        }
        _grid?.UpdatePoints(_savedPoints, _pointColors);
        GD.Print($"Точка {index}: {_savedPoints[index]}");
    }

    private void OnMoveButtonPressed()
    {
        if (float.TryParse(_positionInput.Text, out float targetX))
            _burner?.MoveToPosition(new Vector2(targetX, 0)); // Отправляем Vector2
    }

    private void OnStopButtonPressed() => _burner?.StopAutoMovement();

    private void OnStartSequencePressed()
    {
        if (_savedPoints[0].X <= 0 || _savedPoints[1].X <= 0 || _savedPoints[2].X <= 0)
        {
            ShowError("Точки не заданы!");
            return;
        }
        _burner?.StartAutoSequence(_savedPoints, (int)_cyclesInput.Value);
        UpdateStatus("Старт...");
    }

    private void UpdateStatus(string msg) => _statusLabel.Text = msg;

    private void OnPauseChanged(double val)
    {
        if (_burner != null) _burner.PauseDuration = (float)val;
    }

    private void OnPauseUpdated(float time)
    {
        if (time > 0) UpdateStatus($"Пауза: {time:N1}");
        else UpdateStatus("Работа...");
    }

    private void OnPauseButtonPressed()
    {
        _isManualPaused = !_isManualPaused;
        _burner?.SetManualPause(_isManualPaused);
        _pauseButton.Text = _isManualPaused ? "Продолжить" : "Пауза";
        _pauseButton.AddThemeColorOverride("font_color", _isManualPaused ? Colors.Red : Colors.White);
    }

    private void OnEmergencyStopPressed()
    {
        SendCommand("s");
        _burner?.EmergencyStop();
        GD.Print("STOP");
    }

    private void ShowError(string msg)
    {
        var d = new AcceptDialog { Title = "Error", DialogText = msg };
        GetTree().Root.AddChild(d);
        d.PopupCentered();
    }

    public override void _ExitTree()
    {
        _serialPort?.Close();
        _serialPort?.Dispose();
    }
}