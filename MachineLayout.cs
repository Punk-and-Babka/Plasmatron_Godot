using Godot;
using System;

public partial class MachineLayout : Control
{
    [Export] private Node2D _machineRoot;
    [Export] private Control _blackPanel;
    [Export] private CoordinateGrid _grid;

    // Отступ от краев (в пикселях экрана)
    private const float ScreenPadding = 30f;

    public override void _Ready()
    {
        // Привязки
        if (_machineRoot == null) _machineRoot = GetNodeOrNull<Node2D>("MachineRoot");
        if (_blackPanel == null) _blackPanel = GetNodeOrNull<Control>("BlackPanel");
        if (_machineRoot != null && _grid == null) _grid = _machineRoot.GetNodeOrNull<CoordinateGrid>("CoordinateGrid");

        // Подписки на изменение размера
        Resized += UpdateLayout;
        if (_blackPanel != null) _blackPanel.Resized += UpdateLayout;

        CallDeferred(nameof(UpdateLayout));
    }

    private void UpdateLayout()
    {
        if (_blackPanel == null || _machineRoot == null || _grid == null) return;

        Vector2 panelSize = _blackPanel.Size;
        if (panelSize.X <= 10 || panelSize.Y <= 10) return; // Защита от нулевого размера

        // 1. Реальный размер станка (1600 x 900)
        float machineW = _grid.RealWorldWidthMM;
        float machineH = _grid.RealWorldHeightMM;

        // 2. Доступное место на экране
        float availW = panelSize.X - (ScreenPadding * 2);
        float availH = panelSize.Y - (ScreenPadding * 2);

        // 3. Считаем коэффициент масштаба (Zoom)
        // Чтобы 1600 мм влезло в N пикселей экрана
        float scaleX = availW / machineW;
        float scaleY = availH / machineH;
        float finalZoom = Math.Min(scaleX, scaleY);

        // 4. Применяем масштаб ко ВСЕМУ станку сразу!
        // Теперь 1 единица координат внутри MachineRoot будет визуально меньше 1 пикселя
        _machineRoot.Scale = new Vector2(finalZoom, finalZoom);

        // 5. Центрируем станок
        // Размер станка в пикселях после масштабирования:
        float visualW = machineW * finalZoom;
        float visualH = machineH * finalZoom;

        float offsetX = (panelSize.X - visualW) / 2;
        float offsetY = (panelSize.Y - visualH) / 2;

        _machineRoot.Position = new Vector2(offsetX, offsetY);

        // 6. Сетке больше не нужно знать про пиксели экрана. 
        // Она рисует себя в размере 1600x900, а Scale делает остальное.
        _grid.QueueRedraw();

        // GD.Print($"Layout Updated: Panel={panelSize}, Zoom={finalZoom}");
    }
}