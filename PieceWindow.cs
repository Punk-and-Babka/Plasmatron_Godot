using Godot;
using System;
using System.Globalization;

public partial class PieceWindow : Window
{
    // Сигнал, передающий размеры детали (Width, Height)
    [Signal] public delegate void PieceDimensionsAppliedEventHandler(float w, float h);

    [Export] private LineEdit _inputWidth;
    [Export] private LineEdit _inputHeight;
    [Export] private Button _btnDraw;
    [Export] private Button _btnClear;

    public override void _Ready()
    {
        CloseRequested += Hide;

        if (_btnDraw != null) _btnDraw.Pressed += OnDrawPressed;
        if (_btnClear != null) _btnClear.Pressed += OnClearPressed;
    }

    private void OnDrawPressed()
    {
        // Парсим ввод с учетом культуры (точки и запятые)
        if (float.TryParse(_inputWidth.Text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out float width) &&
            float.TryParse(_inputHeight.Text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out float height))
        {
            if (width > 0 && height > 0)
            {
                // Отправляем сигнал с размерами
                EmitSignal(SignalName.PieceDimensionsApplied, width, height);
                Hide(); // Закрываем окно
            }
            else
            {
                // Можно добавить индикацию ошибки, пока просто не закрываем окно
                GD.PrintErr("Размеры должны быть больше нуля");
            }
        }
    }

    private void OnClearPressed()
    {
        _inputWidth.Clear();
        _inputHeight.Clear();
        // Отправляем нулевые размеры, чтобы очистить сетку
        EmitSignal(SignalName.PieceDimensionsApplied, 0f, 0f);
        Hide();
    }
}