using Godot;
using System;
using System.Globalization;
using System.Text.RegularExpressions;

public partial class SpeedInputHandler : LineEdit
{
    [Export] private float MinSpeed = 0f;
    [Export] private float MaxSpeed = 100f;
    [Export] private float Step = 0.1f;

    [Signal] public delegate void SpeedChangedEventHandler(float newSpeed);

    private readonly Regex _numberRegex = new Regex(@"^(\d+([\.,]\d*)?|[\.,]\d+)$");
    private const string Prefix = "Скорость: ";
    private const string Suffix = " см/с";
    private float _speed = 0f;
    private bool _isEditing = false;

    public override void _Ready()
    {
        UpdateDisplay(_speed);
        TextSubmitted += OnTextSubmitted;
        FocusExited += OnFocusLost;
        FocusEntered += OnFocusGained;
    }

    private void OnFocusGained()
    {
        _isEditing = true;
        Text = _speed.ToString("0.0", CultureInfo.InvariantCulture);
        CaretColumn = Text.Length;
    }

    private void OnFocusLost()
    {
        if (_isEditing) FinalizeInput();
    }

    private void OnTextSubmitted(string newText)
    {
        FinalizeInput();
        ReleaseFocus();
    }

    protected void OnTextChanged(string newText)
    {
        if (!_isEditing) return;

        // Временная проверка формата
        if (!_numberRegex.IsMatch(newText))
        {
            DeleteText(CaretColumn - 1, CaretColumn);
        }
    }

    private void FinalizeInput()
    {
        if (TryParseSpeed(Text, out float newSpeed))
        {
            newSpeed = Math.Clamp(newSpeed, MinSpeed, MaxSpeed);
            newSpeed = (float)Math.Round(newSpeed / Step) * Step;

            if (Math.Abs(_speed - newSpeed) > 0.01f)
            {
                _speed = newSpeed;
                EmitSignal(nameof(SpeedChanged), newSpeed);
            }
        }

        _isEditing = false;
        UpdateDisplay(_speed);
    }

    private void UpdateDisplay(float speed)
    {
        string formatted = speed.ToString("0.0", CultureInfo.InvariantCulture);
        Text = $"{Prefix}{formatted}{Suffix}";
    }
    public void SetSpeed(float speed)
    {
        _speed = speed;
        UpdateDisplay(speed);
    }
    private bool TryParseSpeed(string input, out float speed)
    {
        input = input.Replace(",", ".");

        if (float.TryParse(input, NumberStyles.Float,
            CultureInfo.InvariantCulture, out speed))
        {
            return true;
        }

        speed = _speed;
        return false;
    }

    public float GetSpeed() => _speed;
}