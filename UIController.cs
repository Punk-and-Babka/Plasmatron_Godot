using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Ports;
using System.Linq;

/// <summary>
/// Главный контроллер пользовательского интерфейса.
/// Управляет меню, COM-портом, скоростью и связью с горелкой.
/// </summary>
public partial class UIController : Control
{
    #region Экспортируемые элементы UI
    [ExportGroup("Ссылки")]
    [Export] private CoordinateGrid _grid;
    [Export] private LineEdit _positionInput;
    [Export] private Button _moveButton;
    [Export] private Button _stopButton;
    [Export] private Burner _burner;            // Ссылка на компонент горелки
    [Export] private HSlider _speedSlider;      // Слайдер для регулировки скорости
    [Export] private Label _positionLabel;      // Метка для отображения позиции
    [Export] private LineEdit _speedLabel;      // Метка для отображения скорости
    [Export] private MenuBar _mainMenu;         // Главное меню приложения

    [ExportGroup("Окна и Сцены")]
    [Export] private PackedScene _aboutWindowScene; // Сцена окна "О программе"
    [Export] private PackedScene _portWindowScene;  // Сцена окна выбора порта
    // Сюда позже добавим сцены для новых окон (Task #2, #3, #4, #5)

    [ExportGroup("Управление")]
    [Export] private Button _startSequenceButton;
    [Export] private SpinBox _cyclesInput;
    [Export] private Label _statusLabel;
    [Export] private Button _emergencyStopButton;
    [Export] private SpeedInputHandler _speedInput;
    [Export] private float DefaultSpeed = 100f; // 100 мм/с
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

    // Новые ссылки на меню
    [ExportGroup("Меню")]
    [Export] private PopupMenu settingsMenu;
    [Export] private PopupMenu helpMenu;
    #endregion

    #region Приватные поля
    private SerialPort _serialPort;                 // Объект для работы с COM-портом
    private PortSelectionWindow _portWindow;        // Окно выбора порта
    private float _lastPosition;                    // Последняя зафиксированная позиция
    private float _lastSpeed;                       // Последняя зафиксированная скорость
    private float _updateTimer;                     // Таймер для обновления UI
    private string _lastText = string.Empty;        // Кэш последнего отображаемого текста
    private float _lastSentSpeed = -1;              // Последняя отправленная скорость
    private float _lastSentSliderSpeed = -1;
    private float[] _savedPoints = new float[3];
    private bool _isManualPaused;
    #endregion

    #region Инициализация
    public override void _Ready()
    {
        // Явная настройка культуры для всего приложения (точки вместо запятых)
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        InitializeEventHandlers();  // Настройка обработчиков событий
        InitializeMainMenu();       // Настройка меню (Task #6)

        ShowPortSelectionWindow();  // Показать окно выбора порта при старте

        CallDeferred(nameof(InitializeDefaultSpeed));
    }

    private void InitializeDefaultSpeed()
    {
        _lastSentSliderSpeed = DefaultSpeed;
        if (_burner != null) _burner.MaxSpeedMM = DefaultSpeed;

        UpdateSpeedSlider(DefaultSpeed);
        SendSpeedCommand(DefaultSpeed, true); // true - принудительная отправка
    }

    private void InitializeEventHandlers()
    {
        // Подписка на события горелки
        if (_burner != null)
        {
            _burner.PositionChanged += OnPositionUpdated;
            _burner.SpeedChanged += OnSpeedUpdated;
            _burner.MovementStarted += OnBurnerMovementStarted;
            _burner.MovementStopped += OnBurnerMovementStopped;
            _burner.PauseUpdated += OnPauseUpdated;
        }

        // Подписка на UI
        _speedSlider.ValueChanged += OnSpeedSliderChanged;
        _speedInput.SpeedChanged += OnSpeedInputChanged;
        _speedSlider.TickCount = 11;

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
    #endregion

    #region Логика Меню (Task #6 - Реализация)
    private void InitializeMainMenu()
    {
        if (_mainMenu == null) return;

        // 1. Находим подменю автоматически, если они не привязаны в инспекторе
        if (settingsMenu == null) settingsMenu = _mainMenu.GetChild(0) as PopupMenu;
        if (helpMenu == null) helpMenu = _mainMenu.GetChild(1) as PopupMenu;

        // 2. Настраиваем меню "Настройки"
        if (settingsMenu != null)
        {
            settingsMenu.Clear();
            settingsMenu.AddItem("Подключение к COM-порту", 0);
            settingsMenu.AddItem("Расчет скорости (Диаметр)", 1);
            settingsMenu.AddItem("Настройки ускорения", 2);
            settingsMenu.AddItem("Добавить деталь", 3);

            // Безопасное переподключение сигнала
            if (settingsMenu.IsConnected(PopupMenu.SignalName.IdPressed, new Callable(this, nameof(OnSettingsMenuIdPressed))))
                settingsMenu.Disconnect(PopupMenu.SignalName.IdPressed, new Callable(this, nameof(OnSettingsMenuIdPressed)));

            settingsMenu.IdPressed += OnSettingsMenuIdPressed;
        }

        // 3. Настраиваем меню "Help"
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

    // Обработчик нажатий меню Настройки
    private void OnSettingsMenuIdPressed(long id)
    {
        switch (id)
        {
            case 0: ShowPortSelectionWindow(); break;
            case 1: ShowSpeedCalculationWindow(); break;
            case 2: ShowAccelerationWindow(); break;
            case 3: ShowWorkpieceWindow(); break;
        }
    }

    // Обработчик нажатий меню Help
    private void OnHelpMenuIdPressed(long id)
    {
        switch (id)
        {
            case 0: ShowScriptHelpWindow(); break;
            case 1: ShowAboutWindow(); break;
        }
    }
    #endregion

    #region Управление Окнами (Методы открытия)
    private void ShowPortSelectionWindow()
    {
        // Проверка: если окно уже открыто и валидно, просто показываем его
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
        else
        {
            GD.PrintErr("Сцена PortWindowScene не назначена!");
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
        else GD.Print("Окно 'О программе' не назначено.");
    }

    // --- Заглушки для будущих задач ---
    private void ShowSpeedCalculationWindow() => GD.Print("TODO: Открыть окно расчета скорости (Task #2)");
    private void ShowAccelerationWindow() => GD.Print("TODO: Открыть окно ускорения (Task #3)");
    private void ShowWorkpieceWindow() => GD.Print("TODO: Открыть окно детали (Task #4)");
    private void ShowScriptHelpWindow() => GD.Print("TODO: Открыть справку (Task #5)");
    #endregion

    #region Управление COM-портом
    private void OnPortSelected()
    {
        if (_portWindow.UseMockPort)
        {
            GD.Print("Активирован режим эмуляции");
            InitializeMockPort();
            return;
        }
        InitializeSerialPort();
    }

    private void InitializeSerialPort()
    {
        try
        {
            // Закрываем старый, если есть
            if (_serialPort != null && _serialPort.IsOpen) _serialPort.Close();

            _serialPort = new SerialPort(
                _portWindow.SelectedPort,
                _portWindow.SelectedBaudRate,
                Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 500,
                WriteTimeout = 500
            };

            _serialPort.Open();
            _serialPort.DataReceived += SerialDataReceived;
            GD.Print($"Успешное подключение к {_portWindow.SelectedPort}");
        }
        catch (Exception ex)
        {
            ShowError($"Ошибка подключения: {ex.Message}");
            _serialPort?.Close();
        }
    }

    private void InitializeMockPort()
    {
        var timer = new Timer();
        timer.WaitTime = 0.5f; // Эмуляция прихода данных
        timer.Timeout += () =>
        {
            // Эмуляция чтения (если нужно)
        };
        AddChild(timer);
        timer.Start();
    }
    #endregion

    #region Обработка данных
    private void SerialDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var data = _serialPort.ReadLine().Trim();
            CallDeferred(nameof(ProcessIncomingData), data);
        }
        catch { /* Игнорируем таймауты */ }
    }

    private void ProcessIncomingData(string data)
    {
        try
        {
            if (data.StartsWith("v") && int.TryParse(data[1..], out int val))
            {
                // Обратная конвертация: val / 0.8 = мм/с
                float speedMM = val / 0.8f;
                _lastSpeed = speedMM;
                GD.Print($"Получена скорость: {speedMM:N0} мм/с (raw: {val})");
                UpdateUIDisplay();
            }
        }
        catch (Exception ex)
        {
            ShowError($"Ошибка обработки данных: {ex.Message}");
        }
    }
    #endregion

    #region Управление скоростью
    private int ConvertSpeedToCommandValue(float mmPerSecond)
    {
        mmPerSecond = Mathf.Clamp(mmPerSecond, 0f, 500f);
        // Коэффициент 0.8 (100 мм/с -> 80)
        return (int)(mmPerSecond * 0.8f);
    }

    public void SendSpeedCommand(float speedMM, bool forceSend = false)
    {
        if (forceSend || !Mathf.IsEqualApprox(_lastSentSpeed, speedMM))
        {
            int commandValue = ConvertSpeedToCommandValue(speedMM);

            // Лог (виден даже в Mock режиме)
            GD.Print($"[{DateTime.Now:T}] [DEBUG] {speedMM:F0} мм/с -> v{commandValue}");

            _lastSentSpeed = speedMM;

            // Отправляем в порт только если это НЕ заглушка
            if (_portWindow?.UseMockPort != true)
            {
                SendCommand($"v{commandValue}");
            }
        }
    }

    private void OnSpeedSliderChanged(double value)
    {
        float newSpeed = (float)Math.Round(value, 0); // Округление до целого мм
        if (Math.Abs(_lastSentSliderSpeed - newSpeed) < 1f) return;

        _lastSentSliderSpeed = newSpeed;
        UpdateSpeedSlider(newSpeed);
        SendSpeedCommand(newSpeed);
        if (_burner != null) _burner.MaxSpeedMM = newSpeed;
        HighlightSpeedLabel();
    }

    private void OnSpeedUpdated(float speed)
    {
        _lastSpeed = (float)Math.Round(speed, 2);
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
    #endregion

    #region Основной цикл и Обновление UI
    public override void _Process(double delta)
    {
        _updateTimer += (float)delta;

        // --- ИСПРАВЛЕНИЕ БАГА ЗАСТЫВАНИЯ ---
        // Принудительно читаем позицию из горелки каждый кадр, не полагаясь только на события
        if (_burner != null)
        {
            _lastPosition = _burner.PositionXMM;
        }
        // -----------------------------------

        if (_updateTimer > 0.05f) // Обновление каждые 50 мс
        {
            UpdateUIDisplay();
            _updateTimer = 0f;
        }
    }

    private void UpdateUIDisplay()
    {
        string newText = $"Позиция: {_lastPosition:N1} мм\n"
                       + $"Скорость: {_lastSpeed:N0} мм/сек\n";

        if (newText != _lastText)
        {
            _positionLabel.Text = newText;
            _lastText = newText;
        }
    }

    private void OnPositionUpdated(float position)
    {
        _lastPosition = position;
        if (_burner != null && _burner.IsAutoSequenceActive)
        {
            _burner.HandleMovementCompletion();
            UpdateStatus($"Циклов осталось: {_burner.CyclesRemaining}");
        }
    }
    #endregion

    #region Вспомогательные методы
    public void SendCommand(string command)
    {
        if (_portWindow?.UseMockPort == true) return;

        try
        {
            if (_serialPort?.IsOpen == true)
            {
                _serialPort.WriteLine(command);
                GD.Print($"[{DateTime.Now:T}] Отправлена команда: {command}");
            }
        }
        catch (Exception ex)
        {
            ShowError($"Ошибка отправки: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        var dialog = new AcceptDialog { Title = "Ошибка", DialogText = message };
        GetTree().Root.AddChild(dialog);
        dialog.PopupCentered();
        GD.PrintErr($"[{DateTime.Now:T}] {message}");
    }

    private async void HighlightSpeedLabel()
    {
        if (_speedLabel == null) return;
        _speedLabel.AddThemeColorOverride("font_color", Colors.Yellow);
        await ToSignal(GetTree().CreateTimer(0.3), "timeout");
        _speedLabel.RemoveThemeColorOverride("font_color");
    }
    #endregion

    #region Точки и Статус
    private void SavePoint(int index)
    {
        _savedPoints[index] = _lastPosition;
        UpdatePointLabel(index);
        _grid?.UpdatePoints(_savedPoints, _pointColors);
        GD.Print($"Точка {index} сохранена: {_lastPosition:N1} мм");
    }

    private void UpdatePointLabel(int index)
    {
        Label targetLabel = index switch { 0 => _point0Label, 1 => _point1Label, 2 => _point2Label, _ => null };
        if (targetLabel != null)
        {
            targetLabel.Text = $"{_savedPoints[index]:N1} мм";
            targetLabel.AddThemeColorOverride("font_color", _pointColors[index]);
        }
    }

    private void UpdateStatus(string message) => _statusLabel.Text = message;

    private void OnPauseChanged(double value)
    {
        if (_burner != null) _burner.PauseDuration = (float)value;
        GD.Print($"Установлена пауза: {value} сек");
    }

    private void OnPauseUpdated(float remainingTime) => CallDeferred(nameof(DeferredPauseUpdate), remainingTime);

    private void DeferredPauseUpdate(float remainingTime)
    {
        if (remainingTime > 0) UpdateStatus($"Пауза: {remainingTime:N1} сек");
        else
        {
            string baseMessage = _statusLabel.Text.Contains('\n')
                ? _statusLabel.Text.Split('\n')[0]
                : "Готов к работе";
            UpdateStatus(baseMessage);
            GD.Print("Пауза завершена");
        }
    }

    private void OnPauseButtonPressed()
    {
        _isManualPaused = !_isManualPaused;
        _burner?.SetManualPause(_isManualPaused);
        _pauseButton.Text = _isManualPaused ? "Продолжить" : "Пауза";
        _pauseButton.AddThemeColorOverride("font_color", _isManualPaused ? Colors.Red : Colors.White);
    }
    #endregion

    #region Кнопки Движения
    private void OnBurnerMovementStarted(Burner.MovementDirection dir)
    {
        string command = dir == Burner.MovementDirection.Right ? "f" : "b";
        SendCommand(command);
    }

    private void OnBurnerMovementStopped() => SendCommand("s");

    private void OnMoveButtonPressed()
    {
        if (float.TryParse(_positionInput.Text, out float target)) _burner?.MoveToPosition(target);
        else ShowError("Некорректное значение позиции");
    }

    private void OnStopButtonPressed() => _burner?.StopAutoMovement();

    private void OnEmergencyStopPressed()
    {
        SendCommand("s");
        _burner?.EmergencyStop();
        GD.Print("[ЭКСТРЕННО] Движение прервано");
    }

    private void OnStartSequencePressed()
    {
        if (_savedPoints[0] <= 0 || _savedPoints[1] <= 0 || _savedPoints[2] <= 0)
        {
            ShowError("Не все точки сохранены!");
            return;
        }
        _burner?.StartAutoSequence(_savedPoints, (int)_cyclesInput.Value);
        UpdateStatus("Запуск последовательности...");
    }
    #endregion

    public override void _ExitTree()
    {
        _serialPort?.Close();
        _serialPort?.Dispose();
    }
}