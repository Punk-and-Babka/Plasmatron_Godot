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

    // region ПАРАМЕТРЫ
    [ExportGroup("Размеры (мм)")]
    [Export] public float RealWidthMM { get; set; } = 100f;
    [Export] public float RealHeightMM { get; set; } = 72f;

    [ExportGroup("Внешний вид")]
    [Export] public Color BurnerColor { get; set; } = new Color(1, 0.5f, 0, 0.8f);

    [ExportGroup("Движение")]
    [Export] public float MaxSpeedMM { get; set; } = 100f;
    [Export] public float AccelerationTime = 0.25f;
    [Export] public float DecelerationTime = 0.25f;

    [ExportGroup("Связи")]
    [Export] private CoordinateGrid _grid;
    // endregion

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
            float x = Mathf.Clamp(value.X, 0, MaxPositionMM.X);
            float y = Mathf.Clamp(value.Y, 0, MaxPositionMM.Y);
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

    private void CalculatePhysicsRates()
    {
        _accelerationRate = AccelerationTime > 0 ? MaxSpeedMM / AccelerationTime : MaxSpeedMM * 10;
        _decelerationRate = DecelerationTime > 0 ? MaxSpeedMM / DecelerationTime : MaxSpeedMM * 10;
    }

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

    // --- ФИЗИКА И ВВОД ---
    private void HandlePhysics(float delta)
    {
        if (_isManualPaused) return;

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
            // Получаем ввод по X и Y
            float inputX = Input.GetAxis("Burner_left", "Burner_right");

            // В Godot Y вниз положителен для 2D, но в нашем глоб. смысле "Вверх" = +Y (высота)
            // Input.GetAxis("down", "up") вернет 1 если up, -1 если down.
            float inputY = Input.GetAxis("Burner_down", "Burner_up");

            Vector2 inputDir = new Vector2(inputX, inputY).Normalized();

            if (inputDir != Vector2.Zero)
            {
                targetVelocity = inputDir * MaxSpeedMM;
                currentRate = _accelerationRate;

                // Отправляем команду при ручном вводе
                // (Отправляем только если реально разгоняемся, чтобы не спамить)
                if (_currentVelocity.Length() > 1.0f)
                {
                    SendMovementCommand(PositionMM + inputDir * 100f); // "Виртуальная цель" впереди
                }
            }
            else
            {
                targetVelocity = Vector2.Zero;
                currentRate = _decelerationRate;
                // Если остановились и не в паузе - шлем стоп (один раз обработается в StopAutoMovement или по таймеру)
            }
        }

        // Применяем инерцию
        _currentVelocity = _currentVelocity.MoveToward(targetVelocity, currentRate * delta);
        PositionMM += _currentVelocity * delta;

        // Ручная отправка STOP если скорость упала до 0 после движения
        if (!IsMovingToTarget && !IsAutoSequenceActive && _currentVelocity == Vector2.Zero && targetVelocity == Vector2.Zero)
        {
            // Здесь можно добавить логику отправки 's' один раз, если нужно
        }
    }

    // --- УПРАВЛЕНИЕ ---
    public void MoveToPosition(Vector2 target)
    {
        float x = Mathf.Clamp(target.X, 0, MaxPositionMM.X);
        float y = Mathf.Clamp(target.Y, 0, MaxPositionMM.Y);
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

        // Вычисляем разницу
        Vector2 diff = target - PositionMM;

        // Определяем доминирующую ось
        string cmd = "";

        // Если движение по X больше, чем по Y
        if (Mathf.Abs(diff.X) > Mathf.Abs(diff.Y))
        {
            cmd = diff.X > 0 ? "f" : "b";
        }
        else
        {
            cmd = diff.Y > 0 ? "u" : "d"; // u - Up (Вверх), d - Down (Вниз)
        }

        _uiController.SendCommand(cmd);
    }

    private void SendStopCommand() => _uiController?.SendCommand("s");

    // --- АВТОМАТИЗАЦИЯ И ПРОЧЕЕ (Без изменений логики) ---
    public void StartAutoSequence(Vector2[] points, int cycles)
    {
        ResetSequenceState();
        if (points.Length < 3) return;

        _sequencePoints = points;
        _cyclesRemaining = cycles;
        _baseSpeed = MaxSpeedMM;
        _isAutoSequenceActive = true;

        float distToP0 = PositionMM.DistanceTo(_sequencePoints[0]);

        if (distToP0 < _stopRadius)
        {
            GD.Print("Уже в точке 0.");
            _currentTargetIndex = 1;
            SetMovementSpeed(_fastSpeed);
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

    public void SetManualPause(bool state)
    {
        _isManualPaused = state;
        if (state) _uiController?.SendCommand("s");
        else if (IsMovingToTarget) MoveToPosition(TargetPosition);
    }

    public void SetMovementSpeed(float speed)
    {
        if (Mathf.IsEqualApprox(MaxSpeedMM, speed)) return;
        MaxSpeedMM = speed;
        CalculatePhysicsRates();
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

    // --- ОТРИСОВКА ---
    public override void _Draw()
    {
        if (_grid?.GridArea.Size == Vector2.Zero) return;

        Vector2 sizePx = new Vector2(RealWidthMM * _grid.PixelsPerMM_X, RealHeightMM * _grid.PixelsPerMM_Y);
        float x = _grid.GridArea.Position.X + (PositionMM.X * _grid.PixelsPerMM_X) - sizePx.X / 2;
        float y = _grid.GridArea.Position.Y + (_grid.RealWorldHeightMM - PositionMM.Y) * _grid.PixelsPerMM_Y - sizePx.Y;
        Vector2 pos = new Vector2(x, y);

        DrawRect(new Rect2(pos, sizePx), BurnerColor, true);
        DrawRect(new Rect2(pos, sizePx), Colors.White, false, 2);

        float flameH = sizePx.Y * 0.7f;
        Vector2[] flame = {
            pos + new Vector2(sizePx.X/2, sizePx.Y + flameH),
            pos + new Vector2(sizePx.X/4, sizePx.Y),
            pos + new Vector2(sizePx.X*0.75f, sizePx.Y)
        };
        DrawColoredPolygon(flame, new Color(1, 0, 0, 0.6f));
    }
}