using Godot;
using System;
using System.Globalization;

public partial class CalculationWindow : Window
{
    // Сигнал, который мы отправим в UIController, когда нажмем "Применить"
    [Signal] public delegate void SpeedAppliedEventHandler(float speed);

    [ExportGroup("Inputs")]
    [Export] private LineEdit _inputDiameter;
    [Export] private LineEdit _inputRPM;

    [ExportGroup("Outputs")]
    [Export] private LineEdit _outCircumference; // Длина окружности
    [Export] private LineEdit _outTime;          // Время оборота
    [Export] private LineEdit _outResultSpeed;   // Итоговая скорость

    [ExportGroup("Buttons")]
    [Export] private Button _btnCalculate;
    [Export] private Button _btnApply;
    [Export] private Button _btnClear;

    // Константы из старого кода
    private const double Vp = 200.0; // Базовая константа (мм/сек?)
    private const double ScanStepMM = 20.0; // Шаг напыления (из строки 15: 20.0 / time)

    public override void _Ready()
    {
        // При закрытии окна через крестик - просто скрываем его, а не удаляем
        CloseRequested += Hide;

        if (_btnCalculate != null) _btnCalculate.Pressed += CalculateForward;
        if (_btnApply != null) _btnApply.Pressed += ApplySpeed;
        if (_btnClear != null) _btnClear.Pressed += ClearFields;

        // Обратный расчет при вводе RPM и нажатии Enter
        if (_inputRPM != null) _inputRPM.TextSubmitted += (t) => CalculateReverse();
    }

    // --- ПРЯМОЙ РАСЧЕТ (По Диаметру) ---
    private void CalculateForward()
    {
        if (!double.TryParse(_inputDiameter.Text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double diameter))
        {
            _inputDiameter.Text = "Ошибка";
            return;
        }

        // 1. Длина окружности (C = D * PI)
        double C = diameter * Math.PI;
        _outCircumference.Text = C.ToString("F2");

        // 2. Время оборота (Time = C / Vp)
        // Внимание: в старом коде Vp=200. Если C < 200, время будет < 1 сек.
        double time = C / Vp;
        _outTime.Text = time.ToString("F3");

        if (time <= 0.001) return; // Защита от деления на ноль

        // 3. RPM (60 / time)
        double rpm = 60.0 / time;
        _inputRPM.Text = rpm.ToString("F2");

        // 4. Скорость напыления (20 / time)
        double pointSpeed = ScanStepMM / time;
        _outResultSpeed.Text = pointSpeed.ToString("F2");
    }

    // --- ОБРАТНЫЙ РАСЧЕТ (По RPM) ---
    private void CalculateReverse()
    {
        if (!double.TryParse(_inputRPM.Text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double rpm))
            return;

        if (rpm <= 0) return;

        // 1. Время оборота из RPM
        double time = 60.0 / rpm;
        _outTime.Text = time.ToString("F3");

        // 2. Скорость напыления
        double pointSpeed = ScanStepMM / time;
        _outResultSpeed.Text = pointSpeed.ToString("F2");

        // (Опционально) Можно пересчитать диаметр обратно, если нужно,
        // но в старом коде этого не было.
    }

    private void ApplySpeed()
    {
        if (float.TryParse(_outResultSpeed.Text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out float speed))
        {
            // Отправляем сигнал "Мы рассчитали скорость X"
            EmitSignal(SignalName.SpeedApplied, speed);
            Hide(); // Закрываем окно после применения
        }
    }

    private void ClearFields()
    {
        _inputDiameter.Clear();
        _inputRPM.Clear();
        _outCircumference.Clear();
        _outTime.Clear();
        _outResultSpeed.Clear();
    }
}