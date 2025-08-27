using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

public partial class ScriptInterpreter : Node
{
    public enum InterpreterState { Idle, Running, Paused, Error }

    [Export] private TextEdit _scriptInput;
    [Export] private Button _runButton;
    [Export] private Button _stopButton;
    [Export] private Label _statusLabel;
    [Export] private CoordinateGrid _grid;
    [Export] public Burner TargetBurner { get; set; } // Это наша ссылка на горелку

    private InterpreterState _state = InterpreterState.Idle;
    private Queue<string> _commandQueue = new Queue<string>();
    private Dictionary<string, Action<string[]>> _commandHandlers;
    private float _currentDelay = 0;
    private bool _waitingForMovement = false;
    private bool _waitingForSequence = false;

    private int _currentLineIndex = -1;
    private int _lastHighlightedLine = -1;

    private int _currentExecutionIndex = 0;
    private int _lastExecutedLineIndex = -1;

    private readonly Dictionary<int, int> _executionIndexMap = new Dictionary<int, int>();
    private int _commandCounter = 0;

    private float _movementWaitTimer = 0f;
    private float _sequenceWaitTimer = 0f;
    private const float WaitTimeout = 60f; // Максимальное время ожидания в секундах

    private string _currentCommand;

    public override void _Ready()
    {
        // Поиск горелки в родительской сцене
        TargetBurner = GetParent().GetNodeOrNull<Burner>("Burner");

        // Если не найдено, поищем среди дочерних элементов
        if (TargetBurner == null)
        {
            TargetBurner = GetNodeOrNull<Burner>("../Burner");
        }

        // Если всё ещё не найдено, попробуем найти по типу
        if (TargetBurner == null)
        {
            TargetBurner = GetTree().Root.GetNodeOrNull<Burner>("Main/Burner");
        }

        // Последний вариант: поиск во всей сцене
        if (TargetBurner == null)
        {
            var nodes = GetTree().GetNodesInGroup("burners");
            if (nodes.Count > 0 && nodes[0] is Burner burner)
            {
                TargetBurner = burner;
            }
        }

        if (TargetBurner == null)
        {
            GD.PrintErr("Burner reference not found! Please set it manually in inspector.");
            _runButton.Disabled = true;
            _statusLabel.Text = "Ошибка: Горелка не найдена";
        }
        else
        {
            GD.Print("Burner reference found successfully!");
        }

        InitializeCommandHandlers();
        GetTree().Root.ChildEnteredTree += (node) => UpdateHighlighting();
        _runButton.Pressed += RunScript;
        _stopButton.Pressed += StopScript;
    }

    public override void _Process(double delta)
    {
        if (_state != InterpreterState.Running) return;

        // Обработка паузы
        if (_currentDelay > 0)
        {
            _currentDelay -= (float)delta;
            _statusLabel.Text = $"Пауза: {_currentDelay:F1} сек";
            return;
        }

        // Обработка ожидания движения
        if (_waitingForMovement)
        {
            _movementWaitTimer -= (float)delta;

            // Проверяем, достигли ли цели
            if (TargetBurner != null && !TargetBurner.IsMovingToTarget)
            {
                _waitingForMovement = false;
                _movementWaitTimer = 0f;
                GD.Print("Движение завершено");
            }
            // Проверка таймаута
            else if (_movementWaitTimer <= 0)
            {
                GD.PrintErr("Таймаут ожидания движения!");
                _waitingForMovement = false;
            }
            else
            {
                return;
            }
        }

        // Обработка ожидания последовательности
        if (_waitingForSequence)
        {
            _sequenceWaitTimer -= (float)delta;

            // Проверяем, завершена ли последовательность
            if (TargetBurner != null && !TargetBurner.IsAutoSequenceActive)
            {
                _waitingForSequence = false;
                _sequenceWaitTimer = 0f;
                GD.Print("Последовательность завершена");
            }
            // Проверка таймаута
            else if (_sequenceWaitTimer <= 0)
            {
                GD.PrintErr("Таймаут ожидания последовательности!");
                _waitingForSequence = false;
            }
            else
            {
                return;
            }
        }

        // Если все ожидания завершены - выполняем следующую команду
        ExecuteNextCommand();
    }

    private void InitializeCommandHandlers()
    {
        _commandHandlers = new Dictionary<string, Action<string[]>>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPEED"] = args => HandleSpeed(args),
            ["GO"] = args => HandleGo(args),
            ["CYCLE"] = args => HandleCycle(args),
            ["PAUSE"] = args => HandlePause(args),
            ["START"] = args => GD.Print("Начало выполнения скрипта"),
            ["END"] = args => HandleEnd()
        };
    }

    private void RunScript()
    {
        if (_state == InterpreterState.Running) return;

        // Сбрасываем подсветку
        ClearHighlighting();

        ParseScript(_scriptInput.Text);
        _state = InterpreterState.Running;
        _statusLabel.Text = "Выполнение...";
        _runButton.Disabled = true;
        _stopButton.Disabled = false;
    }
    private bool _movementWasPaused;
    private void StopScript()
    {
        if (_state == InterpreterState.Running)
        {
            _state = InterpreterState.Paused;
            _statusLabel.Text = "Пауза";

            if (TargetBurner != null)
            {
                // Сохраняем состояние паузы движения
                _movementWasPaused = TargetBurner.IsManualPaused;

                // Ставим на паузу только если горелка не была уже на паузе
                if (!TargetBurner.IsManualPaused)
                {
                    TargetBurner.SetManualPause(true);
                }
            }
        }
        else if (_state == InterpreterState.Paused)
        {
            _state = InterpreterState.Running;
            _statusLabel.Text = "Возобновление...";

            if (TargetBurner != null)
            {
                // Восстанавливаем состояние паузы движения
                if (!_movementWasPaused && TargetBurner.IsManualPaused)
                {
                    TargetBurner.SetManualPause(false);
                }
            }
        }
        ClearHighlighting();
    }

    private void ParseScript(string script)
    {
        _commandQueue.Clear();
        _executionIndexMap.Clear();
        _commandCounter = 0;

        var lines = script.Split('\n');
        int lineIndex = 0;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrEmpty(trimmedLine)) continue;
            if (trimmedLine.StartsWith("//")) continue;

            // Сохраняем соответствие между номером команды и строкой
            _executionIndexMap[_commandCounter] = lineIndex;
            _commandQueue.Enqueue(trimmedLine);
            _commandCounter++;

            lineIndex++;
        }
    }

    private void ExecuteNextCommand()
    {
        if (_state == InterpreterState.Paused)
        {
            // Если на паузе - не выполняем команды
            return;
        }

        if (_commandQueue.Count > 0)
        {
            _currentCommand = _commandQueue.Peek();
        }

        if (_commandQueue.Count == 0)
        {
            _state = InterpreterState.Idle;
            _statusLabel.Text = "Скрипт завершен";
            _runButton.Disabled = false;
            _stopButton.Disabled = true;
            return;
        }

        int commandIndex = _commandCounter - _commandQueue.Count;
        var commandLine = _commandQueue.Dequeue();

        if (_executionIndexMap.TryGetValue(commandIndex, out int originalLineIndex))
        {
            HighlightCurrentLine(originalLineIndex);
        }
        else
        {
            GD.PrintErr($"Не найдено соответствие для команды {commandIndex}");
        }


        // Подсвечиваем правильную строку по оригинальному индексу
        HighlightCurrentLine(originalLineIndex);
        var parts = Regex.Split(commandLine, @"\(|\)|,")
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToArray();

        if (parts.Length == 0) return;

        var command = parts[0].ToUpper();
        var args = parts.Skip(1).ToArray();

        if (_commandHandlers.ContainsKey(command))
        {
            try
            {
                _commandHandlers[command](args);
            }
            catch (Exception ex)
            {
                _state = InterpreterState.Error;
                _statusLabel.Text = $"Ошибка: {ex.Message}";
                GD.PrintErr($"Ошибка выполнения команды '{command}': {ex}");
            }
        }
        else
        {
            GD.Print($"Неизвестная команда: {command}");
        }

    }

    private void HighlightCurrentLine(int lineIndex)
{
    // Убираем подсветку с предыдущей строки
    if (_lastHighlightedLine >= 0 && _lastHighlightedLine < _scriptInput.GetLineCount())
    {
        _scriptInput.SetLineBackgroundColor(_lastHighlightedLine, new Color(0, 0, 0, 0));
    }
    
    // Подсвечиваем текущую строку
    if (lineIndex >= 0 && lineIndex < _scriptInput.GetLineCount())
    {
        _scriptInput.SetLineBackgroundColor(lineIndex, new Color(0.2f, 0.5f, 0.8f, 0.3f));
        _lastHighlightedLine = lineIndex;
        
        // Прокручиваем к текущей строке
        int visibleLines = _scriptInput.GetVisibleLineCount();
        _scriptInput.ScrollVertical = (int)Mathf.Clamp(
            lineIndex - visibleLines / 2,
            0,
            Math.Max(0, _scriptInput.GetLineCount() - visibleLines)
        );
    }
}

    private int FindLineIndexInEditor(string command)
    {
        for (int i = 0; i < _scriptInput.GetLineCount(); i++)
        {
            string line = _scriptInput.GetLine(i).StripEdges();
            if (line.Contains(command.StripEdges()))
            {
                return i;
            }
        }
        return -1;
    }

    private void ClearHighlighting()
    {
        if (_lastHighlightedLine >= 0 && _lastHighlightedLine < _scriptInput.GetLineCount())
        {
            _scriptInput.SetLineBackgroundColor(_lastHighlightedLine, new Color(0, 0, 0, 0));
        }
        _lastHighlightedLine = -1;
    }

    private void UpdateHighlighting()
    {
        if (_state == InterpreterState.Running && _currentLineIndex >= 0)
        {
            if (_currentLineIndex < _scriptInput.GetLineCount())
            {
                _scriptInput.SetLineBackgroundColor(_currentLineIndex, new Color(0.2f, 0.5f, 0.8f, 0.3f));
            }
        }
    }
    private void EnsureLineVisible(int lineIndex)
    {
        int visibleLines = _scriptInput.GetVisibleLineCount();
        if (lineIndex < _scriptInput.ScrollVertical)
        {
            _scriptInput.ScrollVertical = lineIndex;
        }
        else if (lineIndex >= _scriptInput.ScrollVertical + visibleLines)
        {
            _scriptInput.ScrollVertical = lineIndex - visibleLines + 1;
        }
    }

    private void HandleSpeed(string[] args)
    {
        if (args.Length < 1 || !float.TryParse(args[0], out float speed))
            throw new ArgumentException("Некорректная скорость");

        if (TargetBurner == null)
        {
            _statusLabel.Text = "Ошибка: Горелка не найдена";
            return;
        }

        // Используем новый метод установки скорости
        TargetBurner.SetMovementSpeed(speed);

        GD.Print($"Установлена скорость: {speed} см/с");
        _statusLabel.Text = $"Скорость установлена: {speed} см/с";
    }

    private void HandleGo(string[] args)
    {
        if (args.Length < 1 || !float.TryParse(args[0], out float position))
            throw new ArgumentException("Некорректная позиция");

        if (TargetBurner == null)
        {
            _statusLabel.Text = "Ошибка: Горелка не найдена";
            return;
        }

        // Всегда устанавливаем цель, даже при паузе
        TargetBurner.MoveToPosition(position);

        // Заменяем _isPaused на проверку состояния интерпретатора
        if (_state == InterpreterState.Running) // Используем состояние вместо _isPaused
        {
            _waitingForMovement = true;
            _movementWaitTimer = WaitTimeout;
            _statusLabel.Text = $"Движение к позиции: {position} см";
            GD.Print($"Начато движение к позиции: {position} см");
        }
    }

    private float _cyclePointA;
    private float _cyclePointB;
    private int _cycleCount;
    private int _currentCycle;
    private float _cyclePause;
    private bool _isLargeCycle;

    private void HandleCycle(string[] args)
    {
        if (args.Length < 3)
            throw new ArgumentException("Недостаточно параметров для CYCLE");

        if (!float.TryParse(args[0], out float pointA) ||
            !float.TryParse(args[1], out float pointB) ||
            !int.TryParse(args[2], out int cycles))
            throw new ArgumentException("Некорректные параметры цикла");

        if (cycles <= 0)
        {
            _statusLabel.Text = "Ошибка: Количество циклов должно быть > 0";
            return;
        }

        // Парсим опциональный аргумент паузы
        float pauseDuration = 0.5f; // значение по умолчанию
        if (args.Length >= 4 && float.TryParse(args[3], out float customPause))
        {
            pauseDuration = customPause;
        }

        // Обновляем точки на сетке (создаем массивы длиной 3)
        if (_grid != null)
        {
            // Создаем массив из трех точек: первые две - точки цикла, третья - неиспользуемая
            float[] cyclePoints = new float[3] { pointA, pointB, -1 };

            // Специальные цвета для точек цикла
            Color[] cycleColors = new Color[3] {
            new Color(1, 1, 0),    // Желтый для точки A
            new Color(1, 0.5f, 0), // Оранжевый для точки B
            Colors.Transparent      // Прозрачный для третьей точки (не используется)
        };

            _grid.UpdatePoints(cyclePoints, cycleColors);
        }

        // Для циклов более 50 используем итеративный подход
        if (cycles > 50)
        {
            _cyclePointA = pointA;
            _cyclePointB = pointB;
            _cycleCount = cycles;
            _currentCycle = 1;
            _cyclePause = pauseDuration;
            _isLargeCycle = true;

            // Добавляем только первую команду
            _commandQueue.Enqueue($"Go({pointA})");
            _commandQueue.Enqueue($"Pause({pauseDuration})");

            GD.Print($"Начат большой цикл ({cycles} повторений)");
        }
        else
        {
            _isLargeCycle = false;

            // Генерация всех команд для малых циклов
            var cycleCommands = new List<string>();

            for (int i = 0; i < cycles; i++)
            {
                cycleCommands.Add($"Go({pointA})");
                cycleCommands.Add($"Pause({pauseDuration})");
                cycleCommands.Add($"Go({pointB})");
                cycleCommands.Add($"Pause({pauseDuration})");
            }

            // Финишное возвращение в точку A
            cycleCommands.Add($"Go({pointA})");

            // Вставляем сгенерированные команды в начало очереди
            var newQueue = new Queue<string>(cycleCommands);
            while (_commandQueue.Count > 0)
            {
                newQueue.Enqueue(_commandQueue.Dequeue());
            }
            _commandQueue = newQueue;

            GD.Print($"Развернут цикл на {cycles} повторений");
        }

        _statusLabel.Text = $"Цикл: {pointA} ↔ {pointB} ({cycles}x)";
    }

    private void HandlePause(string[] args)
    {
        if (args.Length < 1 || !float.TryParse(args[0], out float seconds))
            throw new ArgumentException("Некорректное время паузы");

        _currentDelay = seconds;
        _statusLabel.Text = $"Пауза: {seconds} сек";
        GD.Print($"Начата пауза на {seconds} сек");

        // Запускаем таймер для обработки завершения паузы
        var timer = new Timer();
        timer.WaitTime = seconds;
        timer.OneShot = true;
        timer.Timeout += () => {
            OnPauseFinished();
            timer.QueueFree();
        };
        AddChild(timer);
        timer.Start();
    }

    private void HandleEnd()
    {
        _commandQueue.Clear();
        _state = InterpreterState.Idle;
        _statusLabel.Text = "Скрипт завершен";
        _runButton.Disabled = false;
        _stopButton.Disabled = true;
        ClearHighlighting();
    }

    private void OnMovementStopped()
    {
        if (TargetBurner != null)
        {
            TargetBurner.MovementStopped -= OnMovementStopped;
        }
        _waitingForMovement = false;

        // Обработка больших циклов
        if (_isLargeCycle && _state == InterpreterState.Running)
        {
            // Определяем следующий шаг
            string nextCommand = "";

            // После паузы в точке A - двигаться к B
            if (_currentCycle % 2 == 1)
            {
                nextCommand = $"Go({_cyclePointB})";
            }
            // После паузы в точке B - либо двигаться к A, либо завершить
            else
            {
                if (_currentCycle < _cycleCount * 2)
                {
                    nextCommand = $"Go({_cyclePointA})";
                }
            }

            // Добавляем следующую команду в очередь
            if (!string.IsNullOrEmpty(nextCommand))
            {
                _commandQueue.Enqueue(nextCommand);
                GD.Print($"Добавлен шаг цикла: {nextCommand}");
            }
        }
    }

    private void OnPauseFinished()
    {
        if (_isLargeCycle && _state == InterpreterState.Running)
        {
            // Определяем следующее действие
            string nextCommand = "";

            // После движения к точке A - добавить паузу
            if (_currentCycle % 2 == 1)
            {
                nextCommand = $"Pause({_cyclePause})";
                _currentCycle++;
            }
            // После движения к точке B - добавить паузу или завершить
            else if (_currentCycle % 2 == 0)
            {
                // Если это последний цикл
                if (_currentCycle >= _cycleCount * 2)
                {
                    GD.Print($"Цикл завершен ({_cycleCount} повторений)");
                    _isLargeCycle = false;
                }
                else
                {
                    nextCommand = $"Pause({_cyclePause})";
                    _currentCycle++;
                }
            }

            // Добавляем команду паузы в очередь
            if (!string.IsNullOrEmpty(nextCommand))
            {
                _commandQueue.Enqueue(nextCommand);
                GD.Print($"Добавлена пауза цикла: {nextCommand}");
            }
        }
    }

    private void OnSequenceFinished()
    {
        // Используем TargetBurner вместо _burner
        if (TargetBurner != null)
        {
            TargetBurner.SequenceFinished -= OnSequenceFinished;
        }
        _waitingForSequence = false;
    }
}