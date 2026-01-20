using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
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
    [Export] private PackedScene _calcWindowScene; // Ссылка на сцену калькулятора
    [Export] private PackedScene _pieceWindowScene;
    [Export] private PackedScene _accelWindowScene;


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
    // НОВЫЕ ССЫЛКИ ДЛЯ ФАЙЛОВ
    [Export] private FileDialog _fileDialog;
    [Export] private Button _btnSaveScript;
    [Export] private Button _btnLoadScript;

    [ExportGroup("Стрелки (ArrowPad)")]
    [Export] private Button _btnUp;
    [Export] private Button _btnDown;
    [Export] private Button _btnLeft;
    [Export] private Button _btnRight;
    #endregion

    private SerialPort _serialPort;
    private PortSelectionWindow _portWindow;
    private CalculationWindow _calcWindowInstance;
    private PieceWindow _pieceWindowInstance;
    private AccelerationWindow _accelWindowInstance;

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
        InitializeFileSystem(); // Настройка папок и диалогов

        CallDeferred(nameof(InitializeDefaultSpeed));
    }

    // --- Настройка файловой системы ---
    private void InitializeFileSystem()
    {
        string scriptPath = "user://scripts";

        // 1. Создаем папку
        if (!DirAccess.DirExistsAbsolute(scriptPath))
        {
            DirAccess.MakeDirRecursiveAbsolute(scriptPath);
        }

        // 2. Настраиваем диалог
        if (_fileDialog != null)
        {
            // Важно: Сначала указываем, что работаем с папкой пользователя
            _fileDialog.Access = FileDialog.AccessEnum.Userdata; 

            // Вместо RootSubfolder используем CurrentDir - это не вызывает ошибок
            _fileDialog.CurrentDir = "scripts";

            // Фильтры (чтобы видеть только скрипты)
            _fileDialog.Filters = new string[] { "*.txt ; Текстовые файлы", "*.cnc ; G-Code" };

            _fileDialog.FileSelected += OnFileSelected;
        }

        if (_btnSaveScript != null) _btnSaveScript.Pressed += OnSaveScriptPressed;
        if (_btnLoadScript != null) _btnLoadScript.Pressed += OnLoadScriptPressed;
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

            // Безопасное переподключение сигнала
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
    private void OnBackgroundGuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouse && mouse.Pressed)
        {
            // Снимаем фокус с любого элемента при клике в пустоту
            Control focused = GetViewport().GuiGetFocusOwner();
            if (focused != null) focused.ReleaseFocus();
        }
    }

    private void OnSettingsMenuIdPressed(long id)
    {
        switch (id)
        {
            case 0: ShowPortSelectionWindow(); break;
            case 1: ShowCalculator(); break;
            case 2: ShowAccelWindow(); break;
            case 3: ShowPieceWindow(); break;
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

    // --- ЛОГИКА КАЛЬКУЛЯТОРА ---
    private void ShowCalculator()
    {
        // Если окно уже создано и открыто - просто показываем его
        if (_calcWindowInstance != null && GodotObject.IsInstanceValid(_calcWindowInstance))
        {
            _calcWindowInstance.PopupCentered();
            return;
        }

        // Если сцену забыли назначить в инспекторе
        if (_calcWindowScene == null)
        {
            ShowError("Ошибка: Сцена калькулятора не назначена в UIController!");
            return;
        }

        // Создаем новое окно
        _calcWindowInstance = _calcWindowScene.Instantiate<CalculationWindow>();
        AddChild(_calcWindowInstance);

        // Подписываемся на сигнал "Применить скорость"
        _calcWindowInstance.SpeedApplied += OnCalculatorSpeedApplied;

        _calcWindowInstance.PopupCentered();
    }

    private void OnCalculatorSpeedApplied(float speed)
    {
        GD.Print($"[Calc] Применена скорость: {speed}");

        // Используем твой существующий метод для применения скорости
        // Он сам обновит слайдер, отправит команду в порт и подсветит поле
        ApplySpeedChange(speed);
    }
    // ---------------------------

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
    // --- НАСТРОЙКИ УСКОРЕНИЯ ---
    private void ShowAccelWindow()
    {
        // 1. Создаем, если нет
        if (_accelWindowInstance == null || !GodotObject.IsInstanceValid(_accelWindowInstance))
        {
            if (_accelWindowScene == null)
            {
                ShowError("Сцена окна ускорения не назначена!");
                return;
            }
            _accelWindowInstance = _accelWindowScene.Instantiate<AccelerationWindow>();
            AddChild(_accelWindowInstance);
            _accelWindowInstance.AccelerationSettingsApplied += OnAccelSettingsApplied;
        }

        // 2. Обязательно подгружаем текущие значения из горелки, чтобы оператор видел, что сейчас стоит
        if (_burner != null)
        {
            _accelWindowInstance.InitValues(_burner.AccelerationTime, _burner.DecelerationTime);
        }

        _accelWindowInstance.PopupCentered();
    }

    private void OnAccelSettingsApplied(float accel, float decel)
    {
        if (_burner != null)
        {
            _burner.UpdateAccelerationParameters(accel, decel);
            GD.Print($"[UI] Новые параметры динамики: {accel} / {decel}");
        }
        else
        {
            ShowError("Горелка не найдена, параметры не применены.");
        }
    }
    // ----------------------------
    private void ShowAboutWindow()
    {
        if (_aboutWindowScene != null)
        {
            var win = _aboutWindowScene.Instantiate<Window>();
            AddChild(win);
            win.PopupCentered();
        }
    }

    // --- ЛОГИКА ОКНА ДЕТАЛИ ---
    private void ShowPieceWindow()
    {
        if (_pieceWindowInstance != null && GodotObject.IsInstanceValid(_pieceWindowInstance))
        {
            _pieceWindowInstance.PopupCentered();
            return;
        }

        if (_pieceWindowScene == null)
        {
            ShowError("Ошибка: Сцена окна детали не назначена!");
            return;
        }

        _pieceWindowInstance = _pieceWindowScene.Instantiate<PieceWindow>();
        AddChild(_pieceWindowInstance);

        // Подписываемся на сигнал
        _pieceWindowInstance.PieceDimensionsApplied += OnPieceDimensionsApplied;

        _pieceWindowInstance.PopupCentered();
    }

    private void OnPieceDimensionsApplied(float width, float height)
    {
        if (_grid == null) return;

        GD.Print($"Применена деталь: {width}x{height} мм");

        // Передаем данные в сетку
        _grid.SetPieceRectangle(width, height);
    }
    // ---------------------------

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

    // --- НОВОЕ: Методы сохранения и загрузки ---
    private void OnSaveScriptPressed()
    {
        if (_fileDialog == null) return;
        _fileDialog.FileMode = FileDialog.FileModeEnum.SaveFile;
        _fileDialog.Title = "Сохранить скрипт";
        _fileDialog.PopupCentered();
    }

    private void OnLoadScriptPressed()
    {
        if (_fileDialog == null) return;
        _fileDialog.FileMode = FileDialog.FileModeEnum.OpenFile;
        _fileDialog.Title = "Загрузить скрипт";
        _fileDialog.PopupCentered();
    }

    private void OnFileSelected(string path)
    {
        if (_fileDialog.FileMode == FileDialog.FileModeEnum.SaveFile)
            SaveScriptToFile(path);
        else if (_fileDialog.FileMode == FileDialog.FileModeEnum.OpenFile)
            LoadScriptFromFile(path);
    }

    private void SaveScriptToFile(string path)
    {
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (file != null)
        {
            file.StoreString(_scriptInput.Text); // Используем _scriptInput
            GD.Print($"Скрипт сохранен: {path}");
        }
        else
            ShowError($"Ошибка создания файла: {path}");
    }

    private void LoadScriptFromFile(string path)
    {
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (file != null)
        {
            string content = file.GetAsText();
            _scriptInput.Text = content;
            GD.Print($"Скрипт загружен: {path}");

            // СРАЗУ РИСУЕМ ПРЕВЬЮ
            ParseScriptAndDraw(content);
        }
        else
            ShowError($"Ошибка чтения файла: {path}");
    }
    // --- НОВЫЙ МЕТОД: Предпросмотр траектории ---
    // Обновленный метод парсинга, полностью повторяющий логику ScriptInterpreter
    private void ParseScriptAndDraw(string scriptText)
    {
        if (_grid == null) return;

        // Если текст пустой - очищаем сетку
        if (string.IsNullOrWhiteSpace(scriptText))
        {
            _grid.UpdatePoints(new Vector2[0], new Color[0]);
            return;
        }

        List<Vector2> points = new List<Vector2>();
        List<Color> colors = new List<Color>();

        string[] lines = scriptText.Split('\n');

        foreach (string rawLine in lines)
        {
            // 1. Очистка и приведение к верхнему регистру (как в интерпретаторе)
            string line = rawLine.Trim().ToUpperInvariant();

            // Пропуск комментариев (// и #)
            if (string.IsNullOrEmpty(line) || line.StartsWith("//") || line.StartsWith("#")) continue;

            // 2. Разбиваем строку по разделителям: скобки и запятые
            // Это "сердце" вашего интерпретатора - позволяет понимать форматы GO(x,y) и GO x,y
            var parts = Regex.Split(line, @"\(|\)|,")
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();

            if (parts.Length == 0) continue;

            string command = parts[0];       // Имя команды (GO, CYCLE...)
            string[] args = parts.Skip(1).ToArray(); // Аргументы

            // 3. Обработка команд
            if (command == "GO")
            {
                // Логика: GO X или GO X, Y
                if (args.Length >= 1)
                {
                    float x = ParseFloatSafe(args[0]);
                    float y = (args.Length >= 2) ? ParseFloatSafe(args[1]) : 0f;

                    points.Add(new Vector2(x, y));
                    colors.Add(Colors.Green); // Зеленый для обычного движения
                }
            }
            else if (command == "CYCLE")
            {
                // Логика: Ищем 2 точки (Start и End)
                // Вариант А: X1, Y1, X2, Y2, Count (5 аргументов)
                if (args.Length >= 5)
                {
                    float x1 = ParseFloatSafe(args[0]);
                    float y1 = ParseFloatSafe(args[1]);
                    float x2 = ParseFloatSafe(args[2]);
                    float y2 = ParseFloatSafe(args[3]);

                    // 1. Старт
                    points.Add(new Vector2(x1, y1));
                    colors.Add(Colors.Yellow);

                    // 2. Конец
                    points.Add(new Vector2(x2, y2));
                    colors.Add(Colors.Orange);

                    // 3. ИСПРАВЛЕНИЕ: Визуально замыкаем цикл (возврат в А)
                    points.Add(new Vector2(x1, y1));
                    colors.Add(Colors.Yellow);
                }
                // Вариант Б: X1, X2, Count (Y=0)
                else if (args.Length >= 3)
                {
                    float x1 = ParseFloatSafe(args[0]);
                    float x2 = ParseFloatSafe(args[1]);

                    points.Add(new Vector2(x1, 0));
                    colors.Add(Colors.Yellow);

                    points.Add(new Vector2(x2, 0));
                    colors.Add(Colors.Orange);

                    // 3. ИСПРАВЛЕНИЕ: Визуально замыкаем цикл
                    points.Add(new Vector2(x1, 0));
                    colors.Add(Colors.Yellow);
                }
            }
        }

        // 4. Отрисовка
        if (points.Count > 0)
        {
            _grid.UpdatePoints(points, colors);
            GD.Print($"[Preview] Отрисовано {points.Count} точек.");
        }
        else
        {
            // Если ничего не нашли (пустой скрипт или одни комменты) - чистим сетку
            _grid.UpdatePoints(new Vector2[0], new Color[0]);
        }
    }

    // Вспомогательный безопасный парсер (чтобы не дублировать try-catch)
    private float ParseFloatSafe(string val)
    {
        // Заменяем запятую на точку для надежности
        if (float.TryParse(val.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out float result))
        {
            return result;
        }
        return 0f;
    }
    // ------------------------------------------

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
        Vector2 target = ParsePoint(_positionInput.Text);

        // Проверка на наличие разделителя (теперь ищем запятую или точку с запятой для совместимости)
        if (!_positionInput.Text.Contains(",") && !_positionInput.Text.Contains(";"))
        {
            float currentY = _burner != null ? _burner.PositionMM.Y : 0;
            target.Y = currentY;
        }

        if (_burner != null)
        {
            _burner.MoveToPosition(target);
        }
        else
        {
            // Обновили текст ошибки
            ShowError("Горелка не найдена или неверный формат.\nИспользуйте: 'X, Y' (например: 100, 200)");
        }
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

        // БЫЛО: targetInput.Text = $"{pos.X:F0}; {pos.Y:F0}";
        // СТАЛО: Разделитель - запятая
        targetInput.Text = $"{pos.X:F0}, {pos.Y:F0}";

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

        // Разделяем по: запятой, точке с запятой или пробелу
        string[] parts = text.Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);

        float x = 0;
        float y = 0;

        // Важно: CultureInfo.InvariantCulture гарантирует, что точка - это дробь
        if (parts.Length > 0) float.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out x);
        if (parts.Length > 1) float.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out y);

        return new Vector2(x, y);
    }

    // ---------------------------------------------------

    private void OnPositionUpdated(Vector2 position)
    {
        if (_burner != null && _burner.IsAutoSequenceActive)
            UpdateStatus($"Циклов осталось: {_burner.CyclesRemaining}");
    }
    public override void _Input(InputEvent @event)
    {
        // 1. ESCAPE (Сброс фокуса)
        if (@event.IsActionPressed("ui_cancel"))
        {
            Control focused = GetViewport().GuiGetFocusOwner();
            if (focused != null) focused.ReleaseFocus();
            return;
        }

        // 2. ENTER (Подтверждение ввода)
        if (@event is InputEventKey kEnter && kEnter.Pressed &&
           (kEnter.Keycode == Key.Enter || kEnter.Keycode == Key.KpEnter))
        {
            Control focused = GetViewport().GuiGetFocusOwner();
            if (focused is LineEdit) focused.ReleaseFocus();
        }

        // 3. БЛОКИРОВКА ПРЫЖКОВ ФОКУСА (СТРЕЛКИ)
        if (@event is InputEventKey k && k.Pressed)
        {
            // Проверяем, нажата ли одна из стрелок
            if (k.Keycode == Key.Up || k.Keycode == Key.Down ||
                k.Keycode == Key.Left || k.Keycode == Key.Right)
            {
                Control focused = GetViewport().GuiGetFocusOwner();

                // Логика:
                // Если фокуса НЕТ вообще (focused == null) -> Блокируем, чтобы не прыгнул на SpinBox.
                // Если фокус ЕСТЬ, но это НЕ поле ввода -> Блокируем, чтобы не менял фокус на соседнюю кнопку.
                // Если фокус ЕСТЬ и это поле ввода (LineEdit) -> НЕ блокируем, даем двигать курсор в тексте.

                bool isTyping = focused is LineEdit || focused is CodeEdit || focused is TextEdit;

                if (!isTyping)
                {
                    // Говорим движку: "Я уже обработал эту кнопку, не отдавай её в GUI"
                    GetViewport().SetInputAsHandled();
                }
            }
        }
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