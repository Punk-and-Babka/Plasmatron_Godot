using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

public partial class ScriptInterpreter : Node
{
    public enum InterpreterState { Idle, Running, Paused, Error }

    [ExportGroup("UI")]
    [Export] private CodeEdit _scriptInput;
    [Export] private Button _runButton;
    [Export] private Button _stopButton;
    [Export] private Button _resetButton; // Кнопка сброса (рядом со Стоп)
    [Export] private Label _statusLabel;

    [ExportGroup("Objects")]
    [Export] private CoordinateGrid _grid;
    [Export] public Burner TargetBurner { get; set; }

    private InterpreterState _state = InterpreterState.Idle;
    private Queue<string> _commandQueue = new Queue<string>();
    private Dictionary<string, Action<string[]>> _commandHandlers;

    // Состояние выполнения
    private float _currentDelay = 0;
    private bool _waitingForMovement = false;
    private float _movementWaitTimer = 0f;
    private const float WaitTimeout = 60f;

    // Подсветка и маппинг строк
    private int _lastHighlightedLine = -1;
    private readonly Dictionary<int, int> _executionIndexMap = new Dictionary<int, int>();
    private int _commandCounter = 0;
    private bool _movementWasPaused;

    // Переменные для циклов
    private Vector2 _cyclePointA;
    private Vector2 _cyclePointB;
    private int _cycleCount;
    private int _currentCycleStep;
    private float _cyclePause;
    private bool _isLargeCycle;

    public override void _Ready()
    {
        LocateBurner();
        InitializeCommandHandlers();

        if (_runButton != null) _runButton.Pressed += () => RunScript(_scriptInput?.Text ?? "");

        // Кнопка STOP теперь умеет сбрасывать ошибки
        if (_stopButton != null) _stopButton.Pressed += StopScript;

        // Отдельная кнопка RESET
        if (_resetButton != null) _resetButton.Pressed += HardReset;

        // Живая отрисовка при редактировании
        if (_scriptInput != null)
        {
            _scriptInput.TextChanged += OnScriptTextChanged;
            OnScriptTextChanged(); // Первый запуск
        }
    }

    // --- МЕТОД ПОЛНОГО СБРОСА ---
    public void HardReset()
    {
        // 1. Очистка данных
        _commandQueue.Clear();
        _executionIndexMap.Clear();
        _isLargeCycle = false;
        _waitingForMovement = false;
        _currentDelay = 0;

        // 2. Сброс состояния
        _state = InterpreterState.Idle;

        // 3. Остановка горелки
        if (TargetBurner != null)
        {
            TargetBurner.StopAutoMovement();
            TargetBurner.SetManualPause(false);
        }

        // 4. Сброс UI
        ToggleButtons(false); // Run=Active, Stop=Disabled
        ClearHighlighting();

        if (_statusLabel != null)
        {
            _statusLabel.Text = "Сброс выполнен";
            _statusLabel.Modulate = Colors.White; // Возвращаем белый цвет
        }

        GD.Print("[ScriptInterpreter] Hard Reset performed.");
    }

    // --- ЛОГИКА ЖИВОГО ПРЕДПРОСМОТРА ---
    private void OnScriptTextChanged()
    {
        // Если скрипт выполняется, не мешаем ему
        if (_state == InterpreterState.Running) return;
        if (_scriptInput == null) return;
        ScanAndDrawVisuals(_scriptInput.Text);
    }

    private void ScanAndDrawVisuals(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _grid == null) return;

        var lines = text.Split('\n');
        bool cycleFound = false;

        // Ищем последнюю команду CYCLE
        foreach (var line in lines)
        {
            string cleanLine = line.Trim().ToUpperInvariant();
            if (cleanLine.StartsWith("CYCLE"))
            {
                try
                {
                    var parts = Regex.Split(cleanLine, @"\(|\)|,")
                        .Select(p => p.Trim())
                        .Where(p => !string.IsNullOrEmpty(p))
                        .Skip(1) // Пропускаем слово CYCLE
                        .ToArray();

                    if (ExtractCycleCoordinates(parts, out Vector2 a, out Vector2 b))
                    {
                        UpdateGridPoints(a, b);
                        cycleFound = true;
                    }
                }
                catch { /* Игнорируем ошибки при наборе текста */ }
            }
        }

        // Если циклов нет, убираем точки с экрана
        if (!cycleFound) UpdateGridPoints(Vector2.Zero, Vector2.Zero);
    }

    private bool ExtractCycleCoordinates(string[] args, out Vector2 a, out Vector2 b)
    {
        a = Vector2.Zero; b = Vector2.Zero;
        try
        {
            // Формат 1: X1, Y1, X2, Y2, Count
            if (args.Length >= 5)
            {
                a = new Vector2(ParseFloat(args[0]), ParseFloat(args[1]));
                b = new Vector2(ParseFloat(args[2]), ParseFloat(args[3]));
                return true;
            }
            // Формат 2: X1, X2, Count (Y=0)
            else if (args.Length >= 3)
            {
                a = new Vector2(ParseFloat(args[0]), 0);
                b = new Vector2(ParseFloat(args[1]), 0);
                return true;
            }
        }
        catch { return false; }
        return false;
    }

    private void LocateBurner()
    {
        if (TargetBurner == null) TargetBurner = GetParent().GetNodeOrNull<Burner>("Burner");
        if (TargetBurner == null) TargetBurner = GetNodeOrNull<Burner>("../Burner");
        if (TargetBurner == null)
        {
            var nodes = GetTree().GetNodesInGroup("burners");
            if (nodes.Count > 0 && nodes[0] is Burner burner) TargetBurner = burner;
        }
    }

    public override void _Process(double delta)
    {
        if (_state != InterpreterState.Running) return;

        // 1. ПАУЗА
        if (_currentDelay > 0)
        {
            _currentDelay -= (float)delta;
            if (_statusLabel != null) _statusLabel.Text = $"Пауза: {_currentDelay:F1} сек";
            if (_currentDelay <= 0) { _currentDelay = 0; OnPauseFinished(); }
            return;
        }

        // 2. ОЖИДАНИЕ ДВИЖЕНИЯ
        if (_waitingForMovement)
        {
            _movementWaitTimer -= (float)delta;
            bool isMoving = TargetBurner != null && TargetBurner.IsMovingToTarget;

            if (!isMoving)
            {
                _waitingForMovement = false; _movementWaitTimer = 0f; OnMovementFinished();
            }
            else if (_movementWaitTimer <= 0)
            {
                HandleRuntimeError("Таймаут ожидания движения!");
                return;
            }
            return;
        }

        // 3. ВЫПОЛНЕНИЕ КОМАНДЫ
        ExecuteNextCommand();
    }

    private void ExecuteNextCommand()
    {
        // Если ошибка или пауза - ничего не делаем, ждем действий пользователя
        if (_state == InterpreterState.Paused || _state == InterpreterState.Error) return;

        if (_commandQueue.Count == 0)
        {
            HandleEnd();
            return;
        }

        int currentCommandIndex = _commandCounter - _commandQueue.Count;
        string commandLine = _commandQueue.Dequeue();

        if (_executionIndexMap.TryGetValue(currentCommandIndex, out int originalLineIndex))
        {
            HighlightCurrentLine(originalLineIndex);
        }

        var parts = Regex.Split(commandLine, @"\(|\)|,")
            .Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)).ToArray();

        if (parts.Length == 0) return;

        var command = parts[0].ToUpperInvariant();
        var args = parts.Skip(1).ToArray();

        if (_commandHandlers.ContainsKey(command))
        {
            try
            {
                _commandHandlers[command](args);
            }
            catch (Exception ex)
            {
                HandleRuntimeError($"Ошибка в '{command}': {ex.Message}");
            }
        }
        else
        {
            HandleRuntimeError($"Неизвестная команда: {command}");
        }
    }

    // --- ОБРАБОТКА ОШИБОК ---
    private void HandleRuntimeError(string msg)
    {
        _state = InterpreterState.Error;
        GD.PrintErr(msg);

        if (_statusLabel != null)
        {
            _statusLabel.Text = msg;
            _statusLabel.Modulate = Colors.Red; // Красный текст ошибки
        }

        // Кнопки НЕ блокируем полностью, чтобы можно было нажать Stop/Reset
    }

    // --- ЛОГИКА БОЛЬШОГО ЦИКЛА ---
    private void OnMovementFinished()
    {
        if (_isLargeCycle && _state == InterpreterState.Running)
        {
            string pauseCmd = $"PAUSE({_cyclePause.ToString(CultureInfo.InvariantCulture)})";
            PushPriorityCommand(pauseCmd);
        }
    }

    private void OnPauseFinished()
    {
        if (_isLargeCycle && _state == InterpreterState.Running)
        {
            _currentCycleStep++;
            int totalSteps = _cycleCount * 2;

            if (_currentCycleStep >= totalSteps)
            {
                GD.Print($"Цикл завершен ({_cycleCount} итераций)");
                _isLargeCycle = false;
                return;
            }

            Vector2 target = (_currentCycleStep % 2 != 0) ? _cyclePointB : _cyclePointA;
            string moveCmd = $"GO({target.X.ToString(CultureInfo.InvariantCulture)}, {target.Y.ToString(CultureInfo.InvariantCulture)})";
            PushPriorityCommand(moveCmd);
        }
    }

    private void PushPriorityCommand(string cmd)
    {
        var newQueue = new Queue<string>();
        newQueue.Enqueue(cmd);
        foreach (var c in _commandQueue) newQueue.Enqueue(c);
        _commandQueue = newQueue;
        _commandCounter++;
    }

    // --- ОБРАБОТЧИКИ КОМАНД ---
    private void InitializeCommandHandlers()
    {
        _commandHandlers = new Dictionary<string, Action<string[]>>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPEED"] = args => HandleSpeed(args),
            ["GO"] = args => HandleGo(args),
            ["CYCLE"] = args => HandleCycle(args),
            ["PAUSE"] = args => HandlePause(args),
            ["START"] = args => { },
            ["END"] = args => HandleEnd()
        };
    }

    private void HandleSpeed(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Укажите скорость");
        float speed = ParseFloat(args[0]);
        TargetBurner?.SetMovementSpeed(speed);
        if (_statusLabel != null) _statusLabel.Text = $"Скорость: {speed} мм/с";
    }

    private void HandleGo(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Укажите X");

        float x = ParseFloat(args[0]);
        float y = 0;
        if (args.Length > 1) y = ParseFloat(args[1]);

        TargetBurner?.MoveToPosition(new Vector2(x, y));

        _waitingForMovement = true;
        _movementWaitTimer = WaitTimeout;
        if (_statusLabel != null) _statusLabel.Text = $"Движение к {x:F0}:{y:F0} мм...";
    }

    private void HandlePause(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Укажите секунды");
        float seconds = ParseFloat(args[0]);
        _currentDelay = seconds;
        if (_statusLabel != null) _statusLabel.Text = $"Пауза: {seconds} сек";
    }

    private void HandleCycle(string[] args)
    {
        if (!ExtractCycleCoordinates(args, out Vector2 vecA, out Vector2 vecB))
        {
            throw new ArgumentException("Формат: (X1, X2, Count) или (X1, Y1, X2, Y2, Count)");
        }

        int countIndex = (args.Length >= 5) ? 4 : 2;
        int pauseIndex = (args.Length >= 5) ? 5 : 3;

        int count = (int)ParseFloat(args[countIndex]);
        float pause = 0.5f;
        if (args.Length > pauseIndex) pause = ParseFloat(args[pauseIndex]);

        if (count <= 0) return;

        // Рисуем на сетке
        UpdateGridPoints(vecA, vecB);

        _cyclePointA = vecA;
        _cyclePointB = vecB;
        _cycleCount = count;
        _cyclePause = pause;
        _isLargeCycle = true;
        _currentCycleStep = 0;

        string startCmd = $"GO({vecA.X.ToString(CultureInfo.InvariantCulture)}, {vecA.Y.ToString(CultureInfo.InvariantCulture)})";
        PushPriorityCommand(startCmd);
    }

    private void HandleEnd()
    {
        _commandQueue.Clear();
        _isLargeCycle = false;
        _state = InterpreterState.Idle;
        if (_statusLabel != null)
        {
            _statusLabel.Text = "Выполнено";
            _statusLabel.Modulate = Colors.White;
        }
        ToggleButtons(false);
        ClearHighlighting();
    }

    // --- ПУБЛИЧНОЕ УПРАВЛЕНИЕ ---

    public void RunScript(string scriptText)
    {
        if (_state == InterpreterState.Running) return;
        if (string.IsNullOrEmpty(scriptText)) return;

        ClearHighlighting();
        ParseScriptText(scriptText);

        _state = InterpreterState.Running;
        ToggleButtons(true);

        if (_statusLabel != null)
        {
            _statusLabel.Text = "Запуск...";
            _statusLabel.Modulate = Colors.White;
        }
    }

    public void StopScript()
    {
        // Аварийный сброс, если мы в ошибке
        if (_state == InterpreterState.Error)
        {
            HardReset();
            return;
        }

        if (_state == InterpreterState.Running)
        {
            _state = InterpreterState.Paused;
            if (_statusLabel != null) _statusLabel.Text = "Пауза (Польз.)";
            if (TargetBurner != null && !TargetBurner.IsManualPaused)
            {
                TargetBurner.SetManualPause(true);
                _movementWasPaused = true;
            }
        }
        else if (_state == InterpreterState.Paused)
        {
            _state = InterpreterState.Running;
            if (_statusLabel != null) _statusLabel.Text = "Продолжение...";
            if (TargetBurner != null && _movementWasPaused)
            {
                TargetBurner.SetManualPause(false);
                _movementWasPaused = false;
            }
        }
    }

    // --- ВСПОМОГАТЕЛЬНЫЕ ---

    private float ParseFloat(string input)
    {
        input = input.Replace(',', '.');
        if (float.TryParse(input, NumberStyles.Any, CultureInfo.InvariantCulture, out float result))
            return result;
        throw new FormatException($"Число '{input}' некорректно");
    }

    private void ParseScriptText(string script)
    {
        _commandQueue.Clear();
        _executionIndexMap.Clear();
        _commandCounter = 0;
        _isLargeCycle = false;

        var lines = script.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("//") || line.StartsWith("#"))
                continue;

            _executionIndexMap[_commandCounter] = i;
            _commandQueue.Enqueue(line);
            _commandCounter++;
        }
    }

    private void UpdateGridPoints(Vector2 a, Vector2 b)
    {
        if (_grid == null) return;
        Vector2[] pts = { a, b, Vector2.Zero };
        Color[] cols = { Colors.Yellow, Colors.Orange, Colors.Transparent };
        _grid.UpdatePoints(pts, cols);
    }

    private void ToggleButtons(bool isRunning)
    {
        if (_runButton != null) _runButton.Disabled = isRunning;
        if (_stopButton != null) _stopButton.Disabled = !isRunning;
    }

    private void HighlightCurrentLine(int lineIndex)
    {
        if (_scriptInput == null) return;
        if (_lastHighlightedLine >= 0 && _lastHighlightedLine < _scriptInput.GetLineCount())
            _scriptInput.SetLineBackgroundColor(_lastHighlightedLine, new Color(0, 0, 0, 0));

        if (lineIndex >= 0 && lineIndex < _scriptInput.GetLineCount())
        {
            _scriptInput.SetLineBackgroundColor(lineIndex, new Color(0.2f, 0.6f, 1f, 0.25f));
            _scriptInput.SetLineAsCenterVisible(lineIndex);
            _lastHighlightedLine = lineIndex;
        }
    }

    private void ClearHighlighting()
    {
        if (_scriptInput == null) return;
        if (_lastHighlightedLine >= 0 && _lastHighlightedLine < _scriptInput.GetLineCount())
            _scriptInput.SetLineBackgroundColor(_lastHighlightedLine, new Color(0, 0, 0, 0));
        _lastHighlightedLine = -1;
    }
}