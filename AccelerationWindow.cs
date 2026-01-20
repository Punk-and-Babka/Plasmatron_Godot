using Godot;
using System;
using System.Globalization;

public partial class AccelerationWindow : Window
{
    // Сигнал: передаем (accelTime, decelTime)
    [Signal] public delegate void AccelerationSettingsAppliedEventHandler(float accel, float decel);

    [Export] private LineEdit _inputAccel;
    [Export] private LineEdit _inputDecel;
    [Export] private Button _btnApply;
    [Export] private Button _btnCancel;

    public override void _Ready()
    {
        CloseRequested += Hide;
        if (_btnApply != null) _btnApply.Pressed += OnApplyPressed;
        if (_btnCancel != null) _btnCancel.Pressed += Hide;
    }

    // Метод для инициализации полей текущими значениями (вызывается из UIController)
    public void InitValues(float currentAccel, float currentDecel)
    {
        if (_inputAccel != null) _inputAccel.Text = currentAccel.ToString(CultureInfo.InvariantCulture);
        if (_inputDecel != null) _inputDecel.Text = currentDecel.ToString(CultureInfo.InvariantCulture);
    }

    private void OnApplyPressed()
    {
        if (float.TryParse(_inputAccel.Text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out float a) &&
            float.TryParse(_inputDecel.Text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out float d))
        {
            if (a > 0 && d > 0)
            {
                EmitSignal(SignalName.AccelerationSettingsApplied, a, d);
                Hide();
            }
            else
            {
                GD.PrintErr("Время должно быть больше 0");
            }
        }
        else
        {
            GD.PrintErr("Ошибка формата числа");
        }
    }
}