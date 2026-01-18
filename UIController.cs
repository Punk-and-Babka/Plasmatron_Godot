using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.IO.Ports;
using System.Linq;
using static Burner;

/// <summary>
/// Главный контроллер пользовательского интерфейса, управляющий взаимодействием между элементами UI,
/// горелкой и COM-портом.
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
    [Export] private LineEdit _speedLabel;         // Метка для отображения скорости
    [Export] private MenuBar _mainMenu;         // Главное меню приложения
    [Export] private PackedScene _aboutWindowScene; // Сцена окна "О программе"
    [Export] private PackedScene _portWindowScene;  // Сцена окна выбора порта
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
    #endregion

    #region Приватные поля
    private SerialPort _serialPort;             // Объект для работы с COM-портом
    private PortSelectionWindow _portWindow;    // Окно выбора порта
    private float _lastPosition;                // Последняя зафиксированная позиция
    private float _lastSpeed;                   // Последняя зафиксированная скорость
    private float _updateTimer;                 // Таймер для обновления UI
    private string _lastText = string.Empty;    // Кэш последнего отображаемого текста
    private float _lastSentSpeed = -1;          // Последняя отправленная скорость
    private float _lastSentSliderSpeed = -1;
    private float[] _savedPoints = new float[3];
    private bool _isManualPaused;
    #endregion

    #region Инициализация
    /// <summary>
    /// Инициализация контроллера при загрузке сцены
    /// </summary>
    public override void _Ready()
    {
        InitializeEventHandlers();  // Настройка обработчиков событий
        ShowPortSelectionWindow();  // Показать окно выбора порта
        InitializeMainMenu();

        CallDeferred(nameof(InitializeDefaultSpeed));
    }
    private void InitializeDefaultSpeed()
    {
        // Устанавливаем начальные значения
        _lastSentSliderSpeed = DefaultSpeed;
        _burner.MaxSpeedMM = DefaultSpeed;

        // Обновляем UI
        UpdateSpeedSlider(DefaultSpeed);

        // Отправляем команду
        SendSpeedCommand(DefaultSpeed, true); // true - принудительная отправка
    }
    private void OnPositionUpdated(float position)
    {
        _lastPosition = position;

        // Добавляем проверку активности авторежима
        if (_burner.IsAutoSequenceActive)
        {
            _burner.HandleMovementCompletion();
            UpdateStatus($"Циклов осталось: {_burner.CyclesRemaining}");
        }
    }
    /// <summary>
    /// Настройка подписки на события горелки и меню
    /// </summary>
    private void InitializeEventHandlers()
    {
        // Подписка на события горелки
        _burner.PositionChanged += OnPositionUpdated;
        _burner.SpeedChanged += OnSpeedUpdated;
        _burner.MovementStarted += OnBurnerMovementStarted;
        _burner.MovementStopped += OnBurnerMovementStopped;
        _speedSlider.ValueChanged += OnSpeedSliderChanged;
        _speedInput.SpeedChanged += OnSpeedInputChanged;
        _speedSlider.TickCount = 11; // Деления каждые 1 единицу
        _moveButton.Pressed += OnMoveButtonPressed;
        _stopButton.Pressed += OnStopButtonPressed;
        _savePoint0Button.Pressed += () => SavePoint(0);
        _savePoint1Button.Pressed += () => SavePoint(1);
        _savePoint2Button.Pressed += () => SavePoint(2);
        _startSequenceButton.Pressed += OnStartSequencePressed;
        _emergencyStopButton.Pressed += OnEmergencyStopPressed;
        _pauseInput.ValueChanged += OnPauseChanged;
        _burner.PauseUpdated += OnPauseUpdated;
        _pauseButton.Pressed += OnPauseButtonPressed;

        // Явная настройка культуры для всего приложения
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        // Подписка на событие выбора пункта меню "О программе"
        var aboutMenu = _mainMenu.GetChild(1) as PopupMenu;
        aboutMenu.IdPressed += OnAboutMenuSelected;
    }
    #endregion

    #region Управление COM-портом
    /// <summary>
    /// Отображение окна выбора COM-порта
    /// </summary>
    private void ShowPortSelectionWindow()
    {
        _portWindow = _portWindowScene.Instantiate<PortSelectionWindow>();
        AddChild(_portWindow);
        _portWindow.ConnectionConfirmed += OnPortSelected; // Подписка на подтверждение подключения
        _portWindow.PopupCentered(); // Показать окно по центру
    }

    /// <summary>
    /// Обработчик завершения выбора порта
    /// </summary>
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

    /// <summary>
    /// Инициализация подключения к реальному COM-порту
    /// </summary>
    private void InitializeSerialPort()
    {
        try
        {
            _serialPort = new SerialPort(
                _portWindow.SelectedPort,
                _portWindow.SelectedBaudRate,
                Parity.None,
                8,
                StopBits.One)
            {
                ReadTimeout = 500,    // Таймаут чтения 500 мс
                WriteTimeout = 500    // Таймаут записи 500 мс
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

    /// <summary>
    /// Инициализация мок-порта для тестирования
    /// </summary>
    private void InitializeMockPort()
    {
        var timer = new Timer();
        timer.WaitTime = 0.1f;
        timer.Timeout += () => ProcessIncomingData(GD.Randf().ToString("F2"));
        AddChild(timer);
        timer.Start();
    }
    #endregion

    #region Обработка данных
    /// <summary>
    /// Обработчик получения данных от устройства
    /// </summary>
    private void SerialDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var data = _serialPort.ReadLine().Trim();
            CallDeferred(nameof(ProcessIncomingData), data);
        }
        catch (TimeoutException)
        {
            GD.Print("Таймаут чтения данных");
        }
        catch (Exception ex)
        {
            ShowError($"Ошибка чтения: {ex.Message}");
        }
    }
    /// <summary>
    /// Обработчик изменения положения слайдера скорости
    /// </summary>
    private void OnSpeedSliderChanged(double value)
    {
        // Округляем до одного знака
        float newSpeed = (float)Math.Round(value, 1);
        // Игнорируем изменения, если значение совпадает
        if (Math.Abs(_lastSentSliderSpeed - newSpeed) < 0.05f) return;

        if (Math.Abs(_lastSentSliderSpeed - newSpeed) >= 0.05f)
        {
            _lastSentSliderSpeed = newSpeed;
            UpdateSpeedSlider(newSpeed);
            SendSpeedCommand(newSpeed);
            _burner.MaxSpeedMM = newSpeed;
            HighlightSpeedLabel();
            GD.Print($"Новая скорость: {newSpeed:F1}");
        }
    }

    /// <summary>
    /// Обработка входящих данных для обновления UI
    /// </summary>
    private void ProcessIncomingData(string data)
    {
        try
        {
            // ПРИЕМ ДАННЫХ (Обратная конвертация)
            if (data.StartsWith("v") && int.TryParse(data[1..], out int val))
            {
                // Формула: val / 0.8 = скорость_мм
                // Пример: v80 -> 80 / 0.8 = 100 мм/с
                float speedMM = val / 0.8f;

                _lastSpeed = speedMM;
                GD.Print($"Получена скорость: {speedMM:N0} мм/с (raw: {val})");
                UpdateUIDisplay();
            }
            else if (float.TryParse(data, NumberStyles.Any, CultureInfo.InvariantCulture, out float position))
            {
                // Если ардуино шлет позицию, убедись, что она тоже в мм
                // _lastPosition = position;
                // UpdateUIDisplay();
            }
        }
        catch (Exception ex)
        {
            ShowError($"Ошибка обработки данных: {ex.Message}");
        }
    }
    #endregion
    private void OnPauseChanged(double value)
    {
        _burner.PauseDuration = (float)value;
        GD.Print($"Установлена пауза: {value} сек");
    }
    #region Обновление UI
    // Метод для обновления текстовых меток
    private void UpdatePointLabel(int index)
    {
        Label targetLabel = index switch
        {
            0 => _point0Label,
            1 => _point1Label,
            2 => _point2Label,
            _ => null
        };

        if (targetLabel != null)
        {
            targetLabel.Text = $"{_savedPoints[index]:N1} мм";
            targetLabel.AddThemeColorOverride("font_color", _pointColors[index]);
        }
    }
    private void OnStartSequencePressed()
    {
        if (_savedPoints[0] <= 0 || _savedPoints[1] <= 0 || _savedPoints[2] <= 0)
        {
            ShowError("Не все точки сохранены!");
            return;
        }

        _burner.StartAutoSequence(_savedPoints, (int)_cyclesInput.Value);
        UpdateStatus("Запуск последовательности...");
    }

    private void UpdateStatus(string message)
    {
        _statusLabel.Text = message;
    }
    private void UpdatePauseStatus(float remainingTime)
    {
        // Добавляем проверку на наличие разделителя
        string[] parts = _statusLabel.Text.Split('\n');
        string baseMessage = parts.Length > 0 ? parts[0] : "Статус:";

        _statusLabel.Text = $"{baseMessage}\nПауза: {remainingTime:N1} сек";
        GD.Print($"Обновление паузы: {remainingTime:N1} сек"); // Отладочный вывод
    }
    private void OnPauseUpdated(float remainingTime)
    {
        GD.Print($"Получено время паузы: {remainingTime} сек"); // Отладочный вывод
        CallDeferred(nameof(DeferredPauseUpdate), remainingTime);
    }
    private void DeferredPauseUpdate(float remainingTime)
    {
        if (remainingTime > 0)
        {
            UpdatePauseStatus(remainingTime);
        }
        else
        {
            // Восстанавливаем исходный статус
            string baseMessage = _statusLabel.Text.Split('\n')[0];
            _statusLabel.Text = baseMessage.Contains(":")
                ? baseMessage
                : "Готов к работе";
            GD.Print("Пауза завершена");
        }
    }
    private void ClearPauseStatus()
    {
        string baseMessage = _statusLabel.Text.Split('\n')[0];
        _statusLabel.Text = baseMessage;
    }
    /// <summary>
    /// Основной цикл обновления интерфейса
    /// </summary>
    public override void _Process(double delta)
    {
        _updateTimer += (float)delta;
        if (_updateTimer > 0.05f) // Обновление каждые 50 мс
        {
            UpdateUIDisplay();
            _updateTimer = 0f;
        }
    }

    /// <summary>
    /// Обновление текстовых меток
    /// </summary>
    private void UpdateUIDisplay()
    {
        string newText = $"Позиция: {_lastPosition:N1} мм\n"
                       + $"Скорость: {_lastSpeed:N1} мм/сек\n";

        if (newText != _lastText)
        {
            _positionLabel.Text = newText;
            _lastText = newText;
        }
    }
    private void SavePoint(int index)
    {
        _savedPoints[index] = _lastPosition;

        // Обновляем Label
        UpdatePointLabel(index);

        _grid?.UpdatePoints(_savedPoints, _pointColors);
        GD.Print($"Точка {index} сохранена: {_lastPosition:N1} мм");
    }
    #endregion

    #region Управление скоростью
    /// <summary>
    /// Конвертация мм/сек в значение для команды
    /// Формула: мм/сек * 0.8
    /// </summary>
    private int ConvertSpeedToCommandValue(float mmPerSecond)
    {
        mmPerSecond = Mathf.Clamp(mmPerSecond, 0f, 500f); // Ограничение до 500 мм/с (50 см/с)

        // КЛЮЧЕВОЕ ИЗМЕНЕНИЕ: Коэффициент 0.8
        // 100 * 0.8 = 80
        // 300 * 0.8 = 240
        return (int)(mmPerSecond * 0.8f);
    }

    public void SendSpeedCommand(float speedMM, bool forceSend = false)
    {
        if (_portWindow?.UseMockPort == true) return;

        if (forceSend || !Mathf.IsEqualApprox(_lastSentSpeed, speedMM))
        {
            int commandValue = ConvertSpeedToCommandValue(speedMM);
            SendCommand($"v{commandValue}");
            _lastSentSpeed = speedMM;
            GD.Print($"[{DateTime.Now:T}] Скорость: {speedMM:F0} мм/с -> v{commandValue}");
        }
    }

    /// <summary>
    /// Обработчик изменения скорости
    /// </summary>
    private void OnSpeedUpdated(float speed)
    {
        _lastSpeed = (float)Math.Round(speed, 2); // Гарантируем два знака
        UpdateUIDisplay();
    }
    private void OnSpeedInputChanged(float newSpeed)
    {
        // Обновляем слайдер
        _speedSlider.Value = newSpeed;

        // Обновляем горелку
        _burner.MaxSpeedMM = newSpeed;
        SendSpeedCommand(newSpeed);

        // Синхронизируем интерфейс
        UpdateSpeedSlider(newSpeed);
    }
    public void UpdateSpeedSlider(float speed)
    {
        // Обновляем поле ввода
        _speedInput.SetSpeed(speed);

        // Обновляем слайдер и метку
        _speedSlider.Value = speed;
        _speedLabel.Text = $"Скорость: {speed:N1} мм/с";
    }

    private async void HighlightSpeedLabel()
    {
        // Сохраняем оригинальный цвет из темы
        Color originalColor = _speedLabel.GetThemeColor("font_color");

        // Устанавливаем временный цвет
        _speedLabel.AddThemeColorOverride("font_color", Colors.Yellow);

        // Ждём 0.3 секунды
        await ToSignal(GetTree().CreateTimer(0.3), "timeout");

        // Восстанавливаем оригинальный цвет
        _speedLabel.RemoveThemeColorOverride("font_color");
        // Или если это не работает:
        // _speedLabel.AddThemeColorOverride("font_color", originalColor);
    }


    #endregion

    #region Управление движением
    /// <summary>
    /// Обработчик начала движения
    /// </summary>
    private void OnBurnerMovementStarted(MovementDirection direction)
    {
        string command = direction == MovementDirection.Right ? "f" : "b";
        SendCommand(command);
    }

    /// <summary>
    /// Обработчик остановки движения
    /// </summary>
    private void OnBurnerMovementStopped()
    {
        SendCommand("s");
    }
    /// <summary>
    /// Обработчик кнопки старта движения
    /// </summary>
    private void OnMoveButtonPressed()
    {
        if (float.TryParse(_positionInput.Text, out float target))
        {
            _burner.MoveToPosition(target);
        }
        else
        {
            ShowError("Некорректное значение позиции");
        }
    }

    /// <summary>
    /// Обработчик кнопки остановки
    /// </summary>
    private void OnStopButtonPressed()
    {
        _burner.StopAutoMovement();
    }

    private void OnEmergencyStopPressed()
    {
        // 1. Мгновенная остановка движения
        SendCommand("s");

        // 2. Прерывание автоматической последовательности
        _burner.EmergencyStop();

        // 3. Визуальная индикация
        GD.Print("[ЭКСТРЕННО] Движение прервано");
    }

    private void OnPauseButtonPressed()
    {
        _isManualPaused = !_isManualPaused;
        _burner.SetManualPause(_isManualPaused);

        // Обновляем текст кнопки
        _pauseButton.Text = _isManualPaused ? "Продолжить" : "Пауза";

        // Добавляем визуальную индикацию
        _pauseButton.AddThemeColorOverride("font_color", _isManualPaused ? Colors.Red : Colors.White);
    }
    #endregion

    #region Вспомогательные методы
    /// <summary>
    /// Отправка команды на устройство
    /// </summary>
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
        catch (TimeoutException)
        {
            ShowError("Таймаут отправки команды");
        }
        catch (InvalidOperationException)
        {
            ShowError("Порт не открыт");
        }
        catch (Exception ex)
        {
            ShowError($"Ошибка отправки: {ex.Message}");
        }
    }

    /// <summary>
    /// Отображение диалога с ошибкой
    /// </summary>
    private void ShowError(string message)
    {
        var dialog = new AcceptDialog
        {
            Title = "Ошибка",
            DialogText = message
        };
        GetTree().Root.AddChild(dialog);
        dialog.PopupCentered();
        GD.PrintErr($"[{DateTime.Now:T}] {message}");
    }
    #endregion

    #region Завершение работы
    /// <summary>
    /// Очистка ресурсов при завершении
    /// </summary>
    public override void _ExitTree()
    {
        _serialPort?.Close();
        _serialPort?.Dispose();
        GC.SuppressFinalize(this);
    }
    #endregion

    #region Обработка меню
    // Ссылки на меню, созданные в редакторе
    [ExportGroup("Menu References")]
    [Export] private PopupMenu fileMenu;
    [Export] private PopupMenu settingsMenu;
    [Export] private PopupMenu helpMenu;
    [Export] private PackedScene _detailWindowScene;

    /// <summary>
    /// Словарь для хранения меню
    /// </summary>
    private Dictionary<string, PopupMenu> _menus = new();
    private List<MenuItemData> _menuItems = new();

    public class MenuItemData
    {
        public string Name { get; set; }
        public Action Callback { get; set; }
        public int Order { get; set; }
        public string ParentMenu { get; set; } = "Main";
    }

    public void RegisterMenuItem(MenuItemData item)
    {
        _menuItems.Add(item);
        _menuItems = _menuItems.OrderBy(x => x.Order).ToList();
    }

    private void InitializeMainMenu()
    {
        // Регистрируем новый пункт меню
        RegisterMenuItem(new MenuItemData
        {
            Name = "New Window",
            Callback = ShowNewWindow,
            ParentMenu = "Help", // Или другое меню
            Order = 1
        });

        //// Создаем основные меню
        //CreateMenu("File", 0);
        //CreateMenu("Settings", 1);
        //CreateMenu("Help", 2);
        _menus = new Dictionary<string, PopupMenu>
    {
        { "File", fileMenu },
        { "Settings", settingsMenu },
        { "Help", helpMenu }
    };
        // Регистрируем пункты меню
        RegisterMenuItem(new MenuItemData
        {
            Name = "New Project",
            Callback = () => GD.Print("New Project clicked"),
            ParentMenu = "SettingsMenu",
            Order = 0
        });

        // Инициализация пунктов меню
        foreach (var menuPair in _menus)
        {
            var menu = menuPair.Value;
            menu.IdPressed += (id) => HandleMenuSelection(menu, (int)id);
        }
    }
    private void ShowNewWindow()
    {
        if (_detailWindowScene == null)
        {
            GD.PrintErr("Окно не назначено в инспекторе!");
            return;
        }

        // Создаем экземпляр окна
        var newWindow = _detailWindowScene.Instantiate<Window>();

        // Добавляем окно в корневой узел сцены
        GetTree().Root.AddChild(newWindow);

        // Настраиваем и показываем окно
        newWindow.PopupCentered(new Vector2I(400, 300));
        newWindow.Visible = true;

        GD.Print("Окно успешно открыто"); // Для отладки
    }
    private void CreateMenu(string menuTitle, int index)
    {
        // Создаем PopupMenu
        var popup = new PopupMenu();
        popup.Name = menuTitle;

        // Добавляем в MenuBar
        _mainMenu.AddChild(popup);

        // Связываем с главным меню
        _mainMenu.GetMenuPopup(index).Title = menuTitle;
        _menus[menuTitle] = popup;
    }

    private void HandleMenuSelection(PopupMenu menu, int id)
    {
        string menuTitle = menu.Name;
        string itemName = menu.GetItemText(id);

        var item = _menuItems.FirstOrDefault(i =>
            i.ParentMenu == menuTitle &&
            i.Name == itemName
        );

        item?.Callback?.Invoke();
    }

    private void OnAboutMenuSelected(long id)
    {
        if (id == 0 && _aboutWindowScene != null)
        {
            var aboutWindow = _aboutWindowScene.Instantiate<PopupPanel>();
            AddChild(aboutWindow);
            aboutWindow.PopupCentered(new Vector2I(400, 300));
        }
    }
    #endregion
}