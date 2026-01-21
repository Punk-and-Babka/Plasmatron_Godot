using Godot;
using System;

public partial class Burner : Node2D
{
    // region СОБЫТИЯ И СВОЙСТВА
    public bool IsManualPaused => _isManualPaused;

    [ExportGroup("UI Settings")]
    [Export] private HSlider _speedSlider;
    [Export] private Label _speedLabel;
    [Export] private Label _positionLabel;

    [Signal] public delegate void SequenceFinishedEventHandler();

    public event Action<float> PauseUpdated;
    public event Action<Vector2> PositionChanged;
    public event Action<float> SpeedChanged;

    private UIController _uiController;

    // --- ФИЗИКА (Vector2) ---
    public Vector2 TargetPosition { get; private set; }
    public bool IsMovingToTarget { get; private set; }
    public float CurrentSpeedScalar => _currentVelocity.Length();

    private float _stopRadius = 1.0f;

    // Границы
    private Vector2 MaxPositionMM => _grid != null
        ? new Vector2(_grid.RealWorldWidthMM - RealWidthMM, _grid.RealWorldHeightMM - RealHeightMM)
        : new Vector2(100, 100);

    // Автоматизация
    private bool _isAutoSequenceActive;
    private int _currentTargetIndex;
    private int _cyclesRemaining;
    private Vector2[] _sequencePoints = new Vector2[3];
    private bool _isManualPaused;
    private float _baseSpeed;
    private float _fastSpeed = 300f;

    public bool IsAutoSequenceActive => _isAutoSequenceActive;
    public int CyclesRemaining => _cyclesRemaining;


    // Вектор ввода от экранных кнопок (UI)
    public Vector2 InterfaceInputVector { get; set; } = Vector2.Zero;

    // ==========================================
    // НОВЫЕ ПАРАМЕТРЫ ВНЕШНЕГО ВИДА
    // ==========================================
    // region ПАРАМЕТРЫ
    [ExportGroup("Размеры (мм)")]
    [Export] public float RealWidthMM { get; set; } = 100f;
    [Export] public float RealHeightMM { get; set; } = 72f;

    [ExportGroup("Внешний вид")]
    [Export] public Color BodyColor { get; set; } = new Color(0.2f, 0.2f, 0.2f, 0.9f); // Темно-серый корпус
    [Export] public Color NozzleColor { get; set; } = new Color(1, 0.6f, 0, 1f); // Оранжевое сопло
    [Export] public Color TargetPointColor { get; set; } = new Color(0, 1, 1, 1f); // Голубой "лазер" (Cyan)

    [Export(PropertyHint.Range, "-100, 100")]
    public Vector2 VisualOffsetMM { get; set; } = new Vector2(0, 0); // Смещение картинки относительно точки

    [ExportGroup("Движение")]
    [Export] public float MaxSpeedMM { get; set; } = 100f;
    [Export] public float AccelerationTime { get; set; } = 0.25f; // Сделал свойством для доступа извне
    [Export] public float DecelerationTime { get; set; } = 0.25f;

    [ExportGroup("Связи")]
    [Export] private CoordinateGrid _grid;
    // endregion
    // ==========================================

    [Export] public float PauseDuration { get; set; } = 3.0f;
    private float _pauseTimer;
    private bool _isPaused;

    private float _accelerationRate;
    private float _decelerationRate;
    private Vector2 _positionMM;

    // Вектор текущей скорости (X, Y)
    private Vector2 _currentVelocity;

    public Vector2 PositionMM
    {
        get => _positionMM;
        private set
        {
            float x = Mathf.Clamp(value.X, 0, _grid?.RealWorldWidthMM ?? 1000);
            float y = Mathf.Clamp(value.Y, 0, _grid?.RealWorldHeightMM ?? 1000);
            Vector2 clamped = new Vector2(x, y);

            if (!_positionMM.IsEqualApprox(clamped))
            {
                _positionMM = clamped;
                PositionChanged?.Invoke(_positionMM);
                QueueRedraw();
            }
        }
    }

    public override void _Ready()
    {
        AddToGroup("burners");
        if (_grid == null) _grid = GetParent().GetNodeOrNull<CoordinateGrid>("CoordinateGrid");
        _uiController = GetNodeOrNull<UIController>("../UIController");

        CalculatePhysicsRates();

        if (_positionLabel == null) _positionLabel = GetNodeOrNull<Label>("../CanvasLayer/PositionLabel");
        if (_speedSlider == null) _speedSlider = GetNodeOrNull<HSlider>("../CanvasLayer/UIController/VBoxContainer/SpeedSlider");
        if (_speedSlider != null) ConnectSlider();
    }

    // --- ОБНОВЛЕННЫЙ МЕТОД РАСЧЕТА ФИЗИКИ ---
    // Теперь вызывается и при смене времени разгона (из окна настроек)
    private void CalculatePhysicsRates()
    {
        _accelerationRate = AccelerationTime > 0 ? MaxSpeedMM / AccelerationTime : MaxSpeedMM * 10;
        _decelerationRate = DecelerationTime > 0 ? MaxSpeedMM / DecelerationTime : MaxSpeedMM * 10;
    }

    // Метод для вызова из окна настроек ускорения
    public void UpdateAccelerationParameters(float accelTime, float decelTime)
    {
        AccelerationTime = Mathf.Max(0.01f, accelTime);
        DecelerationTime = Mathf.Max(0.01f, decelTime);
        CalculatePhysicsRates();
    }
    // ----------------------------------------

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        if (_isPaused && _isAutoSequenceActive && !_isManualPaused)
        {
            _pauseTimer -= dt;
            PauseUpdated?.Invoke(Mathf.Max(0, _pauseTimer));
            if (_pauseTimer <= 0) CompletePause();
            return;
        }

        HandlePhysics(dt);
    }

    // --- ИСПРАВЛЕННАЯ ФИЗИКА И ВВОД (HARD STOP) ---
    private void HandlePhysics(float delta)
    {
        // 1. ЖЕЛЕЗОБЕТОННАЯ ПАУЗА
        if (_isManualPaused)
        {
            // Если мы на паузе, мы ОБЯЗАНЫ стоять.
            // Гасим инерцию мгновенно.
            _currentVelocity = Vector2.Zero;
            return;
        }

        Vector2 targetVelocity = Vector2.Zero;
        float currentRate = _decelerationRate;

        // 1. АВТОМАТИЧЕСКОЕ ДВИЖЕНИЕ
        if (IsMovingToTarget)
        {
            Vector2 diff = TargetPosition - PositionMM;
            float dist = diff.Length();

            if (dist < _stopRadius)
            {
                PositionMM = TargetPosition;
                _currentVelocity = Vector2.Zero;
                StopAutoMovement();
                return;
            }

            float maxPermittedSpeed = Mathf.Sqrt(2 * _decelerationRate * dist);
            float targetSpeed = Mathf.Min(MaxSpeedMM, maxPermittedSpeed);

            targetVelocity = diff.Normalized() * targetSpeed;
            currentRate = targetSpeed < MaxSpeedMM ? _decelerationRate : _accelerationRate;
        }
        // 2. РУЧНОЕ УПРАВЛЕНИЕ
        else if (!IsAutoSequenceActive)
        {
            Vector2 keyboardInput = Vector2.Zero;

            // --- ЗАЩИТА: Получаем элемент в фокусе ---
            Control focusedControl = GetViewport().GuiGetFocusOwner();

            // Читаем клавиатуру, ТОЛЬКО если фокус НЕ на текстовом поле
            if (!(focusedControl is CodeEdit || focusedControl is LineEdit || focusedControl is TextEdit))
            {
                float keyX = Input.GetAxis("Burner_left", "Burner_right");
                float keyY = Input.GetAxis("Burner_down", "Burner_up");
                keyboardInput = new Vector2(keyX, keyY);
            }
            // ----------------------------------------

            // Суммируем с экранными кнопками
            Vector2 combinedInput = keyboardInput + InterfaceInputVector;

            if (combinedInput.Length() > 1) combinedInput = combinedInput.Normalized();

            Vector2 inputDir = combinedInput;

            if (inputDir != Vector2.Zero)
            {
                targetVelocity = inputDir * MaxSpeedMM;
                currentRate = _accelerationRate;

                // Отправляем команду
                if (_currentVelocity.Length() > 1.0f)
                {
                    SendMovementCommand(PositionMM + inputDir * 100f);
                }
            }
            else
            {
                targetVelocity = Vector2.Zero;
                currentRate = _decelerationRate;
            }
        }

        _currentVelocity = _currentVelocity.MoveToward(targetVelocity, currentRate * delta);
        PositionMM += _currentVelocity * delta;
    }

    // --- УПРАВЛЕНИЕ ---
    // target - это координата в системе пользователя (относительно Set Zero)
    public void MoveToPosition(Vector2 userTarget)
    {
        // Переводим в машинные координаты
        Vector2 machineTarget = userTarget + _workOffset;

        // Проверяем лимиты по МАШИННЫМ координатам
        float x = Mathf.Clamp(machineTarget.X, 0, _grid?.RealWorldWidthMM ?? 1000);
        float y = Mathf.Clamp(machineTarget.Y, 0, _grid?.RealWorldHeightMM ?? 1000);

        TargetPosition = new Vector2(x, y);

        if (!_isManualPaused)
        {
            IsMovingToTarget = true;
            SendMovementCommand(TargetPosition);
        }
    }

    public void StopAutoMovement()
    {
        if (IsMovingToTarget)
        {
            IsMovingToTarget = false;
            SendStopCommand();
            if (_isAutoSequenceActive) HandleMovementCompletion();
        }
    }

    // --- COM PORT ---
    private void SendMovementCommand(Vector2 target)
    {
        if (_uiController == null) return;

        // Тут мы отправляем G-Code, если перешли на него, или старый формат.
        // Пока оставляю твой старый формат для совместимости, но готовься менять на G-Code.
        Vector2 diff = target - PositionMM;
        string cmd = "";
        if (Mathf.Abs(diff.X) > Mathf.Abs(diff.Y)) cmd = diff.X > 0 ? "f" : "b";
        else cmd = diff.Y > 0 ? "u" : "d";
        _uiController.SendCommand(cmd);
    }

    private void SendStopCommand() => _uiController?.SendCommand("s");

    // --- АВТОМАТИЗАЦИЯ ---
    public void StartAutoSequence(Vector2[] points, int cycles)
    {
        ResetSequenceState();
        if (points.Length < 3) return;

        _sequencePoints = points; // Это Относительные точки (User coords)
        _cyclesRemaining = cycles;
        _baseSpeed = MaxSpeedMM;
        _isAutoSequenceActive = true;

        // ИСПРАВЛЕНИЕ: Сравниваем расстояние используя WorkPosition (Относительную позицию горелки)
        // так как _sequencePoints[0] тоже относительная.
        float distToP0 = WorkPosition.DistanceTo(_sequencePoints[0]);

        if (distToP0 < _stopRadius)
        {
            GD.Print("Уже в точке P0 (Relative).");
            _currentTargetIndex = 1;
            SetMovementSpeed(_fastSpeed);
            // MoveToPosition сам добавит Offset внутри себя, так что подаем "чистую" точку
            MoveToPosition(_sequencePoints[1]);
        }
        else
        {
            _currentTargetIndex = 0;
            SetMovementSpeed(_fastSpeed);
            MoveToPosition(_sequencePoints[0]);
        }
    }

    private void HandleMovementCompletion()
    {
        if (_isPaused) return;

        switch (_currentTargetIndex)
        {
            case 0:
                if (_cyclesRemaining > 0)
                {
                    SetMovementSpeed(_fastSpeed);
                    _currentTargetIndex = 1;
                    MoveToPosition(_sequencePoints[1]);
                }
                else
                {
                    _isAutoSequenceActive = false;
                    EmitSignal(nameof(SequenceFinished));
                }
                break;

            case 1:
                if (_cyclesRemaining > 0)
                {
                    SetMovementSpeed(_baseSpeed);
                    StartPauseAndMove(_sequencePoints[2], 2);
                }
                else
                {
                    StartPauseAndMove(_sequencePoints[0], 0);
                }
                break;

            case 2:
                _cyclesRemaining--;
                StartPauseAndMove(_sequencePoints[1], 1);
                break;
        }
    }

    private Vector2 _nextAfterPauseTarget;
    private int _nextAfterPauseIndex;

    private void StartPauseAndMove(Vector2 nextTarget, int nextIndex)
    {
        _isPaused = true;
        _pauseTimer = PauseDuration;
        _nextAfterPauseTarget = nextTarget;
        _nextAfterPauseIndex = nextIndex;
    }

    private void CompletePause()
    {
        _isPaused = false;
        if (!_isAutoSequenceActive) return;
        _currentTargetIndex = _nextAfterPauseIndex;
        MoveToPosition(_nextAfterPauseTarget);
    }

    public void ResetSequenceState()
    {
        _isAutoSequenceActive = false;
        IsMovingToTarget = false;
        _isPaused = false;
        _isManualPaused = false;
        _uiController?.SendCommand("s");
    }

    // --- ИСПРАВЛЕННАЯ ПАУЗА ---
    public void SetManualPause(bool state)
    {
        _isManualPaused = state;

        if (state)
        {
            // ПАУЗА: Мгновенно останавливаем всё
            _currentVelocity = Vector2.Zero;
            _uiController?.SendCommand("s"); // Шлем 's' (Stop) на Ардуино
            GD.Print("Burner: PAUSED (Hard stop)");
        }
        else
        {
            // ПРОДОЛЖЕНИЕ:
            // Если мы ехали к цели, нужно снова отправить команду движения,
            // потому что Ардуино уже забыла про неё после команды 's'.
            if (IsMovingToTarget)
            {
                GD.Print("Burner: RESUMING move to " + TargetPosition);
                SendMovementCommand(TargetPosition);
            }
        }
    }

    public void SetMovementSpeed(float speed)
    {
        if (Mathf.IsEqualApprox(MaxSpeedMM, speed)) return;
        MaxSpeedMM = speed;
        CalculatePhysicsRates(); // Пересчитываем ускорения при смене скорости
        SpeedChanged?.Invoke(MaxSpeedMM);
        _uiController?.SendSpeedCommand(MaxSpeedMM);
        _uiController?.UpdateSpeedSlider(MaxSpeedMM);
    }

    private void ConnectSlider()
    {
        _speedSlider.ValueChanged += v => SetMovementSpeed((float)v);
    }

    public void EmergencyStop()
    {
        ResetSequenceState();
        SetMovementSpeed(100f);
    }
    // Флаг состояния плазмы
    public bool IsTorchOn { get; private set; }
    public Vector2 WorkOffset => _workOffset;

    // Смещение координат (разница между машинным нулем и рабочим нулем)
    private Vector2 _workOffset = Vector2.Zero;

    // Публичное свойство для отображения (Рабочие координаты)
    public Vector2 WorkPosition => PositionMM - _workOffset;

    // Событие обновления состояния факела (для UI)
    public event Action<bool> TorchStateChanged;

    // Метод: Установить текущую позицию как (0,0)
    public void SetZero()
    {
        _workOffset = PositionMM;

        // Обновляем UI, чтобы цифры стали по нулям
        PositionChanged?.Invoke(WorkPosition);
        GD.Print($"New Zero Set. Absolute: {PositionMM}, Offset: {_workOffset}");
    }

    // Метод: Вернуться в машинный ноль (абсолютный)
    public void GoHome()
    {
        MoveToPosition(Vector2.Zero); // Едем в физический 0
    }

    // Метод: Переключение плазмы
    public void SetTorch(bool on)
    {
        IsTorchOn = on;
        TorchStateChanged?.Invoke(on);

        // Отправляем M-коды (стандарт G-code: M3 = вкл, M5 = выкл)
        // Или ваши кастомные команды
        string cmd = on ? "M3" : "M5";
        _uiController?.SendCommand(cmd);

        QueueRedraw(); // Перерисовать, чтобы показать огонь
    }

    // ==========================================
    // ЛОГИКА ОТРИСОВКИ
    // ==========================================
    public override void _Draw()
    {
        if (_grid?.GridArea.Size == Vector2.Zero) return;

        float tipX = _grid.GridArea.Position.X + (PositionMM.X * _grid.PixelsPerMM_X);
        float tipY = _grid.GridArea.Position.Y + (_grid.RealWorldHeightMM - PositionMM.Y) * _grid.PixelsPerMM_Y;
        Vector2 tipScreenPos = new Vector2(tipX, tipY);

        Vector2 bodySizePx = new Vector2(RealWidthMM * _grid.PixelsPerMM_X, RealHeightMM * _grid.PixelsPerMM_Y);
        Vector2 offsetPx = new Vector2(VisualOffsetMM.X * _grid.PixelsPerMM_X, -VisualOffsetMM.Y * _grid.PixelsPerMM_Y);

        Vector2 bodyTopLeft = new Vector2(
            tipScreenPos.X - (bodySizePx.X / 2) + offsetPx.X,
            tipScreenPos.Y - bodySizePx.Y + offsetPx.Y
        );
        Rect2 bodyRect = new Rect2(bodyTopLeft, bodySizePx);

        // СЛОЙ 1: Линия крепления
        DrawLine(bodyRect.GetCenter(), tipScreenPos, Colors.Gray, 2f);

        // СЛОЙ 2: Корпус
        DrawRect(bodyRect, BodyColor, true);
        DrawRect(bodyRect, new Color(0.5f, 0.5f, 0.5f), false, 1);

        // РИСУЕМ ПЛАЗМУ (если включена)
        if (IsTorchOn)
        {
            // Рисуем яркий круг под соплом
            DrawCircle(tipScreenPos, 15f, Colors.Cyan); // Ядро
            DrawCircle(tipScreenPos, 25f, new Color(0, 1, 1, 0.4f)); // Ореол
        }

        // СЛОЙ 3: Прицел (Крест) - Увеличен размер
        float crossSize = 30f;
        DrawLine(tipScreenPos - new Vector2(crossSize, 0), tipScreenPos + new Vector2(crossSize, 0), TargetPointColor, 1f);
        DrawLine(tipScreenPos - new Vector2(0, crossSize), tipScreenPos + new Vector2(0, crossSize), TargetPointColor, 1f);

        // СЛОЙ 4: Активность
        if (IsAutoSequenceActive || (IsMovingToTarget && CurrentSpeedScalar > 10))
        {
            DrawCircle(tipScreenPos, 8f, new Color(1, 0, 0, 0.3f));
            DrawCircle(tipScreenPos, 4f, new Color(1, 0.2f, 0.2f, 1f));
        }
        else
        {
            DrawCircle(tipScreenPos, 3f, TargetPointColor);
        }
    }
}