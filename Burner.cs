using Godot;
using System;
using static Godot.TextServer;

public partial class Burner : Node2D
{
    public enum MovementDirection { Left, Right }
    public bool IsManualPaused => _isManualPaused;

    [ExportGroup("UI Settings")]
    [Export] private HSlider _speedSlider;
    [Export] private Label _speedLabel;
    [Export] private Label _positionLabel;

    [Signal] public delegate void SequenceFinishedEventHandler();

    private float _pauseDuration = 3f;
    private float _pauseTimer;
    private bool _isPaused;
    public event Action<float> PauseUpdated;
    public event Action<float> PositionChanged;
    public event Action<float> SpeedChanged;

    private bool _isDecelerating;
    private UIController _uiController;

    public float TargetPosition { get; private set; }
    public bool IsMovingToTarget { get; private set; }

    // Порог остановки: 0.1 см = 1.0 мм
    private float _stopThreshold = 1.0f;

    // Проверка границ: 0.1 см = 1.0 мм
    public bool IsAtLeftBound => PositionXMM <= 1.0f;
    public bool IsAtRightBound => PositionXMM >= MaxPositionXMM - 1.0f;

    private bool _isAutoSequenceActive;
    private int _currentTargetIndex;
    private int _cyclesRemaining;
    private float[] _sequencePoints = new float[3];
    private bool _isManualPaused;
    private float _baseSpeed;

    // Быстрая скорость: 30 см/с -> 300 мм/с
    private float _fastSpeed = 300f;

    public bool IsAutoSequenceActive => _isAutoSequenceActive;
    public int CyclesRemaining => _cyclesRemaining;

    // region Основные параметры горелки (В МИЛЛИМЕТРАХ)
    [ExportGroup("Размеры")]
    [Export] public float RealWidthMM { get; set; } = 100f;   // 100 мм (было 10 см)
    [Export] public float RealHeightMM { get; set; } = 72f;   // 72 мм (было 7.2 см)

    [ExportGroup("Внешний вид")]
    [Export] public Color BurnerColor { get; set; } = new Color(1, 0.5f, 0, 0.8f);

    [ExportGroup("Движение")]
    [Export] public float MaxSpeedMM { get; set; } = 100f;    // 100 мм/с (было 10 см/с)

    [Export(PropertyHint.Range, "0.1,5.0")]
    public float AccelerationTime = 0.25f;
    [Export(PropertyHint.Range, "0.1,5.0")]
    public float DecelerationTime = 0.25f;

    [ExportGroup("Связи")]
    [Export] private CoordinateGrid _grid;
    // endregion

    [Export]
    public float PauseDuration
    {
        get => _pauseDuration;
        set => _pauseDuration = Mathf.Max(value, 0.1f);
    }

    // region Внутренние состояния
    private float _currentSpeedMM;     // Текущая скорость (мм/сек)
    public float CurrentSpeedMM
    {
        get => _currentSpeedMM;
        private set
        {
            if (Mathf.IsEqualApprox(_currentSpeedMM, value)) return;
            _currentSpeedMM = value;
            SpeedChanged?.Invoke(value);
        }
    }

    private float _positionXMM;        // Позиция по X в миллиметрах
    private float _accelerationRate;   // мм/сек²
    private float _decelerationRate;   // мм/сек²
    // endregion


    // region Свойства
    /// <summary>
    /// Текущая позиция горелки в мм
    /// </summary>
    public float PositionXMM
    {
        get => _positionXMM;
        private set
        {
            value = Mathf.Clamp(value, 0f, MaxPositionXMM);

            if (Mathf.IsEqualApprox(_positionXMM, value)) return;

            _positionXMM = value;
            PositionChanged?.Invoke(value);
            QueueRedraw();
        }
    }

    /// <summary>
    /// Максимально допустимая позиция (правая граница минус ширина горелки)
    /// </summary>
    private float MaxPositionXMM =>
        _grid != null ? Mathf.Max(_grid.RealWorldWidthMM - RealWidthMM, 0) : 0f;
    // endregion

    // region Жизненный цикл
    public override void _Ready()
    {
        AddToGroup("burners");
        if (_grid == null)
            _grid = GetParent().GetNode<CoordinateGrid>("CoordinateGrid");

        ZIndex = 3;

        _uiController = GetNode<UIController>("../UIController");

        // Расчет ускорения в мм/с²
        _accelerationRate = MaxSpeedMM / AccelerationTime;
        _decelerationRate = MaxSpeedMM / DecelerationTime;

        if (_positionLabel == null)
        {
            _positionLabel = GetNode<Label>("../CanvasLayer/PositionLabel");
        }

        if (_speedSlider != null)
        {
            ConnectSlider();
        }
    }

    private float _updateTimer;
    public override void _Process(double delta)
    {
        HandleMovement((float)delta);

        _updateTimer += (float)delta;
        if (_updateTimer > 0.1f)
        {
            _updateTimer = 0;
            // GD.Print($"Speed: {_currentSpeedMM:N1}");
        }
    }
    // endregion

    private float _savedSpeedBeforePause;
    private float _savedTargetBeforePause;
    private bool _wasMovingBeforePause;

    public void SetManualPause(bool state)
    {
        if (_isManualPaused == state) return;

        _isManualPaused = state;

        if (_isManualPaused)
        {
            _savedSpeedBeforePause = MaxSpeedMM;
            _savedTargetBeforePause = TargetPosition;
            _wasMovingBeforePause = IsMovingToTarget;

            SendStopCommand();
            GD.Print("Ручная пауза активирована");
        }
        else
        {
            if (_wasMovingBeforePause)
            {
                MaxSpeedMM = _savedSpeedBeforePause;
                MoveToPosition(_savedTargetBeforePause);
            }
            GD.Print("Ручная пауза деактивирована");
        }
    }

    public void ResetSequenceState()
    {
        _isAutoSequenceActive = false;
        _isPaused = false;
        _isDecelerating = false;
        IsMovingToTarget = false;
        _currentTargetIndex = -1;
        _pauseTimer = 0;
        _isManualPaused = false;

        SendStopCommand();
        GD.Print("Полный сброс состояния последовательности");
    }

    public void StartAutoSequence(float[] points, int cycles)
    {
        ResetSequenceState();

        if (points.Length != 3) return;

        _baseSpeed = MaxSpeedMM;
        _sequencePoints = points; // Предполагается, что точки уже приходят в ММ
        _cyclesRemaining = cycles;
        _currentTargetIndex = 0;
        _isAutoSequenceActive = true;

        GD.Print($"Запуск последовательности. Циклов: {cycles}");

        if (Mathf.IsEqualApprox(PositionXMM, _sequencePoints[0]))
        {
            GD.Print("Уже в точке 0. Переходим к точке 1.");
            _currentTargetIndex = 1;
            SetMovementSpeed(_fastSpeed);
            MoveToPosition(_sequencePoints[1]);
        }
        else
        {
            SetMovementSpeed(_fastSpeed);
            MoveToPosition(_sequencePoints[0]);
        }
    }

    public void SetMovementSpeed(float speed)
    {
        if (Mathf.IsEqualApprox(MaxSpeedMM, speed)) return;

        MaxSpeedMM = speed;
        _accelerationRate = MaxSpeedMM / AccelerationTime;
        _decelerationRate = MaxSpeedMM / DecelerationTime;

        SpeedChanged?.Invoke(MaxSpeedMM);
        _uiController?.SendSpeedCommand(MaxSpeedMM);
        _uiController?.UpdateSpeedSlider(MaxSpeedMM);
    }

    public void StopAutoSequence()
    {
        _isAutoSequenceActive = false;
        SetMovementSpeed(_baseSpeed);
        StopAutoMovement();
    }

    public void HandleMovementCompletion()
    {
        if (!_isAutoSequenceActive || !IsAtTargetPosition() || _isPaused)
            return;

        switch (_currentTargetIndex)
        {
            case 0:
                if (_cyclesRemaining > 0)
                {
                    SetMovementSpeed(_fastSpeed);
                    MoveToPosition(_sequencePoints[1]);
                    _currentTargetIndex = 1;
                }
                else
                {
                    GD.Print("Финишная точка достигнута");
                    _currentTargetIndex = -1;

                    if (!_isPaused)
                    {
                        _isAutoSequenceActive = false;
                        EmitSignal(nameof(SequenceFinished));
                    }

                    StartPauseBeforeMovement(_sequencePoints[0], -1);
                }
                break;

            case 1:
                if (_cyclesRemaining > 0)
                {
                    SetMovementSpeed(_baseSpeed);
                    StartPauseBeforeMovement(_sequencePoints[2], 2);
                }
                else
                {
                    StartPauseBeforeMovement(_sequencePoints[0], 0);
                }
                break;

            case 2:
                StartPauseBeforeMovement(_sequencePoints[1], 1);
                _cyclesRemaining = Math.Max(0, _cyclesRemaining - 1);
                GD.Print($"Осталось циклов: {_cyclesRemaining}");
                break;
        }
    }

    private void StartPauseBeforeMovement(float target, int nextIndex)
    {
        CancelCurrentPause();

        _isAutoSequenceActive = true;
        SetMovementSpeed(nextIndex == 0 ? _fastSpeed : _baseSpeed);

        _isPaused = true;
        _pauseTimer = PauseDuration;

        GD.Print($"Начало паузы перед движением к точке {nextIndex}");
        AsyncPause(() =>
        {
            if (!_isAutoSequenceActive)
            {
                GD.Print("Пауза отменена: последовательность прервана");
                return;
            }

            if (nextIndex == -1)
            {
                _isAutoSequenceActive = false;
                EmitSignal(nameof(SequenceFinished));
                GD.Print("Последовательность завершена");
                return;
            }

            GD.Print($"Пауза завершена. Движение к цели: {target}");
            MoveToPosition(target);
            _currentTargetIndex = nextIndex;
        });
    }

    private void CancelCurrentPause()
    {
        _isPaused = false;
        _pauseTimer = 0;
    }

    private async void AsyncPause(Action callback)
    {
        try
        {
            while (_pauseTimer > 0 && _isPaused)
            {
                await ToSignal(GetTree().CreateTimer(1.0), "timeout");
                _pauseTimer -= 1f;
                CallDeferred(nameof(UpdatePauseDelegate), _pauseTimer);
                GD.Print($"Пауза: {_pauseTimer} сек");
            }

            if (_isAutoSequenceActive)
            {
                _isPaused = false;
                callback?.Invoke();
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Ошибка в AsyncPause: {ex}");
        }
    }

    private void UpdatePauseDelegate(float timer)
    {
        PauseUpdated?.Invoke(timer);
    }

    private bool IsAtTargetPosition()
    {
        return Mathf.Abs(PositionXMM - TargetPosition) <= _stopThreshold;
    }

    private void SendMovementCommand(MovementDirection direction)
    {
        if (!CanMoveInDirection(direction))
        {
            GD.Print("Движение заблокировано: достигнута граница");
            return;
        }

        if (_uiController == null) return;

        string command = direction == MovementDirection.Right ? "f" : "b";
        _uiController.SendCommand(command);
    }

    private void SendStopCommand()
    {
        if (_uiController == null) return;
        _uiController.SendCommand("s");
    }

    public void StopAutoMovement()
    {
        if (IsMovingToTarget)
        {
            GD.Print("Остановка автоматического движения");
            IsMovingToTarget = false;
            _isDecelerating = false;
            SendStopCommand();
            HandleMovementCompletion();
        }
    }

    public void MoveToPosition(float target)
    {
        TargetPosition = Mathf.Clamp(target, 0, MaxPositionXMM);

        if (!_isManualPaused)
        {
            IsMovingToTarget = true;
            _isDecelerating = false;

            GD.Print($"Новая цель: {TargetPosition:N1} мм");
            var direction = TargetPosition > PositionXMM ?
                MovementDirection.Right :
                MovementDirection.Left;
            SendMovementCommand(direction);
        }
    }

    public void EmergencyStop()
    {
        _isManualPaused = false;
        _isPaused = false;

        if (_isAutoSequenceActive)
        {
            _isAutoSequenceActive = false;
            EmitSignal(nameof(SequenceFinished));
        }

        StopAutoMovement();
        ResetToBaseSpeed();
    }

    public void ResetToBaseSpeed()
    {
        if (!Mathf.IsEqualApprox(MaxSpeedMM, _baseSpeed))
        {
            MaxSpeedMM = _baseSpeed;
            _accelerationRate = MaxSpeedMM / AccelerationTime;
            _decelerationRate = MaxSpeedMM / DecelerationTime;
            _uiController?.SendSpeedCommand(MaxSpeedMM);
        }
    }

    private void HandleAutoMovement(float delta)
    {
        if (_isManualPaused) return;

        float distance = TargetPosition - PositionXMM;
        float direction = Mathf.Sign(distance);
        float decelerationDistance = CalculateDecelerationDistance();
        bool shouldDecelerate = Mathf.Abs(distance) <= decelerationDistance;

        if (shouldDecelerate && !_isDecelerating)
        {
            _isDecelerating = true;
            SendStopCommand();
        }

        if (_isDecelerating)
        {
            CurrentSpeedMM = Mathf.MoveToward(
                CurrentSpeedMM,
                0f,
                _decelerationRate * delta
            );
        }
        else
        {
            CurrentSpeedMM = Mathf.MoveToward(
                CurrentSpeedMM,
                MaxSpeedMM * direction,
                _accelerationRate * delta
            );
        }

        PositionXMM += CurrentSpeedMM * delta;

        if (_isDecelerating && Mathf.Abs(CurrentSpeedMM) < _stopThreshold)
        {
            PositionXMM = TargetPosition;
            StopAutoMovement();
        }
    }

    private float CalculateDecelerationDistance()
    {
        float currentSpeed = Mathf.Abs(CurrentSpeedMM);
        return (currentSpeed * currentSpeed) / (2 * _decelerationRate);
    }

    // region Логика движения
    private MovementDirection _currentDirection;
    private bool _isMoving;

    private void HandleMovement(float delta)
    {
        if (IsMovingToTarget)
        {
            HandleAutoMovement(delta);
            return;
        }

        float input = Input.GetActionStrength("Burner_right")
                    - Input.GetActionStrength("Burner_left");

        if (IsAtRightBound && input > 0)
        {
            input = 0;
            GD.Print("Достигнута правая граница!");
        }
        else if (IsAtLeftBound && input < 0)
        {
            input = 0;
            GD.Print("Достигнута левая граница!");
        }

        if (!Mathf.IsZeroApprox(input))
        {
            var newDirection = input > 0 ? MovementDirection.Right : MovementDirection.Left;

            if (newDirection != _currentDirection || !_isMoving)
            {
                if (CanMoveInDirection(newDirection))
                {
                    _currentDirection = newDirection;
                    _isMoving = true;
                    MovementStarted?.Invoke(_currentDirection);
                }
                else
                {
                    input = 0;
                }
            }
        }
        else
        {
            if (_isMoving)
            {
                _isMoving = false;
                MovementStopped?.Invoke();
            }
        }

        if (_isMoving)
        {
            Accelerate(input, delta);
        }
        else
        {
            Decelerate(delta);
        }

        PositionXMM += CurrentSpeedMM * delta;
    }

    private bool CanMoveInDirection(MovementDirection direction)
    {
        return direction switch
        {
            MovementDirection.Left => !IsAtLeftBound,
            MovementDirection.Right => !IsAtRightBound,
            _ => true
        };
    }

    public event Action MovementStopped;
    public event Action<MovementDirection> MovementStarted;

    private void Accelerate(float input, float delta)
    {
        float targetSpeed = input * MaxSpeedMM;
        CurrentSpeedMM = Mathf.MoveToward(
            _currentSpeedMM,
            targetSpeed,
            _accelerationRate * delta
        );
    }

    private void Decelerate(float delta)
    {
        CurrentSpeedMM = Mathf.MoveToward(
            _currentSpeedMM,
            0f,
            _decelerationRate * delta
        );
    }
    // endregion

    // region Отрисовка
    public override void _Draw()
    {
        if (_grid?.GridArea.Size == Vector2.Zero) return;

        // 1. Рассчитываем размеры в пикселях через PixelsPerMM
        Vector2 burnerSize = new Vector2(
            RealWidthMM * _grid.PixelsPerMM_X,
            RealHeightMM * _grid.PixelsPerMM_Y
        );

        // 2. Корректируем позицию
        Vector2 burnerPos = new Vector2(
            _grid.GridArea.Position.X + (PositionXMM * _grid.PixelsPerMM_X) - burnerSize.X / 2,
            _grid.GridArea.Position.Y + _grid.GridArea.Size.Y - burnerSize.Y
        );

        // 3. Отрисовка
        DrawRect(new Rect2(burnerPos, burnerSize), BurnerColor, true);
        DrawRect(new Rect2(burnerPos, burnerSize), Colors.White, false, 2);

        float flameHeight = burnerSize.Y * 0.7f;
        Vector2[] flamePoints = {
            burnerPos + new Vector2(burnerSize.X/2, -flameHeight/2),
            burnerPos + new Vector2(burnerSize.X/4, -flameHeight),
            burnerPos + new Vector2(burnerSize.X*0.75f, -flameHeight)
        };

        DrawColoredPolygon(flamePoints, new Color(1, 0, 0, 0.6f));
    }
    // endregion

    private void ConnectSlider()
    {
        _speedSlider.ValueChanged += OnSpeedChanged;
        UpdateSpeedDisplay(MaxSpeedMM);
    }

    private void OnSpeedChanged(double value)
    {
        MaxSpeedMM = (float)value;
        _accelerationRate = MaxSpeedMM / AccelerationTime;
        _decelerationRate = MaxSpeedMM / DecelerationTime;
        UpdateSpeedDisplay(MaxSpeedMM);
    }

    private void UpdateSpeedDisplay(float speed)
    {
        if (_speedLabel != null)
        {
            _speedLabel.Text = $"Скорость: {speed:N0} мм/сек";
        }
    }
}