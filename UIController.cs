using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Ports;
using System.Text.RegularExpressions;

public partial class UIController : Control
{
    #region Экспорты
    [ExportGroup("Ссылки")]
    [Export] private CoordinateGrid _grid;
    [Export] private LineEdit _positionInput; // Поле "Переместить"
    [Export] private Button _moveButton;
    [Export] private Button _stopButton;
    [Export] private Burner _burner;

    [Export] private HSlider _speedSlider;
    [Export] private LineEdit _speedInput;

    [ExportGroup("Метки Значений")]
    [Export] private Label _lblPositionValue;
    [Export] private Label _lblSpeedValue;

    [ExportGroup("Меню")]
    [Export] private MenuBar _mainMenu;
    [Export] private PopupMenu settingsMenu;
    [Export] private PopupMenu helpMenu;

    [ExportGroup("Окна")]
    [Export] private PackedScene _aboutWindowScene;
    [Export] private PackedScene _portWindowScene;

    [ExportGroup("Управление")]
    [Export] private Button _startSequenceButton;
    [Export] private SpinBox _cyclesInput;
    [Export] private Label _statusLabel;
    [Export] private Button _emergencyStopButton;

    [Export] private float DefaultSpeed = 100f;
    [Export] private SpinBox _pauseInput;
    [Export] private Button _pauseButton;

    // --- ИЗМЕНЕНИЕ: Новая группа для ручного ввода точек ---
    [ExportGroup("Точки (Inputs)")]
    [Export] private LineEdit _inputPoint0;
    [Export] private LineEdit _inputPoint1;
    [Export] private LineEdit _inputPoint2;

    [ExportGroup("Кнопки захвата")]
    [Export] private Button _btnGetPoint0;
    [Export] private Button _btnGetPoint1;
    [Export] private Button _btnGetPoint2;

    [Export] private Color[] _pointColors = { Colors.Green, Colors.Red, Colors.Blue };

    [ExportGroup("Скриптинг")]
    [Export] private CodeEdit _scriptInput;      // Окно ввода кода
    [Export] private Button _runScriptButton;    // Кнопка Старт (скрипта)
    [Export] private Button _stopScriptButton;   // Кнопка Стоп (скрипта)
    [Export] private ScriptInterpreter _interpreter; // Ссылка на узел логики

    [ExportGroup("Стрелки (ArrowPad)")]
    [Export] private Button _btnUp;
    [Export] private Button _btnDown;
    [Export] private Button _btnLeft;
    [Export] private Button _btnRight;
    #endregion

    private SerialPort _serialPort;
    private PortSelectionWindow _portWindow;

    private Vector2 _lastPosition;
    private float _lastSpeed;

    private float _updateTimer;
    private float _lastSentSpeed = -1;
    private float _lastSentSliderSpeed = -1;
    private bool _isManualPaused;

    public override void _Ready()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        InitializeEventHandlers();
        InitializeMainMenu();
        InitializeSlider();
        ShowPortSelectionWindow();

        CallDeferred(nameof(InitializeDefaultSpeed));
    }

    private void InitializeSlider()
    {
        if (_speedSlider != null)
        {
            _speedSlider.MinValue = 0;
            _speedSlider.MaxValue = 500;
            _speedSlider.Step = 1;
            _speedSlider.TickCount = 11;
            _speedSlider.TicksOnBorders = true;
        }
    }

    private void InitializeDefaultSpeed()
    {
        _lastSentSliderSpeed = DefaultSpeed;
        if (_burner != null) _burner.SetMovementSpeed(DefaultSpeed);

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
            _burner.SequenceFinished += () => UpdateStatus("Цикл завершен");
        }

        if (_speedSlider != null) _speedSlider.ValueChanged += OnSpeedSliderChanged;

        if (_speedInput != null)
        {
            _speedInput.TextSubmitted += (text) =>
            {
                string cleanText = Regex.Replace(text, @"[^\d.,]", "").Replace(",", ".");

                if (float.TryParse(cleanText, NumberStyles.Any, CultureInfo.InvariantCulture, out float val))
                {
                    OnSpeedInputChanged(val);
                    _speedInput.ReleaseFocus();
                }
                else
                {
                    UpdateSpeedSlider(_lastSentSliderSpeed);
                }
            };

            _speedInput.FocusEntered += () =>
            {
                _speedInput.Text = _lastSentSliderSpeed.ToString("F0");
            };

            _speedInput.FocusExited += () =>
            {
                UpdateSpeedSlider(_lastSentSliderSpeed);
            };
        }

        _moveButton.Pressed += OnMoveButtonPressed;
        _stopButton.Pressed += OnStopButtonPressed;

        if (_inputPoint0 != null) _inputPoint0.TextChanged += (t) => UpdateGridVisuals();
        if (_inputPoint1 != null) _inputPoint1.TextChanged += (t) => UpdateGridVisuals();
        if (_inputPoint2 != null) _inputPoint2.TextChanged += (t) => UpdateGridVisuals();

        // --- ИЗМЕНЕНИЕ: Подключаем кнопки захвата (GET) ---
        if (_btnGetPoint0 != null) _btnGetPoint0.Pressed += () => SetInputFromBurner(_inputPoint0);
        if (_btnGetPoint1 != null) _btnGetPoint1.Pressed += () => SetInputFromBurner(_inputPoint1);
        if (_btnGetPoint2 != null) _btnGetPoint2.Pressed += () => SetInputFromBurner(_inputPoint2);

        _startSequenceButton.Pressed += OnStartSequencePressed;
        _emergencyStopButton.Pressed += OnEmergencyStopPressed;

        _pauseInput.ValueChanged += OnPauseChanged;
        _pauseButton.Pressed += OnPauseButtonPressed;

        // Подключаем кнопки скриптов
        if (_runScriptButton != null)
            _runScriptButton.Pressed += OnRunScriptPressed;

        if (_stopScriptButton != null)
            _stopScriptButton.Pressed += OnStopScriptPressed;

        // Подключаем стрелки управления
        SetupArrowButton(_btnUp, new Vector2(0, 1));  // Вверх (+Y в логике горелки)
        SetupArrowButton(_btnDown, new Vector2(0, -1)); // Вниз
        SetupArrowButton(_btnLeft, new Vector2(-1, 0)); // Влево
        SetupArrowButton(_btnRight, new Vector2(1, 0)); // Вправо
    }

    public override void _Process(double delta)
    {
        _updateTimer += (float)delta;

        if (_burner != null)
        {
            _lastPosition = _burner.PositionMM;
            _lastSpeed = _burner.CurrentSpeedScalar;
        }

        if (_updateTimer > 0.05f)
        {
            UpdateUIDisplay();
            _updateTimer = 0;
        }
    }

    private void UpdateUIDisplay()
    {
        if (_lblPositionValue != null)
            _lblPositionValue.Text = $"X: {_lastPosition.X:F1}  Y: {_lastPosition.Y:F1} мм";

        if (_lblSpeedValue != null)
            _lblSpeedValue.Text = $"{_lastSpeed:F0} мм/сек";
    }

    // --- СИНХРОНИЗАЦИЯ СКОРОСТИ ---

    private void OnSpeedSliderChanged(double value)
    {
        float newSpeed = (float)Math.Round(value, 0);
        if (Math.Abs(_lastSentSliderSpeed - newSpeed) < 1f) return;

        _lastSentSliderSpeed = newSpeed;
        ApplySpeedChange(newSpeed);
    }

    private void OnSpeedInputChanged(float newSpeed)
    {
        ApplySpeedChange(newSpeed);
    }

    private void OnSpeedUpdated(float speed)
    {
        _lastSpeed = speed;
        UpdateUIDisplay();

        if (Math.Abs(_lastSentSliderSpeed - speed) > 1.0f)
        {
            _lastSentSliderSpeed = speed;
            UpdateSpeedSlider(speed);
        }
    }

    private void ApplySpeedChange(float newSpeed)
    {
        UpdateSpeedSlider(newSpeed);
        if (_burner != null) _burner.SetMovementSpeed(newSpeed);
        SendSpeedCommand(newSpeed);
        HighlightSpeedLabel();
    }

    public void UpdateSpeedSlider(float speed)
    {
        if (_speedSlider != null)
            _speedSlider.SetValueNoSignal(speed);

        if (_speedInput != null && !_speedInput.HasFocus())
            _speedInput.Text = $"Скорость: {speed:F0} мм/сек";
    }

    private async void HighlightSpeedLabel()
    {
        if (_speedInput == null) return;
        _speedInput.AddThemeColorOverride("font_color", Colors.Yellow);
        await ToSignal(GetTree().CreateTimer(0.3), "timeout");
        if (IsInstanceValid(_speedInput)) _speedInput.RemoveThemeColorOverride("font_color");
    }

    #region Menu & Windows
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
            case 1: GD.Print("TODO: Calc"); break;
            case 2: GD.Print("TODO: Accel"); break;
            case 3: GD.Print("TODO: Piece"); break;
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
        if (_portWindow != null && GodotObject.IsInstanceValid(_portWindow)) { _portWindow.PopupCentered(); return; }
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

    private void OnPortSelected() { if (_portWindow.UseMockPort) InitializeMockPort(); else InitializeSerialPort(); }
    private void InitializeMockPort() { GD.Print("Mock Port Started"); }
    private void InitializeSerialPort()
    {
        try
        {
            if (_serialPort != null && _serialPort.IsOpen) _serialPort.Close();
            _serialPort = new SerialPort(_portWindow.SelectedPort, _portWindow.SelectedBaudRate, Parity.None, 8, StopBits.One);
            _serialPort.Open();
            _serialPort.DataReceived += (s, e) => { try { CallDeferred(nameof(ProcessIncoming), _serialPort.ReadLine()); } catch { } };
        }
        catch (Exception e) { ShowError(e.Message); }
    }
    private void ProcessIncoming(string data) { /* Обработка входящих данных */ }
    #endregion

    #region Movement & Commands

    // Метод для мгновенного обновления визуала
    private void UpdateGridVisuals()
    {
        if (_grid == null) return;
        // Берем текущие значения из полей и сразу рисуем
        Vector2[] currentPoints = GetPointsFromInputs();
        _grid.UpdatePoints(currentPoints, _pointColors);
    }

    // Вспомогательный метод для быстрой привязки
    private void SetupArrowButton(Button btn, Vector2 direction)
    {
        if (btn == null) return;

        // Когда нажали - добавляем направление
        btn.ButtonDown += () => UpdateManualInput(direction);

        // Когда отпустили - вычитаем направление (обнуляем)
        btn.ButtonUp += () => UpdateManualInput(-direction);
    }

    // Текущий вектор нажатия экранных кнопок
    private Vector2 _currentManualVector = Vector2.Zero;

    private void UpdateManualInput(Vector2 change)
    {
        _currentManualVector += change;

        // Защита от дребезга (иногда float может стать 0.00001)
        if (Mathf.Abs(_currentManualVector.X) < 0.1f) _currentManualVector.X = 0;
        if (Mathf.Abs(_currentManualVector.Y) < 0.1f) _currentManualVector.Y = 0;

        // Передаем в горелку
        if (_burner != null)
        {
            _burner.InterfaceInputVector = _currentManualVector;
        }
    }
    private void OnRunScriptPressed()
    {
        if (_interpreter == null)
        {
            ShowError("Интерпретатор не привязан!");
            return;
        }

        if (_scriptInput == null || string.IsNullOrWhiteSpace(_scriptInput.Text))
        {
            ShowError("Введите код скрипта.");
            return;
        }

        _interpreter.RunScript(_scriptInput.Text);
        UpdateStatus("Выполнение скрипта...");
    }

    private void OnStopScriptPressed()
    {
        _interpreter?.StopScript();
        UpdateStatus("Скрипт остановлен.");
    }

    public void SendCommand(string command)
    {
        if (_portWindow?.UseMockPort != true && _serialPort?.IsOpen == true)
        {
            try { _serialPort.WriteLine(command); }
            catch (Exception ex) { ShowError($"Ошибка отправки: {ex.Message}"); }
        }
    }

    public void SendSpeedCommand(float speedMM, bool force = false)
    {
        if (force || !Mathf.IsEqualApprox(_lastSentSpeed, speedMM))
        {
            int val = (int)(Mathf.Clamp(speedMM, 0, 500) * 0.8f);
            SendCommand($"v{val}");
            GD.Print($"[SPEED] {speedMM:F0} -> v{val}");
            _lastSentSpeed = speedMM;
        }
    }

    private void OnMoveButtonPressed()
    {
        // Используем универсальный парсер для одиночного движения
        Vector2 target = ParsePoint(_positionInput.Text);

        // Если Y не задан (0), можно попробовать использовать текущий Y, 
        // но ParsePoint вернет 0, если его нет. 
        // Добавим проверку: если в строке не было разделителя, оставляем Y текущим.
        if (!_positionInput.Text.Contains(";") && !_positionInput.Text.Contains(","))
        {
            float currentY = _burner != null ? _burner.PositionMM.Y : 0;
            target.Y = currentY;
        }

        _burner?.MoveToPosition(target);
    }

    private void OnStopButtonPressed() => _burner?.StopAutoMovement();

    private void OnEmergencyStopPressed()
    {
        _burner?.EmergencyStop();
        SendCommand("s");
    }

    // --- ИЗМЕНЕНИЕ: Новые методы для работы с точками ---

    // 1. Метод захвата позиции (вызывается кнопкой GET)
    private void SetInputFromBurner(LineEdit targetInput)
    {
        if (_burner == null || targetInput == null) return;
        Vector2 pos = _burner.PositionMM;
        targetInput.Text = $"{pos.X:F0}; {pos.Y:F0}";

        UpdateGridVisuals();
    }

    // 2. Метод чтения точек из полей ввода
    public Vector2[] GetPointsFromInputs()
    {
        List<Vector2> points = new List<Vector2>();

        if (_inputPoint0 != null) points.Add(ParsePoint(_inputPoint0.Text));
        if (_inputPoint1 != null) points.Add(ParsePoint(_inputPoint1.Text));
        if (_inputPoint2 != null) points.Add(ParsePoint(_inputPoint2.Text));

        return points.ToArray();
    }

    // 3. Универсальный парсер текста
    private Vector2 ParsePoint(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Vector2.Zero;

        // Удаляем лишнее, разделяем по ; , или пробелу
        string[] parts = text.Split(new char[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

        float x = 0;
        float y = 0;

        if (parts.Length > 0) float.TryParse(parts[0].Replace('.', ','), NumberStyles.Any, CultureInfo.InvariantCulture, out x);
        if (parts.Length > 1) float.TryParse(parts[1].Replace('.', ','), NumberStyles.Any, CultureInfo.InvariantCulture, out y);

        return new Vector2(x, y);
    }

    // ---------------------------------------------------

    private void OnPositionUpdated(Vector2 position)
    {
        if (_burner != null && _burner.IsAutoSequenceActive)
            UpdateStatus($"Циклов осталось: {_burner.CyclesRemaining}");
    }

    private void OnStartSequencePressed()
    {
        Vector2[] points = GetPointsFromInputs();

        _grid?.UpdatePoints(points, _pointColors);

        _burner?.StartAutoSequence(points, (int)_cyclesInput.Value);
    }

    private void OnPauseChanged(double v) { if (_burner != null) _burner.PauseDuration = (float)v; }
    private void OnPauseButtonPressed()
    {
        _isManualPaused = !_isManualPaused;
        _burner?.SetManualPause(_isManualPaused);
        _pauseButton.Text = _isManualPaused ? "Продолжить" : "Пауза";
    }
    private void OnPauseUpdated(float time) => _statusLabel.Text = time > 0 ? $"Пауза: {time:N1} сек" : "Работа";
    private void UpdateStatus(string msg) => _statusLabel.Text = msg;
    private void ShowError(string msg) { var d = new AcceptDialog { DialogText = msg }; GetTree().Root.AddChild(d); d.PopupCentered(); }

    public override void _ExitTree()
    {
        _serialPort?.Close();
        _serialPort?.Dispose();
    }
    #endregion
}